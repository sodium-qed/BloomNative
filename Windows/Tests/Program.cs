using System;
using System.Globalization;
using System.IO;
using BloomNative.Windows;

internal static class Program
{
    private static int checks;

    private static int Main()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "BloomNative-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            CheckLaunchArguments();
            CheckAnimationBounds();
            CheckSettings(temporary);
            CheckArtworkRejection(temporary);
            Console.WriteLine($"PASS: {checks} checks; no artwork downloaded and no Windows GUI required.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL after {checks} successful checks: {exception}");
            return 1;
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    private static void CheckLaunchArguments()
    {
        Equal(LaunchMode.Controls, LaunchOptions.Parse(Array.Empty<string>()).Mode, "No arguments opens controls");
        foreach (string command in new[] { "/s", "/S", "-s", "--screensaver" })
            Equal(LaunchMode.Saver, LaunchOptions.Parse(new[] { command }).Mode, $"Saver command {command}");
        foreach (string command in new[] { "/c", "/C", "-c", "/c:1234" })
            Equal(LaunchMode.Controls, LaunchOptions.Parse(new[] { command }).Mode, $"Configure command {command}");

        foreach (string[] arguments in new[]
        {
            new[] { "/p", "1234" }, new[] { "/P", "1234" },
            new[] { "-p", "1234" }, new[] { "/p:1234" }
        })
        {
            var options = LaunchOptions.Parse(arguments);
            Equal(LaunchMode.Preview, options.Mode, $"Preview mode {string.Join(' ', arguments)}");
            Equal(new IntPtr(1234), options.Parent, "Preview retains the parent HWND");
        }

        foreach (string[] arguments in new[]
        {
            new[] { "/p" }, new[] { "/p:" }, new[] { "/p", "" },
            new[] { "/p", "garbage" }, new[] { "/p:garbage" },
            new[] { "/p", "-1" }, new[] { "/p:-1" }, new[] { "/p", "0" },
            new[] { "/p", "9223372036854775808" },
            new[] { "/p", "1234", "extra" }, new[] { "/unknown" },
            new[] { "--self-test" }, new[] { "--self-test", "one", "two" }
        })
            Equal(LaunchMode.Invalid, LaunchOptions.Parse(arguments).Mode, $"Reject arguments {string.Join(' ', arguments)}");

        var wideHandle = LaunchOptions.Parse(new[] { "/p", "2147483648" });
        Equal(IntPtr.Size == 8 ? LaunchMode.Preview : LaunchMode.Invalid, wideHandle.Mode, "Parent handle respects pointer width");
        const string outputPath = "self test output.json";
        var selfTest = LaunchOptions.Parse(new[] { "--self-test", outputPath });
        Equal(LaunchMode.SelfTest, selfTest.Mode, "Self-test mode recognized");
        Equal(outputPath, selfTest.TestOutput, "Self-test preserves output path with spaces");
    }

    private static void CheckAnimationBounds()
    {
        foreach (var sample in new (double Input, double Expected)[]
        {
            (0, 0), (0.35, 0.35), (1, 1), (-4, 0), (4, 1),
            (double.NaN, 1), (double.NegativeInfinity, 1), (double.PositiveInfinity, 1)
        })
            Equal(sample.Expected, BloomMath.ClampProgress(sample.Input), $"Clamp progress {sample.Input}");

        Near(0, BloomMath.FrameTime(0, 5), "Closed pose starts at first frame");
        Near(5 - 1.0 / 60, BloomMath.FrameTime(1, 5), "Open pose seeks one frame before EOF");
        Near((5 - 1.0 / 60) / 2, BloomMath.FrameTime(0.5, 5), "Intermediate pose preserves interpolation");
        Near(0, BloomMath.FrameTime(-1, 5), "Frame seek clamps low progress");
        Near(5 - 1.0 / 60, BloomMath.FrameTime(3, 5), "Frame seek clamps high progress");
        Near(5 - 1.0 / 60, BloomMath.FrameTime(double.NaN, 5), "Frame seek handles nonfinite progress");
        Near(0, BloomMath.FrameTime(1, 0), "Zero-duration clip never seeks negative");
        Near(0, BloomMath.FrameTime(1, 0.001), "Sub-frame clip never seeks negative");
        Near(0, BloomMath.FrameTime(1, -5), "Negative duration never seeks negative");
    }

    private static void CheckSettings(string temporary)
    {
        string path = Path.Combine(temporary, "nested", "settings.json");
        CultureInfo originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var missing = Settings.Load(path);
            Equal(1.0, missing.Progress, "Missing settings defaults to open pose");
            Equal(true, missing.Breathe, "Missing settings enables breathing");
            Equal(true, missing.ReplayOnWake, "Missing settings enables wake replay");
            Equal("en", missing.Language, "English culture selects English");

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            Equal("zh", Settings.Load(path).Language, "Chinese culture selects Chinese");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ broken json");
            Equal(1.0, Settings.Load(path).Progress, "Malformed JSON falls back to defaults");
            File.WriteAllText(path, "null");
            Equal("en", Settings.Load(path).Language, "Null JSON falls back to defaults");
            File.WriteAllText(path, "{\"Progress\":-10,\"Language\":\"unsupported\"}");
            var invalid = Settings.Load(path);
            Equal(0.0, invalid.Progress, "Disk settings clamps negative progress");
            Equal("en", invalid.Language, "Disk settings normalizes unknown language");

            var expected = new Settings { Progress = 0.42, Breathe = false, ReplayOnWake = false, Language = "zh" };
            expected.Save(path);
            var actual = Settings.Load(path);
            Equal(expected.Progress, actual.Progress, "Save/load preserves progress");
            Equal(expected.Breathe, actual.Breathe, "Save/load preserves breathing preference");
            Equal(expected.ReplayOnWake, actual.ReplayOnWake, "Save/load preserves wake preference");
            Equal(expected.Language, actual.Language, "Save/load preserves Chinese preference");
            Equal(false, File.Exists(path + ".tmp"), "Successful save removes temporary file");

            expected.Progress = double.NaN;
            expected.Language = "unrecognized";
            expected.Save(path);
            actual = Settings.Load(path);
            Equal(1.0, actual.Progress, "Save normalizes nonfinite progress before JSON serialization");
            Equal("en", actual.Language, "Save normalizes unknown language");
            expected.Progress = 7;
            expected.Save(path);
            Equal(1.0, Settings.Load(path).Progress, "Save clamps oversized progress");

            string newPath = Path.Combine(temporary, "created", "on-save", "settings.json");
            expected.Save(newPath);
            Equal(true, File.Exists(newPath), "Save creates missing parent directories");
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    private static void CheckArtworkRejection(string temporary)
    {
        string missing = Path.Combine(temporary, "missing.mp4");
        Equal(false, Artwork.Verify(missing), "Missing artwork is rejected");
        string badArtwork = Path.Combine(temporary, "wrong.mp4");
        File.WriteAllText(badArtwork, "This is a tiny test fixture, not Bloom artwork.");
        Equal(false, Artwork.Verify(badArtwork), "Incorrect SHA-256 is rejected");
        Throws<IOException>(() => Artwork.Import(badArtwork), "Import rejects wrong SHA-256 before installing artwork");
        Throws<IOException>(() => Artwork.Import(missing), "Import rejects missing artwork");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}.");
        checks++;
    }

    private static void Near(double expected, double actual, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-9)
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}.");
        checks++;
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { checks++; return; }
        throw new InvalidOperationException($"{message}: expected {typeof(T).Name}.");
    }
}
