using System;
using BloomNative.Windows;

internal static class DesktopRecoveryChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        var recovery = new DesktopRecovery();
        TimeSpan now = TimeSpan.Zero;
        Check(!recovery.Requested && !recovery.ShouldRetry(now), "Wallpaper starts only on user request.");
        recovery.Request(true);
        foreach (int seconds in new[] { 3, 6, 12, 24, 30 })
        {
            recovery.Failed(now);
            Check(recovery.Requested, "Failure must preserve the user's enabled intent.");
            Check(recovery.RetryAt == now + TimeSpan.FromSeconds(seconds), "Recovery must back off between failures.");
            Check(!recovery.ShouldRetry(now + TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1)), "Recovery must not run before its deadline.");
            now += TimeSpan.FromSeconds(seconds);
            Check(recovery.ShouldRetry(now), "Recovery must run when its deadline arrives.");
            recovery.Started(now);
            recovery.Healthy(now + TimeSpan.FromSeconds(1));
            Check(recovery.FailureCount > 0 && recovery.RetryAt == null, "A brief successful attach must not reset repeated media failures.");
        }
        recovery.Failed(now);
        Check(recovery.Exhausted && !recovery.ShouldRetry(now + TimeSpan.FromDays(1)), "Repeated failures must stop automatic retries.");
        recovery.Request(false);
        Check(!recovery.Requested && !recovery.Exhausted && !recovery.ShouldRetry(now + TimeSpan.FromDays(1)), "User Stop must cancel exhausted recovery.");
        recovery.Request(true);
        recovery.Failed(now);
        recovery.Request(false);
        recovery.Failed(now);
        Check(!recovery.ShouldRetry(now + TimeSpan.FromDays(1)), "A late error must not re-enable stopped wallpaper.");
        recovery.Request(true);
        recovery.Failed(now);
        recovery.Started(now);
        recovery.Healthy(now + TimeSpan.FromSeconds(29));
        Check(recovery.FailureCount == 1, "Failure streak remains until sustained healthy operation.");
        recovery.Healthy(now + TimeSpan.FromSeconds(30));
        Check(recovery.FailureCount == 0, "Sustained healthy operation resets the failure streak.");
        recovery.Failed(now + TimeSpan.FromSeconds(31));
        Check(recovery.RetryAt == now + TimeSpan.FromSeconds(34), "A failure after stable operation starts with the first retry delay.");
        return checks;
    }
}
