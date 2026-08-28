using MailKit;
using MailKit.Search;
using MimeKit;

namespace p2poolmail
{
    /// <summary>Mail processing of <see cref="ImapClientService"/>: fetch unread, process new messages, mark as seen.</summary>
    public partial class ImapClientService
    {
        public async Task<IList<(UniqueId uid, MimeMessage message)>> FetchUnreadAsync(string? folderName = null, int maxMessages = 1, DateTimeOffset? since = null)
        {
            if (!_client.IsConnected)
                await ConnectAsync().ConfigureAwait(false);

            var folder = await ResolveAndOpenFolderAsync(folderName, FolderAccess.ReadOnly).ConfigureAwait(false);
            var uids = (await folder.SearchAsync(SearchQuery.NotSeen).ConfigureAwait(false))
                .OrderBy(x => x.Id)
                .Take(Math.Max(1, maxMessages))
                .ToList();

            var results = new List<(UniqueId uid, MimeMessage message)>();
            foreach (var uid in uids)
            {
                try
                {
                    results.Add((uid, await folder.GetMessageAsync(uid).ConfigureAwait(false)));
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"Failed to fetch message {uid}: {ex.Message}");
                }
            }

            return results;
        }

        public async Task MarkAsSeenAsync(string? folderName, IEnumerable<UniqueId> uids)
        {
            if (uids == null)
                return;

            if (!_client.IsConnected)
                await ConnectAsync().ConfigureAwait(false);

            var folder = await ResolveAndOpenFolderAsync(folderName, FolderAccess.ReadWrite).ConfigureAwait(false);
            var uidsList = uids as IList<UniqueId> ?? uids.ToList();
            await folder.AddFlagsAsync(uidsList, MessageFlags.Seen, true).ConfigureAwait(false);
        }

        private async Task CheckAndProcessNewMessageAsync(IMailFolder folder, bool isIdle, Func<MimeMessage, Task> onNewMessage, CancellationToken cancellationToken)
        {
            try
            {
                var candidateUids = await GetCandidateUidsAsync(folder, cancellationToken).ConfigureAwait(false);
                if (candidateUids.Count == 0)
                {
                    if (!_lastProcessedUid.HasValue)
                        _logger?.Invoke("No new messages (initialization mode)");
                    return;
                }

                foreach (var uid in candidateUids)
                {
                    _logger?.Invoke($"Processing new message: UID {uid} (lastProcessedUid: {_lastProcessedUid}, via {(isIdle ? "IDLE" : "POLL")})");

                    try
                    {
                        var message = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
                        _logger?.Invoke($"Calling onNewMessage callback for: {message.Subject}");
                        await onNewMessage(message).ConfigureAwait(false);
                        if (!await MarkMessageAsSeenAsync(folder, uid, cancellationToken).ConfigureAwait(false))
                            break;

                        _lastProcessedUid = uid;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Invoke($"Error processing UID {uid}: {ex.GetType().Name} - {ex.Message}");
                        break;
                    }
                }
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

        private async Task<List<UniqueId>> GetCandidateUidsAsync(IMailFolder folder, CancellationToken cancellationToken)
        {
            var uidsList = await folder.SearchAsync(SearchQuery.NotSeen, cancellationToken).ConfigureAwait(false);

            if (uidsList.Count > 0)
                _logger?.Invoke($"Found {uidsList.Count} unread messages");

            return uidsList
                .Where(u => !_lastProcessedUid.HasValue || u.Id > _lastProcessedUid.Value.Id)
                .OrderBy(u => u.Id)
                .Take(_candidateLimit)
                .ToList();
        }

        private async Task<bool> MarkMessageAsSeenAsync(IMailFolder folder, UniqueId uid, CancellationToken cancellationToken)
        {
            try
            {
                await folder.AddFlagsAsync([uid], MessageFlags.Seen, true, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Warning: failed to mark UID {uid} as seen: {ex.Message}");
                return false;
            }
        }
    }
}
