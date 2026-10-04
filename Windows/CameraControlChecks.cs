using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BloomNative.Windows;

internal static class CameraControlChecks
{
    internal sealed record Result(bool Passed, int Assertions, string Scope);
    internal static Result Run() => CameraTrackingWindow.RunControlChecks();
}

internal sealed partial class CameraTrackingWindow
{
    /// <summary>
    /// Uses the real controls and brightness presenter without showing the
    /// window, enumerating cameras, starting capture or playing actual sounds.
    /// Synthetic frames enter the production tracker, then its UI presenter.
    /// </summary>
    internal static CameraControlChecks.Result RunControlChecks()
    {
        if (Application.Current is null)
            throw new InvalidOperationException("Camera controls require a WPF application.");
        Application.Current.Dispatcher.VerifyAccess();
        int assertions = 0, cues = 0;
        var progress = new List<double>();
        var tracking = new List<bool>();
        var window = new CameraTrackingWindow(false, () => cues++);
        window.ProgressChanged += progress.Add;
        window.TrackingChanged += tracking.Add;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Camera control fixture: " + message);
            assertions++;
        }
        static void Complete(Task task)
        {
            // With no capture session or worker operation, these production
            // methods must complete without blocking or pumping the dispatcher.
            if (!task.IsCompleted)
                throw new InvalidOperationException("The camera-free control fixture unexpectedly started asynchronous work.");
            task.GetAwaiter().GetResult();
        }
        TimeSpan timestamp = TimeSpan.Zero;
        void Feed(byte level, int frames)
        {
            byte[] gray = new byte[64];
            Array.Fill(gray, level);
            for (int frame = 0; frame < frames; frame++)
            {
                timestamp += TimeSpan.FromMilliseconds(100);
                CameraBrightnessStage previous = window.brightness.LastResult.Stage;
                CameraBrightnessResult result = window.brightness.Process(gray, 8, 8, timestamp);
                window.ShowBrightnessResult(result, previous);
            }
        }

        try
        {
            Check(window.BrightnessMode && window.trackingMode.SelectedIndex == 0,
                "the real camera window defaults to hands-free brightness mode");
            Check(!window.IsCameraRunning && !window.enumerating && !window.IsLoaded && !window.frameWatchdog.IsEnabled,
                "constructing controls neither shows the window nor enumerates or activates a camera");
            Check(window.calibrateBrightness.Visibility == Visibility.Visible && window.brightnessInstructions.Visibility == Visibility.Visible &&
                window.brightnessReading.Visibility == Visibility.Visible,
                "brightness instructions, reading and calibration button are visible by default");
            Check(window.motionCalibrationRow.Visibility == Visibility.Collapsed && window.motionInstructions.Visibility == Visibility.Collapsed &&
                window.motionSteps.Visibility == Visibility.Collapsed && !window.overlay.IsHitTestVisible,
                "brightness mode hides motion steps and does not intercept preview input for target selection");
            Check(!window.calibrateBrightness.IsEnabled && !window.stop.IsEnabled && !window.reset.IsEnabled &&
                window.brightnessReading.Text.Contains("camera off", StringComparison.Ordinal),
                "camera-off controls cannot calibrate, reset or stop an absent camera session");
            window.StartBrightnessCalibration();
            Check(window.brightness.LastResult.Stage == CameraBrightnessStage.Idle && cues == 0 && progress.Count == 0,
                "the actual calibration command refuses to start without camera frames");

            window.trackingMode.SelectedIndex = 1;
            Check(!window.BrightnessMode && window.motionCalibrationRow.Visibility == Visibility.Visible &&
                window.motionInstructions.Visibility == Visibility.Visible && window.overlay.IsHitTestVisible &&
                window.calibrateBrightness.Visibility == Visibility.Collapsed,
                "selecting motion mode exposes its controls and target selection instead of brightness calibration");
            window.trackingMode.SelectedIndex = 0;
            Check(window.BrightnessMode && !window.overlay.IsHitTestVisible && window.motionCalibrationRow.Visibility == Visibility.Collapsed,
                "returning to brightness restores its intended input and control visibility");

            // Start at the tracker boundary to keep all device access disabled.
            // Every subsequent result is produced by the real frame algorithm.
            window.brightness.StartCalibration(timestamp);
            Feed(204, 34);
            Check(cues == 0 && progress.Count == 0 && tracking.Count == 0 && window.BrightnessCalibrating,
                "settling frames neither chime nor take animation control prematurely");
            Check(window.calibrateBrightness.Content?.ToString() == "Calibrating…" &&
                window.status.Text.Contains("first chime", StringComparison.Ordinal),
                "the UI displays ongoing calibration and explains when to lower the lid");
            Feed(204, 1);
            Check(window.brightness.LastResult.Stage == CameraBrightnessStage.WaitingForDark && cues == 1 && progress.Count == 0,
                "the open endpoint produces one first cue without emitting an uncalibrated pose");
            Feed(51, 29);
            Check(cues == 1 && progress.Count == 0 && window.status.Text.Contains("Lower the lid", StringComparison.Ordinal),
                "the lowering interval keeps its instruction without repeated first cues");
            Feed(51, 15);
            Check(window.brightness.LastResult.Stage == CameraBrightnessStage.FoldedSettling && cues == 1 && progress.Count == 0,
                "an incomplete dark hold does not emit the completion cue or a pose");
            Feed(51, 1);
            Check(window.brightness.LastResult.Stage == CameraBrightnessStage.Tracking && cues == 2 &&
                progress.Count == 1 && progress[0] == 0 && window.calibrated && tracking.SequenceEqual(new[] { true }),
                "the second endpoint cues completion once, takes animation control and emits folded position");
            Check(window.estimate.Text.Contains(0d.ToString("P0"), StringComparison.Ordinal) &&
                window.status.Text.Contains("Calibration complete", StringComparison.Ordinal),
                "successful calibration is reflected in the real estimate and status controls");
            Feed(204, 30);
            Check(cues == 2 && tracking.Count == 1 && progress[^1] == 1 &&
                progress.All(value => double.IsFinite(value) && value >= 0 && value <= 1),
                "tracking publishes bounded unfolding changes without replaying calibration cues or control events");

            Complete(window.ResetCalibrationAsync());
            Check(window.brightness.LastResult.Stage == CameraBrightnessStage.Idle && !window.calibrated && !window.trackingActive &&
                tracking.SequenceEqual(new[] { true, false }),
                "the real reset clears calibration and releases animation control exactly once");
            int progressAfterReset = progress.Count;
            Feed(51, 10);
            Check(cues == 2 && progress.Count == progressAfterReset && !window.trackingActive,
                "frames after reset cannot silently reacquire calibration, emit poses or chime");
            window.brightness.StartCalibration(timestamp);
            Feed(30, 35);
            Check(window.brightness.LastResult.Stage == CameraBrightnessStage.Failed && cues == 2 &&
                progress.Count == progressAfterReset && !window.trackingActive &&
                window.status.Text.Contains("too dark", StringComparison.Ordinal),
                "a rejected dark calibration displays its reason without cues or animation control");

            Complete(window.ResetCalibrationAsync());
            window.brightness.StartCalibration(timestamp);
            Feed(204, 35);
            Feed(51, 45);
            Check(cues == 4 && window.trackingActive && tracking.SequenceEqual(new[] { true, false, true }),
                "an explicit fresh calibration can cue both endpoints and regain control after reset");

            // Seed only in-memory preview/pending buffers; no camera is opened.
            var lateFrame = new CameraFrame(1, timestamp + TimeSpan.FromMilliseconds(100), 8, 8, new byte[64], new byte[256]);
            window.displayedFrame = window.pendingFrame = lateFrame;
            window.bitmap = new WriteableBitmap(8, 8, 96, 96, PixelFormats.Bgra32, null);
            window.preview.Source = window.bitmap;
            int progressBeforeStop = progress.Count;
            Complete(window.StopAsync());
            Check(!window.IsCameraRunning && !window.trackingActive && !window.calibrated &&
                window.brightness.LastResult.Stage == CameraBrightnessStage.Idle && tracking.SequenceEqual(new[] { true, false, true, false }),
                "the real stop clears calibration and releases animation control");
            Check(window.pendingFrame is null && window.displayedFrame is null && window.bitmap is null && window.preview.Source is null &&
                !window.frameWatchdog.IsEnabled,
                "stop clears the actual preview, pending buffers and frame watchdog");
            window.pendingFrame = lateFrame;
            Complete(window.ProcessPendingFrameAsync());
            Check(window.pendingFrame is null && window.displayedFrame is null && window.preview.Source is null &&
                cues == 4 && progress.Count == progressBeforeStop && !window.trackingActive,
                "a queued late frame after stop cannot restore preview, progress, tracking or cues");
            Complete(window.StopAsync());
            Check(tracking.Count == 4 && cues == 4 && progress.Count == progressBeforeStop && !window.enumerating,
                "repeated stop is idempotent and never starts camera discovery");

            return new CameraControlChecks.Result(true, assertions,
                "Unshown production WPF controls: brightness defaults/mode visibility, synthetic frame algorithm plus UI presenter, injected cue counts, progress/control events, reset/stop and stopped-frame rejection. No camera enumeration, activation, active-capture delivery, real sounds, physical lid or hardware validation.");
        }
        finally
        {
            window.Close();
        }
    }
}
