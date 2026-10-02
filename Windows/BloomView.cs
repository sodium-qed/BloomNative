using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BloomNative.Windows;

/// <summary>A muted, seekable view of the original Bloom animation.</summary>
public sealed class BloomView : UserControl, IDisposable
{
    private readonly string videoPath;
    private readonly MediaElement media;
    private readonly ScaleTransform scale = new ScaleTransform(1, 1);
    private readonly TranslateTransform translation = new TranslateTransform();
    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = new Stopwatch();
    private bool sourceAssigned;
    private bool opened;
    private bool disposed;
    private bool suspended;
    private bool replaying;
    private bool breathe = true;
    private double target = 1;
    private double lastTick;
    private double motionTime;
    private TimeSpan lastFrame;

    public event Action<string>? PlaybackFailed;

    public BloomView(string videoPath)
    {
        this.videoPath = videoPath;
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(148, 186, 212));
        media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            IsMuted = true,
            Volume = 0,
            Stretch = Stretch.UniformToFill,
            RenderTransformOrigin = new Point(0.5, 0.5),
            IsHitTestVisible = false
        };
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(translation);
        media.RenderTransform = transforms;
        Content = media;
        timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1.0 / 30.0)
        };
        timer.Tick += OnTick;
        media.MediaOpened += OnMediaOpened;
        media.MediaFailed += OnMediaFailed;
        media.MediaEnded += OnMediaEnded;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    public bool Breathe
    {
        get => breathe;
        set
        {
            if (disposed) return;
            breathe = value;
            if (!MotionEnabled) ResetTransform();
            UpdateActivity();
        }
    }

    /// <summary>Pause on a frame. Values outside [0, 1] are clamped.</summary>
    public void SetProgress(double normalized)
    {
        if (disposed) return;
        target = BloomMath.ClampProgress(normalized);
        replaying = false;
        if (opened)
        {
            media.Pause();
            media.Position = TargetPosition;
        }
        UpdateActivity();
    }

    /// <summary>Play from the beginning at the video's native speed, stopping at the target.</summary>
    public void ReplayTo(double normalized)
    {
        if (disposed) return;
        target = BloomMath.ClampProgress(normalized);
        replaying = target > 0;
        if (opened)
        {
            media.Pause();
            media.Position = TimeSpan.Zero;
        }
        UpdateActivity();
    }

    /// <summary>Freeze playback and breathing while the host is minimized or inactive.</summary>
    public void SetSuspended(bool value)
    {
        if (disposed || suspended == value) return;
        suspended = value;
        UpdateActivity();
    }

    private bool Active => !disposed && !suspended && IsLoaded && IsVisible;
    private bool MotionEnabled => breathe && SystemParameters.ClientAreaAnimation;
    private TimeSpan TargetPosition => TimeSpan.FromTicks((long)(lastFrame.Ticks * target));

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (disposed) return;
        if (!sourceAssigned)
        {
            sourceAssigned = true;
            try
            {
                if (!File.Exists(videoPath))
                {
                    Fail("BloomOriginal.mp4 was not found. Rebuild with the original artwork or restore the complete application folder.");
                    return;
                }
                media.Source = new Uri(Path.GetFullPath(videoPath), UriKind.Absolute);
                // Manual MediaElement behavior needs an explicit command to open the source.
                // Pause plus scrubbing decodes a still frame without starting a visible replay.
                media.Pause();
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is NotSupportedException)
            {
                Fail("Could not open the Bloom video: " + exception.Message);
            }
        }
        UpdateActivity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => UpdateActivity();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateActivity();

    private void OnMediaOpened(object sender, RoutedEventArgs args)
    {
        if (disposed) return;
        if (!media.NaturalDuration.HasTimeSpan || media.NaturalDuration.TimeSpan <= TimeSpan.Zero)
        {
            Fail("The Bloom video has no seekable duration. Use the original, complete BloomOriginal.mp4 file.");
            return;
        }
        // Seeking exactly to EOF can produce a black frame. Keep the final still before EOF.
        lastFrame = TimeSpan.FromSeconds(Math.Max(0, media.NaturalDuration.TimeSpan.TotalSeconds - 1.0 / 60.0));
        opened = true;
        media.Pause();
        media.Position = replaying ? TimeSpan.Zero : TargetPosition;
        UpdateActivity();
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs args)
    {
        if (!disposed)
            Fail("Windows could not decode the Bloom video. Check the MP4 file and install Media Feature Pack on Windows N. " + args.ErrorException.Message);
    }

    private void OnMediaEnded(object sender, RoutedEventArgs args)
    {
        if (disposed || !opened) return;
        FinishReplay();
    }

    private void FinishReplay()
    {
        replaying = false;
        media.Pause();
        media.Position = TargetPosition;
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        if (disposed) return;
        if (opened)
        {
            if (Active && replaying) media.Play();
            else media.Pause();
        }
        if (Active && opened && (replaying || MotionEnabled))
        {
            if (!timer.IsEnabled)
            {
                clock.Restart();
                lastTick = 0;
                timer.Start();
            }
        }
        else
        {
            timer.Stop();
            clock.Stop();
        }
    }

    private void OnTick(object? sender, EventArgs args)
    {
        if (!Active || !opened) { UpdateActivity(); return; }
        double now = clock.Elapsed.TotalSeconds;
        double elapsed = Math.Min(0.1, Math.Max(0, now - lastTick));
        lastTick = now;
        if (replaying && media.Position >= TargetPosition) FinishReplay();
        if (MotionEnabled)
        {
            motionTime += elapsed;
            double zoom = 1.015 + 0.008 * Math.Sin(motionTime * 0.42);
            scale.ScaleX = scale.ScaleY = zoom;
            translation.X = Math.Sin(motionTime * 0.25) * ActualWidth * 0.003;
            translation.Y = Math.Cos(motionTime * 0.19) * ActualHeight * 0.003;
        }
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSystemParametersChanged(sender, args)));
            return;
        }
        if (disposed) return;
        if (!MotionEnabled) ResetTransform();
        UpdateActivity();
    }

    private void ResetTransform()
    {
        scale.ScaleX = scale.ScaleY = 1;
        translation.X = translation.Y = 0;
    }

    private void Fail(string message)
    {
        opened = false;
        replaying = false;
        timer.Stop();
        clock.Stop();
        media.Close();
        PlaybackFailed?.Invoke(message);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        clock.Stop();
        timer.Tick -= OnTick;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        IsVisibleChanged -= OnVisibilityChanged;
        media.MediaOpened -= OnMediaOpened;
        media.MediaFailed -= OnMediaFailed;
        media.MediaEnded -= OnMediaEnded;
        media.Close();
        media.Source = null;
        PlaybackFailed = null;
    }
}
