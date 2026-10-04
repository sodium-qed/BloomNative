using System;
using System.IO;
using System.Reflection;
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
                // Exercise WPF and briefly show an owned synthetic pixel fixture.
                // No Explorer windows, wallpaper settings, camera or network are changed.
                var window = new Window { Width = 240, Height = 120, ShowInTaskbar = false, Content = new TextBlock { Text = "Bloom Native smoke check" } };
                window.Show();
                window.UpdateLayout();
                bool passed = window.IsVisible && System.Windows.Forms.Screen.AllScreens.Length > 0;
                window.Close();
                var desktopAttachment = DesktopAttachmentChecks.Run();
                passed &= desktopAttachment.Passed;
                // Unsupported media stacks are reported explicitly. Once the
                // ordinary decoder baseline works, either host failing is fatal.
                var mediaComposition = MediaCompositionChecks.Run();
                // Exercise the bundled WinRT projection without enumerating or opening devices.
                bool cameraApiAvailable = global::Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Media.Capture.MediaCapture") &&
                    global::Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Media.Capture.Frames.MediaFrameReader");
                bool cameraPixelCopy = cameraApiAvailable && CameraCapture.CheckPixelConversion();
                passed &= cameraApiAvailable && cameraPixelCopy;
                string assemblyVersion = typeof(App).Assembly.GetName().Version!.ToString(3);
                bool versionMatches = assemblyVersion == ApplicationInfo.Version;
                passed &= versionMatches;
                File.WriteAllText(options.TestOutput!, JsonSerializer.Serialize(new
                {
                    passed,
                    version = ApplicationInfo.Version,
                    build = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    versionMatches,
                    runtime = Environment.Version.ToString(),
                    os = Environment.OSVersion.ToString(),
                    desktopAttachment = new { passed = desktopAttachment.Passed, assertions = desktopAttachment.Assertions,
                        compositionVerified = desktopAttachment.CompositionVerified, scope = desktopAttachment.Scope },
                    mediaComposition = new { verified = mediaComposition.Verified, assertions = mediaComposition.Assertions,
                        status = mediaComposition.Status, reason = mediaComposition.Reason, scope = mediaComposition.Scope },
                    cameraApiAvailable,
                    cameraPixelCopy,
                    scope = "WPF initialization, owned on-screen HWND/DWM pixel fixture, conditional synthetic decoded-media composition, WinRT API availability and in-memory camera pixel conversion; no camera activation, creator artwork, real Explorer embedding, or hardware validation"
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
                MessageBox.Show($"You opened Bloom Native Windows {ApplicationInfo.Version}, but another Bloom instance is already running. Quit that instance from its controls or tray menu, then open this file again.", ApplicationInfo.DisplayName);
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
