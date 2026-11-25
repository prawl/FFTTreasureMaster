using System;

namespace FFTTreasureMaster;

/// <summary>Poll loop behind the Rare treasure only grant.</summary>
internal static class ReadinessWait
{
    /// <summary>True as soon as a probe succeeds, false once the slept time reaches the cap.
    /// Callers log their own give-up line.</summary>
    internal static bool Until(Func<bool> isReady, Action<int> sleep, int intervalMs, int capMs)
    {
        int elapsed = 0;
        while (!isReady())
        {
            if (elapsed >= capMs) return false;
            sleep(intervalMs);
            elapsed += intervalMs;
        }
        return true;
    }
}
