using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CornwallUtilities
{
    /// <summary>
    /// Global rate limiter for bot DMs: max 10 DMs per 3 minutes to avoid Discord spam flags.
    /// Shared by all DM commands so the limit applies across the bot.
    /// </summary>
    internal static class DmRateLimiter
    {
        private const int MaxDmsPerWindow = 10;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(3);

        private static readonly object Lock = new object();
        private static readonly List<DateTimeOffset> SentTimestamps = new List<DateTimeOffset>();

        /// <summary>
        /// Waits until a DM slot is available (under 10 in the last 3 minutes), then records the send.
        /// Call this before each DM; if the window is full, this will async wait until the oldest send exits the window.
        /// </summary>
        public static async Task WaitForSlotAsync()
        {
            while (true)
            {
                TimeSpan? waitFor = null;
                lock (Lock)
                {
                    var now = DateTimeOffset.UtcNow;
                    var cutoff = now - Window;
                    SentTimestamps.RemoveAll(t => t <= cutoff);

                    if (SentTimestamps.Count < MaxDmsPerWindow)
                    {
                        SentTimestamps.Add(now);
                        return;
                    }

                    var oldest = SentTimestamps[0];
                    waitFor = (oldest + Window) - now;
                    if (waitFor.Value < TimeSpan.Zero)
                        waitFor = TimeSpan.FromMilliseconds(100);
                }

                await Task.Delay(waitFor!.Value).ConfigureAwait(false);
            }
        }
    }
}
