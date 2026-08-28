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

                        // Advance the watermark BEFORE flagging as seen: the callback
                        // already fired (a reply was enqueued), so a failed flag write
                        // must not re-run it next iteration - that would send a
                        // duplicate reply. The message simply stays unread on the
                        // server, which is harmless.
                        _lastProcessedUid = uid;
                        _stuckUid = null;
                        _stuckUidAttempts = 0;

                        await folder.AddFlagsAsync([uid], MessageFlags.Seen, true, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // A message that repeatedly cannot be fetched/replied/flagged (e.g.
                        // expunged mid-flight, malformed MIME) must not stall the queue
                        // forever: after MaxAttemptsPerUid strikes we advance the watermark
                        // past it (it stays unread on the server) so newer mail still flows.
                        if (uid == _stuckUid)
                            _stuckUidAttempts++;
                        else
                        {
                            _stuckUid = uid;
                            _stuckUidAttempts = 1;
                        }

                        if (_stuckUidAttempts >= MaxAttemptsPerUid)
                        {
                            _logger?.Invoke($"ERROR: UID {uid} failed {_stuckUidAttempts} times ({ex.GetType().Name}: {ex.Message}) - skipping to keep newer mail flowing");
                            _lastProcessedUid = uid;
                            _stuckUid = null;
                            _stuckUidAttempts = 0;
                            continue;
                        }

                        _logger?.Invoke($"Error processing UID {uid} (attempt {_stuckUidAttempts}/{MaxAttemptsPerUid}): {ex.GetType().Name} - {ex.Message}");
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
    }
}
