namespace WhatsappMessageSender.Services;

/// <summary>
/// Sliding-window limit: at most <see cref="MaxSendsPerMinute"/> successful sends per UTC minute
/// for messages whose dispatch priority is greater than or equal to <see cref="HighPriorityLessThan"/>.
/// Priorities strictly less than that value bypass the cap (immediate).
/// </summary>
public sealed class WhatsAppSendRateLimiter : IWhatsAppSendRateLimiter
{
    private readonly object _gate = new();
    private readonly Queue<DateTime> _successUtc = new();
    private readonly int _highPriorityLessThan;
    private readonly int _maxPerMinute;
    private readonly bool _enabled;

    public WhatsAppSendRateLimiter(int highPriorityLessThan, int maxSendsPerMinute, bool enabled = true)
    {
        _highPriorityLessThan = Math.Max(0, highPriorityLessThan);
        _maxPerMinute = Math.Max(1, maxSendsPerMinute);
        _enabled = enabled;
    }

    public Task WaitForSendSlotAsync(int dispatchPriority, CancellationToken cancellationToken = default)
    {
        if (!_enabled || dispatchPriority < _highPriorityLessThan)
        {
            return Task.CompletedTask;
        }

        return WaitThrottledAsync(cancellationToken);
    }

    private async Task WaitThrottledAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan wait;
            lock (_gate)
            {
                PruneOlderThanOneMinute(DateTime.UtcNow);
                if (_successUtc.Count < _maxPerMinute)
                {
                    return;
                }

                var oldest = _successUtc.Peek();
                wait = oldest.AddMinutes(1) - DateTime.UtcNow;
                if (wait < TimeSpan.FromMilliseconds(50))
                {
                    wait = TimeSpan.FromMilliseconds(50);
                }
            }

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    public void NotifySuccessfulSendIfThrottled(int dispatchPriority)
    {
        if (!_enabled || dispatchPriority < _highPriorityLessThan)
        {
            return;
        }

        lock (_gate)
        {
            PruneOlderThanOneMinute(DateTime.UtcNow);
            _successUtc.Enqueue(DateTime.UtcNow);
        }
    }

    private void PruneOlderThanOneMinute(DateTime utcNow)
    {
        var cutoff = utcNow.AddMinutes(-1);
        while (_successUtc.Count > 0 && _successUtc.Peek() < cutoff)
        {
            _successUtc.Dequeue();
        }
    }
}
