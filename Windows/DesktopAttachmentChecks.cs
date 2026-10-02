using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rectangle = System.Drawing.Rectangle;

namespace BloomNative.Windows;

/// <summary>
/// Tests production attachment and DWM pixels in brief, owned windows. Reads only
/// individual covered pixels, never a screenshot; never changes Explorer, system
/// wallpaper, media, or camera state.
/// </summary>
internal static class DesktopAttachmentChecks
{
    private const string BlackFixtureClass = "BloomBlackCompositionFixture";
    private const string WhiteFixtureClass = "BloomWhiteCompositionFixture";
    private static readonly Native.WindowProcedure FixtureProcedure = FixtureWindowMessage;
    private static bool fixtureClassesRegistered;
    internal sealed record Result(bool Passed, int Assertions, string Scope, bool CompositionVerified);

    internal static Result Run()
    {
        if (Application.Current is null)
            throw new InvalidOperationException("The desktop attachment fixture requires a WPF application.");
        Application.Current.Dispatcher.VerifyAccess();
        int assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Desktop attachment fixture: " + message);
            assertions++;
        }
        void CheckPixel(nint fixture, int x, int y, uint expected, string message)
        {
            bool visible = WaitForPixel(fixture, x, y, expected, out uint actual, out string diagnostics);
            Check(visible, $"{message}; expected RGB {DescribeColor(expected)}, observed {DescribeColor(actual)}; {diagnostics}");
        }

        nint previousDpi = Native.SetThreadDpiAwarenessContext(-4);
        if (previousDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint root = 0;
        NativeDesktopSurface? host = null;
        Window? surface = null;
        try
        {
            Check(Native.DwmIsCompositionEnabled(out bool composition) >= 0 && composition,
                "an interactive DWM-composed desktop is required for pixel verification");
            Rectangle workArea = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                ?? throw new InvalidOperationException("No primary desktop is available for the fixture.");
            Check(workArea.Width >= 280 && workArea.Height >= 220, "the desktop has room for the owned fixture");

            // Reproduce Progman's NOREDIRECTIONBITMAP parent. Controlled layered
            // stock/icon children emulate visible wallpaper and one icon, not
            // Explorer's full DefView implementation or transparency policy.
            root = CreateFixtureWindow(0, workArea.Left + 16, workArea.Top + 16, 240, 180, noRedirection: true);
            nint stock = CreateFixtureWindow(root, 0, 0, 240, 180, layered: true);
            nint icons = CreateFixtureWindow(root, 12, 12, 24, 24, layered: true, white: true);
            Require(Native.SetWindowPos(icons, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010), "Could not order fixture children.");
            ShowFixture(root);
            Check((Native.GetStyle(root, -20) & 0x00200000) != 0, "the raised fixture parent has no redirection bitmap");
            Check(Native.GetWindow(root, 5) == icons && Native.GetWindow(icons, 2) == stock,
                "fixture icons start above its stock wallpaper child");
            Check(Native.GetWindowRect(root, out Native.Rect rootRect), "the fixture root rectangle is available");
            int sampleX = rootRect.Left + 120, sampleY = rootRect.Top + 90;
            CheckPixel(root, sampleX, sampleY, 0x000000, "the stock fixture wallpaper is visibly black before attachment");

            var target = new Rectangle(rootRect.Left + 8, rootRect.Top + 8, 224, 164);
            var layer = new DesktopHost.ShellLayer(root, stock, root, icons, (uint)Environment.ProcessId, true);
            host = DesktopHost.CreateNativeHost(target, layer);
            Check(host is not null, "the raised layout creates a separate native host");
            nint outer = host!.Handle;
            var content = new Border { Background = Brushes.Magenta };
            surface = DesktopHost.CreateWindowForLayer(content, target, 96, 96, layer);
            nint handle = new WindowInteropHelper(surface).EnsureHandle();
            DesktopHost.AttachWindow(handle, layer, host);
            DesktopHost.PositionWindow(handle, target, layer, host);
            host.Show();
            surface.Show();
            DesktopHost.PositionWindow(handle, target, layer, host);
            surface.UpdateLayout();
            DrainDispatcher();

            Check(Native.GetParent(outer) == root && Native.GetParent(handle) == outer,
                "the native host is a desktop sibling and WPF is its child");
            Check(Native.GetWindow(root, 5) == icons && Native.GetWindow(icons, 2) == outer && Native.GetWindow(outer, 2) == stock,
                "the actual sibling order is icons, native Bloom host, stock wallpaper");
            Check(host.IsOpaqueLayered, "the native host retains constant alpha 255 without a color key");
            Check((Native.GetStyle(outer, -20) & 0x00080000) != 0 && (Native.GetStyle(handle, -20) & 0x00080000) == 0,
                "only the native host owns WS_EX_LAYERED");
            Check(HwndSource.FromHwnd(handle)?.CompositionTarget?.UsesPerPixelOpacity == false && !surface.AllowsTransparency,
                "WPF uses its ordinary opaque rendering path");
            Check(HasBounds(outer, target) && HasBounds(handle, target), "native and WPF bounds match the requested screen rectangle");
            Check(DesktopHost.IsSurfaceHealthy(outer, layer) && DesktopHost.IsContentHealthy(handle, layer, host),
                "both attachment and content are structurally healthy");
            CheckPixel(root, sampleX, sampleY, 0xFF00FF, "the production host visibly composites magenta WPF content above stock wallpaper");
            CheckPixel(root, rootRect.Left + 20, rootRect.Top + 20, 0xFFFFFF, "the overlapping icon marker remains visible above Bloom");
            content.Background = Brushes.Cyan;
            CheckPixel(root, sampleX, sampleY, 0xFFFF00, "a WPF content update changes the composed screen pixel to cyan");

            // A correct parent does not prove visibility. Cover Bloom with the
            // stock child and require both detection and visible recovery.
            Require(Native.SetWindowPos(outer, 1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010), "Could not cover the owned Bloom surface.");
            Check(!DesktopHost.IsSurfaceHealthy(outer, layer), "a stock-covered Bloom host is unhealthy");
            CheckPixel(root, sampleX, sampleY, 0x000000, "stock wallpaper visibly covers the intentionally misordered host");
            DesktopHost.PositionWindow(outer, target, layer);
            Check(DesktopHost.IsSurfaceHealthy(outer, layer), "production placement restores the correct host order");
            CheckPixel(root, sampleX, sampleY, 0xFFFF00, "restored placement makes the WPF content visible again");
            Native.ShowWindow(outer, 0);
            CheckPixel(root, sampleX, sampleY, 0x000000, "hiding Bloom exposes the original fixture wallpaper");

            // Check negative monitor coordinates after screen-pixel checks finish.
            Rectangle virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
            Require(Native.SetWindowPos(root, 0, virtualScreen.Left - 800, virtualScreen.Top - 600, 0, 0,
                0x0001 | 0x0004 | 0x0010), "Could not move the fixture for the coordinate check.");
            Check(Native.GetWindowRect(root, out rootRect), "the moved fixture rectangle is available");
            target = new Rectangle(rootRect.Left + 8, rootRect.Top + 8, 224, 164);
            DesktopHost.PositionWindow(outer, target, layer);
            DesktopHost.PositionWindow(handle, target, layer, host);
            Check(HasBounds(outer, target) && HasBounds(handle, target), "host/content mapping preserves negative screen coordinates");
            surface.Close();
            surface = null;
            host.Dispose();
            host = null;
            DrainDispatcher();
            Check(!Native.IsWindow(handle) && !Native.IsWindow(outer), "cleanup destroys both application-owned HWNDs");
            Check(Native.IsWindow(root) && Native.IsWindow(icons) && Native.IsWindow(stock), "cleanup preserves the fixture shell windows");
            Check(Native.GetWindow(root, 5) == icons && Native.GetWindow(icons, 2) == stock, "cleanup restores original fixture ordering");
            Native.DestroyWindow(root);
            root = 0;

            // Classic attachment has a normal redirected native parent and an
            // ordinary WPF child, without an additional layered native host.
            root = CreateFixtureWindow(0, workArea.Left + 16, workArea.Top + 16, 240, 180);
            // Production WPF content intentionally passes hit testing through.
            // Give that pass-through an owned black backing child, as in the
            // raised fixture, so the pixel ownership guard never needs to accept
            // an unrelated window underneath the fixture.
            nint classicBacking = CreateFixtureWindow(root, 0, 0, 240, 180);
            ShowFixture(root);
            Check(Native.GetWindowRect(root, out rootRect), "the classic fixture rectangle is available");
            target = new Rectangle(rootRect.Left + 8, rootRect.Top + 8, 224, 164);
            var classic = new DesktopHost.ShellLayer(root, root, 0, 0, (uint)Environment.ProcessId, false);
            host = DesktopHost.CreateNativeHost(target, classic);
            Check(host is null, "classic attachment does not create a layered wrapper");
            surface = DesktopHost.CreateWindowForLayer(new Border { Background = Brushes.Lime }, target, 96, 96, classic);
            handle = new WindowInteropHelper(surface).EnsureHandle();
            DesktopHost.AttachWindow(handle, classic);
            DesktopHost.PositionWindow(handle, target, classic);
            surface.Show();
            DesktopHost.PositionWindow(handle, target, classic);
            surface.UpdateLayout();
            Check(Native.GetParent(handle) == root && DesktopHost.IsContentHealthy(handle, classic) &&
                HwndSource.FromHwnd(handle)?.CompositionTarget?.UsesPerPixelOpacity == false,
                "classic WPF content stays opaque and directly parented");
            Check(Native.GetWindow(root, 5) == handle && Native.GetWindow(handle, 2) == classicBacking,
                "classic WPF content stays above its owned hit-testable backing");
            CheckPixel(root, rootRect.Left + 120, rootRect.Top + 90, 0x00FF00, "classic attachment visibly presents lime WPF content");
            surface.Close();
            surface = null;
            CheckPixel(root, rootRect.Left + 120, rootRect.Top + 90, 0x000000, "classic cleanup reveals the underlying fixture background");

            return new Result(true, assertions,
                "Owned on-screen DWM fixture: native constant-alpha host plus opaque WPF child under a no-redirection parent, sampled color changes/icon occlusion, classic opaque rendering, geometry and cleanup. No real Explorer discovery/DefView behavior, artwork, playback, camera or hardware validation.", true);
        }
        finally
        {
            try { surface?.Close(); }
            finally
            {
                try { host?.Dispose(); }
                finally
                {
                    if (root != 0) Native.DestroyWindow(root);
                    Native.SetThreadDpiAwarenessContext(previousDpi);
                }
            }
        }
    }

    private static bool HasBounds(nint handle, Rectangle expected) => Native.GetWindowRect(handle, out Native.Rect actual) &&
        actual.Left == expected.Left && actual.Top == expected.Top &&
        actual.Right - actual.Left == expected.Width && actual.Bottom - actual.Top == expected.Height;

    private static nint CreateFixtureWindow(nint parent, int x, int y, int width, int height,
        bool noRedirection = false, bool layered = false, bool white = false)
    {
        EnsureFixtureClasses();
        uint style = (parent == 0 ? 0x80000000u : 0x40000000u) | 0x10000000u | 0x02000000u | 0x04000000u;
        uint extended = 0x00000080 | 0x08000000 | (noRedirection ? 0x00200000u : 0) | (layered ? 0x00080000u : 0);
        nint handle = Native.CreateWindowEx(extended, white ? WhiteFixtureClass : BlackFixtureClass, string.Empty,
            style, x, y, width, height, parent, 0, Native.GetModuleHandle(null), 0);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the owned desktop fixture.");
        if (layered && !Native.SetLayeredWindowAttributes(handle, 0, 255, 2))
        {
            Native.DestroyWindow(handle);
            throw new InvalidOperationException("Could not make the fixture stock/icon layer opaque.");
        }
        return handle;
    }

    private static void EnsureFixtureClasses()
    {
        if (fixtureClassesRegistered) return;
        // WindowFromPoint intentionally skips STATIC text controls. Use owned
        // window classes with explicit HTCLIENT so the ownership guard can verify
        // a sample point without falling through to somebody else's window.
        foreach (var item in new[] { (BlackFixtureClass, 4), (WhiteFixtureClass, 0) })
        {
            var definition = new Native.WindowClass
            {
                Procedure = Marshal.GetFunctionPointerForDelegate(FixtureProcedure),
                Instance = Native.GetModuleHandle(null),
                Background = Native.GetStockObject(item.Item2),
                Name = item.Item1
            };
            if (Native.RegisterClass(ref definition) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the owned fixture class.");
        }
        fixtureClassesRegistered = true;
    }

    private static nint FixtureWindowMessage(nint handle, uint message, nint wParam, nint lParam)
    {
        if (message == 0x0084) return 1; // WM_NCHITTEST: HTCLIENT for owned-pixel checks.
        if (message == 0x0021) return 3; // WM_MOUSEACTIVATE: MA_NOACTIVATE.
        return Native.DefWindowProc(handle, message, wParam, lParam);
    }

    private static void ShowFixture(nint root)
    {
        Require(Native.SetWindowPos(root, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040), "Could not show the owned composition fixture.");
        Require(Native.RedrawWindow(root, 0, 0, 0x0001 | 0x0004 | 0x0080 | 0x0100), "Could not paint the owned fixture.");
    }

    private static bool WaitForPixel(nint root, int x, int y, uint expected, out uint actual, out string diagnostics)
    {
        actual = uint.MaxValue;
        diagnostics = "Pixel read not attempted.";
        var timeout = Stopwatch.StartNew();
        do
        {
            DrainDispatcher();
            if (Native.DwmFlush() < 0) throw new InvalidOperationException("DWM presentation is unavailable for the owned pixel fixture.");
            nint hit = Native.WindowFromPoint(new Native.Point(x, y));
            // Sample only a point covered by our own test-window tree.
            if (hit == root || Native.IsChild(root, hit))
            {
                nint dc = Native.GetDC(0);
                if (dc != 0)
                {
                    try
                    {
                        actual = Native.GetPixel(dc, x, y);
                        diagnostics = actual == uint.MaxValue
                            ? $"GetPixel returned CLR_INVALID; Win32 error={Marshal.GetLastWin32Error()}"
                            : "Owned screen pixel was read but has not reached the expected color";
                    }
                    finally { Native.ReleaseDC(0, dc); }
                    if (ColorMatches(actual, expected)) return true;
                }
                else diagnostics = $"GetDC(screen) returned null; Win32 error={Marshal.GetLastWin32Error()}";
            }
            else diagnostics = $"Ownership guard rejected hit HWND=0x{hit:X}; root HWND=0x{root:X}; IsChild=false";
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        } while (timeout.ElapsedMilliseconds < 2000);
        Native.GetWindowRect(root, out Native.Rect bounds);
        int cloakResult = Native.DwmGetWindowAttribute(root, 14, out uint cloaked, sizeof(uint));
        diagnostics += $"; root visible={Native.IsWindowVisible(root)}, cloaked={(cloakResult >= 0 ? cloaked.ToString() : "unavailable")}, bounds=({bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}), sample=({x},{y}), user interactive={Environment.UserInteractive}, process session={Process.GetCurrentProcess().SessionId}";
        return false;
    }

    private static bool ColorMatches(uint actual, uint expected) => actual != uint.MaxValue &&
        Math.Abs((int)(actual & 255) - (int)(expected & 255)) <= 8 &&
        Math.Abs((int)((actual >> 8) & 255) - (int)((expected >> 8) & 255)) <= 8 &&
        Math.Abs((int)((actual >> 16) & 255) - (int)((expected >> 16) & 255)) <= 8;
    private static string DescribeColor(uint color) => color == uint.MaxValue ? "unavailable" : $"({color & 255},{(color >> 8) & 255},{(color >> 16) & 255})";
    private static void Require(bool result, string message)
    {
        if (!result) throw new Win32Exception(Marshal.GetLastWin32Error(), message);
    }
    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static class Native
    {
        internal delegate nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            internal uint Style;
            internal nint Procedure;
            internal int ClassExtra, WindowExtra;
            internal nint Instance, Icon, Cursor, Background;
            [MarshalAs(UnmanagedType.LPWStr)] internal string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)] internal string Name;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal readonly struct Point
        {
            internal readonly int X, Y;
            internal Point(int x, int y) { X = x; Y = y; }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClass(ref WindowClass definition);
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] internal static extern nint DefWindowProc(nint handle, uint message, nint wParam, nint lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
        [DllImport("gdi32.dll")] internal static extern nint GetStockObject(int index);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsChild(nint parent, nint child);
        [DllImport("user32.dll")] internal static extern nint GetParent(nint handle);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint handle, uint command);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint handle, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint handle, int command);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetLayeredWindowAttributes(nint handle, uint color, byte alpha, uint flags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RedrawWindow(nint handle, nint rect, nint region, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint GetDC(nint handle);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(nint handle, nint dc);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern uint GetPixel(nint dc, int x, int y);
        [DllImport("dwmapi.dll")] internal static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint handle, uint attribute, out uint value, uint size);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint handle, int index);
        internal static long GetStyle(nint handle, int index) => IntPtr.Size == 8
            ? GetWindowLongPtr(handle, index).ToInt64() : unchecked((uint)GetWindowLong(handle, index));
    }
}
