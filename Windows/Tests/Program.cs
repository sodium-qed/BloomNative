using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
            CheckArtworkDownloadFailures(temporary);
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

    private static void CheckArtworkDownloadFailures(string temporary)
    {
        // The transport is entirely in memory. These tests never contact the creator
        // or use the application's real LocalAppData artwork directory.
        foreach (bool existing in new[] { false, true })
        {
            string directory = Path.Combine(temporary, existing ? "forbidden-existing" : "forbidden-new");
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, "BloomOriginal.mp4");
            const string previousContents = "Previously installed file must survive a failed download.";
            if (existing) File.WriteAllText(destination, previousContents);

            using var handler = new StubHttpHandler(request =>
            {
                string userAgent = request.Headers.UserAgent.ToString();
                Equal(true, userAgent.StartsWith("BloomNative-Windows/", StringComparison.Ordinal) &&
                    userAgent.Contains("https://github.com/sodium-qed/BloomNative", StringComparison.Ordinal),
                    "Download identifies the application and its source in User-Agent");
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("<html>Forbidden</html>")
                };
            });
            using var client = new HttpClient(handler);
            var error = Throws<HttpRequestException>(
                () => Artwork.DownloadToAsync(client, destination, new Progress<int>(), CancellationToken.None).GetAwaiter().GetResult(),
                "Forbidden download reports an HTTP error");
            Equal<HttpStatusCode?>(HttpStatusCode.Forbidden, error.StatusCode, "Forbidden status survives for actionable UI guidance");
            Equal(1, handler.RequestCount, "Forbidden download makes a single request without fallback or retry");
            if (existing)
                Equal(previousContents, File.ReadAllText(destination), "Forbidden download preserves installed artwork");
            else
                Equal(false, File.Exists(destination), "Forbidden download does not install an error response");
            Equal(0, Directory.GetFiles(directory, "*.download").Length, "Forbidden download leaves no partial files");
        }

        string invalidDirectory = Path.Combine(temporary, "invalid-download");
        Directory.CreateDirectory(invalidDirectory);
        string invalidDestination = Path.Combine(invalidDirectory, "BloomOriginal.mp4");
        const string installed = "Existing artwork fixture.";
        File.WriteAllText(invalidDestination, installed);
        using var invalidHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>An HTTP 200 error page is not the original animation.</html>")
        });
        using var invalidClient = new HttpClient(invalidHandler);
        var checksumError = Throws<IOException>(
            () => Artwork.DownloadToAsync(invalidClient, invalidDestination, new Progress<int>(), CancellationToken.None).GetAwaiter().GetResult(),
            "Successful HTTP status cannot bypass artwork integrity validation");
        Equal(true, checksumError.Message.Contains("checksum mismatch", StringComparison.OrdinalIgnoreCase), "Invalid content reports checksum mismatch");
        Equal(1, invalidHandler.RequestCount, "Invalid content makes only the requested download");
        Equal(installed, File.ReadAllText(invalidDestination), "Invalid content cannot replace installed artwork");
        Equal(0, Directory.GetFiles(invalidDirectory, "*.download").Length, "Checksum rejection removes the downloaded partial file");
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> response;
        internal int RequestCount { get; private set; }
        internal StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => this.response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(response(request));
        }
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

    private static T Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T exception) { checks++; return exception; }
        throw new InvalidOperationException($"{message}: expected {typeof(T).Name}.");
    }
}
