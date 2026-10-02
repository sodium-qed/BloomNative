using System;
using System.Collections.Generic;

namespace BloomNative.Windows;

internal readonly record struct CameraRegion(int X, int Y, int Width, int Height);
internal enum CameraMotionState { NotInitialized, Tracking, Lost }
internal readonly record struct CameraMotionResult(CameraMotionState State, double Position,
    double Confidence, int Inliers, int FeatureCount, double? Progress, string Message);

/// <summary>
/// Tracks the vertical image displacement of a user-selected stationary target.
/// This is a relative optical proxy, not a hinge sensor or an angle measurement.
/// The keyboard base and target must remain stationary. Never use face position.
/// No frame leaves this class, no template adapts, and tracking loss is latched.
/// Callers must serialize all operations and provide packed, unmirrored grayscale.
/// </summary>
internal sealed class CameraMotionTracker
{
    private const int PatchRadius = 5;
    private const int PatchSize = 2 * PatchRadius + 1;
    private const int SearchRadius = 12;
    private const int MaximumFeatures = 18;
    private const int MinimumFeatures = 6;
    private const double MinimumCorrelation = 0.82;
    private const double MinimumSeparation = 0.055;
    private const double InlierDistance = 1.8;
    private const double MinimumEndpointSpan = 8;

    private byte[]? reference;
    private int width, height;
    private CameraRegion region;
    private readonly List<Feature> features = new();
    private Affine transform = Affine.Identity;
    private double? folded, unfolded;
    private double featureSpanX, featureSpanY;

    internal CameraMotionResult LastResult { get; private set; } = EmptyResult;
    internal bool IsCalibrated => folded.HasValue && unfolded.HasValue;
    private static CameraMotionResult EmptyResult => new(CameraMotionState.NotInitialized, 0, 0, 0, 0, null,
        "Select a textured stationary target, away from people.");

    internal void Reset()
    {
        reference = null;
        features.Clear();
        transform = Affine.Identity;
        folded = unfolded = null;
        width = height = 0;
        LastResult = EmptyResult;
    }

    internal bool Initialize(byte[] gray, int frameWidth, int frameHeight, CameraRegion selection, out string message)
    {
        Reset();
        if (!ValidFrame(gray, frameWidth, frameHeight))
            return InitializationFailure("The camera frame is invalid or too large.", out message);
        if (selection.Width < 45 || selection.Height < 35 || selection.X < 0 || selection.Y < 0 ||
            (long)selection.X + selection.Width > frameWidth || (long)selection.Y + selection.Height > frameHeight)
            return InitializationFailure("Choose a larger target fully inside the camera image.", out message);

        width = frameWidth;
        height = frameHeight;
        region = selection;
        reference = (byte[])gray.Clone();
        var candidates = new List<(int X, int Y, double Score)>();
        // Shi-Tomasi's smaller gradient-covariance eigenvalue rejects flat patches
        // and one-dimensional edges that cannot constrain both image directions.
        for (int y = selection.Y + PatchRadius + 1; y < selection.Y + selection.Height - PatchRadius - 1; y += 3)
        for (int x = selection.X + PatchRadius + 1; x < selection.X + selection.Width - PatchRadius - 1; x += 3)
        {
            double xx = 0, yy = 0, xy = 0;
            for (int oy = -3; oy <= 3; oy++)
            for (int ox = -3; ox <= 3; ox++)
            {
                int p = (y + oy) * width + x + ox;
                double gx = reference[p + 1] - reference[p - 1];
                double gy = reference[p + width] - reference[p - width];
                xx += gx * gx;
                yy += gy * gy;
                xy += gx * gy;
            }
            double score = (xx + yy - Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy)) / 98;
            if (score >= 90) candidates.Add((x, y, score));
        }
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        double spacing = Math.Max(13, Math.Min(selection.Width, selection.Height) / 5.0);
        foreach (var candidate in candidates)
        {
            bool nearby = false;
            foreach (var existing in features)
                if (Square(candidate.X - existing.X) + Square(candidate.Y - existing.Y) < spacing * spacing)
                { nearby = true; break; }
            if (nearby) continue;
            var feature = Feature.Create(reference, width, candidate.X, candidate.Y);
            if (feature.Energy < 121 * 100) continue;
            // A periodic texture can have strong corners but ambiguous identities.
            // Require this patch to identify its own position before accepting it.
            if (!TryMatch(reference, feature, candidate.X, candidate.Y, Math.Min(SearchRadius, 9), out _)) continue;
            features.Add(feature);
            if (features.Count == MaximumFeatures) break;
        }
        FeatureBounds(features, out featureSpanX, out featureSpanY);
        if (features.Count < MinimumFeatures || featureSpanX < selection.Width * 0.4 || featureSpanY < selection.Height * 0.4)
            return InitializationFailure("This target lacks enough distinct, spread-out detail. Choose a patterned stationary area.", out message);

        message = "Target selected. Mark both positions while moving the lid slowly and keeping the base still.";
        LastResult = new(CameraMotionState.Tracking, 0, 1, features.Count, features.Count, null, message);
        return true;
    }

    internal CameraMotionResult Track(byte[] gray, int frameWidth, int frameHeight)
    {
        // Never search for a different object after loss. Explicit reselection is
        // required, even if a later frame looks similar to the old reference.
        if (LastResult.State != CameraMotionState.Tracking) return LastResult;
        if (!ValidFrame(gray, frameWidth, frameHeight) || frameWidth != width || frameHeight != height || reference is null)
            return Lose("The camera image changed size. Select the target again and recalibrate.");

        var matches = new List<Match>(features.Count);
        foreach (var feature in features)
        {
            var prediction = transform.Apply(feature.X, feature.Y);
            if (!TryMatch(gray, feature, (int)Math.Round(prediction.X), (int)Math.Round(prediction.Y), SearchRadius, out var found))
                continue;
            // Forward/backward consistency rejects a coincidental patch that does
            // not map back to the same feature in the frozen reference image.
            var backwardPatch = Feature.Create(gray, width, found.X, found.Y);
            if (!TryMatch(reference, backwardPatch, feature.X, feature.Y, 5, out var backward) ||
                Square(backward.X - feature.X) + Square(backward.Y - feature.Y) > 2.25)
                continue;
            matches.Add(new(feature.X, feature.Y, found.X, found.Y, found.Correlation));
        }
        int required = Math.Max(MinimumFeatures, (features.Count + 1) / 2);
        if (matches.Count < required || !TryConsensus(matches, out var next, out var inliers) || inliers.Count < required ||
            inliers.Count < matches.Count * 0.7)
            return Lose("The target is obscured, changed, or moved too far. Select it again and recalibrate.");

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, quality = 0;
        foreach (var m in inliers)
        {
            minX = Math.Min(minX, m.SourceX); maxX = Math.Max(maxX, m.SourceX);
            minY = Math.Min(minY, m.SourceY); maxY = Math.Max(maxY, m.SourceY);
            quality += m.Correlation;
        }
        if (maxX - minX < featureSpanX * 0.5 || maxY - minY < featureSpanY * 0.5 || !next.IsPlausible())
            return Lose("Too little of the original target remains reliable. Select it again and recalibrate.");

        double centerX = region.X + region.Width / 2.0, centerY = region.Y + region.Height / 2.0;
        var oldCenter = transform.Apply(centerX, centerY);
        var newCenter = next.Apply(centerX, centerY);
        if (Square(newCenter.X - oldCenter.X) + Square(newCenter.Y - oldCenter.Y) > Square(SearchRadius + 2) ||
            Math.Abs(next.A - transform.A) > 0.15 || Math.Abs(next.B - transform.B) > 0.15 ||
            Math.Abs(next.C - transform.C) > 0.15 || Math.Abs(next.D - transform.D) > 0.15)
            return Lose("The target moved too quickly. Select it again and recalibrate with slower lid movement.");

        double position = newCenter.Y - centerY;
        if (!double.IsFinite(position)) return Lose("The optical estimate is invalid. Select the target again.");
        transform = next;
        double? progress = null;
        if (folded.HasValue && unfolded.HasValue && TryMapProgress(position, folded.Value, unfolded.Value, out double mapped))
            progress = mapped;
        LastResult = new(CameraMotionState.Tracking, position, quality / inliers.Count * inliers.Count / features.Count,
            inliers.Count, features.Count, progress, progress.HasValue ? "Tracking a relative visual estimate. Keep the base and target still." :
            "Tracking target. Mark both positions to control the animation.");
        return LastResult;
    }

    internal bool CaptureEndpoint(bool unfolded, out string message)
    {
        if (LastResult.State != CameraMotionState.Tracking || !double.IsFinite(LastResult.Position))
        { message = "Select a reliable target before marking positions."; return false; }
        double position = LastResult.Position;
        double? other = unfolded ? folded : this.unfolded;
        if (other.HasValue && Math.Abs(position - other.Value) < MinimumEndpointSpan)
        { message = "The two positions are too close. Move the lid slowly until the target shifts farther, then try again."; return false; }
        if (unfolded) this.unfolded = position;
        else folded = position;
        double? progress = null;
        if (folded.HasValue && this.unfolded.HasValue && TryMapProgress(position, folded.Value, this.unfolded.Value, out double mapped))
            progress = mapped;
        message = progress.HasValue ? "Both positions marked. The animation now follows this relative visual estimate." :
            "Position marked. Move the lid slowly and mark the other position.";
        LastResult = LastResult with { Progress = progress, Message = message };
        return true;
    }

    internal static bool TryMapProgress(double position, double foldedPosition, double unfoldedPosition, out double progress)
    {
        progress = 0;
        if (!double.IsFinite(position) || !double.IsFinite(foldedPosition) || !double.IsFinite(unfoldedPosition)) return false;
        double span = unfoldedPosition - foldedPosition;
        if (!double.IsFinite(span) || Math.Abs(span) < MinimumEndpointSpan) return false;
        double value = (position - foldedPosition) / span;
        if (!double.IsFinite(value)) return false;
        progress = Math.Clamp(value, 0, 1);
        return true;
    }

    private bool InitializationFailure(string reason, out string message)
    {
        Reset();
        message = reason;
        LastResult = LastResult with { Message = reason };
        return false;
    }

    private CameraMotionResult Lose(string reason)
    {
        LastResult = LastResult with { State = CameraMotionState.Lost, Confidence = 0, Inliers = 0, Message = reason };
        return LastResult;
    }

    private static bool ValidFrame(byte[]? gray, int width, int height) => gray is not null && width >= 64 && height >= 48 &&
        width <= 640 && height <= 480 && (long)width * height == gray.Length;
    private static double Square(double value) => value * value;

    private bool TryMatch(byte[] image, Feature template, int centerX, int centerY, int radius, out Found found)
    {
        found = default;
        int left = Math.Max(PatchRadius, centerX - radius), right = Math.Min(width - PatchRadius - 1, centerX + radius);
        int top = Math.Max(PatchRadius, centerY - radius), bottom = Math.Min(height - PatchRadius - 1, centerY + radius);
        if (left > right || top > bottom) return false;
        double best = -1;
        int bestX = 0, bestY = 0;
        var scores = new double[(right - left + 1) * (bottom - top + 1)];
        int index = 0;
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            double score = Correlation(image, template, x, y);
            scores[index++] = score;
            if (score > best) { best = score; bestX = x; bestY = y; }
        }
        if (best < MinimumCorrelation) return false;
        double second = -1;
        index = 0;
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            double score = scores[index++];
            if (Square(x - bestX) + Square(y - bestY) > 9) second = Math.Max(second, score);
        }
        if (best - second < MinimumSeparation) return false;
        found = new(bestX, bestY, best);
        return true;
    }

    private double Correlation(byte[] image, Feature feature, int x, int y)
    {
        double sum = 0, squares = 0, dot = 0;
        int index = 0;
        for (int oy = -PatchRadius; oy <= PatchRadius; oy++)
        {
            int start = (y + oy) * width + x - PatchRadius;
            for (int ox = 0; ox < PatchSize; ox++)
            {
                double value = image[start + ox];
                sum += value;
                squares += value * value;
                dot += feature.Patch[index++] * value;
            }
        }
        double energy = squares - sum * sum / (PatchSize * PatchSize);
        return energy < 121 * 60 ? -1 : Math.Clamp(dot / Math.Sqrt(feature.Energy * energy), -1, 1);
    }

    private static bool TryConsensus(List<Match> matches, out Affine model, out List<Match> inliers)
    {
        model = Affine.Identity;
        inliers = new();
        double bestError = double.PositiveInfinity;
        // At most 816 triples: deterministic RANSAC without nondeterministic state.
        for (int i = 0; i < matches.Count - 2; i++)
        for (int j = i + 1; j < matches.Count - 1; j++)
        for (int k = j + 1; k < matches.Count; k++)
        {
            var triple = new List<Match>(3) { matches[i], matches[j], matches[k] };
            if (!TryFit(triple, out var candidate) || !candidate.IsPlausible()) continue;
            var accepted = new List<Match>();
            double error = 0;
            foreach (var match in matches)
            {
                var point = candidate.Apply(match.SourceX, match.SourceY);
                double residual = Square(point.X - match.X) + Square(point.Y - match.Y);
                if (residual <= InlierDistance * InlierDistance) { accepted.Add(match); error += residual; }
            }
            if (accepted.Count > inliers.Count || (accepted.Count == inliers.Count && error < bestError))
            { inliers = accepted; model = candidate; bestError = error; }
        }
        if (inliers.Count < MinimumFeatures || !TryFit(inliers, out model)) return false;
        // A least-squares refinement must itself retain the original consensus.
        var refined = new List<Match>();
        foreach (var match in inliers)
        {
            var point = model.Apply(match.SourceX, match.SourceY);
            if (Square(point.X - match.X) + Square(point.Y - match.Y) <= InlierDistance * InlierDistance) refined.Add(match);
        }
        inliers = refined;
        return inliers.Count >= MinimumFeatures && TryFit(inliers, out model);
    }

    private static bool TryFit(List<Match> matches, out Affine model)
    {
        model = Affine.Identity;
        if (matches.Count < 3) return false;
        double sx = 0, sy = 0, tx = 0, ty = 0;
        foreach (var m in matches) { sx += m.SourceX; sy += m.SourceY; tx += m.X; ty += m.Y; }
        sx /= matches.Count; sy /= matches.Count; tx /= matches.Count; ty /= matches.Count;
        double xx = 0, yy = 0, xy = 0, xTx = 0, yTx = 0, xTy = 0, yTy = 0;
        foreach (var m in matches)
        {
            double x = m.SourceX - sx, y = m.SourceY - sy;
            xx += x * x; yy += y * y; xy += x * y;
            xTx += x * (m.X - tx); yTx += y * (m.X - tx);
            xTy += x * (m.Y - ty); yTy += y * (m.Y - ty);
        }
        double determinant = xx * yy - xy * xy;
        if (determinant < 100 || determinant < 0.0001 * xx * yy) return false;
        double a = (xTx * yy - yTx * xy) / determinant, b = (yTx * xx - xTx * xy) / determinant;
        double c = (xTy * yy - yTy * xy) / determinant, d = (yTy * xx - xTy * xy) / determinant;
        model = new(a, b, c, d, tx - a * sx - b * sy, ty - c * sx - d * sy);
        return model.IsPlausible();
    }

    private static void FeatureBounds(List<Feature> selected, out double spanX, out double spanY)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var feature in selected)
        {
            minX = Math.Min(minX, feature.X); maxX = Math.Max(maxX, feature.X);
            minY = Math.Min(minY, feature.Y); maxY = Math.Max(maxY, feature.Y);
        }
        spanX = selected.Count == 0 ? 0 : maxX - minX;
        spanY = selected.Count == 0 ? 0 : maxY - minY;
    }

    private sealed record Feature(int X, int Y, double[] Patch, double Energy)
    {
        internal static Feature Create(byte[] image, int width, int x, int y)
        {
            var patch = new double[PatchSize * PatchSize];
            double sum = 0, energy = 0;
            int index = 0;
            for (int oy = -PatchRadius; oy <= PatchRadius; oy++)
            for (int ox = -PatchRadius; ox <= PatchRadius; ox++)
            { double value = image[(y + oy) * width + x + ox]; patch[index++] = value; sum += value; }
            double mean = sum / patch.Length;
            for (int i = 0; i < patch.Length; i++) { patch[i] -= mean; energy += patch[i] * patch[i]; }
            return new(x, y, patch, energy);
        }
    }

    private readonly record struct Found(int X, int Y, double Correlation);
    private readonly record struct Match(int SourceX, int SourceY, int X, int Y, double Correlation);
    private readonly record struct Affine(double A, double B, double C, double D, double Tx, double Ty)
    {
        internal static Affine Identity => new(1, 0, 0, 1, 0, 0);
        internal (double X, double Y) Apply(double x, double y) => (A * x + B * y + Tx, C * x + D * y + Ty);
        internal bool IsPlausible()
        {
            double determinant = A * D - B * C;
            double xLength = A * A + C * C, yLength = B * B + D * D;
            return double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) &&
                double.IsFinite(Tx) && double.IsFinite(Ty) && determinant > 0.45 && determinant < 2.2 &&
                xLength > 0.45 && xLength < 2.25 && yLength > 0.45 && yLength < 2.25 &&
                Math.Abs(A * B + C * D) < Math.Sqrt(xLength * yLength) * 0.65;
        }
    }
}
