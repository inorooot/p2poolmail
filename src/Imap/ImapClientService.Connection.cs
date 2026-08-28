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
            catch (Exception ex) when (_ignoreCertificateErrors && !_client.IsConnected)
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
            try
            {
                if (_client.IsConnected)
                    _client.Disconnect(true);
            }
            catch { }
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
                _logger?.Invoke($"Failed to connect to {_host}:{_port}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                throw;
            }
        }

        private async Task<IMailFolder> ResolveFolderAsync(string? folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName) || string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase))
                return _client.Inbox;

            try
            {
                return await _client.GetFolderAsync(folderName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Failed to resolve IMAP folder '{folderName}': {ex.Message}");
                throw;
            }
        }

        private async Task<IMailFolder> ResolveAndOpenFolderAsync(string? folderName, FolderAccess access, CancellationToken cancellationToken = default)
        {
            var folder = await ResolveFolderAsync(folderName).ConfigureAwait(false);
            await folder.OpenAsync(access, cancellationToken).ConfigureAwait(false);
            return folder;
        }
    }
}
