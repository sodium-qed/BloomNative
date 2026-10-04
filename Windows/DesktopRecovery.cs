using System;

namespace BloomNative.Windows;

/// <summary>Preserves user intent while bounding automatic Explorer/media recovery.</summary>
internal sealed class DesktopRecovery
{
    internal const int MaximumFailures = 6;
    private TimeSpan? stableSince;
    internal bool Requested { get; private set; }
    internal int FailureCount { get; private set; }
    internal TimeSpan? RetryAt { get; private set; }
    internal bool Exhausted => Requested && FailureCount >= MaximumFailures && stableSince is null;

    internal void Request(bool value)
    {
        Requested = value;
        FailureCount = 0;
        RetryAt = stableSince = null;
    }

    internal void Started(TimeSpan now)
    {
        RetryAt = null;
        stableSince = Requested ? now : null;
    }

    internal void Failed(TimeSpan now)
    {
        stableSince = null;
        if (!Requested) return;
        FailureCount = Math.Min(MaximumFailures, FailureCount + 1);
        RetryAt = FailureCount < MaximumFailures
            ? now + TimeSpan.FromSeconds(Math.Min(30, 3 * (1 << (FailureCount - 1))))
            : null;
    }

    internal bool ShouldRetry(TimeSpan now) => Requested && RetryAt is TimeSpan due && now >= due;

    internal void Healthy(TimeSpan now)
    {
        // A successful attach alone says nothing about subsequent media failures.
        // Reset the streak only after the surface remains healthy for a while.
        if (Requested && stableSince is TimeSpan start && now - start >= TimeSpan.FromSeconds(30))
            FailureCount = 0;
    }
}
