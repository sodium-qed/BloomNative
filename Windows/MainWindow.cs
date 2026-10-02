using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BloomNative.Windows;

internal sealed class MainWindow : Window
{
    private readonly Settings settings = Settings.Load();
    private readonly Forms.NotifyIcon tray;
    private readonly DesktopHost desktop;
    private readonly DispatcherTimer maintenance;
    private readonly CancellationTokenSource lifetime = new();
    private BloomView? preview;
    private Border previewContainer = null!;
    private TextBlock status = null!;
    private TextBlock poseLabel = null!;
    private CheckBox enabled = null!;
    private Button download = null!;
    private Button import = null!;
    private Button browserDownload = null!;
    private Button replay = null!;
    private Button saver = null!;
    private CameraTrackingWindow? cameraWindow;
    private bool cameraTracking;
    private Slider slider = null!;
    private bool ready, quitting, downloading, rebuilding, locked, sleeping, displayOff, lidClosed, pendingReplay;
    private string? artworkError;
    private uint? lastLid;
    private HwndSource? hwndSource;
    private IntPtr displayRegistration, lidRegistration;
    private static readonly Guid DisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    private static readonly Guid LidState = new("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");
    private bool Paused => locked || sleeping || displayOff || lidClosed;
    private string T(string en, string zh) => settings.Language == "zh" ? zh : en;

    public MainWindow()
    {
        Title = "Bloom Native · Windows 0.1.3";
        Width = 840; Height = 790; MinWidth = 620; MinHeight = 580;
        Background = new SolidColorBrush(Color.FromRgb(242, 245, 251));
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        desktop = new DesktopHost(Artwork.VideoPath, ReportDesktopError);
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Bloom Native", Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowControls);
        BuildTray();
        BuildUi();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        IsVisibleChanged += (_, _) => UpdatePaused();
        StateChanged += (_, _) => UpdatePaused();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        maintenance = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        maintenance.Tick += (_, _) => { if (desktop.IsRunning && !Paused && !desktop.IsHealthy()) ReattachDesktop(); };
        maintenance.Start();
        Loaded += async (_, _) =>
        {
            try { ready = await System.Threading.Tasks.Task.Run(() => Artwork.Verify(Artwork.VideoPath)); }
            catch (Exception ex) { SetStatus(ex.Message); }
            if (quitting) return;
            UpdateReady();
        };
    }
    private void BuildTray()
    {
        tray.ContextMenuStrip?.Dispose();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(T("Open controls", "打开控制面板"), null, (_, _) => Dispatcher.BeginInvoke(ShowControls));
        menu.Items.Add(T("Replay unfolding", "重播展开动画"), null, (_, _) => Dispatcher.BeginInvoke(Replay));
        menu.Items.Add(T("Stop wallpaper", "停止动态壁纸"), null, (_, _) => Dispatcher.BeginInvoke(() => enabled.IsChecked = false));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(T("Quit", "退出"), null, (_, _) => Dispatcher.BeginInvoke(Quit));
        tray.ContextMenuStrip = menu;
    }
    private Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 10, 8) };
        button.Click += (_, _) => action();
        return button;
    }
    private void BuildUi()
    {
        rebuilding = true;
        preview?.Dispose(); preview = null;
        bool active = desktop.IsRunning;
        var body = new StackPanel { Margin = new Thickness(26) };
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var header = new DockPanel();
        var language = new ComboBox { Width = 146, Margin = new Thickness(12, 0, 0, 12), ItemsSource = new[] { "English", "简体中文" }, SelectedIndex = settings.Language == "zh" ? 1 : 0 };
        DockPanel.SetDock(language, Dock.Right); header.Children.Add(language);
        header.Children.Add(new TextBlock { Text = "Bloom Native", FontSize = 30, FontWeight = FontWeights.SemiBold }); body.Children.Add(header);
        body.Children.Add(new TextBlock { Text = T("The original Bloom animation, on your Windows desktop.", "在 Windows 桌面欣赏原版 Bloom 展开动画。"), Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 18) });
        previewContainer = new Border { Height = 260, Background = new SolidColorBrush(Color.FromRgb(210, 222, 238)), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = new TextBlock { Text = T("Download the original animation to get started.", "下载原版动画以开始使用。"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        body.Children.Add(previewContainer);
        var artworkButtons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        download = Button(T("Download original artwork", "下载原版动画"), () => _ = Download());
        import = Button(T("Use a local copy…", "选择本地副本…"), Import);
        browserDownload = Button(T("Open video in browser", "在浏览器中打开视频"), OpenArtworkInBrowser);
        artworkButtons.Children.Add(download); artworkButtons.Children.Add(browserDownload); artworkButtons.Children.Add(import); body.Children.Add(artworkButtons);
        body.Children.Add(new TextBlock { Text = T("Artwork: Microsoft / Six N. Five. Downloaded from the creator and verified locally.", "动画版权归 Microsoft / Six N. Five。直接从原作者下载并在本机校验。"), FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        enabled = new CheckBox { Content = T("Dynamic desktop wallpaper", "启用动态桌面壁纸"), IsChecked = active, Margin = new Thickness(0, 20, 0, 14), FontWeight = FontWeights.SemiBold };
        enabled.Checked += (_, _) => ToggleDesktop(); enabled.Unchecked += (_, _) => ToggleDesktop(); body.Children.Add(enabled);
        poseLabel = new TextBlock(); body.Children.Add(poseLabel);
        slider = new Slider { Minimum = 0, Maximum = 1, Value = settings.Progress, SmallChange = .01, LargeChange = .1, Margin = new Thickness(0, 8, 0, 12), IsSnapToTickEnabled = false };
        slider.ValueChanged += (_, _) => { settings.Progress = slider.Value; UpdatePose(); preview?.SetProgress(settings.Progress); desktop.SetProgress(settings.Progress); };
        body.Children.Add(slider); UpdatePose();
        var options = new WrapPanel();
        var breathe = new CheckBox { Content = T("Gentle breathing", "轻微呼吸效果"), IsChecked = settings.Breathe, Margin = new Thickness(0, 0, 24, 12) };
        breathe.Click += (_, _) => { settings.Breathe = breathe.IsChecked == true; ApplyBreathing(); SaveSettings(); };
        var wake = new CheckBox { Content = T("Replay on wake / lid open", "唤醒或开盖时重播"), IsChecked = settings.ReplayOnWake, Margin = new Thickness(0, 0, 0, 12) };
        wake.Click += (_, _) => { settings.ReplayOnWake = wake.IsChecked == true; SaveSettings(); };
        options.Children.Add(breathe); options.Children.Add(wake); body.Children.Add(options);
        body.Children.Add(new TextBlock { Text = T("Manual unfolding works on any PC. Experimental webcam tracking estimates lid movement against a stationary background; it does not measure a hinge angle.", "任何电脑都可手动调节展开程度。实验性摄像头追踪通过静止背景估计屏幕运动，并非测量铰链角度。"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 12, Margin = new Thickness(0, 0, 0, 14) });
        var actions = new WrapPanel();
        replay = Button(T("Replay unfolding", "重播展开动画"), Replay);
        saver = Button(T("Preview screen saver", "预览屏幕保护程序"), PreviewSaver);
        actions.Children.Add(replay); actions.Children.Add(saver);
        actions.Children.Add(Button(T("Webcam tracking (experimental)…", "摄像头追踪（实验性）…"), OpenCameraTracking));
        actions.Children.Add(Button(T("Hide to tray", "隐藏到托盘"), Hide));
        actions.Children.Add(Button(T("Copy diagnostics", "复制诊断信息"), CopyDiagnostics));
        actions.Children.Add(Button(T("Quit", "退出"), Quit)); body.Children.Add(actions);
        status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 6, 0, 0) }; body.Children.Add(status);
        language.SelectionChanged += (_, _) => { if (rebuilding) return; settings.Language = language.SelectedIndex == 1 ? "zh" : "en"; SaveSettings(); BuildTray(); BuildUi(); };
        rebuilding = false;
        UpdateReady();
    }
    private void UpdatePose() => poseLabel.Text = T($"Unfolding: {settings.Progress:P0}", $"展开程度：{settings.Progress:P0}");
    private void UpdateReady()
    {
        enabled.IsEnabled = ready; slider.IsEnabled = ready && !cameraTracking; replay.IsEnabled = ready && !cameraTracking; saver.IsEnabled = ready;
        download.IsEnabled = !ready && !downloading; import.IsEnabled = !ready && !downloading;
        browserDownload.IsEnabled = !ready && !downloading;
        if (ready && preview == null)
        {
            preview = new BloomView(Artwork.VideoPath) { Breathe = settings.Breathe && !cameraTracking };
            preview.PlaybackFailed += error => Dispatcher.BeginInvoke(() => SetStatus(T("Playback failed: ", "播放失败：") + error));
            previewContainer.Child = preview; preview.SetProgress(settings.Progress); UpdatePaused();
        }
        SetStatus(ready ? T("Ready. Closing this window keeps the app in the system tray. Use Quit to stop it.", "已就绪。关闭此窗口后程序会留在托盘；请选择“退出”以停止。") : artworkError ?? T("The artwork is not bundled. Download it above, or select a matching local BloomOriginal.mp4.", "安装包不含动画素材。请下载，或选择匹配的 BloomOriginal.mp4。"));
    }
    private async System.Threading.Tasks.Task Download()
    {
        if (downloading) return;
        artworkError = null;
        downloading = true; UpdateReady(); SetStatus(T("Downloading from sixnfive.com…", "正在从 sixnfive.com 下载…"));
        try
        {
            await Artwork.DownloadAsync(new Progress<int>(p => SetStatus(T($"Downloading original artwork… {p}%", $"正在下载原版动画… {p}%"))), lifetime.Token);
            ready = true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
        {
            artworkError = T($"The artwork website refused this download (HTTP {(int)ex.StatusCode.Value}). Choose Open video in browser, save the video, then select it with Use a local copy…",
                $"动画网站拒绝了本次下载（HTTP {(int)ex.StatusCode.Value}）。请选择“在浏览器中打开视频”并保存视频，然后使用“选择本地副本…”导入。");
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            artworkError = T("The download timed out. Try again, or open the video in your browser and import a saved copy.", "下载超时。请重试，或在浏览器中保存视频后导入本地副本。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { artworkError = ex.Message; }
        finally { downloading = false; if (!quitting) UpdateReady(); }
    }
    private void OpenArtworkInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Artwork.SourceUrl) { UseShellExecute = true });
            artworkError = T("Save the video in your browser, then choose Use a local copy… to import it. The original checksum will still be verified.", "请在浏览器中保存视频，然后选择“选择本地副本…”导入。程序仍会校验原版视频的哈希值。");
            SetStatus(artworkError);
        }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = "Original Bloom animation (*.mp4)|*.mp4", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { Artwork.Import(dialog.FileName); ready = true; UpdateReady(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Bloom Native", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void CopyDiagnostics()
    {
        try
        {
            string report = "Bloom Native Windows 0.1.3\n" +
                $"Artwork verified: {ready}\nWallpaper enabled: {enabled.IsChecked == true}\nPaused: {Paused}\n" +
                $"Camera running: {cameraWindow?.IsCameraRunning == true}\nCamera controls animation: {cameraTracking}\n" +
                desktop.GetDiagnostics();
            Clipboard.SetText(report);
            SetStatus(T("Diagnostics copied. Paste them into your support conversation if the wallpaper is still missing.", "诊断信息已复制。如果桌面仍未显示动画，请将信息粘贴到支持对话中。"));
        }
        catch (Exception ex) { SetStatus(T("Could not copy diagnostics: ", "无法复制诊断信息：") + ex.Message); }
    }
    private void ToggleDesktop()
    {
        if (rebuilding) return;
        if (enabled.IsChecked == true && ready)
        {
            try { desktop.Start(settings.Progress, settings.Breathe && !cameraTracking); UpdatePaused(); if (desktop.IsRunning) SetStatus(T("Wallpaper is running behind your desktop icons.", "动态壁纸正在桌面图标下方运行。")); }
            catch (Exception ex) { ReportDesktopError(ex.Message); }
        }
        else { desktop.Stop(); SaveSettings(); SetStatus(T("Wallpaper stopped. Your Windows wallpaper is unchanged.", "动态壁纸已停止，原有 Windows 壁纸未被更改。")); }
    }
    private void ReportDesktopError(string error) => Dispatcher.BeginInvoke(() =>
    {
        desktop.Stop(); enabled.IsChecked = false;
        SetStatus(T("Desktop wallpaper could not start: ", "无法启动动态壁纸：") + error + T(" You can still use the preview and screen saver.", "您仍可使用预览和屏幕保护程序。"));
    });
    private void ReattachDesktop()
    {
        if (quitting || !desktop.IsRunning) return;
        desktop.Stop();
        try { desktop.Start(settings.Progress, settings.Breathe && !cameraTracking); UpdatePaused(); }
        catch (Exception ex) { ReportDesktopError(ex.Message); }
    }
    private void Replay()
    {
        if (!ready || cameraTracking) return;
        if (Paused) { pendingReplay = true; return; }
        preview?.ReplayTo(settings.Progress); desktop.ReplayTo(settings.Progress);
    }
    private void UpdatePaused()
    {
        if (quitting) return;
        if (Paused && cameraWindow != null) _ = StopCameraForPause();
        preview?.SetSuspended(Paused || !IsVisible || WindowState == WindowState.Minimized); desktop.SetSuspended(Paused);
        if (!Paused && pendingReplay) { pendingReplay = false; Replay(); }
    }
    private void WakeReplay() { if (settings.ReplayOnWake && !cameraTracking) pendingReplay = true; UpdatePaused(); }
    private void ApplyBreathing()
    {
        bool breathe = settings.Breathe && !cameraTracking;
        if (preview != null) preview.Breathe = breathe;
        desktop.SetBreathe(breathe);
    }
    private void OpenCameraTracking()
    {
        if (Paused || quitting) return;
        if (cameraWindow == null)
        {
            // Keep a separate taskbar window visible even when controls hide to tray.
            // Constructing this window never opens a camera; its Start button does.
            var window = new CameraTrackingWindow(settings.Language == "zh");
            cameraWindow = window;
            window.ProgressChanged += progress =>
            {
                if (quitting || Paused || !double.IsFinite(progress)) return;
                settings.Progress = Math.Clamp(progress, 0, 1);
                slider.Value = settings.Progress;
                UpdatePose();
                preview?.SetProgress(settings.Progress);
                desktop.SetProgress(settings.Progress);
            };
            window.TrackingChanged += active =>
            {
                cameraTracking = active;
                pendingReplay = false;
                slider.IsEnabled = ready && !active;
                replay.IsEnabled = ready && !active;
                ApplyBreathing();
            };
            window.Closed += (_, _) =>
            {
                if (!ReferenceEquals(cameraWindow, window)) return;
                cameraWindow = null;
                cameraTracking = false;
                if (!quitting) { UpdateReady(); ApplyBreathing(); SaveSettings(); }
            };
        }
        cameraWindow.Show();
        cameraWindow.WindowState = WindowState.Normal;
        cameraWindow.Activate();
    }
    private async System.Threading.Tasks.Task StopCameraForPause()
    {
        try { if (cameraWindow != null) await cameraWindow.StopAsync(); }
        catch (Exception ex) { SetStatus(T("Could not stop camera cleanly: ", "无法正常停止摄像头：") + ex.Message); }
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
        if (e.Reason == SessionSwitchReason.SessionUnlock) { locked = false; WakeReplay(); }
        UpdatePaused();
    });
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.Mode == PowerModes.Suspend) sleeping = true;
        if (e.Mode == PowerModes.Resume) { sleeping = false; WakeReplay(); }
        UpdatePaused();
    });
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ReattachDesktop);
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        hwndSource = HwndSource.FromHwnd(hwnd); hwndSource?.AddHook(WindowMessage);
        Guid display = DisplayState, lid = LidState;
        displayRegistration = RegisterPowerSettingNotification(hwnd, ref display, 0);
        lidRegistration = RegisterPowerSettingNotification(hwnd, ref lid, 0);
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
        if (message == WM_POWERBROADCAST && wParam.ToInt64() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            Guid setting = Marshal.PtrToStructure<Guid>(lParam);
            int length = Marshal.ReadInt32(lParam, 16);
            if (length == 4)
            {
                uint value = unchecked((uint)Marshal.ReadInt32(lParam, 20));
                if (setting == DisplayState) { bool wasOff = displayOff; displayOff = value == 0; if (wasOff && !displayOff) WakeReplay(); }
                if (setting == LidState) { lidClosed = value == 0; if (lastLid == 0 && value != 0) WakeReplay(); lastLid = value; }
                UpdatePaused();
            }
        }
        return IntPtr.Zero;
    }
    private void PreviewSaver()
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "/s") { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void SaveSettings()
    {
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus(T("Could not save preferences: ", "无法保存设置：") + ex.Message); }
    }
    private void SetStatus(string message) { if (!quitting && status != null) status.Text = message; }
    private void ShowControls() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (quitting) return;
        e.Cancel = true; SaveSettings(); Hide();
    }
    private async void Quit()
    {
        if (quitting) return;
        SaveSettings(); quitting = true; lifetime.Cancel(); maintenance.Stop();
        await StopCameraForPause();
        SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.PowerModeChanged -= OnPowerModeChanged; SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (displayRegistration != IntPtr.Zero) UnregisterPowerSettingNotification(displayRegistration);
        if (lidRegistration != IntPtr.Zero) UnregisterPowerSettingNotification(lidRegistration);
        hwndSource?.RemoveHook(WindowMessage);
        desktop.Dispose(); preview?.Dispose(); tray.Visible = false; tray.Dispose(); lifetime.Dispose();
        Application.Current.Shutdown();
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
