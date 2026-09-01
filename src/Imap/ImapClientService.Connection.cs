using MailKit;
using MailKit.Net.Imap;

namespace p2poolmail
{
    /// <summary>Connection lifecycle of <see cref="ImapClientService"/>: connect, authenticate, folder access.</summary>
    public partial class ImapClientService
    {
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_client.IsConnected)
                return;

            SetState(ImapRunState.Connecting);

            try
            {
                await ConnectAndAuthenticateAsync(cancellationToken).ConfigureAwait(false);
                SetState(ImapRunState.Idle);
                LogIdleSupport("Connected");
            }
            // Cancellation must not take the bypass path: with a cancelled token the
            // retry below would throw immediately and just mask the original reason.
            catch (Exception ex) when (_ignoreCertificateErrors && !_client.IsConnected && ex is not OperationCanceledException)
            {
                _logger?.Invoke($"IMAP normal SSL connect failed for {_host}:{_port}, retrying with certificate validation disabled. Error: {ex.Message}");
                await ReconnectWithCertificateValidationBypassAsync(cancellationToken).ConfigureAwait(false);
                SetState(ImapRunState.Idle);
                LogIdleSupport("Connected using certificate validation bypass");
            }
            catch
            {
                SetState(ImapRunState.Reconnecting);
                if (_client.IsConnected)
                    _client.Disconnect(true);
                throw;
            }
        }

        private async Task ConnectAndAuthenticateAsync(CancellationToken cancellationToken)
        {
            _client.AuthenticationMechanisms.Remove("XOAUTH2");
            await _client.ConnectAsync(_host, _port, _useSsl, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(_username))
            {
                await _client.AuthenticateAsync(_username!, _password!, cancellationToken).ConfigureAwait(false);
            }

            // Standard-client behavior: announce ourselves via the IMAP ID extension.
            // Most servers just log it; some providers (163/126/QQ mail) REQUIRE a client
            // identification before any other command and reject everything with
            // "Unsafe Login" otherwise.
            if (_client.Capabilities.HasFlag(ImapCapabilities.Id))
            {
                try
                {
                    // MailKitLite names this type ImapImplementation (upstream MailKit:
                    // ImapIdentification); same IMAP ID payload either way.
                    await _client.IdentifyAsync(new ImapImplementation
                    {
                        Name = "p2poolmail",
                        Version = "1.0",
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Identification is advisory - never let it fail the connection.
                    _logger?.Invoke($"IMAP ID command not accepted by {_host}: {ex.Message}");
                }
            }
        }

        private async Task ReconnectWithCertificateValidationBypassAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_client.IsConnected)
                    _client.Disconnect(true);
                _client.Dispose();
            }
            catch { }

            _client = new ImapClient
            {
                ServerCertificateValidationCallback = (_, _, _, _) =>
                {
                    _logger?.Invoke($"IMAP SSL certificate validation bypassed for {_host}:{_port}");
                    return true;
                }
            };

            await ConnectAndAuthenticateAsync(cancellationToken).ConfigureAwait(false);
        }

        private void LogIdleSupport(string prefix)
        {
            var supportsIdle = _client.Capabilities.HasFlag(ImapCapabilities.Idle);
            _logger?.Invoke(supportsIdle
                ? $"{prefix} to {_host}:{_port}; server supports IMAP IDLE"
                : $"{prefix} to {_host}:{_port}; server does not advertise IMAP IDLE");
        }

        public async Task DisconnectAsync()
        {
            await StopIdleAsync().ConfigureAwait(false);
            await TryDisconnectAsync().ConfigureAwait(false);
        }

        private async Task TryDisconnectAsync()
        {
            try
            {
                if (_client.IsConnected)
                    _client.Disconnect(true);
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error during disconnect: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        private async Task EnsureConnectedAsync(CancellationToken token)
        {
            try
            {
                await ConnectAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // No delay here: the IDLE loop applies its own exponential backoff.
                _logger?.Invoke($"Failed to connect to {_host}:{_port}: {ex.Message}");
                throw;
            }
        }

        private async Task<IMailFolder> ResolveFolderAsync(string? folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName) || IsInboxRequest(folderName))
                return _client.Inbox;

            try
            {
                return await _client.GetFolderAsync(folderName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Failed to resolve folder '{folderName}': {ex.Message}");
                throw;
            }
        }

        private static bool IsInboxRequest(string? folderName) =>
            string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase);

        private async Task<IMailFolder> ResolveAndOpenFolderAsync(string? folderName, FolderAccess access, CancellationToken cancellationToken = default)
        {
            var folder = await ResolveFolderAsync(folderName).ConfigureAwait(false);
            await folder.OpenAsync(access, cancellationToken).ConfigureAwait(false);
            return folder;
        }
    }
}
