using System;
using System.Collections.Generic;

namespace BloomNative.Windows;

internal enum CameraBrightnessStage { Idle, OpenSettling, WaitingForDark, FoldedSettling, Tracking, Failed }

internal readonly record struct CameraBrightnessResult(CameraBrightnessStage Stage, double Brightness, double? Progress, string Message);

/// <summary>
/// Maps image brightness between two explicitly calibrated lid positions. This is
/// a light-based animation control, not a hinge-angle measurement. All samples
/// and calibration stay in memory, and callers must serialize access.
/// </summary>
internal sealed class CameraBrightnessTracker
{
    internal static readonly TimeSpan OpenSettlingTime = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan StabilityTime = TimeSpan.FromSeconds(1.5);
    internal static readonly TimeSpan LoweringTime = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan CalibrationTimeout = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan MaximumFrameGap = TimeSpan.FromSeconds(1);
    internal const double MinimumOpenBrightness = 0.18;
    internal const double MinimumAbsoluteDrop = 0.10;
    internal const double MinimumRelativeDrop = 0.25;
    internal const double MaximumStableRange = 0.03;
    private const int MinimumStableSamples = 8;
    private const double SmoothingSeconds = 0.25;
    private const double ProgressDeadband = 0.01;
    private readonly List<(TimeSpan Time, double Brightness)> stability = new();
    private TimeSpan calibrationStarted, openCaptured;
    private TimeSpan? previousTimestamp;
    private int frameWidth, frameHeight;
    private double openBrightness, foldedBrightness, smoothedProgress, emittedProgress;

    internal CameraBrightnessResult LastResult { get; private set; } = IdleResult;
    private static CameraBrightnessResult IdleResult => new(CameraBrightnessStage.Idle, 0, null,
        "Start hands-free calibration with the lid at your normal open position.");

    internal void Reset()
    {
        stability.Clear();
        previousTimestamp = null;
        frameWidth = frameHeight = 0;
        calibrationStarted = openCaptured = TimeSpan.Zero;
        openBrightness = foldedBrightness = smoothedProgress = emittedProgress = 0;
        LastResult = IdleResult;
    }

    internal void StartCalibration(TimeSpan timestamp)
    {
        double brightness = LastResult.Brightness;
        Reset();
        LastResult = LastResult with { Brightness = brightness };
        if (timestamp < TimeSpan.Zero)
        {
            Fail("The camera timestamp is invalid. Restart the camera and try again.");
            return;
        }
        calibrationStarted = timestamp;
        LastResult = new(CameraBrightnessStage.OpenSettling, brightness, null,
            "Keep the lid at its normal open position and hold still while the camera settles.");
    }

    internal CameraBrightnessResult Process(byte[] gray, int width, int height, TimeSpan timestamp)
    {
        if (gray is null || width < 1 || height < 1 || width > 640 || height > 480 || (long)width * height != gray.Length)
            return Fail("The camera frame is invalid. Restart the camera and calibrate again.");
        long sum = 0;
        foreach (byte value in gray) sum += value;
        double brightness = sum / (255.0 * gray.Length);
        // A failed calibration never silently recovers. Continue displaying live
        // brightness so the user can improve the lighting before explicitly retrying.
        if (LastResult.Stage == CameraBrightnessStage.Failed)
            return LastResult = LastResult with { Brightness = brightness };
        if (timestamp < TimeSpan.Zero ||
            (previousTimestamp is TimeSpan previous && (timestamp <= previous || timestamp - previous > MaximumFrameGap)))
            return Fail("Camera frames stopped or arrived out of order. Restart calibration to resume.");
        if (frameWidth != 0 && (width != frameWidth || height != frameHeight))
            return Fail("The camera image size changed. Restart calibration to resume.");
        bool calibrating = LastResult.Stage is CameraBrightnessStage.OpenSettling or CameraBrightnessStage.WaitingForDark or CameraBrightnessStage.FoldedSettling;
        if (calibrating && timestamp < calibrationStarted)
            return Fail("The camera clock changed. Restart calibration to resume.");
        double elapsed = previousTimestamp is TimeSpan last ? (timestamp - last).TotalSeconds : 0;
        previousTimestamp = timestamp;
        frameWidth = width;
        frameHeight = height;
        LastResult = LastResult with { Brightness = brightness };
        if (calibrating && timestamp - calibrationStarted >= CalibrationTimeout)
            return Fail(LastResult.Stage == CameraBrightnessStage.OpenSettling
                ? "The open view did not become steady. Keep the lighting steady and try calibration again."
                : "No steady, clearly darker folded view was found. Keep the camera uncovered and try again in steadier lighting.");

        switch (LastResult.Stage)
        {
            case CameraBrightnessStage.Idle:
                return LastResult;
            case CameraBrightnessStage.OpenSettling:
                if (timestamp - calibrationStarted < OpenSettlingTime) return LastResult;
                if (!TryStableBrightness(timestamp, brightness, out double open)) return LastResult;
                if (open < MinimumOpenBrightness)
                    return Fail("The open view is too dark to distinguish the lid positions. Add light and try calibration again.");
                openBrightness = open;
                openCaptured = timestamp;
                stability.Clear();
                return LastResult = LastResult with
                {
                    Stage = CameraBrightnessStage.WaitingForDark,
                    Message = "Open position captured. Lower the lid to a comfortable folded position, keep the camera uncovered, and hold still."
                };
            case CameraBrightnessStage.WaitingForDark:
            case CameraBrightnessStage.FoldedSettling:
                if (timestamp - openCaptured < LoweringTime) return LastResult;
                double requiredDrop = Math.Max(MinimumAbsoluteDrop, openBrightness * MinimumRelativeDrop);
                if (openBrightness - brightness < requiredDrop)
                {
                    stability.Clear();
                    return LastResult = LastResult with
                    {
                        Stage = CameraBrightnessStage.WaitingForDark,
                        Message = "Waiting for a clearly darker, steady view. Lower the lid gently and keep the camera uncovered."
                    };
                }
                LastResult = LastResult with { Stage = CameraBrightnessStage.FoldedSettling, Message = "Darker view detected. Hold still while the folded position is captured." };
                if (!TryStableBrightness(timestamp, brightness, out double folded)) return LastResult;
                if (openBrightness - folded < requiredDrop) return LastResult;
                foldedBrightness = folded;
                smoothedProgress = emittedProgress = 0;
                stability.Clear();
                return LastResult = new(CameraBrightnessStage.Tracking, brightness, 0,
                    "Calibration complete. The animation now follows image brightness. Changes in room lighting can change the estimate.");
            case CameraBrightnessStage.Tracking:
                double target = Math.Clamp((brightness - foldedBrightness) / (openBrightness - foldedBrightness), 0, 1);
                // Averaging identical samples can shift an endpoint by a few
                // floating-point ulps. Preserve exact endpoint behavior without
                // treating meaningful brightness differences as endpoint values.
                if (target <= 1e-9) target = 0;
                else if (target >= 1 - 1e-9) target = 1;
                double amount = 1 - Math.Exp(-elapsed / SmoothingSeconds);
                smoothedProgress += amount * (target - smoothedProgress);
                if (target == 0 && smoothedProgress <= ProgressDeadband) emittedProgress = 0;
                else if (target == 1 && smoothedProgress >= 1 - ProgressDeadband) emittedProgress = 1;
                else if (Math.Abs(smoothedProgress - emittedProgress) >= ProgressDeadband) emittedProgress = smoothedProgress;
                return LastResult = LastResult with { Progress = emittedProgress };
            default:
                return LastResult;
        }
    }

    private bool TryStableBrightness(TimeSpan timestamp, double brightness, out double average)
    {
        stability.Add((timestamp, brightness));
        // Retain a sample at or just before the window boundary, so irregular
        // delivery still has to cover a complete duration with fresh observations.
        while (stability.Count > 1 && timestamp - stability[1].Time >= StabilityTime)
            stability.RemoveAt(0);
        average = 0;
        if (stability.Count < MinimumStableSamples || timestamp - stability[0].Time < StabilityTime) return false;
        double minimum = 1, maximum = 0, sum = 0;
        foreach (var sample in stability)
        {
            minimum = Math.Min(minimum, sample.Brightness);
            maximum = Math.Max(maximum, sample.Brightness);
            sum += sample.Brightness;
        }
        if (maximum - minimum > MaximumStableRange) return false;
        average = sum / stability.Count;
        return true;
    }

    private CameraBrightnessResult Fail(string message)
    {
        stability.Clear();
        return LastResult = LastResult with { Stage = CameraBrightnessStage.Failed, Message = message };
    }
}
