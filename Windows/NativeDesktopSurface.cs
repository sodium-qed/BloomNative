using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BloomNative.Windows;

/// <summary>
/// Owns a system-managed, constant-alpha desktop layer. WPF renders into an
/// ordinary child HWND; WPF never owns or changes this HWND's layering mode.
/// </summary>
internal sealed class NativeDesktopSurface : IDisposable
{
    private const string ClassName = "BloomNativeDesktopSurface";
    private static readonly object RegistrationGate = new();
    private static readonly Native.WindowProcedure Procedure = WindowMessage;
    private static bool registered;
    private nint handle;

    internal NativeDesktopSurface(nint parent)
    {
        if (parent == 0 || !Native.IsWindow(parent))
            throw new InvalidOperationException("The desktop layer's parent is unavailable.");
        EnsureClass();
        // No CS_OWNDC / CS_CLASSDC or WS_EX_NOREDIRECTIONBITMAP: this layer needs
        // the normal system-managed redirection bitmap for its child content.
        handle = Native.CreateWindowEx(0x00080000 | 0x00000020 | 0x08000000 | 0x00000080,
            ClassName, "Bloom Native desktop surface", 0x40000000 | 0x02000000 | 0x04000000,
            0, 0, 1, 1, parent, 0, Native.GetModuleHandle(null), 0);
        if (handle == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the desktop composition surface.");
        if (!Native.SetLayeredWindowAttributes(handle, 0, 255, 0x00000002)) // LWA_ALPHA only
        {
            int error = Marshal.GetLastPInvokeError();
            Dispose();
            throw new Win32Exception(error, "Could not configure the desktop composition surface.");
        }
    }

    internal nint Handle => handle;

    internal bool IsOpaqueLayered => handle != 0 && Native.IsWindow(handle) &&
        Native.GetLayeredWindowAttributes(handle, out _, out byte alpha, out uint flags) &&
        alpha == 255 && flags == 0x00000002;

    internal void Show()
    {
        ObjectDisposedException.ThrowIf(handle == 0, this);
        Native.ShowWindow(handle, 4); // SW_SHOWNOACTIVATE
    }

    public void Dispose()
    {
        nint old = handle;
        handle = 0;
        if (old == 0 || !Native.IsWindow(old)) return;
        Native.GetWindowThreadProcessId(old, out uint process);
        var name = new StringBuilder(128);
        Native.GetClassName(old, name, name.Capacity);
        if (process == (uint)Environment.ProcessId && name.ToString() == ClassName)
            Native.DestroyWindow(old);
    }

    private static void EnsureClass()
    {
        lock (RegistrationGate)
        {
            if (registered) return;
            var definition = new Native.WindowClass
            {
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                Instance = Native.GetModuleHandle(null),
                Background = Native.GetStockObject(4), // BLACK_BRUSH
                Name = ClassName
            };
            if (Native.RegisterClass(ref definition) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not register the desktop composition surface.");
            registered = true;
        }
    }

    private static nint WindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x0084) return -1; // WM_NCHITTEST: HTTRANSPARENT
        if (message == 0x0021) return 3; // WM_MOUSEACTIVATE: MA_NOACTIVATE
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    private static class Native
    {
        internal delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

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

        [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClass(ref WindowClass definition);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title,
            uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLayeredWindowAttributes(nint window, out uint color, out byte alpha, out uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(nint window, StringBuilder name, int capacity);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandle(string? name);
        [DllImport("gdi32.dll")]
        internal static extern nint GetStockObject(int index);
    }
}
