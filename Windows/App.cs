using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace BloomNative.Windows;

internal sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        if (options.Mode == LaunchMode.Invalid) return 2;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            if (options.Mode == LaunchMode.SelfTest)
            {
                // Exercise actual WPF initialization on a Windows runner, without network or desktop mutations.
                var window = new Window { Width = 240, Height = 120, ShowInTaskbar = false, Content = new TextBlock { Text = "Bloom Native smoke check" } };
                window.Show();
                window.UpdateLayout();
                bool passed = window.IsVisible && System.Windows.Forms.Screen.AllScreens.Length > 0;
                window.Close();
                var desktopAttachment = DesktopAttachmentChecks.Run();
                passed &= desktopAttachment.Passed;
                // Exercise the bundled WinRT projection without enumerating or opening devices.
                bool cameraApiAvailable = global::Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Media.Capture.MediaCapture") &&
                    global::Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Media.Capture.Frames.MediaFrameReader");
                bool cameraPixelCopy = cameraApiAvailable && CameraCapture.CheckPixelConversion();
                passed &= cameraApiAvailable && cameraPixelCopy;
                File.WriteAllText(options.TestOutput!, JsonSerializer.Serialize(new
                {
                    passed,
                    runtime = Environment.Version.ToString(),
                    os = Environment.OSVersion.ToString(),
                    desktopAttachment = new { passed = desktopAttachment.Passed, assertions = desktopAttachment.Assertions, scope = desktopAttachment.Scope },
                    cameraApiAvailable,
                    cameraPixelCopy,
                    scope = "WPF initialization, owned HWND desktop-attachment fixture, WinRT API availability and in-memory camera pixel conversion; no camera activation, artwork, playback, real Explorer embedding, or hardware validation"
                }));
                return passed ? 0 : 1;
            }
            if (options.Mode is LaunchMode.Saver or LaunchMode.Preview)
            {
                if (!Artwork.Verify(Artwork.VideoPath)) return 1;
                ScreenSaver.CreateWindows(Artwork.VideoPath, options.Parent);
                return app.Run();
            }
            using var mutex = new Mutex(true, "Local\\BloomNative.Windows.Controls", out bool first);
            if (!first)
            {
                MessageBox.Show("Bloom Native is already running. Open its controls from the system tray.", "Bloom Native");
                return 0;
            }
            var controls = new MainWindow();
            app.MainWindow = controls;
            controls.Show();
            return app.Run();
        }
        catch (Exception ex)
        {
            if (options.Mode == LaunchMode.SelfTest)
            {
                if (options.TestOutput != null) File.WriteAllText(options.TestOutput, JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
                return 1;
            }
            MessageBox.Show(ex.Message, "Bloom Native", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
