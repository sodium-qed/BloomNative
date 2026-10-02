using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Rectangle = System.Drawing.Rectangle;

namespace BloomNative.Windows;

/// <summary>
/// Owns the application's wallpaper windows, never the user's wallpaper setting.
/// All public methods must be called on the creating UI thread.
/// </summary>
internal sealed class DesktopHost : IDisposable
{
    private readonly string _videoPath;
    private readonly Action<string> _onError;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<Surface> _surfaces = new();
    private ShellLayer? _layer;
    private DisplayInfo[] _displays = Array.Empty<DisplayInfo>();
    private bool _suspended;
    private bool _unhealthy;
    private bool _stopping;
    private bool _disposed;
    private int _generation;

    public DesktopHost(string videoPath, Action<string> onError)
    {
        _videoPath = videoPath ?? throw new ArgumentNullException(nameof(videoPath));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
    }

    public bool IsRunning => _surfaces.Count > 0 && !_stopping;

    public void Start(double progress, bool breathe)
    {
        VerifyAccess();
        Stop();
        try
        {
            _displays = GetDisplays();
            if (_displays.Length == 0)
                throw new InvalidOperationException("Windows did not report an active display.");

            _layer = FindDesktopLayer();
            _unhealthy = false;
            foreach (DisplayInfo display in _displays)
                CreateSurface(display, Normalize(progress), breathe);
            // Initial placement can synchronously deliver WM_DPICHANGED.
            _unhealthy = false;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or COMException)
        {
            Stop();
            _onError("Could not attach Bloom to the Windows desktop. " + error.Message);
        }
    }

    public void Stop()
    {
        _dispatcher.VerifyAccess();
        if (_stopping)
            return;

        _stopping = true;
        _generation++;
        try
        {
            // Hide first, including when Explorer has unexpectedly reparented a window.
            foreach (Surface surface in _surfaces)
                if (Native.IsWindow(surface.Handle))
                    Native.ShowWindow(surface.Handle, 0);

            foreach (Surface surface in _surfaces)
            {
                surface.View.PlaybackFailed -= OnPlaybackFailed;
                surface.View.Dispose();
                if (!surface.Source.IsDisposed)
                    surface.Source.RemoveHook(WindowMessage);
                try
                {
                    surface.Window.Close();
                }
                catch (InvalidOperationException)
                {
                    // Explorer may already have destroyed its child HWNDs.
                }
            }
            _surfaces.Clear();
            _layer = null;
            _displays = Array.Empty<DisplayInfo>();
            _unhealthy = false;
        }
        finally
        {
            _stopping = false;
        }
    }

    public void SetProgress(double progress)
    {
        VerifyAccess();
        foreach (Surface surface in _surfaces)
            surface.View.SetProgress(Normalize(progress));
    }

    public void ReplayTo(double progress)
    {
        VerifyAccess();
        foreach (Surface surface in _surfaces)
            surface.View.ReplayTo(Normalize(progress));
    }

    public void SetSuspended(bool suspended)
    {
        VerifyAccess();
        _suspended = suspended;
        foreach (Surface surface in _surfaces)
            surface.View.SetSuspended(suspended);
    }

    public void SetBreathe(bool breathe)
    {
        VerifyAccess();
        foreach (Surface surface in _surfaces)
            surface.View.Breathe = breathe;
    }

    public bool IsHealthy()
    {
        VerifyAccess();
        if (!IsRunning || _unhealthy || _layer is null || !IsValidLayer(_layer))
            return false;

        try
        {
            if (_surfaces.Count != _displays.Length || !_displays.SequenceEqual(GetDisplays()))
                return false;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return false;
        }

        foreach (Surface surface in _surfaces)
        {
            if (!Native.IsWindow(surface.Handle) || Native.GetParent(surface.Handle) != _layer.Worker)
                return false;
            long style = Native.GetStyle(surface.Handle, Native.GwlStyle);
            if ((style & Native.WsChild) == 0 || (style & Native.WsPopup) != 0)
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed)
            return;
        Stop();
        _disposed = true;
    }

    private void VerifyAccess()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void CreateSurface(DisplayInfo display, double progress, bool breathe)
    {
        ShellLayer layer = _layer ?? throw new InvalidOperationException("The desktop is unavailable.");
        nint parentContext = Native.GetWindowDpiAwarenessContext(layer.Worker);
        if (parentContext == 0)
            throw new Win32Exception("Could not determine Explorer's DPI awareness.");

        // SetParent across different DPI modes can reset the entire child process's
        // awareness. Create the hidden HWND in the parent's exact context instead.
        using var dpi = new DpiContext(parentContext);
        var view = new BloomView(_videoPath) { Breathe = breathe };
        view.SetProgress(progress);
        view.SetSuspended(_suspended);
        view.PlaybackFailed += OnPlaybackFailed;

        var window = new Window
        {
            Title = "Bloom Native wallpaper",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            IsHitTestVisible = false,
            Background = Brushes.Black,
            Content = view,
            Width = display.Bounds.Width * 96.0 / display.DpiX,
            Height = display.Bounds.Height * 96.0 / display.DpiY,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        Surface? surface = null;
        try
        {
            // EnsureHandle creates an invisible HWND. It is never shown as a
            // normal application window, even when attachment fails.
            nint handle = new WindowInteropHelper(window).EnsureHandle();
            HwndSource source = HwndSource.FromHwnd(handle)
                ?? throw new InvalidOperationException("Could not create a wallpaper window.");
            surface = new Surface(window, view, source, handle);
            _surfaces.Add(surface);
            source.AddHook(WindowMessage);

            if (!Native.AreDpiAwarenessContextsEqual(parentContext, Native.GetWindowDpiAwarenessContext(handle)))
                throw new InvalidOperationException("Explorer and the wallpaper use incompatible DPI modes.");

            // SetParent does not adjust WS_CHILD/WS_POPUP itself.
            long style = Native.GetStyle(handle, Native.GwlStyle);
            Native.SetStyle(handle, Native.GwlStyle,
                (style & ~(Native.WsPopup | Native.WsCaption | Native.WsThickFrame)) | Native.WsChild);
            long extended = Native.GetStyle(handle, Native.GwlExStyle);
            Native.SetStyle(handle, Native.GwlExStyle,
                (extended & ~Native.WsExAppWindow) | Native.WsExToolWindow | Native.WsExNoActivate | Native.WsExTransparent);

            Native.SetParent(handle, layer.Worker);
            if (Native.GetParent(handle) != layer.Worker)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Explorer rejected the wallpaper window.");

            PositionSurface(surface, display.Bounds, layer.Worker);
            if (!IsValidLayer(layer))
                throw new InvalidOperationException("The Windows desktop changed during attachment.");

            window.Show();
            // WPF's Show performs its own initial sizing. Reapply the native
            // bounds afterward, in physical pixels, including negative origins.
            PositionSurface(surface, display.Bounds, layer.Worker);
        }
        catch
        {
            if (surface is null)
            {
                view.PlaybackFailed -= OnPlaybackFailed;
                view.Dispose();
                window.Close();
            }
            throw;
        }
    }

    private static void PositionSurface(Surface surface, Rectangle bounds, nint worker)
    {
        using var dpi = new DpiContext(Native.PerMonitorV2);
        var target = new Native.Rect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        int mapped = Native.MapWindowPoints(0, worker, ref target, 2);
        if (mapped == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not map the desktop coordinates.");

        if (!Native.GetClientRect(worker, out Native.Rect client) ||
            target.Left < client.Left || target.Top < client.Top ||
            target.Right > client.Right || target.Bottom > client.Bottom)
            throw new InvalidOperationException("The desktop wallpaper layer does not cover this display layout.");

        if (!Native.SetWindowPos(surface.Handle, 0, target.Left, target.Top,
                target.Right - target.Left, target.Bottom - target.Top,
                Native.SwpNoActivate | Native.SwpFrameChanged | Native.SwpNoOwnerZOrder))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not position the wallpaper window.");
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        if (message == 0x0084) // WM_NCHITTEST
        {
            handled = true;
            return -1; // HTTRANSPARENT
        }
        if (!_stopping && message is 0x0002 or 0x007E or 0x02E0) // destroy/display/DPI changes
            _unhealthy = true;
        return 0;
    }

    private void OnPlaybackFailed(string message)
    {
        if (_disposed || _stopping)
            return;
        // Defer disposal until MediaElement has finished raising its event.
        int generation = _generation;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || !IsRunning || generation != _generation)
                return;
            Stop();
            _onError(message);
        }));
    }

    private static double Normalize(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static DisplayInfo[] GetDisplays()
    {
        using var dpi = new DpiContext(Native.PerMonitorV2);
        return Forms.Screen.AllScreens.OrderBy(screen => screen.DeviceName, StringComparer.Ordinal)
            .Select(screen =>
            {
                Rectangle bounds = screen.Bounds;
                var rect = new Native.Rect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                nint monitor = Native.MonitorFromRect(ref rect, 2);
                int result = Native.GetDpiForMonitor(monitor, 0, out uint x, out uint y);
                if (result != 0 || x == 0 || y == 0)
                    throw new InvalidOperationException("Could not read a display's DPI configuration.");
                return new DisplayInfo(screen.DeviceName, bounds, x, y, screen.Primary);
            }).ToArray();
    }

    private static ShellLayer FindDesktopLayer()
    {
        nint progman = Native.FindWindow("Progman", null);
        if (progman == 0)
            throw new InvalidOperationException("Windows Explorer's desktop is unavailable.");

        // This Explorer message is undocumented and may change between Windows
        // versions. Only verified WorkerW arrangements are accepted below.
        Native.SendMessageTimeout(progman, 0x052C, 0, 0, 0x0002, 1000, out _);
        ShellLayer? layer = LocateDesktopLayer(progman);
        if (layer is null)
        {
            Native.SendMessageTimeout(progman, 0x052C, 0xD, 0, 0x0002, 1000, out _);
            Native.SendMessageTimeout(progman, 0x052C, 0xD, 1, 0x0002, 1000, out _);
            layer = LocateDesktopLayer(progman);
        }
        return layer ?? throw new InvalidOperationException(
            "Explorer did not expose a supported background layer. Your existing wallpaper has been left in place.");
    }

    private static ShellLayer? LocateDesktopLayer(nint progman)
    {
        Native.GetWindowThreadProcessId(progman, out uint explorerProcess);
        nint icons = Native.FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        if (icons != 0)
        {
            // Newer Windows 11 shells keep both the icon view and WorkerW as
            // children of Progman. Accept only a WorkerW below the icon view.
            for (nint sibling = Native.GetWindow(icons, 2); sibling != 0; sibling = Native.GetWindow(sibling, 2))
            {
                var nested = new ShellLayer(progman, sibling, progman, icons, explorerProcess, true);
                if (IsValidLayer(nested))
                    return nested;
            }
        }

        ShellLayer? found = null;
        Native.EnumWindows((top, _) =>
        {
            nint defView = Native.FindWindowEx(top, 0, "SHELLDLL_DefView", null);
            if (defView == 0)
                return true;
            // Classic desktop: the icon host precedes a separate WorkerW in
            // top-level Z-order. Do not choose an arbitrary WorkerW by name.
            for (nint sibling = Native.GetWindow(top, 2); sibling != 0; sibling = Native.GetWindow(sibling, 2))
            {
                var candidate = new ShellLayer(progman, sibling, top, defView, explorerProcess, false);
                if (!IsValidLayer(candidate))
                    continue;
                found = candidate;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    private static bool IsValidLayer(ShellLayer layer)
    {
        if (!Native.IsWindow(layer.Progman) || !Native.IsWindow(layer.Worker) ||
            !Native.IsWindow(layer.IconHost) || !Native.IsWindow(layer.IconView) ||
            !Native.IsWindowVisible(layer.Worker) ||
            !HasClass(layer.Progman, "Progman") || !HasClass(layer.Worker, "WorkerW") ||
            !HasClass(layer.IconView, "SHELLDLL_DefView") ||
            Native.GetParent(layer.IconView) != layer.IconHost ||
            Native.FindWindowEx(layer.Worker, 0, "SHELLDLL_DefView", null) != 0)
            return false;

        foreach (nint window in new[] { layer.Progman, layer.Worker, layer.IconHost, layer.IconView })
        {
            Native.GetWindowThreadProcessId(window, out uint process);
            if (process != layer.ProcessId)
                return false;
        }

        nint above;
        if (layer.Nested)
        {
            if (layer.IconHost != layer.Progman || Native.GetParent(layer.Worker) != layer.Progman)
                return false;
            above = layer.IconView;
        }
        else
        {
            // GetParent also returns an owner for top-level popup windows;
            // WS_CHILD distinguishes that case from an actual child HWND.
            if ((Native.GetStyle(layer.Worker, Native.GwlStyle) & Native.WsChild) != 0 ||
                (Native.GetStyle(layer.IconHost, Native.GwlStyle) & Native.WsChild) != 0)
                return false;
            above = layer.IconHost;
        }
        for (nint sibling = Native.GetWindow(above, 2); sibling != 0; sibling = Native.GetWindow(sibling, 2))
            if (sibling == layer.Worker)
                return true;
        return false;
    }

    private static bool HasClass(nint window, string name)
    {
        var buffer = new StringBuilder(128);
        return Native.GetClassName(window, buffer, buffer.Capacity) > 0 &&
            string.Equals(buffer.ToString(), name, StringComparison.Ordinal);
    }

    private sealed record DisplayInfo(string DeviceName, Rectangle Bounds, uint DpiX, uint DpiY, bool Primary);
    private sealed record Surface(Window Window, BloomView View, HwndSource Source, nint Handle);
    private sealed record ShellLayer(nint Progman, nint Worker, nint IconHost, nint IconView, uint ProcessId, bool Nested);

    private sealed class DpiContext : IDisposable
    {
        private readonly nint _previous;

        public DpiContext(nint context)
        {
            _previous = Native.SetThreadDpiAwarenessContext(context);
            if (_previous == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not select the required display DPI mode.");
        }

        public void Dispose() => Native.SetThreadDpiAwarenessContext(_previous);
    }

    private static class Native
    {
        internal static readonly nint PerMonitorV2 = -4;
        internal const int GwlStyle = -16, GwlExStyle = -20;
        internal const long WsChild = 0x40000000, WsPopup = 0x80000000,
            WsCaption = 0x00C00000, WsThickFrame = 0x00040000,
            WsExAppWindow = 0x00040000, WsExToolWindow = 0x00000080,
            WsExNoActivate = 0x08000000, WsExTransparent = 0x00000020;
        internal const uint SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020, SwpNoOwnerZOrder = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left, Top, Right, Bottom;
            internal Rect(int left, int top, int right, int bottom)
            {
                Left = left; Top = top; Right = right; Bottom = bottom;
            }
        }

        internal delegate bool EnumWindowsCallback(nint window, nint parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindWindow(string className, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
        [DllImport("user32.dll")]
        internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(nint window, StringBuilder name, int capacity);
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam,
            uint flags, uint timeout, out nint result);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetParent(nint child, nint parent);
        [DllImport("user32.dll")]
        internal static extern nint GetParent(nint window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int MapWindowPoints(nint from, nint to, ref Rect points, uint count);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(nint window, out Rect rect);
        [DllImport("user32.dll")]
        internal static extern nint GetWindowDpiAwarenessContext(nint window);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
        [DllImport("user32.dll")]
        internal static extern nint MonitorFromRect(ref Rect rect, uint flags);
        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong(nint window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern nint SetWindowLongPtr(nint window, int index, nint value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong(nint window, int index, int value);

        internal static long GetStyle(nint window, int index) => IntPtr.Size == 8
            ? GetWindowLongPtr(window, index).ToInt64()
            : unchecked((uint)GetWindowLong(window, index));

        internal static void SetStyle(nint window, int index, long value)
        {
            nint previous = IntPtr.Size == 8
                ? SetWindowLongPtr(window, index, new nint(value))
                : SetWindowLong(window, index, unchecked((int)value));
            if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not configure the wallpaper window.");
        }
    }
}
