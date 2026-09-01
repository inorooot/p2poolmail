using MailKit;
using MailKit.Search;
using MimeKit;

namespace p2poolmail
{
    /// <summary>Mail processing of <see cref="ImapClientService"/>: process new messages, mark as seen.</summary>
    public partial class ImapClientService
    {
        private async Task CheckAndProcessNewMessageAsync(IMailFolder folder, bool isIdle, Func<MimeMessage, Task> onNewMessage, CancellationToken cancellationToken)
        {
            try
            {
                var latestUid = await GetLatestUnreadUidAsync(folder, cancellationToken).ConfigureAwait(false);
                if (!latestUid.HasValue)
                {
                    if (!_lastProcessedUid.HasValue)
                        _logger?.Invoke("No unread messages (initialization)");
                    return;
                }

                await ProcessMessageAsync(folder, latestUid.Value, onNewMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error checking messages: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private async Task ProcessMessageAsync(IMailFolder folder, UniqueId uid, Func<MimeMessage, Task> onNewMessage, CancellationToken cancellationToken)
        {
            if (_inFlightUid == uid)
            {
                _logger?.Invoke($"Skipping duplicate in-flight UID {uid}");
                return;
            }

            _inFlightUid = uid;

            try
            {
                _logger?.Invoke($"Processing message: UID {uid} (lastProcessed: {_lastProcessedUid})");
                var message = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
                _logger?.Invoke($"Callback for: {message.Subject}");
                await onNewMessage(message).ConfigureAwait(false);

                // Advance watermark before marking as seen:
                // if flagging fails, at least we won't replay the message.
                _lastProcessedUid = uid;
                await folder.AddFlagsAsync([uid], MessageFlags.Seen, silent: true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error processing UID {uid}: {ex.GetType().Name} - {ex.Message}");
            }
            finally
            {
                _inFlightUid = null;
            }
        }

        private async Task<UniqueId?> GetLatestUnreadUidAsync(IMailFolder folder, CancellationToken cancellationToken)
        {
            var unreadUids = await folder.SearchAsync(SearchQuery.NotSeen, cancellationToken).ConfigureAwait(false);

            if (unreadUids.Count == 0)
                return null;

            if (!_lastUidValidity.HasValue || folder.UidValidity == _lastUidValidity.Value)
            {
                _lastUidValidity = folder.UidValidity;
                return FindLatestUid(unreadUids);
            }

            // UIDVALIDITY changed - reset state and start fresh
            _logger?.Invoke($"UIDVALIDITY changed ({_lastUidValidity.Value} → {folder.UidValidity}) - resetting watermark");
            _lastProcessedUid = null;
            _lastUidValidity = folder.UidValidity;
            return FindLatestUid(unreadUids);
        }

        private UniqueId? FindLatestUid(IEnumerable<UniqueId> uids)
        {
            UniqueId? latest = null;
            uint maxId = _lastProcessedUid?.Id ?? 0;

            foreach (var uid in uids)
            {
                if (uid.Id > maxId)
                {
                    maxId = uid.Id;
                    latest = uid;
                }
            }

            if (latest.HasValue)
                _logger?.Invoke($"Found latest unread: UID {latest}");

            return latest;
        }

    }
}
