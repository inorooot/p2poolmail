using MailKit;
using MailKit.Net.Imap;

namespace p2poolmail
{
    /// <summary>Connection lifecycle of <see cref="ImapClientService"/>: connect, authenticate, folder access.</summary>
    public partial class ImapClientService
    {
        /// <summary>
        /// Connects and authenticates. Must only be called while the SyncRoot gate is
        /// held: MailKit's ImapClient is not thread-safe, and the IDLE loop owns the
        /// gate for the whole service lifetime. The method is private on purpose -
        /// external callers go through <see cref="InitializeAsync"/>, never touch the
        /// client directly.
        /// </summary>
        private async Task ConnectAsync(CancellationToken cancellationToken = default)
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
            await _client.ConnectAsync(_host, _port, _useSsl, cancellationToken).ConfigureAwait(false);

            // MailKit fills AuthenticationMechanisms only after connect, so the
            // removal must happen here. This keeps us from picking XOAUTH2 without
            // real OAuth2 credentials.
            _client.AuthenticationMechanisms.Remove("XOAUTH2");

            if (!string.IsNullOrEmpty(_username))
            {
                await _client.AuthenticateAsync(_username!, _password!, cancellationToken).ConfigureAwait(false);
            }

            // Announce ourselves via the IMAP ID extension. Most servers just log
            // it; some providers (163/126/QQ mail) require it and reject every
            // command with "Unsafe Login" otherwise.
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
            DisconnectQuiet();
        }

        /// <summary>Disconnects without throwing. Safe to call from anywhere.</summary>
        private void DisconnectQuiet()
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
