using System;
using BloomNative.Windows;

internal static class CameraMotionChecks
{
    private const int Width = 320, Height = 240;
    private static readonly CameraRegion Region = new(72, 55, 150, 115);
    private static int checks;

    internal static int Run()
    {
        checks = 0;
        CheckUnusableInput();
        CheckTranslationAndCalibration();
        CheckAffineLightingAndOcclusion();
        CheckLossAndRestart();
        CheckMappingBounds();
        return checks;
    }

    private static void CheckUnusableInput()
    {
        var tracker = new CameraMotionTracker();
        var flat = new byte[Width * Height];
        Array.Fill(flat, (byte)120);
        Check(!tracker.Initialize(flat, Width, Height, Region, out _), "A flat target cannot calibrate");
        Check(!tracker.Initialize(new byte[2], Width, Height, Region, out _), "Truncated frame rejected");
        Check(!tracker.Initialize(flat, int.MaxValue, int.MaxValue, Region, out _), "Oversized dimensions rejected without overflow");
        Check(!tracker.Initialize(flat, Width, Height, new CameraRegion(int.MaxValue, 0, 100, 100), out _), "Overflowing selection rejected");
        Check(!tracker.Initialize(Texture(7), Width, Height, new CameraRegion(1, 1, 20, 20), out _), "Tiny selection rejected");
        var stripes = new byte[Width * Height];
        var periodic = new byte[Width * Height];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            stripes[y * Width + x] = (byte)((x / 3 % 2 == 0) ? 30 : 220);
            periodic[y * Width + x] = (byte)(((x / 2 + y / 2) % 2 == 0) ? 30 : 220);
        }
        Check(!tracker.Initialize(stripes, Width, Height, Region, out _), "One-dimensional edges cannot establish motion in two axes");
        Check(!tracker.Initialize(periodic, Width, Height, Region, out _), "Repeating patches rejected as ambiguous");
        Check(!tracker.CaptureEndpoint(false, out _), "Endpoint requires a selected target");
        Check(tracker.LastResult.State == CameraMotionState.NotInitialized, "Failed selection leaves no live target");
    }

    private static void CheckTranslationAndCalibration()
    {
        byte[] reference = Texture(73);
        var tracker = new CameraMotionTracker();
        byte[] source = (byte[])reference.Clone();
        Check(tracker.Initialize(source, Width, Height, Region, out _), "Distinct target initializes");
        Array.Fill(source, (byte)0);
        Check(tracker.Track(reference, Width, Height).State == CameraMotionState.Tracking, "Tracker owns its frozen reference independently of capture buffer");
        Check(!tracker.LastResult.Progress.HasValue, "No animation progress before calibration");
        Check(tracker.CaptureEndpoint(false, out _), "First endpoint accepted");
        Check(!tracker.CaptureEndpoint(true, out _), "Identical endpoints cannot calibrate");
        var translated = tracker.Track(Warp(reference, ty: 6), Width, Height);
        Check(translated.State == CameraMotionState.Tracking, "Small upward scene motion tracks");
        Near(6, translated.Position, 0.2, "Vertical translation recovered");
        Check(!tracker.CaptureEndpoint(true, out _), "Small calibration span rejected");
        translated = tracker.Track(Warp(reference, tx: 2, ty: 12), Width, Height);
        Check(translated.State == CameraMotionState.Tracking, "Combined horizontal and vertical motion tracks");
        Check(tracker.CaptureEndpoint(true, out _) && tracker.IsCalibrated, "Two separated endpoints calibrate");
        Near(1, tracker.LastResult.Progress!.Value, 1e-9, "Unfolded endpoint maps to one");
        translated = tracker.Track(Warp(reference, tx: 1, ty: 6), Width, Height);
        Check(translated.State == CameraMotionState.Tracking, "Reverse motion retains target");
        Near(0.5, translated.Progress!.Value, 0.025, "Intermediate position maps between endpoints");
        translated = tracker.Track(reference, Width, Height);
        Near(0, translated.Progress!.Value, 0.025, "Return to reference returns to folded endpoint without accumulated drift");
        for (int i = 0; i < 4; i++) translated = tracker.Track(reference, Width, Height);
        Near(0, translated.Position, 0.2, "Repeated stationary frames do not invent motion");
    }

    private static void CheckAffineLightingAndOcclusion()
    {
        byte[] reference = Texture(42);
        var tracker = new CameraMotionTracker();
        Check(tracker.Initialize(reference, Width, Height, Region, out _), "Affine fixture selects target");
        var changed = Warp(reference, a: 1.025, b: 0.012, c: -0.008, d: 0.975, tx: 2, ty: 5, gain: 0.85, offset: 18);
        var reading = tracker.Track(changed, Width, Height);
        Check(reading.State == CameraMotionState.Tracking, "Modest affine deformation and brightness change retain target");
        // Warp pivots around image center, while position refers to ROI center.
        double expected = -0.008 * (Region.X + Region.Width / 2.0 - Width / 2.0) +
            (0.975 - 1) * (Region.Y + Region.Height / 2.0 - Height / 2.0) + 5;
        Near(expected, reading.Position, 1.0, "Affine model reports displacement at selected target center");
        Check(double.IsFinite(reading.Confidence) && reading.Confidence > 0 && reading.Confidence <= 1, "Quality is finite and bounded");

        tracker.Reset();
        Check(tracker.Initialize(reference, Width, Height, Region, out _), "Occlusion fixture selects target");
        byte[] partiallyCovered = Warp(reference, ty: 4);
        // A small corner obstruction removes some features. Distributed consensus
        // should still recover the stationary background rather than the occluder.
        for (int y = Region.Y; y < Region.Y + 35; y++)
        for (int x = Region.X; x < Region.X + 45; x++) partiallyCovered[y * Width + x] = 128;
        reading = tracker.Track(partiallyCovered, Width, Height);
        Check(reading.State == CameraMotionState.Tracking, "Small partial occlusion retains enough distributed features");
        Near(4, reading.Position, 0.3, "Occlusion does not drag the estimate toward covered pixels");
    }

    private static void CheckLossAndRestart()
    {
        byte[] reference = Texture(99);
        var tracker = new CameraMotionTracker();
        Check(tracker.Initialize(reference, Width, Height, Region, out _), "Loss fixture selects target");
        Check(tracker.CaptureEndpoint(false, out _), "Loss fixture marks start");
        Check(tracker.Track(Warp(reference, ty: -10), Width, Height).State == CameraMotionState.Tracking, "Negative travel is supported");
        Check(tracker.CaptureEndpoint(true, out _), "Reversed direction calibrates");
        var good = tracker.Track(Warp(reference, ty: -5), Width, Height);
        Near(0.5, good.Progress!.Value, 0.025, "Reversed endpoints preserve interpolation");
        var lost = tracker.Track(new byte[Width * Height], Width, Height);
        Check(lost.State == CameraMotionState.Lost, "Full occlusion loses tracking");
        Near(good.Position, lost.Position, 1e-9, "Loss freezes last reliable position");
        Near(good.Progress!.Value, lost.Progress!.Value, 1e-9, "Loss freezes last reliable animation pose");
        Check(lost.Confidence == 0, "Lost result does not claim confidence");
        Check(!tracker.CaptureEndpoint(false, out _), "Lost tracking cannot mark endpoint");
        Check(tracker.Track(reference, Width, Height).State == CameraMotionState.Lost, "Similar later image cannot silently reacquire target");
        Check(tracker.Initialize(reference, Width, Height, Region, out _), "Explicit reselection restarts tracking");
        Check(!tracker.IsCalibrated && !tracker.LastResult.Progress.HasValue, "Reselection requires fresh calibration");
        Check(tracker.Track(Texture(123), Width, Height).State == CameraMotionState.Lost, "Unrelated textured image cannot replace target");
        Check(tracker.Initialize(reference, Width, Height, Region, out _), "Fast-motion fixture selects target");
        Check(tracker.Track(Warp(reference, ty: 40), Width, Height).State == CameraMotionState.Lost, "Large frame jump freezes instead of following another patch");
        Check(tracker.Initialize(reference, Width, Height, Region, out _), "Resize fixture selects target");
        Check(tracker.Track(new byte[160 * 120], 160, 120).State == CameraMotionState.Lost, "Camera size change invalidates reference");
        tracker.Reset();
        Check(tracker.LastResult.State == CameraMotionState.NotInitialized && !tracker.IsCalibrated, "Stop/reset drops optical state and calibration");
    }

    private static void CheckMappingBounds()
    {
        Check(CameraMotionTracker.TryMapProgress(-10, 0, 10, out double low) && low == 0, "Mapping clamps below range");
        Check(CameraMotionTracker.TryMapProgress(20, 0, 10, out double high) && high == 1, "Mapping clamps above range");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Check(!CameraMotionTracker.TryMapProgress(invalid, 0, 10, out _), "Nonfinite live position rejected");
            Check(!CameraMotionTracker.TryMapProgress(0, invalid, 10, out _), "Nonfinite folded endpoint rejected");
            Check(!CameraMotionTracker.TryMapProgress(0, 0, invalid, out _), "Nonfinite unfolded endpoint rejected");
        }
        Check(!CameraMotionTracker.TryMapProgress(0, -double.MaxValue, double.MaxValue, out _), "Overflowing calibration span rejected");
        Check(!CameraMotionTracker.TryMapProgress(3, 0, 7, out _), "Degenerate small span rejected");
    }

    private static byte[] Texture(int seed)
    {
        var random = new Random(seed);
        var noise = new byte[Width * Height];
        var result = new byte[noise.Length];
        random.NextBytes(noise);
        for (int y = 1; y < Height - 1; y++)
        for (int x = 1; x < Width - 1; x++)
        {
            int p = y * Width + x;
            result[p] = (byte)(35 + (noise[p] * 4 + noise[p - 1] + noise[p + 1] + noise[p - Width] + noise[p + Width]) * 185 / (8 * 255));
        }
        return result;
    }

    private static byte[] Warp(byte[] source, double a = 1, double b = 0, double c = 0, double d = 1,
        double tx = 0, double ty = 0, double gain = 1, double offset = 0)
    {
        var result = new byte[source.Length];
        double determinant = a * d - b * c;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            double targetX = x - Width / 2.0 - tx, targetY = y - Height / 2.0 - ty;
            double sourceX = (d * targetX - b * targetY) / determinant + Width / 2.0;
            double sourceY = (-c * targetX + a * targetY) / determinant + Height / 2.0;
            int left = (int)Math.Floor(sourceX), top = (int)Math.Floor(sourceY);
            if (left < 0 || top < 0 || left >= Width - 1 || top >= Height - 1) continue;
            double fx = sourceX - left, fy = sourceY - top;
            double value = source[top * Width + left] * (1 - fx) * (1 - fy) + source[top * Width + left + 1] * fx * (1 - fy) +
                source[(top + 1) * Width + left] * (1 - fx) * fy + source[(top + 1) * Width + left + 1] * fx * fy;
            result[y * Width + x] = (byte)Math.Clamp(Math.Round(value * gain + offset), 0, 255);
        }
        return result;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Camera motion: " + message);
        checks++;
    }

    private static void Near(double expected, double actual, double tolerance, string message) =>
        Check(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected}, received {actual}");
}
