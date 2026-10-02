using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace BloomNative.Windows;

internal sealed record CameraDevice(string Id, string Name);

/// <summary>Owned, tightly packed image copies. Bgra has four bytes per pixel; Gray has one.</summary>
internal sealed record CameraFrame(long Sequence, TimeSpan Timestamp, int Width, int Height, byte[] Gray, byte[] Bgra);

/// <summary>
/// Explicit opt-in, video-only camera reader. It records, writes, and transmits nothing.
/// Start on the WPF UI thread; frame/error events arrive on a worker thread and must
/// not synchronously wait for the UI or StopAsync. Each native operation settles
/// before stop completes, even if startup was cancelled while the driver was busy.
/// </summary>
internal sealed class CameraCapture : IAsyncDisposable
{
    private const int MaximumWidth = 320, MaximumHeight = 240;
    private static readonly long FrameInterval = Math.Max(1, Stopwatch.Frequency / 10);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object frameGate = new();
    private readonly Stopwatch clock = new();
    private MediaCapture? capture;
    private MediaFrameReader? reader;
    private int requestVersion, sessionVersion, running, disposed, faultReported;
    private long lastFrameTick, sequence;

    internal event Action<CameraFrame>? FrameArrived;
    internal event Action<string>? Failed;
    internal bool IsRunning => Volatile.Read(ref running) != 0;

    internal static async Task<IReadOnlyList<CameraDevice>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Enumeration lists devices only. It never initializes MediaCapture.
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        cancellationToken.ThrowIfCancellationRequested();
        return devices.Select(device => new CameraDevice(device.Id, device.Name)).ToArray();
    }

    internal async Task StartAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Choose a camera before starting.", nameof(deviceId));
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() != true ||
            Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Start the camera from the application's UI thread.");

        int version = Interlocked.Increment(ref requestVersion);
        Volatile.Write(ref running, 0);
        // Settle queued requests even when their token is cancelled so an old
        // session is not left holding hardware after frame delivery was stopped.
        await lifecycle.WaitAsync();
        bool ownsSession = false;
        try
        {
            if (version != Volatile.Read(ref requestVersion))
                throw new OperationCanceledException("Camera startup was superseded.");
            ownsSession = true;
            await StopCoreAsync();
            CheckCurrent(version, cancellationToken);
            sessionVersion = version;
            faultReported = 0;
            sequence = 0;
            lastFrameTick = 0;
            capture = new MediaCapture();
            capture.Failed += CaptureFailed;

            // MediaCapture.InitializeAsync must begin on the STA UI thread.
            // Preserve the synchronization context and await the actual WinRT
            // operation: cancelling only its Task wrapper could leave a camera
            // initialization running after the UI believes it has stopped.
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly
            });
            CheckCurrent(version, cancellationToken);

            var source = capture.FrameSources.Values
                .Where(value => value.Info.SourceKind == MediaFrameSourceKind.Color &&
                    value.Info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord)
                .OrderBy(value => value.Info.MediaStreamType == MediaStreamType.VideoPreview ? 0 : 1)
                .FirstOrDefault();
            if (source is null)
                throw new InvalidOperationException("This camera does not expose a supported color preview stream. Choose another camera.");

            var format = source.CurrentFormat.VideoFormat;
            if (format is null || format.Width == 0 || format.Height == 0)
                throw new InvalidOperationException("The camera did not report a usable preview size. Choose another camera.");
            double scale = Math.Min(1, Math.Min(MaximumWidth / (double)format.Width, MaximumHeight / (double)format.Height));
            var size = new BitmapSize
            {
                Width = (uint)Math.Max(1, Math.Floor(format.Width * scale)),
                Height = (uint)Math.Max(1, Math.Floor(format.Height * scale))
            };
            // Scaling this reader does not change a shared camera's sensor format.
            // The device may run faster; processing/delivery is capped at 10 fps.
            reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8, size);
            CheckCurrent(version, cancellationToken);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += ReaderFrameArrived;
            MediaFrameReaderStartStatus status = await reader.StartAsync();
            CheckCurrent(version, cancellationToken);
            if (status != MediaFrameReaderStartStatus.Success)
                throw new InvalidOperationException($"The camera preview could not start ({status}). Close other camera apps or choose another device.");

            clock.Restart();
            Volatile.Write(ref running, 1);
        }
        catch (OperationCanceledException)
        {
            if (ownsSession) await StopCoreAsync();
            throw;
        }
        catch (Exception error)
        {
            if (ownsSession) await StopCoreAsync();
            throw new InvalidOperationException(FriendlyError(error), error);
        }
        finally { lifecycle.Release(); }
    }

    internal async Task StopAsync()
    {
        // Invalidate in-flight startup and stop frame delivery immediately.
        int version = Interlocked.Increment(ref requestVersion);
        Volatile.Write(ref running, 0);
        await lifecycle.WaitAsync();
        try
        {
            // A later explicit Start is a newer request. An older queued Stop
            // must not tear down that newly started session.
            if (version == Volatile.Read(ref requestVersion)) await StopCoreAsync();
        }
        finally { lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        await StopAsync();
        FrameArrived = null;
        Failed = null;
        // Do not dispose the gate while a previously queued Start/Stop is waking.
    }

    private void CheckCurrent(int version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (version != Volatile.Read(ref requestVersion) || Volatile.Read(ref disposed) != 0)
            throw new OperationCanceledException("Camera startup was stopped.");
    }

    private async Task StopCoreAsync()
    {
        Volatile.Write(ref running, 0);
        MediaFrameReader? oldReader = reader;
        MediaCapture? oldCapture = capture;
        reader = null;
        capture = null;
        if (oldReader is not null) oldReader.FrameArrived -= ReaderFrameArrived;
        if (oldCapture is not null) oldCapture.Failed -= CaptureFailed;
        try
        {
            if (oldReader is not null)
            {
                try { await oldReader.StopAsync(); }
                catch (Exception) { /* Disconnected/failed readers still require disposal. */ }
            }
        }
        finally
        {
            // A frame callback owns the bitmap until it has copied and delivered
            // it. Wait for that one callback; there is never a queued frame list.
            lock (frameGate)
            {
                try { oldReader?.Dispose(); }
                finally { oldCapture?.Dispose(); clock.Stop(); }
            }
        }
    }

    private void ReaderFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        int version = Volatile.Read(ref sessionVersion);
        if (!IsRunning || version != Volatile.Read(ref requestVersion) || !ReferenceEquals(sender, reader) ||
            !Monitor.TryEnter(frameGate)) return;
        string? failure = null;
        try
        {
            if (!IsRunning || version != Volatile.Read(ref requestVersion)) return;
            long now = Stopwatch.GetTimestamp();
            if (lastFrameTick != 0 && now - lastFrameTick < FrameInterval) return;
            using MediaFrameReference? reference = sender.TryAcquireLatestFrame();
            if (reference is null) return;
            // Retrieving SoftwareBitmap creates a separate owned reference; frame
            // disposal alone does not release it (per Microsoft's reader contract).
            using SoftwareBitmap? bitmap = reference.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) return;
            CameraFrame frame = CopyFrame(bitmap, ++sequence, clock.Elapsed);
            lastFrameTick = now;
            if (IsRunning && version == Volatile.Read(ref requestVersion)) FrameArrived?.Invoke(frame);
        }
        catch (Exception error) { failure = FriendlyError(error); }
        finally { Monitor.Exit(frameGate); }
        if (failure is not null) ReportFailure(version, failure);
    }

    // This is also exercised with an in-memory SoftwareBitmap by the Windows
    // self-test. It neither opens a camera nor keeps a reference to native pixels.
    internal static CameraFrame CopyFrame(SoftwareBitmap bitmap, long sequence, TimeSpan timestamp)
    {
        if (bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1 || bitmap.PixelWidth > MaximumWidth || bitmap.PixelHeight > MaximumHeight)
            throw new InvalidOperationException("The camera returned an unsupported preview size.");
        using SoftwareBitmap converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        int pixels = checked(converted.PixelWidth * converted.PixelHeight);
        var buffer = new global::Windows.Storage.Streams.Buffer(checked((uint)(pixels * 4)));
        converted.CopyToBuffer(buffer);
        var bgra = new byte[pixels * 4];
        using (var data = DataReader.FromBuffer(buffer)) data.ReadBytes(bgra);
        var gray = new byte[pixels];
        for (int pixel = 0, offset = 0; pixel < pixels; pixel++, offset += 4)
        {
            gray[pixel] = (byte)((77 * bgra[offset + 2] + 150 * bgra[offset + 1] + 29 * bgra[offset] + 128) >> 8);
            bgra[offset + 3] = 255;
        }
        return new CameraFrame(sequence, timestamp, converted.PixelWidth, converted.PixelHeight, gray, bgra);
    }

    internal static bool CheckPixelConversion()
    {
        // Activate only a memory bitmap, never MediaCapture or a camera device.
        byte[] corners = { 0, 0, 0, 17, 255, 255, 255, 31, 0, 0, 255, 63, 0, 255, 0, 127 };
        static IBuffer MakeBuffer(byte[] bytes)
        {
            using var writer = new DataWriter();
            writer.WriteBytes(bytes);
            return writer.DetachBuffer();
        }
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(MakeBuffer(corners), BitmapPixelFormat.Bgra8, 2, 2, BitmapAlphaMode.Ignore);
        var timestamp = TimeSpan.FromMilliseconds(25);
        CameraFrame frame = CopyFrame(bitmap, 7, timestamp);
        byte[] expectedBgra = (byte[])corners.Clone();
        for (int index = 3; index < expectedBgra.Length; index += 4) expectedBgra[index] = 255;
        bool pixelsMatch = frame.Width == 2 && frame.Height == 2 && frame.Sequence == 7 && frame.Timestamp == timestamp &&
            frame.Gray.SequenceEqual(new byte[] { 0, 255, 77, 149 }) && frame.Bgra.SequenceEqual(expectedBgra);
        // Mutating both original input and native pixels must not change the
        // managed frame retained by an asynchronous UI/motion consumer.
        Array.Fill(corners, (byte)0);
        bitmap.CopyFromBuffer(MakeBuffer(corners));
        bool ownsPixels = frame.Gray.SequenceEqual(new byte[] { 0, 255, 77, 149 }) && frame.Bgra.SequenceEqual(expectedBgra);
        return pixelsMatch && ownsPixels;
    }

    private void CaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs error)
    {
        if (!ReferenceEquals(sender, capture)) return;
        ReportFailure(Volatile.Read(ref sessionVersion),
            $"The camera stopped or was disconnected. Close other camera apps, check Windows camera access, then choose Start to try again. (0x{error.Code:X8})");
    }

    private void ReportFailure(int version, string message)
    {
        if (version != Volatile.Read(ref requestVersion) || Interlocked.Exchange(ref faultReported, 1) != 0) return;
        Volatile.Write(ref running, 0);
        // Yield away from the native callback before awaiting reader shutdown.
        _ = Task.Run(async () =>
        {
            string notification = message;
            bool notify = false;
            await lifecycle.WaitAsync();
            try
            {
                if (version != Volatile.Read(ref requestVersion)) return;
                try { await StopCoreAsync(); }
                catch (Exception cleanupError) { notification += $" Camera cleanup reported 0x{cleanupError.HResult:X8}."; }
                notify = true;
            }
            finally { lifecycle.Release(); }
            if (notify && version == Volatile.Read(ref requestVersion) && Volatile.Read(ref disposed) == 0)
            {
                try { Failed?.Invoke(notification); }
                catch (Exception) { /* An observer must not crash the capture cleanup path. */ }
            }
        });
    }

    private static string FriendlyError(Exception error)
    {
        if (error is UnauthorizedAccessException || error.HResult == unchecked((int)0x80070005))
            return "Camera access was denied. In Windows Settings, enable Camera access and allow desktop apps to access your camera, then choose Start again.";
        if (error.HResult == unchecked((int)0x80070020))
            return "This camera is busy in another app. Close that app or choose a different camera, then choose Start again.";
        if (error is InvalidOperationException) return error.Message;
        return $"The camera could not provide a preview. Check Windows camera access, close other camera apps, or reconnect the device and try again. (0x{error.HResult:X8})";
    }
}
