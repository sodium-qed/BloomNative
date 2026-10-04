using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BloomNative.Windows;

internal static class Artwork
{
    internal const string SourceUrl = "https://sixnfive.com/wp-content/uploads/2021/07/curls_hero_05_anim_19_light2.mp4";
    internal const string ExpectedSha256 = "01e08e7efd67574db59352a3cb8be79aeb8e65120bb8aba2f27047e501d5bb75";
    internal const string UserAgent = "BloomNative-Windows/" + ApplicationInfo.Version + " (+https://github.com/sodium-qed/BloomNative)";
    private const long MaximumBytes = 128L * 1024 * 1024;
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(5);
    internal static string VideoPath => Path.Combine(Settings.DataDirectory, "BloomOriginal.mp4");
    internal static bool Verify(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase);
    }
    internal static async Task DownloadAsync(IProgress<int> progress, CancellationToken cancellation)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await DownloadToAsync(client, VideoPath, progress, cancellation);
    }
    // Inject the transport and destination so HTTP failures can be regression-tested offline.
    internal static async Task DownloadToAsync(HttpClient client, string destination, IProgress<int> progress, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        // With ResponseHeadersRead, HttpClient.Timeout stops at the headers. Apply a
        // deadline to the entire body as well so a stalled server cannot hang forever.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TransferTimeout);
        cancellation = deadline.Token;
        cancellation.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SourceUrl);
            // The creator's server returns HTTP 403 for requests without a User-Agent.
            // Identify the app honestly; do not impersonate a browser or weaken integrity checks.
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            long length = response.Content.Headers.ContentLength ?? -1;
            if (length > MaximumBytes) throw new IOException("The artwork download exceeds the expected size limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
                {
                    total += count;
                    if (total > MaximumBytes) throw new IOException("The artwork download exceeds the expected size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    if (length > 0) progress.Report((int)Math.Min(100, total * 100 / length));
                }
            }
            cancellation.ThrowIfCancellationRequested();
            if (!Verify(temp)) throw new IOException("Artwork checksum mismatch. The original animation may have changed; no unverified file was installed.");
            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void Import(string source) => ImportTo(source, VideoPath);

    internal static void ImportTo(string source, string destination)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            if (!Verify(source)) throw new IOException("This file does not match BloomNative's original animation (SHA-256 mismatch).");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".import";
        try
        {
            // Verify the exact bytes to be installed, after copying them. Both a failed
            // copy and a changed source leave any previously installed video untouched.
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    total += count;
                    if (total > MaximumBytes) throw new IOException("The artwork import exceeds the expected size limit.");
                    output.Write(buffer, 0, count);
                }
            }
            if (!Verify(temporary)) throw new IOException("This file does not match BloomNative's original animation (SHA-256 mismatch).");
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
