using System;
using BloomNative.Windows;

internal static class CameraBrightnessChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Brightness check: " + message);
            checks++;
        }
        var tracker = new CameraBrightnessTracker();
        byte[] mixed = new byte[64];
        Array.Fill(mixed, (byte)255, 32, 32);
        var result = tracker.Process(mixed, 8, 8, TimeSpan.Zero);
        Check(result.Stage == CameraBrightnessStage.Idle && result.Progress is null && result.Brightness == 0.5,
            "Idle reports measured brightness without controlling the animation.");
        tracker.StartCalibration(TimeSpan.Zero);
        TimeSpan time = TimeSpan.Zero;
        Feed(tracker, 204, ref time, 19);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.OpenSettling, "Initial camera settling cannot capture an endpoint.");
        Feed(tracker, 204, ref time, 15);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.OpenSettling, "Open endpoint needs the full stable observation window.");
        Feed(tracker, 204, ref time, 1);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.WaitingForDark && tracker.LastResult.Progress is null,
            "Stable open endpoint advances hands-free without emitting uncalibrated progress.");
        Feed(tracker, 51, ref time, 29);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.WaitingForDark, "User gets at least three seconds to lower the lid.");
        Feed(tracker, 51, ref time, 1);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.FoldedSettling, "Darkness starts a new stable window rather than capturing immediately.");
        Feed(tracker, 51, ref time, 14);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.FoldedSettling, "A brief dark view cannot complete calibration.");
        Feed(tracker, 51, ref time, 1);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.Tracking && tracker.LastResult.Progress == 0,
            "Steady darker endpoint completes hands-free calibration at folded progress.");

        Feed(tracker, 204, ref time, 1);
        Check(tracker.LastResult.Progress is > 0 and < 0.8, "Tracking smooths a sudden brightness jump instead of snapping.");
        Feed(tracker, 204, ref time, 29);
        Check(tracker.LastResult.Progress == 1, "A held open endpoint settles to exactly 100%.");
        Feed(tracker, 127, ref time, 30);
        double middle = tracker.LastResult.Progress!.Value;
        Check(Math.Abs(middle - (127.0 - 51) / (204 - 51)) < 0.015, "Intermediate brightness maps between the calibrated endpoints.");
        for (int frame = 0; frame < 50; frame++) Feed(tracker, (byte)(frame % 2 == 0 ? 127 : 128), ref time, 1);
        Check(tracker.LastResult.Progress == middle, "Small camera brightness noise stays within the progress deadband.");
        Feed(tracker, 255, ref time, 30);
        Check(tracker.LastResult.Progress == 1, "Brightness above the open endpoint clamps without overshoot.");
        Feed(tracker, 0, ref time, 30);
        Check(tracker.LastResult.Progress == 0, "Brightness below the folded endpoint clamps without negative progress.");

        foreach (bool relativeFailure in new[] { false, true })
        {
            tracker.Reset(); time = TimeSpan.Zero; tracker.StartCalibration(time);
            Feed(tracker, relativeFailure ? (byte)230 : (byte)85, ref time, 35);
            Feed(tracker, relativeFailure ? (byte)200 : (byte)60, ref time, 415);
            Check(tracker.LastResult.Stage == CameraBrightnessStage.Failed && tracker.LastResult.Progress is null,
                relativeFailure ? "A small relative brightness drop cannot calibrate even when the absolute drop is sufficient."
                    : "A small absolute brightness drop cannot calibrate even if it lasts until timeout.");
        }
        tracker.Reset(); time = TimeSpan.Zero; tracker.StartCalibration(time);
        Feed(tracker, 30, ref time, 35);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.Failed && tracker.LastResult.Message.Contains("too dark", StringComparison.Ordinal),
            "An overly dark open view reports why calibration cannot work.");

        tracker.Reset(); time = TimeSpan.Zero; tracker.StartCalibration(time);
        for (int frame = 0; frame < 450; frame++) Feed(tracker, (byte)(frame % 2 == 0 ? 80 : 180), ref time, 1);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.Failed && tracker.LastResult.Progress is null,
            "Persistent open-view noise reaches the bounded timeout without a false baseline.");
        tracker.Reset(); time = TimeSpan.Zero; tracker.StartCalibration(time);
        Feed(tracker, 204, ref time, 35);
        for (int frame = 0; frame < 415; frame++) Feed(tracker, (byte)(frame % 2 == 0 ? 20 : 100), ref time, 1);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.Failed && tracker.LastResult.Progress is null,
            "Persistently unstable dark frames time out instead of completing calibration.");

        tracker.Reset(); time = TimeSpan.Zero; tracker.StartCalibration(time);
        Feed(tracker, 204, ref time, 35);
        Feed(tracker, 51, ref time, 35);
        Feed(tracker, 204, ref time, 1);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.WaitingForDark, "Returning to brightness cancels a partial folded capture.");
        Feed(tracker, 51, ref time, 15);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.FoldedSettling, "A new dark hold cannot reuse stale samples from before interruption.");
        Feed(tracker, 51, ref time, 1);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.Tracking, "A fresh full dark hold can complete after an interrupted attempt.");

        foreach (string fault in new[] { "duplicate", "backward", "gap", "shape", "buffer" })
        {
            tracker = Calibrated(out time);
            Feed(tracker, 140, ref time, 30);
            double held = tracker.LastResult.Progress!.Value;
            if (fault == "duplicate") result = tracker.Process(Frame(140), 8, 8, time);
            else if (fault == "backward") result = tracker.Process(Frame(140), 8, 8, time - TimeSpan.FromMilliseconds(1));
            else if (fault == "gap") result = tracker.Process(Frame(140), 8, 8, time + TimeSpan.FromSeconds(2));
            else if (fault == "shape") result = tracker.Process(Frame(140), 16, 4, time + TimeSpan.FromMilliseconds(100));
            else result = tracker.Process(new byte[3], 8, 8, time + TimeSpan.FromMilliseconds(100));
            Check(result.Stage == CameraBrightnessStage.Failed && result.Progress == held, fault + " failure freezes the last valid output.");
            time += TimeSpan.FromSeconds(3);
            Feed(tracker, 204, ref time, 20);
            Check(tracker.LastResult.Stage == CameraBrightnessStage.Failed && tracker.LastResult.Progress == held,
                fault + " failure cannot silently reacquire when frames return.");
        }
        tracker.Reset(); time = TimeSpan.Zero; tracker.StartCalibration(time);
        // Wall-clock duration alone is insufficient when only two fresh frames
        // cover the stable period; the capture pipeline normally delivers 10 fps.
        for (int second = 1; second <= 10; second++)
            tracker.Process(Frame(204), 8, 8, TimeSpan.FromSeconds(second));
        Check(tracker.LastResult.Stage == CameraBrightnessStage.OpenSettling, "Sparse frames cannot masquerade as a stable sample window.");
        tracker.Process(Frame(204), 8, 8, TimeSpan.FromSeconds(12));
        Check(tracker.LastResult.Stage == CameraBrightnessStage.Failed, "Frame gaps also invalidate calibration before tracking begins.");
        tracker.StartCalibration(TimeSpan.FromSeconds(12));
        time = TimeSpan.FromSeconds(12);
        Feed(tracker, 204, ref time, 35);
        Check(tracker.LastResult.Stage == CameraBrightnessStage.WaitingForDark && tracker.LastResult.Progress is null,
            "Explicit recalibration clears latched failure and establishes a fresh baseline.");
        tracker.Reset();
        Check(tracker.LastResult.Stage == CameraBrightnessStage.Idle && tracker.LastResult.Progress is null,
            "Reset clears calibration and relinquishes animation control.");
        tracker.StartCalibration(TimeSpan.FromSeconds(-1));
        Check(tracker.LastResult.Stage == CameraBrightnessStage.Failed && tracker.LastResult.Progress is null,
            "Negative calibration timestamps are rejected without invalid numeric output.");
        return checks;
    }

    private static CameraBrightnessTracker Calibrated(out TimeSpan time)
    {
        var tracker = new CameraBrightnessTracker();
        time = TimeSpan.Zero;
        tracker.StartCalibration(time);
        Feed(tracker, 204, ref time, 35);
        Feed(tracker, 51, ref time, 45);
        if (tracker.LastResult.Stage != CameraBrightnessStage.Tracking)
            throw new InvalidOperationException("Could not establish the synthetic brightness calibration.");
        return tracker;
    }

    private static void Feed(CameraBrightnessTracker tracker, byte brightness, ref TimeSpan time, int frames)
    {
        byte[] gray = Frame(brightness);
        for (int frame = 0; frame < frames; frame++)
        {
            time += TimeSpan.FromMilliseconds(100);
            var result = tracker.Process(gray, 8, 8, time);
            if (!double.IsFinite(result.Brightness) || result.Brightness < 0 || result.Brightness > 1 ||
                result.Progress is double progress && (!double.IsFinite(progress) || progress < 0 || progress > 1))
                throw new InvalidOperationException("Brightness processing emitted an invalid numeric result.");
        }
    }

    private static byte[] Frame(byte brightness)
    {
        var gray = new byte[64];
        Array.Fill(gray, brightness);
        return gray;
    }
}
