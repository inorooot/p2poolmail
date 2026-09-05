using MailKit;
using MailKit.Search;
using MimeKit;

namespace p2poolmail
{
    /// <summary>Mail processing of <see cref="ImapClientService"/>: process new messages, mark as seen.</summary>
    public partial class ImapClientService
    {
        /// <summary>
        /// Processes only the newest unread message above the watermark; older
        /// unread messages are skipped on purpose (single-reply semantics).
        /// Uses a server-side search so a later seen message cannot hide the
        /// newest unread one.
        /// </summary>
        private async Task CheckAndProcessNewMessageAsync(IMailFolder folder, Func<MimeMessage, Task> onNewMessage, CancellationToken cancellationToken)
        {
            try
            {
                var unread = await GetUnreadUidsAboveWatermarkAsync(folder, cancellationToken).ConfigureAwait(false);

                if (unread.Count == 0)
                {
                    if (!_lastProcessedUid.HasValue)
                        _logger?.Invoke("No unread messages (initialization)");
                    return;
                }

                // Only the newest unread message is processed (single-reply semantics).
                // MaxBy is safe here: unread.Count > 0 was checked above.
                var latest = unread.MaxBy(u => u.Id);
                await ProcessMessageAsync(folder, latest, onNewMessage, cancellationToken).ConfigureAwait(false);
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
            try
            {
                var message = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
                _logger?.Invoke($"Processing: UID {uid}, Subject: {message.Subject}");
                await onNewMessage(message).ConfigureAwait(false);

                // Mark as seen BEFORE advancing the watermark: if marking fails, the
                // watermark stays put and the message is retried on a later wake
                // (at worst a duplicate reply) instead of being lost.
                await folder.AddFlagsAsync([uid], MessageFlags.Seen, silent: true, cancellationToken).ConfigureAwait(false);
                AdvanceWatermark(uid);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error processing UID {uid}: {ex.GetType().Name} - {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the UIDs of all unread messages newer than the watermark.
        /// UIDs are only valid within one UIDVALIDITY epoch; that reset is handled
        /// by <c>CheckAndResetUidValidity</c> before this runs.
        /// </summary>
        private async Task<IReadOnlyList<UniqueId>> GetUnreadUidsAboveWatermarkAsync(IMailFolder folder, CancellationToken cancellationToken)
        {
            if (folder.Count == 0)
                return Array.Empty<UniqueId>();

            var unread = await folder.SearchAsync(SearchQuery.NotSeen, cancellationToken).ConfigureAwait(false);
            if (unread.Count == 0)
                return Array.Empty<UniqueId>();

            if (!_lastProcessedUid.HasValue)
                return [.. unread];

            var aboveWatermark = new List<UniqueId>();
            foreach (var uid in unread)
            {
                if (uid.Id > _lastProcessedUid.Value.Id)
                    aboveWatermark.Add(uid);
            }
            return aboveWatermark;
        }

        /// <summary>
        /// Advances the watermark to the given UID.
        /// Only advances forward, never backward.
        /// </summary>
        private void AdvanceWatermark(UniqueId uid)
        {
            if (!_lastProcessedUid.HasValue || uid.Id > _lastProcessedUid.Value.Id)
            {
                _lastProcessedUid = uid;
            }
        }
    }
}
