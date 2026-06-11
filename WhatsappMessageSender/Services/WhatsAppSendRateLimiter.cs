namespace WhatsappMessageSender.Services;

/// <summary>
/// Sliding-window limit: reserves at most <see cref="MaxSendsPerMinute"/> throttled send slots per UTC minute
/// for messages whose dispatch priority is greater than or equal to <see cref="HighPriorityLessThan"/>.
/// Priorities strictly less than that value bypass the cap (immediate).
/// </summary>
public sealed class WhatsAppSendRateLimiter : IWhatsAppSendRateLimiter
{
    private readonly object _gate = new();
    private readonly Queue<DateTime> _reservedSlotUtc = new();
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
                var now = DateTime.UtcNow;
                PruneOlderThanOneMinute(now);
                if (_reservedSlotUtc.Count < _maxPerMinute)
                {
                    _reservedSlotUtc.Enqueue(now);
                    return;
                }

                var oldest = _reservedSlotUtc.Peek();
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
        // WaitForSendSlotAsync reserves the throttled slot before the Selenium send
        // semaphore is entered. Keeping this method as a no-op preserves the public
        // processor flow while preventing concurrent waiters from oversubscribing
        // the configured per-minute cap.
    }

    private void PruneOlderThanOneMinute(DateTime utcNow)
    {
        var cutoff = utcNow.AddMinutes(-1);
        while (_reservedSlotUtc.Count > 0 && _reservedSlotUtc.Peek() < cutoff)
        {
            _reservedSlotUtc.Dequeue();
        }
    }
}
