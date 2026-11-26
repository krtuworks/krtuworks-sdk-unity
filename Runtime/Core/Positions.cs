#nullable enable
using System;

namespace Krtuworks.Internal
{
    /// <summary>
    /// The rules of krtuworks-sdk-js/src/positions.ts. The game reports where the player is as often as it likes
    /// (every frame is fine); this turns those reports into one `position_sample` per interval, carrying the active
    /// seconds since the one before. Time the game did not report (it stopped sampling for a while, or the app was in
    /// the background) belongs to no position and is not told.
    /// </summary>
    internal sealed class PositionSampler
    {
        internal const int DefaultSeconds = 10;
        const int MaxSeconds = 600;

        readonly int everySeconds;
        double? since;

        internal PositionSampler(int everySeconds)
        {
            this.everySeconds = everySeconds;
        }

        /// <summary>The seconds a sample told now carries, or null when none is due; <paramref name="active"/> is the launch's active seconds.</summary>
        internal int? Due(double active)
        {
            if (!since.HasValue || active < since.Value)
            {
                since = active;
                return null;
            }
            var elapsed = active - since.Value;
            if (elapsed > everySeconds * 3)
            {
                since = active;
                return null;
            }
            if (elapsed < everySeconds) return null;
            since = active;
            return (int)Math.Min(MaxSeconds, Math.Max(1, Math.Round(elapsed, MidpointRounding.AwayFromZero)));
        }

        /// <summary>Starts over: the next report only starts the clock (another player, a new launch).</summary>
        internal void Reset() => since = null;

        /// <summary>The interval the game asked for, checked: whole seconds, 1-600; 10 when not given.</summary>
        internal static int Interval(int? value)
        {
            if (!value.HasValue) return DefaultSeconds;
            if (value.Value < 1 || value.Value > MaxSeconds) throw new ArgumentException("PositionSampleSeconds is a whole number of seconds, 1-600");
            return value.Value;
        }
    }
}
