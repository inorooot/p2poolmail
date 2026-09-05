using MailKit;
using MimeKit;

namespace p2poolmail
{
    /// <summary>One IDLE iteration of <see cref="ImapClientService"/>: open folder, wait for mail, folder events, UID bootstrap.</summary>
    public partial class ImapClientService
    {
        /// <summary>Container for folder event handlers with clear names.</summary>
        private record FolderEventHandlers(
            EventHandler<EventArgs> CountChanged,
            EventHandler<MessageEventArgs> MessageExpunged,
            EventHandler<MessageFlagsChangedEventArgs> MessageFlagsChanged);
        private async Task IdleLoopIterationAsync(Func<MimeMessage, Task> onNewMessage, CancellationToken token)
        {
            var folder = await ResolveAndOpenFolderAsync(null, FolderAccess.ReadWrite, token).ConfigureAwait(false);
            var initialCount = folder.Count;

            CheckAndResetUidValidity(folder);
            await InitializeLastUidIfNeededAsync(folder).ConfigureAwait(false);

            // After a reconnect the server may have new mail that no IDLE event
            // reported. Scan once now so we do not miss it.
            await CheckAndProcessNewMessageAsync(folder, onNewMessage, token).ConfigureAwait(false);

            var sessionId = StartMailCheckSession(folder);
            using var folderEventToken = new CancellationTokenSource();
            var handlers = SubscribeFolderHandlers(folder, folderEventToken, sessionId);

            try
            {
                await WaitForMailAsync(folder, token, folderEventToken.Token).ConfigureAwait(false);
            }
            finally
            {
                UnsubscribeFolderHandlers(folder, handlers);
            }

            LogFolderChange(folder, initialCount);
            await CheckAndProcessNewMessageAsync(folder, onNewMessage, token).ConfigureAwait(false);
        }

        private FolderEventHandlers SubscribeFolderHandlers(IMailFolder folder, CancellationTokenSource folderEventToken, string sessionId)
        {
            void LogAndCancelOnEvent(string eventName, string details)
            {
                try
                {
                    _logger?.Invoke($"[{sessionId}] Folder.{eventName}: {details}");
                    TryCancel(folderEventToken);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[{sessionId}] Error handling {eventName}: {ex.Message}");
                }
            }

            EventHandler<EventArgs> countChangedHandler = (_, _) => 
                LogAndCancelOnEvent("CountChanged", $"Count={folder.Count}, Unread={folder.Unread}");
            
            EventHandler<MessageEventArgs> messageExpungedHandler = (_, e) => 
                LogAndCancelOnEvent("MessageExpunged", $"Index={e.Index}");
            
            EventHandler<MessageFlagsChangedEventArgs> flagsChangedHandler = (_, e) => 
                LogAndCancelOnEvent("MessageFlagsChanged", $"Index={e.Index}, Flags={e.Flags}");

            folder.CountChanged += countChangedHandler;
            folder.MessageExpunged += messageExpungedHandler;
            folder.MessageFlagsChanged += flagsChangedHandler;

            return new FolderEventHandlers(countChangedHandler, messageExpungedHandler, flagsChangedHandler);
        }

        private void UnsubscribeFolderHandlers(IMailFolder folder, FolderEventHandlers handlers)
        {
            UnsubscribeEvent(() => folder.CountChanged -= handlers.CountChanged, "CountChanged");
            UnsubscribeEvent(() => folder.MessageExpunged -= handlers.MessageExpunged, "MessageExpunged");
            UnsubscribeEvent(() => folder.MessageFlagsChanged -= handlers.MessageFlagsChanged, "MessageFlagsChanged");
        }

        private void UnsubscribeEvent(Action action, string eventName)
        {
            try { action(); }
            catch (Exception ex) { _logger?.Invoke($"Failed to unsubscribe from {eventName}: {ex.Message}"); }
        }

        /// <summary>
        /// Sets the watermark to the newest existing message so old mail is skipped
        /// at startup. Errors must not be swallowed here: without a watermark the
        /// scan below would treat old unread mail as new.
        /// </summary>
        private async Task InitializeLastUidIfNeededAsync(IMailFolder folder)
        {
            if (_existingMailSkipped)
                return;

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

            _existingMailSkipped = true;
        }

        private void CheckAndResetUidValidity(IMailFolder folder)
        {
            // UIDs are only comparable within one UIDVALIDITY epoch.
            // When the server changes it (folder recreated, migration),
            // old UIDs would make every new message look "already processed" - reset.
            if (!_lastUidValidity.HasValue || folder.UidValidity == _lastUidValidity.Value)
            {
                _lastUidValidity = folder.UidValidity;
                return;
            }

            _logger?.Invoke($"UIDVALIDITY changed ({_lastUidValidity.Value} → {folder.UidValidity}) - resetting processing state");
            _lastProcessedUid = null;
            _existingMailSkipped = false;
            _lastUidValidity = folder.UidValidity;
        }

        private string StartMailCheckSession(IMailFolder folder)
        {
            var sessionId = Guid.NewGuid().ToString("N")[..8];
            _logger?.Invoke($"[{sessionId}] Starting mail check: folder={folder.FullName}, count={folder.Count}, unread={folder.Unread}");
            return sessionId;
        }

        private void LogFolderChange(IMailFolder folder, int initialCount)
        {
            if (folder.Count != initialCount)
                _logger?.Invoke($"Folder changed: {initialCount} → {folder.Count} messages");
        }
    }
}
