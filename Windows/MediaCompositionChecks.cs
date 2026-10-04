using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rectangle = System.Drawing.Rectangle;

namespace BloomNative.Windows;

/// <summary>Decoded synthetic pixels through the production view, without creator artwork or devices.</summary>
internal static class MediaCompositionChecks
{
    internal sealed record Result(bool Verified, int Assertions, string Status, string Reason, string Scope,
        uint? BaselineRed = null, uint? BaselineCyan = null);
    private const string Scope = "Synthetic H.264 red/cyan clip: ordinary MediaElement capability/color baseline, then production BloomView scrubbing under owned raised and classic desktop hosts, matching baseline pixels within 8 RGB levels. No creator artwork, real Explorer, camera or hardware validation.";

    internal static Result Run()
    {
        Application.Current.Dispatcher.VerifyAccess();
        string path = Path.Combine(Path.GetTempPath(), "BloomNative-media-check-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(path, Convert.FromBase64String(VideoBase64));
        nint previousDpi = Native.SetThreadDpiAwarenessContext(-4);
        try
        {
            if (previousDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Rectangle area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                ?? throw new InvalidOperationException("No desktop is available for synthetic media checks.");
            Rectangle bounds = new(area.Left + 16, area.Top + 16, 240, 180);
            if (!CheckBaseline(path, bounds, out string reason, out uint red, out uint cyan))
                return new Result(false, 0, "unavailable", reason, Scope);

            int assertions = 2; // Both decoded baseline colors were visible.
            assertions += CheckProduction(path, bounds, nested: true, red, cyan);
            assertions += CheckProduction(path, bounds, nested: false, red, cyan);
            return new Result(true, assertions, "verified", "Both decoded colors and production seek updates were visible in both host layouts.", Scope, red, cyan);
        }
        finally
        {
            if (previousDpi != 0) Native.SetThreadDpiAwarenessContext(previousDpi);
            // Media.Close releases asynchronously. Give its dispatcher a bounded
            // opportunity to release the owned file before declaring cleanup failed.
            if (!WaitUntil(() =>
                {
                    try { File.Delete(path); return true; }
                    catch (IOException) { return false; }
                }, 2000))
                throw new IOException("The synthetic media fixture could not release its temporary video.");
        }
    }

    private static bool CheckBaseline(string path, Rectangle bounds, out string reason, out uint red, out uint cyan)
    {
        red = cyan = uint.MaxValue;
        var media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true, IsMuted = true, Volume = 0, Stretch = Stretch.Fill
        };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Background = Brushes.Black, Content = media
        };
        bool opened = false;
        string? failure = null;
        media.MediaOpened += (_, _) => opened = true;
        media.MediaFailed += (_, args) => failure = args.ErrorException.Message;
        try
        {
            window.Show();
            nint root = new WindowInteropHelper(window).Handle;
            Require(Native.SetWindowPos(root, -1, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010 | 0x0040));
            window.UpdateLayout();
            DesktopAttachmentChecks.ShowFixture(root);
            media.Source = new Uri(path, UriKind.Absolute);
            media.Pause();
            if (!WaitUntil(() => opened || failure is not null, 10000) || failure is not null ||
                !media.NaturalDuration.HasTimeSpan || media.NaturalDuration.TimeSpan <= TimeSpan.Zero)
            {
                reason = "Ordinary MediaElement could not open the synthetic H.264 baseline: " +
                    (failure ?? "no usable MediaOpened/duration within 10 seconds");
                return false;
            }
            foreach (var sample in new[] { (0.25, 0x0000FFu), (0.75, 0xFFFF00u) })
            {
                media.Position = TimeSpan.FromSeconds(media.NaturalDuration.TimeSpan.TotalSeconds * sample.Item1);
                // Windows' video color conversion can retain limited-range RGB.
                // Identify the strongly distinct baseline colors within 32, then
                // compare production rendering to these actual pixels within 8.
                if (!DesktopAttachmentChecks.WaitForPixel(root, bounds.Left + 120, bounds.Top + 90,
                    sample.Item2, out uint actual, out string diagnostics, 10000, colorTolerance: 32))
                {
                    reason = $"Ordinary MediaElement did not display its decoded baseline at {sample.Item1:P0}; " +
                        $"pixel=0x{actual:X6}; media error={failure ?? "none"}; {diagnostics}";
                    return false;
                }
                if (sample.Item1 == 0.25) red = actual;
                else cyan = actual;
            }
            reason = string.Empty;
            return true;
        }
        finally
        {
            media.Close();
            media.Source = null;
            window.Close();
            DesktopAttachmentChecks.DrainDispatcher();
        }
    }

    private static int CheckProduction(string path, Rectangle bounds, bool nested, uint red, uint cyan)
    {
        nint root = 0;
        NativeDesktopSurface? host = null;
        BloomView? view = null;
        Window? window = null;
        try
        {
            root = DesktopAttachmentChecks.CreateFixtureWindow(0, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                noRedirection: nested);
            nint stock = DesktopAttachmentChecks.CreateFixtureWindow(root, 0, 0, bounds.Width, bounds.Height, layered: nested);
            nint icons = nested ? DesktopAttachmentChecks.CreateFixtureWindow(root, 12, 12, 24, 24, layered: true, white: true) : 0;
            if (nested) Require(Native.SetWindowPos(icons, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010));
            DesktopAttachmentChecks.ShowFixture(root);
            var target = new Rectangle(bounds.Left + 8, bounds.Top + 8, bounds.Width - 16, bounds.Height - 16);
            var layer = nested
                ? new DesktopHost.ShellLayer(root, stock, root, icons, (uint)Environment.ProcessId, true)
                : new DesktopHost.ShellLayer(root, root, 0, 0, (uint)Environment.ProcessId, false);
            host = DesktopHost.CreateNativeHost(target, layer);
            string? failure = null;
            view = new BloomView(path) { Breathe = false };
            view.PlaybackFailed += message => failure = message;
            view.SetProgress(0.25);
            window = DesktopHost.CreateWindowForLayer(view, target, 96, 96, layer);
            nint handle = new WindowInteropHelper(window).EnsureHandle();
            DesktopHost.AttachWindow(handle, layer, host);
            DesktopHost.PositionWindow(handle, target, layer, host);
            host?.Show();
            window.Show();
            DesktopHost.PositionWindow(handle, target, layer, host);
            window.UpdateLayout();
            DesktopAttachmentChecks.ShowFixture(root);
            int assertions = 0;
            void CheckPixel(int x, int y, uint color, string message)
            {
                bool seen = DesktopAttachmentChecks.WaitForPixel(root, x, y, color, out uint actual, out string diagnostics, 10000);
                if (!seen || failure is not null)
                    throw new InvalidOperationException($"Synthetic media composition ({(nested ? "raised" : "classic")}): {message}; " +
                        $"expected=0x{color:X6}, actual=0x{actual:X6}; media error={failure ?? "none"}; {view.DiagnosticState}; {diagnostics}");
                assertions++;
            }
            CheckPixel(bounds.Left + 120, bounds.Top + 90, red, "production BloomView must display decoded red at 25%");
            view.SetProgress(0.75);
            CheckPixel(bounds.Left + 120, bounds.Top + 90, cyan, "production BloomView must display decoded cyan after seeking to 75%");
            if (nested)
                CheckPixel(bounds.Left + 20, bounds.Top + 20, 0xFFFFFF, "owned icon marker must remain above decoded video");
            return assertions;
        }
        finally
        {
            try { view?.Dispose(); }
            finally
            {
                try { window?.Close(); }
                finally
                {
                    try { host?.Dispose(); }
                    finally { if (root != 0) Native.DestroyWindow(root); }
                }
            }
            DesktopAttachmentChecks.DrainDispatcher();
        }
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMilliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            if (condition()) return true;
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        } while (elapsed.ElapsedMilliseconds < timeoutMilliseconds);
        return condition();
    }

    private static void Require(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not position the owned media baseline.");
    }

    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint handle);
    }

    // Generated locally, not sourced artwork: 160x120, 30 fps, one second red
    // followed by one second cyan, silent H.264 constrained-baseline yuv420p,
    // every frame an I-frame. ffmpeg lavfi color inputs; concat; libx264 -g 1 -bf 0.
    private const string VideoBase64 =
        "AAAAIGZ0eXBpc29tAAACAGlzb21pc28yYXZjMW1wNDEAAAQAbW9vdgAAAGxtdmhkAAAAAAAAAAAAAAAAAAAD6AAAB9AAAQAAAQAAAAAAAAAAAAAAAAEAAAAA" +
        "AAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAyp0cmFrAAAAXHRraGQAAAADAAAAAAAAAAAAAAAB" +
        "AAAAAAAAB9AAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAAKAAAAB4AAAAAAAkZWR0cwAAABxlbHN0AAAAAAAA" +
        "AAEAAAfQAAAAAAABAAAAAAKibWRpYQAAACBtZGhkAAAAAAAAAAAAAAAAAAA8AAAAeABVxAAAAAAALWhkbHIAAAAAAAAAAHZpZGUAAAAAAAAAAAAAAABWaWRl" +
        "b0hhbmRsZXIAAAACTW1pbmYAAAAUdm1oZAAAAAEAAAAAAAAAAAAAACRkaW5mAAAAHGRyZWYAAAAAAAAAAQAAAAx1cmwgAAAAAQAAAg1zdGJsAAAAuXN0c2QA" +
        "AAAAAAAAAQAAAKlhdmMxAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAKAAeABIAAAASAAAAAAAAAABFUxhdmM2MC4zMS4xMDIgbGlieDI2NAAAAAAAAAAAAAAA" +
        "GP//AAAAL2F2Y0MBQsAL/+EAF2dCwAvcKEflwEQAAAMABAAAAwDwPFCuAQAFaM4PLIAAAAAQcGFzcAAAAAEAAAABAAAAFGJ0cnQAAAAAAABZOAAAWTgAAAAY" +
        "c3R0cwAAAAAAAAABAAAAPAAAAgAAAAAcc3RzYwAAAAAAAAABAAAAAQAAADwAAAABAAABBHN0c3oAAAAAAAAAAAAAADwAAAK3AAAAVQAAAFUAAABVAAAAVQAA" +
        "AFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAABV" +
        "AAAAVQAAAFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAABVAAAAVQAA" +
        "AFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAABVAAAAVQAAAFUAAAAUc3RjbwAAAAAAAAABAAAEMAAAAGJ1ZHRhAAAAWm1ldGEAAAAAAAAAIWhkbHIAAAAA" +
        "AAAAAG1kaXJhcHBsAAAAAAAAAAAAAAAALWlsc3QAAAAlqXRvbwAAAB1kYXRhAAAAAQAAAABMYXZmNjAuMTYuMTAwAAAACGZyZWUAABZWbWRhdAAAAl4GBf//" +
        "WtxF6b3m2Ui3lizYINkj7u94MjY0IC0gY29yZSAxNjQgcjMxMDggMzFlMTlmOSAtIEguMjY0L01QRUctNCBBVkMgY29kZWMgLSBDb3B5bGVmdCAyMDAzLTIw" +
        "MjMgLSBodHRwOi8vd3d3LnZpZGVvbGFuLm9yZy94MjY0Lmh0bWwgLSBvcHRpb25zOiBjYWJhYz0wIHJlZj0xIGRlYmxvY2s9MTowOjAgYW5hbHlzZT0weDE6" +
        "MHgxMTEgbWU9aGV4IHN1Ym1lPTcgcHN5PTEgcHN5X3JkPTEuMDA6MC4wMCBtaXhlZF9yZWY9MCBtZV9yYW5nZT0xNiBjaHJvbWFfbWU9MSB0cmVsbGlzPTEg" +
        "OHg4ZGN0PTAgY3FtPTAgZGVhZHpvbmU9MjEsMTEgZmFzdF9wc2tpcD0xIGNocm9tYV9xcF9vZmZzZXQ9LTIgdGhyZWFkcz00IGxvb2thaGVhZF90aHJlYWRz" +
        "PTEgc2xpY2VkX3RocmVhZHM9MCBucj0wIGRlY2ltYXRlPTEgaW50ZXJsYWNlZD0wIGJsdXJheV9jb21wYXQ9MCBjb25zdHJhaW5lZF9pbnRyYT0wIGJmcmFt" +
        "ZXM9MCB3ZWlnaHRwPTAga2V5aW50PTEga2V5aW50X21pbj0xIHNjZW5lY3V0PTQwIGludHJhX3JlZnJlc2g9MCByYz1jcmYgbWJ0cmVlPTAgY3JmPTIzLjAg" +
        "cWNvbXA9MC42MCBxcG1pbj0wIHFwbWF4PTY5IHFwc3RlcD00IGlwX3JhdGlvPTEuNDAgYXE9MToxLjAwAIAAAABRZYiEBLxGKAAKi8cAASjY4AAvrScnJycn" +
        "JycnJ11111111111111111111111111111111111111111111111111111111111111111111114AAAAUWWIggEvEYoAAqLxwABKNjgAC+tJycnJycnJycnX" +
        "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAAFFliIQEvEYoAAqLxwABKNjgAC+tJycnJycnJycnXXXXXXXX" +
        "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAABRZYiCAS8RigACovHAAEo2OAAL60nJycnJycnJyddddddddddddddd" +
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddeAAAAUWWIhAS8RigACovHAAEo2OAAL60nJycnJycnJydddddddddddddddddddddd" +
        "ddddddddddddddddddddddddddddddddddddddddddddddddeAAAAFFliIIBLxGKAAKi8cAASjY4AAvrScnJycnJycnJ1111111111111111111111111111" +
        "1111111111111111111111111111111111111111114AAABRZYiEBLxGKAAKi8cAASjY4AAvrScnJycnJycnJ11111111111111111111111111111111111" +
        "111111111111111111111111111111111114AAAAUWWIggEvEYoAAqLxwABKNjgAC+tJycnJycnJycnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" +
        "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAAFFliIQEvEYoAAqLxwABKNjgAC+tJycnJycnJycnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" +
        "XXXXXXXXXXXXXXXXXXXXXXgAAABRZYiCAS8RigACovHAAEo2OAAL60nJycnJycnJyddddddddddddddddddddddddddddddddddddddddddddddddddddddd" +
        "dddddddddddddddeAAAAUWWIhAS8RigACovHAAEo2OAAL60nJycnJycnJydddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" +
        "ddddddddeAAAAFFliIIBLxGKAAKi8cAASjY4AAvrScnJycnJycnJ11111111111111111111111111111111111111111111111111111111111111111111" +
        "114AAABRZYiEBLxGKAAKi8cAASjY4AAvrScnJycnJycnJ11111111111111111111111111111111111111111111111111111111111111111111114AAAA" +
        "UWWIggEvEYoAAqLxwABKNjgAC+tJycnJycnJycnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAAFFliIQE" +
        "vEYoAAqLxwABKNjgAC+tJycnJycnJycnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAABRZYiCAS8RigAC" +
        "ovHAAEo2OAAL60nJycnJycnJyddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddeAAAAUWWIhAS8RigACovHAAEo" +
        "2OAAL60nJycnJycnJyddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddeAAAAFFliIIBLxGKAAKi8cAASjY4AAvr" +
        "ScnJycnJycnJ11111111111111111111111111111111111111111111111111111111111111111111114AAABRZYiEBLxGKAAKi8cAASjY4AAvrScnJycn" +
        "JycnJ11111111111111111111111111111111111111111111111111111111111111111111114AAAAUWWIggEvEYoAAqLxwABKNjgAC+tJycnJycnJycnX" +
        "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAAFFliIQEvEYoAAqLxwABKNjgAC+tJycnJycnJycnXXXXXXXX" +
        "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAABRZYiCAS8RigACovHAAEo2OAAL60nJycnJycnJyddddddddddddddd" +
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddeAAAAUWWIhAS8RigACovHAAEo2OAAL60nJycnJycnJydddddddddddddddddddddd" +
        "ddddddddddddddddddddddddddddddddddddddddddddddddeAAAAFFliIIBLxGKAAKi8cAASjY4AAvrScnJycnJycnJ1111111111111111111111111111" +
        "1111111111111111111111111111111111111111114AAABRZYiEBLxGKAAKi8cAASjY4AAvrScnJycnJycnJ11111111111111111111111111111111111" +
        "111111111111111111111111111111111114AAAAUWWIggEvEYoAAqLxwABKNjgAC+tJycnJycnJycnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" +
        "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAAFFliIQEvEYoAAqLxwABKNjgAC+tJycnJycnJycnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" +
        "XXXXXXXXXXXXXXXXXXXXXXgAAABRZYiCAS8RigACovHAAEo2OAAL60nJycnJycnJyddddddddddddddddddddddddddddddddddddddddddddddddddddddd" +
        "dddddddddddddddeAAAAUWWIhAS8RigACovHAAEo2OAAL60nJycnJycnJydddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" +
        "ddddddddeAAAAFFliIIBLxGKAAKi8cAASjY4AAvrScnJycnJycnJ11111111111111111111111111111111111111111111111111111111111111111111" +
        "114AAABRZYiEBLxGKAAKREcAASjI4AAvrycnJycnJycnJ11111111111111111111111111111111111111111111111111111111111111111111114AAAA" +
        "UWWIggEvEYoAApERwABKMjgAC+vJycnJycnJycnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAAFFliIQE" +
        "vEYoAApERwABKMjgAC+vJycnJycnJycnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAABRZYiCAS8RigAC" +
        "kRHAAEoyOAAL68nJycnJycnJyddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddeAAAAUWWIhAS8RigACkRHAAEo" +
        "yOAAL68nJycnJycnJyddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddeAAAAFFliIIBLxGKAAKREcAASjI4AAvr" +
        "ycnJycnJycnJ11111111111111111111111111111111111111111111111111111111111111111111114AAABRZYiEBLxGKAAKREcAASjI4AAvrycnJycn" +
        "JycnJ11111111111111111111111111111111111111111111111111111111111111111111114AAAAUWWIggEvEYoAApERwABKMjgAC+vJycnJycnJycnX" +
        "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAAFFliIQEvEYoAApERwABKMjgAC+vJycnJycnJycnXXXXXXXX" +
        "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAABRZYiCAS8RigACkRHAAEoyOAAL68nJycnJycnJyddddddddddddddd" +
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddeAAAAUWWIhAS8RigACkRHAAEoyOAAL68nJycnJycnJydddddddddddddddddddddd" +
        "ddddddddddddddddddddddddddddddddddddddddddddddddeAAAAFFliIIBLxGKAAKREcAASjI4AAvrycnJycnJycnJ1111111111111111111111111111" +
        "1111111111111111111111111111111111111111114AAABRZYiEBLxGKAAKREcAASjI4AAvrycnJycnJycnJ11111111111111111111111111111111111" +
        "111111111111111111111111111111111114AAAAUWWIggEvEYoAApERwABKMjgAC+vJycnJycnJycnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" +
        "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAAFFliIQEvEYoAApERwABKMjgAC+vJycnJycnJycnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" +
        "XXXXXXXXXXXXXXXXXXXXXXgAAABRZYiCAS8RigACkRHAAEoyOAAL68nJycnJycnJyddddddddddddddddddddddddddddddddddddddddddddddddddddddd" +
        "dddddddddddddddeAAAAUWWIhAS8RigACkRHAAEoyOAAL68nJycnJycnJydddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" +
        "ddddddddeAAAAFFliIIBLxGKAAKREcAASjI4AAvrycnJycnJycnJ11111111111111111111111111111111111111111111111111111111111111111111" +
        "114AAABRZYiEBLxGKAAKREcAASjI4AAvrycnJycnJycnJ11111111111111111111111111111111111111111111111111111111111111111111114AAAA" +
        "UWWIggEvEYoAApERwABKMjgAC+vJycnJycnJycnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAAFFliIQE" +
        "vEYoAApERwABKMjgAC+vJycnJycnJycnXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAABRZYiCAS8RigAC" +
        "kRHAAEoyOAAL68nJycnJycnJyddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddeAAAAUWWIhAS8RigACkRHAAEo" +
        "yOAAL68nJycnJycnJyddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddeAAAAFFliIIBLxGKAAKREcAASjI4AAvr" +
        "ycnJycnJycnJ11111111111111111111111111111111111111111111111111111111111111111111114AAABRZYiEBLxGKAAKREcAASjI4AAvrycnJycn" +
        "JycnJ11111111111111111111111111111111111111111111111111111111111111111111114AAAAUWWIggEvEYoAApERwABKMjgAC+vJycnJycnJycnX" +
        "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAAFFliIQEvEYoAApERwABKMjgAC+vJycnJycnJycnXXXXXXXX" +
        "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXgAAABRZYiCAS8RigACkRHAAEoyOAAL68nJycnJycnJyddddddddddddddd" +
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddeAAAAUWWIhAS8RigACkRHAAEoyOAAL68nJycnJycnJydddddddddddddddddddddd" +
        "ddddddddddddddddddddddddddddddddddddddddddddddddeAAAAFFliIIBLxGKAAKREcAASjI4AAvrycnJycnJycnJ1111111111111111111111111111" +
        "1111111111111111111111111111111111111111114=";
}
