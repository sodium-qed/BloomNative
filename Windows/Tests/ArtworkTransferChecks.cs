using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BloomNative.Windows;

internal static class ArtworkTransferChecks
{
    internal static int Run(string temporary)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        string directory = Path.Combine(temporary, "artwork-transfer");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "BloomOriginal.mp4");
        const string original = "The previously installed bytes must survive every failed transfer.";
        File.WriteAllText(destination, original);
        string invalidSource = Path.Combine(directory, "wrong.mp4");
        File.WriteAllText(invalidSource, "Invalid local artwork");
        foreach (string source in new[] { invalidSource, Path.Combine(directory, "missing.mp4") })
        {
            bool rejected = false;
            try { Artwork.ImportTo(source, destination); }
            catch (IOException) { rejected = true; }
            Check(rejected, "Invalid or missing import must be rejected.");
            Check(File.ReadAllText(destination) == original, "Failed import must preserve the previously installed file.");
            Check(Directory.GetFiles(directory, "*.import").Length == 0, "Failed import must remove staged files.");
        }
        bool selfRejected = false;
        try { Artwork.ImportTo(destination, destination); }
        catch (IOException) { selfRejected = true; }
        Check(selfRejected && File.ReadAllText(destination) == original, "Importing the installed path must still verify it without truncating it.");

        // Headers arrive successfully, then the body emits a partial chunk and hangs.
        // HttpClient.Timeout alone does not stop this ResponseHeadersRead transfer.
        using (var stream = new StalledBody())
        using (var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }))
        using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) })
        {
            bool timedOut = false;
            try
            {
                Artwork.DownloadToAsync(client, destination, new InlineProgress(_ => { }), CancellationToken.None, TimeSpan.FromMilliseconds(500))
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { timedOut = true; }
            Check(timedOut, "The whole-transfer deadline must cancel a stalled body after headers.");
            Check(stream.Reads >= 2, "Timeout regression must reach the stalled body rather than fail before headers.");
            Check(File.ReadAllText(destination) == original, "Timed-out body must preserve installed artwork.");
            Check(Directory.GetFiles(directory, "*.download").Length == 0, "Timed-out body must remove the partial download.");
            Check(stream.Disposed, "Timed-out body must release its source stream.");
        }

        using (var cancellation = new CancellationTokenSource())
        using (var handler = new ResponseHandler(() => throw new InvalidOperationException("Canceled request must not reach transport.")))
        using (var client = new HttpClient(handler))
        {
            cancellation.Cancel();
            bool canceled = false;
            try { Artwork.DownloadToAsync(client, destination, new InlineProgress(_ => { }), cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Pre-canceled download must honor the caller's cancellation.");
            Check(handler.Requests == 0 && File.ReadAllText(destination) == original, "Pre-canceled download must neither request nor replace artwork.");
        }

        int maximumProgress = 0;
        using (var handler = new ResponseHandler(() =>
        {
            var content = new ByteArrayContent(new byte[32]);
            content.Headers.ContentLength = 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }))
        using (var client = new HttpClient(handler))
        {
            bool rejected = false;
            try { Artwork.DownloadToAsync(client, destination, new InlineProgress(value => maximumProgress = Math.Max(maximumProgress, value)), CancellationToken.None).GetAwaiter().GetResult(); }
            catch (IOException) { rejected = true; }
            Check(rejected && maximumProgress == 100, "Incorrect Content-Length must not produce progress above 100% or bypass checksum validation.");
            Check(File.ReadAllText(destination) == original, "Malformed response must preserve installed artwork.");
        }
        return checks;
    }

    private sealed class InlineProgress : IProgress<int>
    {
        private readonly Action<int> report;
        internal InlineProgress(Action<int> report) => this.report = report;
        public void Report(int value) => report(value);
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> response;
        internal int Requests { get; private set; }
        internal ResponseHandler(Func<HttpResponseMessage> response) => this.response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(response());
        }
    }

    private sealed class StalledBody : Stream
    {
        internal int Reads { get; private set; }
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++Reads == 1) { buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
