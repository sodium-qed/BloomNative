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
    internal static string VideoPath => Path.Combine(Settings.DataDirectory, "BloomOriginal.mp4");
    internal static bool Verify(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase);
    }
    internal static async Task DownloadAsync(IProgress<int> progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Settings.DataDirectory);
        string temp = VideoPath + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await client.GetAsync(SourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            const long maximumBytes = 128L * 1024 * 1024;
            long length = response.Content.Headers.ContentLength ?? -1;
            if (length > maximumBytes) throw new IOException("The artwork download exceeds the expected size limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
                {
                    total += count;
                    if (total > maximumBytes) throw new IOException("The artwork download exceeds the expected size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    if (length > 0) progress.Report((int)(total * 100 / length));
                }
            }
            if (!Verify(temp)) throw new IOException("Artwork checksum mismatch. The original animation may have changed; no unverified file was installed.");
            File.Move(temp, VideoPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void Import(string source)
    {
        if (!Verify(source)) throw new IOException("This file does not match BloomNative's original animation (SHA-256 mismatch).");
        Directory.CreateDirectory(Settings.DataDirectory);
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(VideoPath), StringComparison.OrdinalIgnoreCase)) File.Copy(source, VideoPath, true);
    }
}
