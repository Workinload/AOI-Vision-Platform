using AOI.Contracts;

namespace AOI.Runtime.Agent01;

/// <summary>
/// Connection-local backoff for the durable outbox. It NEVER deletes or acknowledges
/// a message: only ProductionStore may remove it from pending after a valid persisted ACK.
/// On reconnect the planner can be reconstructed from the durable outbox.
/// </summary>
public sealed class OutboxRetryPlanner
{
    private sealed record Retry(int Attempts, DateTimeOffset NextEligibleUtc);
    private readonly Dictionary<Guid, Retry> _retries = new();
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;

    public OutboxRetryPlanner(TimeSpan? initial = null, TimeSpan? maximum = null)
    {
        _initial = initial ?? TimeSpan.FromMilliseconds(500);
        _max = maximum ?? TimeSpan.FromSeconds(15);
        if (_initial <= TimeSpan.Zero || _max < _initial)
            throw new ArgumentOutOfRangeException(nameof(initial), "Require 0 < initial <= maximum.");
    }

    public IReadOnlyList<PeerEnvelope> SelectDue(
        IReadOnlyCollection<PeerEnvelope> pending, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(pending);
        lock (_retries)
        {
            var liveIds = pending.Select(x => x.MessageId).ToHashSet();
            foreach (var abandoned in _retries.Keys.Where(id => !liveIds.Contains(id)).ToArray())
                _retries.Remove(abandoned);

            var due = new List<PeerEnvelope>();
            foreach (var envelope in pending)
            {
                if (!_retries.TryGetValue(envelope.MessageId, out var prior))
                    prior = new Retry(0, DateTimeOffset.MinValue);
                if (nowUtc < prior.NextEligibleUtc) continue;

                due.Add(envelope);
                var attempts = prior.Attempts + 1;
                var exponent = Math.Min(attempts - 1, 20);
                var millis = Math.Min(_max.TotalMilliseconds,
                    _initial.TotalMilliseconds * Math.Pow(2, exponent));
                _retries[envelope.MessageId] =
                    new Retry(attempts, nowUtc.AddMilliseconds(millis));
            }
            return due;
        }
    }

    public int AttemptsFor(Guid messageId)
    {
        lock (_retries)
            return _retries.TryGetValue(messageId, out var value) ? value.Attempts : 0;
    }
}
