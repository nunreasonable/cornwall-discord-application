using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CornwallUtilities
{
    /// <summary>
    /// Global rate limiter for bot DMs.
    /// Ensures there is at least a 3 second delay between consecutive DMs
    /// across the entire bot to avoid Discord spam flags.
    /// </summary>
    internal static class DmRateLimiter
    {
        private static readonly TimeSpan MinDelayBetweenDms = TimeSpan.FromSeconds(3);

        private static readonly object Lock = new object();
        private static DateTimeOffset? _lastSentAt;

        /// <summary>
        /// Waits until at least 3 seconds have passed since the last DM, then records the send time.
        /// Call this before each DM; if called too soon, this will async wait for the remaining time.
        /// </summary>
        public static async Task WaitForSlotAsync()
        {
            while (true)
            {
                TimeSpan? waitFor = null;
                lock (Lock)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (_lastSentAt is null)
                    {
                        _lastSentAt = now;
                        return;
                    }

                    var elapsed = now - _lastSentAt.Value;
                    if (elapsed >= MinDelayBetweenDms)
                    {
                        _lastSentAt = now;
                        return;
                    }

                    waitFor = MinDelayBetweenDms - elapsed;
                    if (waitFor.Value < TimeSpan.Zero)
                        waitFor = TimeSpan.FromMilliseconds(100);
                }

                await Task.Delay(waitFor!.Value).ConfigureAwait(false);
            }
        }
    }
}
