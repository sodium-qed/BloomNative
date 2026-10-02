using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BloomNative.Windows;

/// <summary>
/// Explicit, local camera controls. Frames and calibration live only in memory;
/// this window never starts capture automatically or hides capture in the tray.
/// </summary>
internal sealed class CameraTrackingWindow : Window
{
    private readonly bool chinese;
    private readonly CameraCapture capture = new();
    private readonly CameraMotionTracker motion = new();
    private readonly SemaphoreSlim motionGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ComboBox cameras;
    private readonly Button refresh, start, stop, folded, unfolded, reset;
    private readonly TextBlock indicator, status, estimate;
    private readonly Image preview;
    private readonly Grid previewStage;
    private readonly Canvas overlay;
    private readonly Rectangle selection;
    private readonly DispatcherTimer frameWatchdog;
    private CancellationTokenSource? startCancellation;
    private Task? startTask, stopTask;
    private CameraFrame? pendingFrame, displayedFrame;
    private WriteableBitmap? bitmap;
    private int frameQueued, generation, cameraGeneration;
    private long lastFrameTick;
    private TimeSpan? lastProcessedTimestamp;
    private string? calibrationNotice;
    private bool starting, stopping, closed, closeAllowed, closing, enumerating;
    private bool initializing, endpointBusy, initialized, foldedCaptured, calibrated, trackingActive;
    private bool dragging;
    private Point dragStart;
    private Rect selectedPreview;
    private CameraMotionState lastState = CameraMotionState.NotInitialized;

    public event Action<double>? ProgressChanged;
    public event Action<bool>? TrackingChanged;
    public bool IsCameraRunning => capture.IsRunning;

    public CameraTrackingWindow(bool chinese)
    {
        this.chinese = chinese;
        Title = T("Camera experiment · Bloom Native", "摄像头实验 · Bloom Native");
        Width = 850; Height = 900; MinWidth = 650; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = new SolidColorBrush(Color.FromRgb(242, 245, 251));
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;

        var body = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(new TextBlock { Text = T("Camera motion — experimental", "摄像头运动估计（实验性）"), FontSize = 26, FontWeight = FontWeights.SemiBold });
        body.Children.Add(Paragraph(T(
            "This estimates relative unfolding from the camera image, not a hinge angle. Use a camera built into the moving lid; a stationary external webcam cannot measure lid movement.",
            "通过摄像头画面估计相对展开程度，不测量铰链角度。请使用随屏幕盖移动的内置摄像头；固定的外接摄像头无法测量开盖运动。")));
        body.Children.Add(Paragraph(T(
            "Camera use starts only when you press Start camera. Processing stays on this PC. No video or audio is saved or sent. Closing this window stops the camera; minimizing keeps it on.",
            "仅在点击“启动摄像头”后启用。本机处理，不保存或发送视频、音频。关闭此窗口会停止摄像头；最小化时仍保持开启。")));

        var cameraRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 4) };
        cameras = new ComboBox { MinWidth = 260, MaxWidth = 400, DisplayMemberPath = nameof(CameraDevice.Name), Margin = new Thickness(0, 0, 10, 8), VerticalContentAlignment = VerticalAlignment.Center };
        cameras.SelectionChanged += (_, _) => UpdateControls();
        refresh = MakeButton(T("Refresh cameras", "刷新摄像头"), async () => await LoadCamerasAsync());
        start = MakeButton(T("Start camera", "启动摄像头"), async () => await StartCameraAsync());
        stop = MakeButton(T("Stop camera", "停止摄像头"), async () => await StopAsync());
        cameraRow.Children.Add(cameras); cameraRow.Children.Add(refresh); cameraRow.Children.Add(start); cameraRow.Children.Add(stop);
        body.Children.Add(cameraRow);
        indicator = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(indicator);

        preview = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false };
        previewStage = new Grid { Height = 335, Background = Brushes.Black, ClipToBounds = true };
        overlay = new Canvas { Background = Brushes.Transparent, Cursor = Cursors.Cross, ClipToBounds = true };
        selection = new Rectangle { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(30, 0, 191, 255)), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        overlay.Children.Add(selection);
        overlay.MouseLeftButtonDown += BeginSelection;
        overlay.MouseMove += MoveSelection;
        overlay.MouseLeftButtonUp += EndSelection;
        overlay.LostMouseCapture += (_, _) => { if (dragging) { dragging = false; selection.Visibility = Visibility.Collapsed; } };
        previewStage.Children.Add(preview); previewStage.Children.Add(overlay);
        body.Children.Add(previewStage);
        body.Children.Add(Paragraph(T(
            "1. Keep the laptop base and background still. Set a comfortable lid opening for 0% (folded); do not close the lid. Drag a rectangle over a textured, stationary background patch. Do not select your face, hands or another screen.",
            "1. 保持电脑底座和背景静止，将屏幕盖放在一个舒适的 0%（折叠）位置，不要合盖。拖出矩形，选择有纹理的固定背景。不要选择脸、手或其他屏幕。")));
        body.Children.Add(Paragraph(T(
            "2. Capture folded. Move only the lid while keeping that patch in view, then capture unfolded at the opening you want to map to 100%. Tracking starts after both captures.",
            "2. 记录折叠位置。仅移动屏幕盖，始终让所选背景留在画面中，再在希望对应 100% 的位置记录展开位置。记录两个端点后开始跟踪。")));

        var calibrationRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        folded = MakeButton(T("Capture folded (0%)", "记录折叠位置（0%）"), async () => await CaptureEndpointAsync(false));
        unfolded = MakeButton(T("Capture unfolded (100%)", "记录展开位置（100%）"), async () => await CaptureEndpointAsync(true));
        reset = MakeButton(T("Reset calibration", "重置校准"), async () => await ResetCalibrationAsync());
        calibrationRow.Children.Add(folded); calibrationRow.Children.Add(unfolded); calibrationRow.Children.Add(reset);
        body.Children.Add(calibrationRow);
        estimate = new TextBlock { FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6), Text = T("Estimated unfolding: not calibrated", "估计展开程度：尚未校准") };
        status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 2, 0, 10) };
        body.Children.Add(estimate); body.Children.Add(status);
        body.Children.Add(Paragraph(T(
            "A lost target freezes the last estimate. Select a background patch again and recalibrate to resume. Moving the base, background or camera can invalidate the estimate. Calibration is cleared when the camera stops.",
            "目标丢失时保持最后的估计值。请重新选择背景并校准以恢复。底座、背景或摄像头移动均可能使估计失效。停止摄像头会清除校准。")));

        capture.FrameArrived += OnFrame;
        capture.Failed += OnCameraFailed;
        frameWatchdog = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        frameWatchdog.Tick += async (_, _) => await CheckFrameWatchdogAsync();
        Loaded += async (_, _) => await LoadCamerasAsync();
        Closing += OnClosing;
        UpdateControls();
    }

    private string T(string en, string zh) => chinese ? zh : en;
    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 7, 0, 0) };

    private Button MakeButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 8) };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!closed) status.Text = T("Camera operation failed: ", "摄像头操作失败：") + error.Message; }
        };
        return button;
    }

    private async Task LoadCamerasAsync()
    {
        if (enumerating || starting || stopping || capture.IsRunning || closing || closed) return;
        enumerating = true; UpdateControls();
        try
        {
            var devices = await CameraCapture.EnumerateAsync(lifetime.Token);
            if (closing || closed) return;
            cameras.ItemsSource = devices;
            cameras.SelectedIndex = devices.Count > 0 ? 0 : -1;
            status.Text = devices.Count == 0
                ? T("No cameras found. Connect a camera and check Windows camera privacy settings, then refresh.", "未找到摄像头。请连接摄像头，检查 Windows 摄像头隐私设置，再刷新。")
                : T("Choose the lid's camera, then press Start camera.", "选择屏幕盖上的摄像头，然后点击“启动摄像头”。");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) status.Text = T("Could not list cameras: ", "无法列出摄像头：") + error.Message; }
        finally { enumerating = false; if (!closed) UpdateControls(); }
    }

    private Task StartCameraAsync()
    {
        if (starting || stopping || capture.IsRunning || closing || closed || cameras.SelectedItem is not CameraDevice device)
            return Task.CompletedTask;
        startTask = StartCoreAsync(device.Id);
        return startTask;
    }

    private async Task StartCoreAsync(string deviceId)
    {
        starting = true;
        startCancellation?.Dispose();
        startCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationToken token = startCancellation.Token;
        generation++;
        cameraGeneration++;
        lastProcessedTimestamp = null;
        status.Text = T("Starting the selected camera…", "正在启动所选摄像头……");
        UpdateControls();
        try
        {
            // MediaCapture.InitializeAsync must begin on this window's STA thread.
            await capture.StartAsync(deviceId, token);
            if (token.IsCancellationRequested || closing || stopping)
                return;
            Interlocked.Exchange(ref lastFrameTick, Stopwatch.GetTimestamp());
            frameWatchdog.Start();
            status.Text = T("Camera on. Set the folded position and drag a rectangle over a stationary background patch.", "摄像头已开启。放到折叠位置，然后拖出矩形选择固定背景。");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            status.Text = T("Could not start the camera. Check Windows camera permissions and whether another app is using it. ", "无法启动摄像头。请检查 Windows 摄像头权限及是否被其他程序占用。 ") + error.Message;
        }
        finally { starting = false; if (!closed) UpdateControls(); }
    }

    public Task StopAsync()
    {
        Dispatcher.VerifyAccess();
        if (stopTask is { IsCompleted: false }) return stopTask;
        stopTask = StopCoreAsync();
        return stopTask;
    }

    private async Task StopCoreAsync()
    {
        stopping = true;
        frameWatchdog.Stop();
        generation++;
        cameraGeneration++;
        startCancellation?.Cancel();
        SetTracking(false);
        CancelSelection();
        if (!closed) UpdateControls();
        try
        {
            Task captureStop = capture.StopAsync();
            if (startTask is { IsCompleted: false })
                await startTask;
            await captureStop;
            await motionGate.WaitAsync();
            try { motion.Reset(); }
            finally { motionGate.Release(); }
            initialized = foldedCaptured = calibrated = false;
            calibrationNotice = null;
            lastState = CameraMotionState.NotInitialized;
            Interlocked.Exchange(ref pendingFrame, null);
            displayedFrame = null; bitmap = null; preview.Source = null;
            lastProcessedTimestamp = null;
            estimate.Text = T("Estimated unfolding: not calibrated", "估计展开程度：尚未校准");
            status.Text = T("Camera stopped. Calibration and in-memory frames cleared.", "摄像头已停止，校准和内存中的画面已清除。");
        }
        finally { stopping = false; if (!closed) UpdateControls(); }
    }

    private void OnFrame(CameraFrame frame)
    {
        if (Volatile.Read(ref stopping) || Volatile.Read(ref closed) || Volatile.Read(ref closing)) return;
        Interlocked.Exchange(ref lastFrameTick, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref pendingFrame, frame);
        QueueFrame();
    }

    private void QueueFrame()
    {
        if (Interlocked.Exchange(ref frameQueued, 1) != 0) return;
        if (Dispatcher.HasShutdownStarted)
        {
            Interlocked.Exchange(ref frameQueued, 0);
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => await ProcessPendingFrameAsync()));
    }

    private async Task ProcessPendingFrameAsync()
    {
        try
        {
            CameraFrame? frame = Interlocked.Exchange(ref pendingFrame, null);
            if (frame is null || !capture.IsRunning || stopping || closing || closed) return;
            if (initialized && lastProcessedTimestamp is TimeSpan previous &&
                (frame.Timestamp <= previous || frame.Timestamp - previous > TimeSpan.FromSeconds(1)))
            {
                await StopForInterruptedFramesAsync();
                return;
            }
            lastProcessedTimestamp = frame.Timestamp;
            int currentGeneration = generation;
            if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
            {
                bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                preview.Source = bitmap;
            }
            bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Width * 4, 0);
            displayedFrame = frame;
            if (!initialized || initializing || endpointBusy) return;

            CameraMotionResult result;
            await motionGate.WaitAsync();
            try
            {
                if (generation != currentGeneration || !initialized || stopping) return;
                result = await Task.Run(() => motion.Track(frame.Gray, frame.Width, frame.Height));
            }
            finally { motionGate.Release(); }
            if (generation == currentGeneration && capture.IsRunning && !stopping && !closing)
                ShowResult(result);
        }
        catch (Exception error)
        {
            if (!closed && !closing && !stopping)
            {
                initialized = false;
                SetTracking(false);
                status.Text = T("Tracking stopped. Select a new target to recalibrate. ", "跟踪已停止，请重新选择目标校准。 ") + error.Message;
                UpdateControls();
            }
        }
        finally
        {
            Interlocked.Exchange(ref frameQueued, 0);
            if (Volatile.Read(ref pendingFrame) is not null && capture.IsRunning && !stopping && !closed && !closing)
                QueueFrame();
        }
    }

    private void ShowResult(CameraMotionResult result)
    {
        lastState = result.State;
        if (result.State == CameraMotionState.Lost)
        {
            status.Text = T("Tracking lost — last estimate held. Select the background target again and recalibrate.", "跟踪丢失，已保持最后的估计值。请重新选择背景目标并校准。");
        }
        else if (result.State == CameraMotionState.Tracking)
        {
            if (calibrated && result.Progress is double progress && double.IsFinite(progress))
            {
                progress = Math.Clamp(progress, 0, 1);
                estimate.Text = T($"Estimated unfolding: {progress:P0}", $"估计展开程度：{progress:P0}");
                status.Text = T($"Tracking background · confidence {result.Confidence:P0}. Only move the lid.", $"正在跟踪背景 · 置信度 {result.Confidence:P0}。请仅移动屏幕盖。");
                ProgressChanged?.Invoke(progress);
            }
            else
            {
                status.Text = calibrationNotice ?? (foldedCaptured
                    ? T("Folded endpoint captured. Move only the lid, keep the target visible, then capture unfolded.", "已记录折叠端点。仅移动屏幕盖，保持目标可见，再记录展开端点。")
                    : T("Target selected. Hold the desired folded position, then capture folded.", "已选择目标。保持希望对应折叠状态的位置，再记录折叠端点。"));
            }
        }
        UpdateControls();
    }

    private Rect ImageBounds()
    {
        if (displayedFrame is null) return Rect.Empty;
        double scale = Math.Min(previewStage.ActualWidth / displayedFrame.Width, previewStage.ActualHeight / displayedFrame.Height);
        double width = displayedFrame.Width * scale, height = displayedFrame.Height * scale;
        return new Rect((previewStage.ActualWidth - width) / 2, (previewStage.ActualHeight - height) / 2, width, height);
    }

    private void BeginSelection(object sender, MouseButtonEventArgs args)
    {
        if (!capture.IsRunning || stopping || starting || initializing || endpointBusy || displayedFrame is null) return;
        Rect bounds = ImageBounds();
        Point point = args.GetPosition(overlay);
        if (bounds.IsEmpty || !bounds.Contains(point)) return;
        dragging = true; dragStart = point; selectedPreview = new Rect(point, point);
        selection.Visibility = Visibility.Visible;
        DrawSelection(); overlay.CaptureMouse(); args.Handled = true;
    }

    private void MoveSelection(object sender, MouseEventArgs args)
    {
        if (!dragging) return;
        Rect bounds = ImageBounds();
        if (bounds.IsEmpty) { CancelSelection(); return; }
        Point point = args.GetPosition(overlay);
        point.X = Math.Clamp(point.X, bounds.Left, bounds.Right);
        point.Y = Math.Clamp(point.Y, bounds.Top, bounds.Bottom);
        selectedPreview = new Rect(dragStart, point);
        DrawSelection(); args.Handled = true;
    }

    private async void EndSelection(object sender, MouseButtonEventArgs args)
    {
        if (!dragging) return;
        MoveSelection(sender, args);
        dragging = false; overlay.ReleaseMouseCapture(); args.Handled = true;
        CameraFrame? frame = displayedFrame;
        Rect bounds = ImageBounds();
        if (frame is null || bounds.IsEmpty) { CancelSelection(); return; }
        int x = (int)Math.Floor((selectedPreview.Left - bounds.Left) * frame.Width / bounds.Width);
        int y = (int)Math.Floor((selectedPreview.Top - bounds.Top) * frame.Height / bounds.Height);
        int right = (int)Math.Ceiling((selectedPreview.Right - bounds.Left) * frame.Width / bounds.Width);
        int bottom = (int)Math.Ceiling((selectedPreview.Bottom - bounds.Top) * frame.Height / bounds.Height);
        x = Math.Clamp(x, 0, frame.Width - 1); y = Math.Clamp(y, 0, frame.Height - 1);
        right = Math.Clamp(right, x, frame.Width); bottom = Math.Clamp(bottom, y, frame.Height);
        if (right - x < 45 || bottom - y < 35)
        {
            selection.Visibility = Visibility.Collapsed;
            status.Text = T("Choose a larger textured background patch (at least 45 × 35 camera pixels).", "请选择更大的纹理背景区域（至少 45 × 35 个摄像头像素）。");
            return;
        }
        try { await InitializeTargetAsync(frame, new CameraRegion(x, y, right - x, bottom - y)); }
        catch (Exception error) { if (!closed) status.Text = T("Could not select the target: ", "无法选择目标：") + error.Message; }
    }

    private void DrawSelection()
    {
        Canvas.SetLeft(selection, selectedPreview.Left); Canvas.SetTop(selection, selectedPreview.Top);
        selection.Width = selectedPreview.Width; selection.Height = selectedPreview.Height;
    }

    private void CancelSelection()
    {
        dragging = false; overlay.ReleaseMouseCapture(); selection.Visibility = Visibility.Collapsed;
    }

    private async Task InitializeTargetAsync(CameraFrame frame, CameraRegion region)
    {
        int currentGeneration = ++generation;
        initializing = true; initialized = foldedCaptured = calibrated = false;
        calibrationNotice = null;
        SetTracking(false);
        lastState = CameraMotionState.NotInitialized;
        estimate.Text = T("Estimated unfolding: not calibrated", "估计展开程度：尚未校准");
        status.Text = T("Checking the selected background texture…", "正在检查所选背景纹理……");
        UpdateControls();
        try
        {
            await motionGate.WaitAsync();
            (bool success, string message, CameraMotionResult result) answer;
            try
            {
                if (generation != currentGeneration || stopping || closing || !capture.IsRunning) return;
                answer = await Task.Run(() =>
                {
                    bool success = motion.Initialize(frame.Gray, frame.Width, frame.Height, region, out string message);
                    return (success, message, motion.LastResult);
                });
            }
            finally { motionGate.Release(); }
            if (generation != currentGeneration || stopping || !capture.IsRunning) return;
            initialized = answer.success;
            if (initialized) ShowResult(answer.result);
            else
            {
                selection.Visibility = Visibility.Collapsed;
                status.Text = T("Choose a clearer, more textured stationary patch. ", "请选择更清晰、更有纹理的固定背景。 ") + answer.message;
            }
        }
        finally { initializing = false; if (!closed) UpdateControls(); }
    }

    private async Task CaptureEndpointAsync(bool isUnfolded)
    {
        if (!initialized || lastState != CameraMotionState.Tracking || !capture.IsRunning || endpointBusy || initializing || stopping || (isUnfolded && !foldedCaptured)) return;
        endpointBusy = true; UpdateControls();
        int currentGeneration = generation;
        try
        {
            await motionGate.WaitAsync();
            (bool success, string message, bool complete, CameraMotionResult result) answer;
            try
            {
                if (generation != currentGeneration || stopping || closing || !capture.IsRunning) return;
                answer = await Task.Run(() =>
                {
                    bool success = motion.CaptureEndpoint(isUnfolded, out string message);
                    return (success, message, motion.IsCalibrated, motion.LastResult);
                });
            }
            finally { motionGate.Release(); }
            if (generation != currentGeneration || !capture.IsRunning || stopping) return;
            if (!answer.success)
            {
                calibrationNotice = T("Endpoint not accepted. Keep the target visible and move the lid farther from the other endpoint. ", "未接受该端点。请保持目标可见，并将屏幕盖移到离另一个端点更远的位置。 ") + answer.message;
                status.Text = calibrationNotice;
                return;
            }
            calibrationNotice = null;
            if (!isUnfolded) foldedCaptured = true;
            calibrated = answer.complete;
            selection.Visibility = Visibility.Collapsed;
            SetTracking(calibrated);
            ShowResult(answer.result);
        }
        finally { endpointBusy = false; if (!closed) UpdateControls(); }
    }

    private async Task ResetCalibrationAsync()
    {
        int currentGeneration = ++generation;
        initialized = foldedCaptured = calibrated = false;
        calibrationNotice = null;
        SetTracking(false); CancelSelection();
        lastState = CameraMotionState.NotInitialized;
        await motionGate.WaitAsync();
        try
        {
            if (generation != currentGeneration || stopping || closing) return;
            motion.Reset();
        }
        finally { motionGate.Release(); }
        estimate.Text = T("Estimated unfolding: not calibrated", "估计展开程度：尚未校准");
        status.Text = T("Calibration cleared. Set the folded position and select a background patch again.", "校准已清除。请放到折叠位置并重新选择背景。");
        UpdateControls();
    }

    private void SetTracking(bool value)
    {
        if (trackingActive == value) return;
        trackingActive = value;
        TrackingChanged?.Invoke(value);
    }

    private void UpdateControls()
    {
        if (closed) return;
        bool running = capture.IsRunning;
        bool busy = starting || stopping || closing;
        cameras.IsEnabled = !running && !busy && !enumerating;
        refresh.IsEnabled = !running && !busy && !enumerating;
        start.IsEnabled = !running && !busy && !enumerating && cameras.SelectedItem is CameraDevice;
        stop.IsEnabled = (running || starting) && !stopping && !closing;
        folded.IsEnabled = running && initialized && lastState == CameraMotionState.Tracking && !busy && !initializing && !endpointBusy;
        unfolded.IsEnabled = folded.IsEnabled && foldedCaptured;
        reset.IsEnabled = running && (initialized || calibrated) && !busy && !initializing && !endpointBusy;
        indicator.Text = stopping ? T("Stopping camera…", "正在停止摄像头……")
            : starting ? T("Starting camera…", "正在启动摄像头……")
            : running ? T("● CAMERA ON — local processing", "● 摄像头已开启 — 本机处理")
            : T("Camera off", "摄像头已关闭");
        indicator.Foreground = running || starting || stopping ? Brushes.DarkRed : Brushes.DimGray;
        Title = stopping ? T("Stopping camera… · Bloom experiment", "正在停止摄像头…… · Bloom 实验") : running || starting
            ? T("● Camera ON · Bloom experiment", "● 摄像头开启 · Bloom 实验")
            : T("Camera experiment · Bloom Native", "摄像头实验 · Bloom Native");
    }

    private void OnCameraFailed(string message)
    {
        int currentGeneration = Volatile.Read(ref cameraGeneration);
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (closed || closing || currentGeneration != cameraGeneration) return;
            try { await StopAsync(); }
            catch (Exception error) { message += " " + error.Message; }
            if (!closed) status.Text = (capture.IsRunning ? T("Camera error: ", "摄像头错误：") : T("Camera stopped after an error: ", "摄像头因错误已停止：")) + message;
        }));
    }

    private async Task CheckFrameWatchdogAsync()
    {
        if (starting || stopping || closing || closed) return;
        if (!capture.IsRunning)
        {
            await StopForInterruptedFramesAsync();
            return;
        }
        long last = Interlocked.Read(ref lastFrameTick);
        if (last != 0 && Stopwatch.GetElapsedTime(last) > TimeSpan.FromSeconds(2))
            await StopForInterruptedFramesAsync();
    }

    private async Task StopForInterruptedFramesAsync()
    {
        try
        {
            await StopAsync();
            if (!closed && !closing)
                status.Text = T("Camera frames stopped or were interrupted. The last unfolding estimate was held. Start the camera and recalibrate to resume.", "摄像头画面已停止或中断，已保持最后的展开估计。请重新启动摄像头并校准。");
        }
        catch (Exception error)
        {
            if (!closed) status.Text = T("Camera frames stopped; could not finish releasing the camera. ", "摄像头画面已停止，但尚未成功释放摄像头。 ") + error.Message;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (closeAllowed) return;
        args.Cancel = true;
        if (closing) return;
        closing = true;
        lifetime.Cancel();
        UpdateControls();
        try
        {
            await StopAsync();
            capture.FrameArrived -= OnFrame;
            capture.Failed -= OnCameraFailed;
            await capture.DisposeAsync();
            closed = true; closeAllowed = true;
            startCancellation?.Dispose(); lifetime.Dispose();
            ProgressChanged = null; TrackingChanged = null;
            Close();
        }
        catch (Exception error)
        {
            closing = false;
            status.Text = T("Could not finish stopping the camera. Try Stop camera again. ", "尚未成功停止摄像头，请再次点击“停止摄像头”。 ") + error.Message;
            UpdateControls();
        }
    }
}
