using System;
using System.Globalization;

namespace BloomNative.Windows;

public static class BloomMath
{
    public static double ClampProgress(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;
    public static double FrameTime(double progress, double duration) => ClampProgress(progress) * Math.Max(0, duration - 1.0 / 60);
}

public enum LaunchMode { Controls, Saver, Preview, SelfTest, Invalid }

public readonly record struct LaunchOptions(LaunchMode Mode, IntPtr Parent, string? TestOutput = null)
{
    public static LaunchOptions Parse(string[] args)
    {
        if (args.Length == 0) return new(LaunchMode.Controls, IntPtr.Zero);
        string arg = args[0].ToLowerInvariant();
        if (arg == "--self-test" && args.Length == 2) return new(LaunchMode.SelfTest, IntPtr.Zero, args[1]);
        if (arg == "/s" || arg == "-s" || arg == "--screensaver") return new(LaunchMode.Saver, IntPtr.Zero);
        if (arg == "/c" || arg == "-c" || arg.StartsWith("/c:", StringComparison.Ordinal)) return new(LaunchMode.Controls, IntPtr.Zero);
        string? parent = arg is "/p" or "-p" ? (args.Length == 2 ? args[1] : null) : arg.StartsWith("/p:", StringComparison.Ordinal) ? arg[3..] : null;
        if (long.TryParse(parent, NumberStyles.None, CultureInfo.InvariantCulture, out long handle) && handle > 0 && (IntPtr.Size == 8 || handle <= int.MaxValue))
            return new(LaunchMode.Preview, new IntPtr(handle));
        return new(LaunchMode.Invalid, IntPtr.Zero);
    }
}
