using MailKit;
using MailKit.Net.Imap;
using MimeKit;

namespace p2poolmail
{
    /// <summary>One IDLE iteration of <see cref="ImapClientService"/>: open folder, wait for mail, folder events, UID bootstrap.</summary>
    public partial class ImapClientService
    {
        private async Task IdleLoopIterationAsync(Func<MimeMessage, Task> onNewMessage, CancellationToken token)
        {
            if (!_client.IsConnected)
                await EnsureConnectedAsync(token).ConfigureAwait(false);

            var folder = await ResolveAndOpenFolderAsync(null, FolderAccess.ReadWrite, token).ConfigureAwait(false);
            var initialCount = folder.Count;

            // UIDs are only comparable within one UIDVALIDITY epoch. When the server
            // changes it (folder recreated, migration), old UIDs would make every new
            // message look "already processed" - reset the watermark and re-bootstrap.
            if (_lastUidValidity.HasValue && folder.UidValidity != _lastUidValidity.Value)
            {
                _logger?.Invoke($"IMAP UIDVALIDITY changed ({_lastUidValidity.Value} -> {folder.UidValidity}) - resetting last processed UID");
                _lastProcessedUid = null;
                _skippedExistingUnreadAtStartup = false;
            }
            _lastUidValidity = folder.UidValidity;

            await InitializeLastUidIfNeededAsync(folder).ConfigureAwait(false);

            var supportsIdle = _client.Capabilities.HasFlag(ImapCapabilities.Idle);
            using var idleDoneCts = new CancellationTokenSource();
            var sessionId = Guid.NewGuid().ToString("N")[..8];
            _logger?.Invoke($"[{sessionId}] IdleLoopIteration start: folder={folder.FullName}, count={folder.Count}, unread={folder.Unread}");

            var handlers = SubscribeFolderHandlers(folder, idleDoneCts, sessionId);

            try
            {
                await WaitForMailAsync(folder, supportsIdle, token, idleDoneCts.Token, sessionId).ConfigureAwait(false);
            }
            finally
            {
                UnsubscribeFolderHandlers(folder, handlers);
            }

            // No re-SELECT here: standard clients keep the folder selected across IDLE
            // wakes and rely on MailKit applying untagged responses to the live folder
            // object. A second SELECT per wake just doubles command traffic; if the
            // connection dropped during the wait, the next command fails and the loop
            // reconnects with backoff - the recovery path is unchanged.
            if (folder.Count != initialCount)
                _logger?.Invoke($"Folder changed: {initialCount} → {folder.Count} messages");

            await CheckAndProcessNewMessageAsync(folder, supportsIdle, onNewMessage, token).ConfigureAwait(false);
        }

        private (EventHandler<EventArgs> CountChanged, EventHandler<MessageEventArgs> MessageExpunged, EventHandler<MessageFlagsChangedEventArgs> MessageFlagsChanged) SubscribeFolderHandlers(IMailFolder folder, CancellationTokenSource idleDoneCts, string sessionId)
        {
            void CancelIdleOnEvent(string eventName, string details)
            {
                try
                {
                    _logger?.Invoke($"[{sessionId}] Folder.{eventName} event: {details}");
                    TryCancel(idleDoneCts);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[{sessionId}] {eventName} handler error: {ex.Message}");
                }
            }

            EventHandler<EventArgs> countChangedHandler = (_, _) => CancelIdleOnEvent("CountChanged", $"Count={folder.Count}, Unread={folder.Unread}");
            EventHandler<MessageEventArgs> messageExpungedHandler = (_, e) => CancelIdleOnEvent("MessageExpunged", $"Index={e.Index}");
            EventHandler<MessageFlagsChangedEventArgs> flagsChangedHandler = (_, e) => CancelIdleOnEvent("MessageFlagsChanged", $"Index={e.Index}, Flags={e.Flags}");

            folder.CountChanged += countChangedHandler;
            folder.MessageExpunged += messageExpungedHandler;
            folder.MessageFlagsChanged += flagsChangedHandler;

            return (countChangedHandler, messageExpungedHandler, flagsChangedHandler);
        }

        private static void UnsubscribeFolderHandlers(IMailFolder folder, (EventHandler<EventArgs> CountChanged, EventHandler<MessageEventArgs> MessageExpunged, EventHandler<MessageFlagsChangedEventArgs> MessageFlagsChanged) handlers)
        {
            try { folder.CountChanged -= handlers.CountChanged; } catch { }
            try { folder.MessageExpunged -= handlers.MessageExpunged; } catch { }
            try { folder.MessageFlagsChanged -= handlers.MessageFlagsChanged; } catch { }
        }

        private async Task InitializeLastUidIfNeededAsync(IMailFolder folder)
        {
            if (_skippedExistingUnreadAtStartup)
                return;

            try
            {
                if (folder.Count > 0)
                {
                    var lastSummary = (await folder.FetchAsync(folder.Count - 1, folder.Count - 1, MessageSummaryItems.UniqueId).ConfigureAwait(false)).FirstOrDefault();
                    if (lastSummary != null)
                    {
                        _lastProcessedUid = lastSummary.UniqueId;
                        _logger?.Invoke($"Initialized: skip existing messages up to UID {_lastProcessedUid}");
                    }
                }
                else
                {
                    _logger?.Invoke("Folder empty at startup");
                }

                // Success: remember the watermark for this UIDVALIDITY epoch and do
                // not re-run bootstrap on later iterations.
                _skippedExistingUnreadAtStartup = true;
            }
            catch (Exception ex)
            {
                // Leave _skippedExistingUnreadAtStartup false so the next iteration
                // retries bootstrapping; marking it done here would leave
                // _lastProcessedUid null and replay ALL unread messages as "new".
                _logger?.Invoke($"Failed to initialize last UID: {ex.Message}");
            }
        }
    }
}
