using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rectangle = System.Drawing.Rectangle;

namespace BloomNative.Windows;

/// <summary>
/// Exercises production attachment against an owned HWND fixture. No Explorer
/// windows, media files, system wallpaper settings, or network are touched.
/// </summary>
internal static class DesktopAttachmentChecks
{
    internal sealed record Result(bool Passed, int Assertions, string Scope);

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

        nint previousDpi = Native.SetThreadDpiAwarenessContext(-4); // Per-monitor v2.
        if (previousDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint root = 0;
        Window? surface = null;
        try
        {
            // Place this test-only tree outside the visible desktop. Its windows
            // remain shown (not minimized), so WPF uses its real rendering path.
            Rectangle virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
            root = CreateFixtureWindow(0, virtualScreen.Left - 800, virtualScreen.Top - 600, 640, 480);
            nint stock = CreateFixtureWindow(root, 0, 0, 640, 480);
            nint icons = CreateFixtureWindow(root, 0, 0, 32, 32);
            // CreateWindowEx puts a new child at the bottom of sibling Z-order.
            // Arrange our owned fixture explicitly, like Explorer's icon layer.
            if (!Native.SetWindowPos(icons, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not arrange the owned desktop fixture.");
            Check(Native.GetWindow(root, 5) == icons && Native.GetWindow(icons, 2) == stock,
                "fixture must start with icons above its stock wallpaper child");
            Check(Native.GetWindowRect(root, out Native.Rect rootRect), "fixture root rectangle is available");
            var target = new Rectangle(rootRect.Left + 23, rootRect.Top + 31, 300, 180);
            var layer = new DesktopHost.ShellLayer(root, stock, root, icons, (uint)Environment.ProcessId, true);
            surface = DesktopHost.CreateWindowForLayer(new Border { Background = Brushes.DodgerBlue }, target, 96, 96, layer);
            nint handle = new WindowInteropHelper(surface).EnsureHandle();

            DesktopHost.AttachWindow(handle, layer);
            DesktopHost.PositionWindow(handle, target, layer);
            surface.Show();
            DesktopHost.PositionWindow(handle, target, layer);
            surface.UpdateLayout();
            DrainDispatcher();
            DrainDispatcher();

            Check(Native.GetParent(handle) == root, "Bloom must be a direct sibling of the stock wallpaper, not its child");
            Check(Native.GetWindow(root, 5) == icons, "the icon view remains the highest fixture child");
            Check(Native.GetWindow(icons, 2) == handle, "Bloom is immediately below the icon view");
            Check(Native.GetWindow(handle, 2) == stock, "Bloom is above the stock wallpaper child");
            long style = Native.GetStyle(handle, -16);
            Check((style & 0x40000000) != 0 && (style & 0x80000000) == 0,
                "the shown surface retains WS_CHILD without WS_POPUP");
            Check((Native.GetStyle(handle, -20) & 0x00080000) != 0,
                "WS_EX_LAYERED survives WPF Show and dispatcher rendering");
            Check(HwndSource.FromHwnd(handle)?.CompositionTarget?.UsesPerPixelOpacity == true,
                "WPF owns per-pixel layering instead of a temporary native style flag");
            Check(surface.Opacity == 1 && surface.Background is SolidColorBrush brush && brush.Color.A == 255,
                "the layered surface remains visually opaque");
            Check(Native.GetWindowRect(handle, out Native.Rect actual) &&
                actual.Left == target.Left && actual.Top == target.Top &&
                actual.Right - actual.Left == target.Width && actual.Bottom - actual.Top == target.Height,
                "screen-space bounds are preserved under a parent with negative coordinates");
            Check(DesktopHost.IsSurfaceHealthy(handle, layer), "the valid attachment is recognized as healthy");

            // Reproduce the observed failure: a live, correctly parented surface
            // can still be invisible if the stock wallpaper is above it.
            Check(Native.SetWindowPos(handle, 1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010),
                "the fixture can move Bloom behind the stock wallpaper");
            Check(!DesktopHost.IsSurfaceHealthy(handle, layer), "a surface covered by stock wallpaper is unhealthy");
            DesktopHost.PositionWindow(handle, target, layer);
            Check(DesktopHost.IsSurfaceHealthy(handle, layer), "production placement restores the correct layer");

            surface.Close();
            surface = null;
            DrainDispatcher();
            Check(!Native.IsWindow(handle), "closing the surface destroys its HWND");
            Check(Native.IsWindow(root) && Native.IsWindow(icons) && Native.IsWindow(stock),
                "surface cleanup preserves the fixture's shell windows");
            Check(Native.GetWindow(root, 5) == icons && Native.GetWindow(icons, 2) == stock,
                "cleanup restores the original icon-to-wallpaper ordering");

            return new Result(true, assertions,
                "Owned native HWND fixture using production WPF creation, attachment, positioning and health checks; no real Explorer, artwork, playback or hardware validation");
        }
        finally
        {
            // Destroy the WPF child before its native parent, including on failure.
            try { surface?.Close(); }
            finally
            {
                if (root != 0) Native.DestroyWindow(root);
                Native.SetThreadDpiAwarenessContext(previousDpi);
            }
        }
    }

    private static nint CreateFixtureWindow(nint parent, int x, int y, int width, int height)
    {
        uint style = (parent == 0 ? 0x80000000u : 0x40000000u) | 0x10000000u;
        nint handle = Native.CreateWindowEx(0x00000080 | 0x08000000, "STATIC", "Bloom attachment fixture",
            style, x, y, width, height, parent, 0, 0, 0);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the owned desktop fixture.");
        return handle;
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint handle);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint handle);
        [DllImport("user32.dll")]
        internal static extern nint GetParent(nint handle);
        [DllImport("user32.dll")]
        internal static extern nint GetWindow(nint handle, uint command);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint handle, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern nint GetWindowLongPtr(nint handle, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(nint handle, int index);
        internal static long GetStyle(nint handle, int index) => IntPtr.Size == 8
            ? GetWindowLongPtr(handle, index).ToInt64() : unchecked((uint)GetWindowLong(handle, index));
    }
}
