using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FormsScreen = System.Windows.Forms.Screen;

namespace BloomNative.Windows;

/// <summary>Hosts the Windows .scr fullscreen and Control Panel preview modes.</summary>
public static class ScreenSaver
{
    /// <summary>
    /// Create and show the saver windows before calling Application.Run().
    /// A zero parent selects all monitors; otherwise the window is embedded in the preview host.
    /// </summary>
    public static Window[] CreateWindows(string videoPath, IntPtr previewParent)
    {
        if (Application.Current == null)
            throw new InvalidOperationException("Create a WPF Application before starting the screen saver.");
        if (previewParent != IntPtr.Zero && !IsWindow(previewParent))
            throw new ArgumentException("The screen saver preview window no longer exists.", nameof(previewParent));

        bool exiting = false;
        Action exit = () =>
        {
            if (exiting) return;
            exiting = true;
            Application.Current?.Shutdown();
        };

        if (previewParent != IntPtr.Zero)
            return new[] { CreatePreview(videoPath, previewParent, exit) };

        using var dpi = new DpiContext(new IntPtr(-4)); // Per-monitor V2: monitor bounds are physical pixels.
        GetCursorPos(out NativePoint initialPointer);
        var windows = new List<Window>();
        foreach (FormsScreen screen in FormsScreen.AllScreens)
        {
            var view = new BloomView(videoPath);
            var window = MakeWindow(view);
            window.Topmost = true;
            window.Cursor = Cursors.None;
            window.ShowActivated = screen.Primary;
            var bounds = screen.Bounds;
            window.SourceInitialized += (_, _) =>
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                PositionFullscreen(handle, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            };
            window.StateChanged += (_, _) => view.SetSuspended(window.WindowState == WindowState.Minimized);
            window.PreviewKeyDown += (_, args) => { args.Handled = true; exit(); };
            window.PreviewMouseDown += (_, args) => { args.Handled = true; exit(); };
            window.PreviewMouseWheel += (_, args) => { args.Handled = true; exit(); };
            window.PreviewMouseMove += (_, _) =>
            {
                // WPF emits MouseMove when a window appears under an idle pointer. Compare
                // physical screen coordinates so that only real movement dismisses the saver.
                if (GetCursorPos(out NativePoint current) &&
                    (Math.Abs((long)current.X - initialPointer.X) > 6 || Math.Abs((long)current.Y - initialPointer.Y) > 6))
                    exit();
            };
            window.Closed += (_, _) => exit();
            windows.Add(window);
            window.Show();
            // Show can apply WPF's initial size after SourceInitialized. Reassert the physical
            // monitor rectangle, including negative origins and monitors with different DPI.
            PositionFullscreen(new WindowInteropHelper(window).Handle, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
        foreach (Window window in windows)
        {
            if (window.ShowActivated)
            {
                window.Activate();
                window.Focus();
                break;
            }
        }
        return windows.ToArray();
    }

    private static Window MakeWindow(BloomView view)
    {
        var contents = new Grid();
        contents.Children.Add(view);
        var window = new Window
        {
            Title = "Bloom Native Screen Saver",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = Brushes.Black,
            Content = contents,
            Width = 640,
            Height = 360
        };
        view.PlaybackFailed += message =>
        {
            contents.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(200, 20, 25, 35)),
                Padding = new Thickness(20),
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 16
            });
        };
        view.SetProgress(1);
        window.Closed += (_, _) => view.Dispose();
        return window;
    }

    private static Window CreatePreview(string videoPath, IntPtr parent, Action exit)
    {
        IntPtr parentContext = GetWindowDpiAwarenessContext(parent);
        if (parentContext == IntPtr.Zero)
            throw new Win32Exception("Could not determine the screen saver preview host's DPI mode.");
        // SetParent with different DPI awareness can reset the child process's DPI mode.
        // Create the preview HWND in the host's own context and restore the thread afterward.
        using var dpi = new DpiContext(parentContext);
        var view = new BloomView(videoPath);
        var window = MakeWindow(view);
        window.ShowActivated = false;
        window.Focusable = false;
        IntPtr handle = IntPtr.Zero;
        NativeRect previousBounds = default;
        Action refreshSuspended = () => view.SetSuspended(!IsWindowVisible(parent) || window.WindowState == WindowState.Minimized);
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        window.SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(window).Handle;
            if (!AreDpiAwarenessContextsEqual(parentContext, GetWindowDpiAwarenessContext(handle)))
                throw new InvalidOperationException("The screen saver preview and host have incompatible DPI modes.");
            long style = GetWindowLongPointer(handle, GwlStyle).ToInt64();
            SetWindowLongPointer(handle, GwlStyle, new IntPtr((style & ~WsPopup) | WsChild));
            long extendedStyle = GetWindowLongPointer(handle, GwlExStyle).ToInt64();
            SetWindowLongPointer(handle, GwlExStyle, new IntPtr((extendedStyle & ~WsExAppWindow) | WsExToolWindow | WsExNoActivate));
            SetParent(handle, parent);
            if (GetParent(handle) != parent)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not attach the screen saver preview.");
            if (GetClientRect(parent, out NativeRect bounds))
            {
                previousBounds = bounds;
                ResizePreview(handle, bounds);
            }
        };
        timer.Tick += (_, _) =>
        {
            if (!IsWindow(parent))
            {
                timer.Stop();
                exit();
                return;
            }
            using var previewDpi = new DpiContext(parentContext);
            refreshSuspended();
            if (GetClientRect(parent, out NativeRect bounds) &&
                (bounds.Right != previousBounds.Right || bounds.Bottom != previousBounds.Bottom))
            {
                previousBounds = bounds;
                ResizePreview(handle, bounds);
            }
        };
        window.StateChanged += (_, _) => refreshSuspended();
        window.Closed += (_, _) => { timer.Stop(); exit(); };
        refreshSuspended();
        window.Show();
        if (GetClientRect(parent, out NativeRect initialBounds))
        {
            previousBounds = initialBounds;
            ResizePreview(handle, initialBounds);
        }
        timer.Start();
        return window;
    }

    private static void ResizePreview(IntPtr handle, NativeRect bounds)
    {
        if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, Math.Max(1, bounds.Right - bounds.Left), Math.Max(1, bounds.Bottom - bounds.Top),
            SwpNoActivate | SwpNoZOrder | SwpFrameChanged))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not size the screen saver preview.");
    }

    private static void PositionFullscreen(IntPtr handle, int x, int y, int width, int height)
    {
        if (!SetWindowPos(handle, HwndTopmost, x, y, width, height, SwpNoActivate))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not size the fullscreen screen saver.");
    }

    private sealed class DpiContext : IDisposable
    {
        private readonly IntPtr previous;
        public DpiContext(IntPtr context)
        {
            previous = SetThreadDpiAwarenessContext(context);
            if (previous == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not select the screen saver's display DPI mode.");
        }
        public void Dispose() => SetThreadDpiAwarenessContext(previous);
    }

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly IntPtr HwndTopmost = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr handle, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr child);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLong64(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr handle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLong64(IntPtr handle, int index, IntPtr value);

    private static IntPtr GetWindowLongPointer(IntPtr handle, int index) =>
        IntPtr.Size == 8 ? GetWindowLong64(handle, index) : new IntPtr(GetWindowLong32(handle, index));

    private static IntPtr SetWindowLongPointer(IntPtr handle, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLong64(handle, index, value) : new IntPtr(SetWindowLong32(handle, index, unchecked((int)value.ToInt64())));
}
