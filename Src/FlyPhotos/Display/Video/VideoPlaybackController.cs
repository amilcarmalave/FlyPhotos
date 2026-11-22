using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.Video;

/// <summary>
/// Plays one window's video in an mpv-rendered child HWND kept over the canvas, between the title bar and the bottom
/// panel. A thin translation of <see cref="VideoPlaybackState"/> transitions into mpv commands and ShowWindow.
/// All public members are called on the UI thread; events are raised on it.
/// </summary>
internal sealed partial class VideoPlaybackController : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const ulong ObserveTimePos = 1, ObserveDuration = 2, ObservePause = 3;

    private readonly FrameworkElement _canvas;
    private readonly FrameworkElement _titleBar;
    private readonly FrameworkElement _bottomPanel;
    private readonly DispatcherQueue _ui;
    private readonly IntPtr _parent;

    private XamlRoot _xamlRoot;
    private IntPtr _child;
    private MpvHandle _mpv;
    // Bumped on every teardown; callbacks queued by an older player compare it and drop themselves.
    private int _generation;
    private double _position;
    private double _duration;
    private bool _disposed;

    public VideoPlaybackState State { get; } = new();

    /// <summary>(position, duration) in seconds, on the UI thread.</summary>
    public event Action<double, double> PositionChanged;

    /// <summary>On the UI thread.</summary>
    public event Action<VideoPhase> PhaseChanged;

    public VideoPlaybackController(Window window, FrameworkElement canvas, FrameworkElement titleBar,
        FrameworkElement bottomPanel, DispatcherQueue ui)
    {
        _canvas = canvas;
        _titleBar = titleBar;
        _bottomPanel = bottomPanel;
        _ui = ui;
        _parent = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _canvas.SizeChanged += OnLayoutChanged;
        _titleBar.SizeChanged += OnLayoutChanged;
        _bottomPanel.SizeChanged += OnLayoutChanged;
    }

    public static int ClampVolume(int volume) => VideoPlaybackState.ClampVolume(volume);

    public void Play(string path)
    {
        if (_disposed) return;
        var before = State.Phase;
        var action = State.Play(path);
        try
        {
            switch (action)
            {
                case PlayAction.Load:
                    Start(path);
                    break;
                default:
                    Apply(action);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not play {0}", path);
            Teardown();
            State.Stop();
        }
        RaiseIfChanged(before);
    }

    public void TogglePause() => Transition(() => Apply(State.TogglePause()));

    public void Seek(double deltaSeconds)
    {
        if (_mpv == null || State.Phase == VideoPhase.Idle) return;
        RunSeek(() => _mpv.Command("seek", Format(deltaSeconds), "relative"));
    }

    public void SeekTo(double seconds, bool exact)
    {
        if (_mpv == null || State.Phase == VideoPhase.Idle) return;
        RunSeek(() => _mpv.Command("seek", Format(Math.Max(0, seconds)), exact ? "absolute" : "absolute+keyframes"));
    }

    public void ChangeVolume(int delta)
    {
        var volume = ClampVolume(ClampVolume(AppConfig.Settings.VideoVolume) + delta);
        AppConfig.Settings.VideoVolume = volume;
        SetProperty("volume", volume.ToString(CultureInfo.InvariantCulture));
        _ = AppConfig.SaveAsync();
    }

    public void ToggleMute()
    {
        var muted = !AppConfig.Settings.VideoMuted;
        AppConfig.Settings.VideoMuted = muted;
        SetProperty("mute", muted ? "yes" : "no");
        _ = AppConfig.SaveAsync();
    }

    public void OverlayOpened() => Transition(() =>
    {
        var action = State.OverlayOpened();
        UpdateVisibility();
        Apply(action);
    });

    public void OverlayClosed() => Transition(() =>
    {
        State.OverlayClosed();
        Reposition();
        UpdateVisibility();
    });

    public void Minimized() => Transition(() => Apply(State.Minimized()));

    /// <summary>Terminates mpv and destroys the child window. Called on every item change, so it never waits on
    /// anything but <see cref="MpvHandle.Dispose"/>.</summary>
    public void Stop()
    {
        var before = State.Phase;
        Teardown();
        State.Stop();
        RaiseIfChanged(before);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _canvas.SizeChanged -= OnLayoutChanged;
        _titleBar.SizeChanged -= OnLayoutChanged;
        _bottomPanel.SizeChanged -= OnLayoutChanged;
        if (_xamlRoot != null) _xamlRoot.Changed -= OnXamlRootChanged;
        _xamlRoot = null;
        PositionChanged = null;
        PhaseChanged = null;
    }

    // ---- player lifetime ----

    private void Start(string path)
    {
        Teardown();
        if (!MpvHandle.TryLoad()) throw new InvalidOperationException("libmpv is not available");

        TrackXamlRoot();
        // WS_DISABLED: the child never takes focus or clicks, so the keyboard stays with the WinUI window.
        var style = Win32.WS_CHILD | Win32.WS_DISABLED | Win32.WS_CLIPSIBLINGS | (State.Hidden ? 0u : Win32.WS_VISIBLE);
        _child = Win32.CreateWindowEx(0, "STATIC", "", style, 0, 0, 16, 16, _parent, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero);
        if (_child == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        Reposition();

        var mpv = MpvHandle.Create(new Dictionary<string, string>
        {
            ["wid"] = _child.ToInt64().ToString(CultureInfo.InvariantCulture),
            ["input-default-bindings"] = "no",
            ["input-vo-keyboard"] = "no",
            ["input-cursor"] = "no",
            ["osc"] = "no",
            ["osd-level"] = "0",
            ["hwdec"] = "auto-safe",
            // At the end mpv keeps the last frame and pauses (no END_FILE); see OnPause.
            ["keep-open"] = "yes",
            ["volume"] = ClampVolume(AppConfig.Settings.VideoVolume).ToString(CultureInfo.InvariantCulture),
            ["mute"] = AppConfig.Settings.VideoMuted ? "yes" : "no",
        });
        _mpv = mpv;
        _position = 0;
        _duration = 0;

        var generation = _generation;
        mpv.DoublePropertyChanged += (name, value) => Post(generation, () => OnDouble(name, value));
        mpv.FlagPropertyChanged += (name, value) => Post(generation, () => OnFlag(name, value));
        mpv.EndFile += () => Post(generation, OnEndFile);
        mpv.Observe(ObserveTimePos, "time-pos");
        mpv.Observe(ObserveDuration, "duration");
        mpv.Observe(ObservePause, "pause");
        mpv.StartEventLoop();
        mpv.Command("loadfile", path);
        Logger.Debug("Playing {0}", path);
    }

    private void Teardown()
    {
        _generation++;
        var mpv = _mpv;
        _mpv = null;
        if (mpv != null)
        {
            try { mpv.Dispose(); }
            catch (Exception ex) { Logger.Warn(ex, "mpv teardown failed"); }
        }
        if (_child != IntPtr.Zero)
        {
            Win32.DestroyWindow(_child);
            _child = IntPtr.Zero;
        }
    }

    // ---- state → mpv ----

    private void Apply(PlayAction action)
    {
        switch (action)
        {
            case PlayAction.Pause:
                SetProperty("pause", "yes");
                break;
            case PlayAction.Resume:
                SetProperty("pause", "no");
                break;
            case PlayAction.Restart:
                if (_mpv == null) return;
                try
                {
                    _mpv.Command("seek", "0", "absolute");
                    _mpv.SetString("pause", "no");
                }
                catch (MpvException ex) { Logger.Warn(ex, "Could not restart the video"); }
                break;
        }
    }

    private void SetProperty(string name, string value)
    {
        if (_mpv == null) return;
        try { _mpv.SetString(name, value); }
        catch (MpvException ex) { Logger.Warn(ex, "Could not set {0}={1}", name, value); }
    }

    private void RunSeek(Action seek)
    {
        try
        {
            seek();
            Transition(State.Seeked);
        }
        catch (MpvException ex) { Logger.Debug(ex, "Seek failed"); }
    }

    private void UpdateVisibility()
    {
        if (_child == IntPtr.Zero) return;
        Win32.ShowWindow(_child, State.Hidden ? Win32.SW_HIDE : Win32.SW_SHOWNA);
    }

    private void Transition(Action change)
    {
        if (_disposed) return;
        var before = State.Phase;
        change();
        RaiseIfChanged(before);
    }

    private void RaiseIfChanged(VideoPhase before)
    {
        if (State.Phase != before) PhaseChanged?.Invoke(State.Phase);
    }

    // ---- mpv events (marshalled) ----

    // Runs on the mpv event thread: never block here (Dispose joins this thread from the UI thread), just queue.
    private void Post(int generation, Action action)
    {
        _ui.TryEnqueue(() =>
        {
            if (generation != _generation || _disposed) return; // from a player torn down since
            action();
        });
    }

    private void OnDouble(string name, double value)
    {
        switch (name)
        {
            case "time-pos": _position = value; break;
            case "duration": _duration = value; break;
            default: return;
        }
        PositionChanged?.Invoke(_position, _duration);
    }

    private void OnFlag(string name, bool value)
    {
        // With keep-open mpv pauses by itself at the end. A pause we asked for has already moved the state off
        // Playing, so a pause seen while Playing is either the end or a late echo; eof-reached tells them apart.
        if (name != "pause" || !value || State.Phase != VideoPhase.Playing || _mpv == null) return;
        string eof;
        try { eof = _mpv.GetString("eof-reached"); }
        catch (Exception ex) { Logger.Debug(ex, "eof-reached unavailable"); return; }
        if (eof == "yes") Transition(State.Ended);
    }

    private void OnEndFile() => Transition(State.Ended);

    // ---- placement ----

    private void OnLayoutChanged(object sender, SizeChangedEventArgs e) => Reposition();

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Reposition();

    private void TrackXamlRoot()
    {
        var root = _canvas.XamlRoot;
        if (ReferenceEquals(root, _xamlRoot)) return;
        if (_xamlRoot != null) _xamlRoot.Changed -= OnXamlRootChanged;
        _xamlRoot = root;
        if (_xamlRoot != null) _xamlRoot.Changed += OnXamlRootChanged;
    }

    // Physical pixels: the canvas bounds minus the title bar at the top and the bottom panel at the bottom.
    private void Reposition()
    {
        if (_child == IntPtr.Zero || _disposed) return;
        TrackXamlRoot();
        if (_xamlRoot == null) return;
        var scale = _xamlRoot.RasterizationScale;
        var origin = _canvas.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var top = _titleBar.ActualHeight;
        var height = Math.Max(1, _canvas.ActualHeight - top - _bottomPanel.ActualHeight);
        var width = Math.Max(1, _canvas.ActualWidth);
        Win32.SetWindowPos(_child, Win32.HWND_TOP, (int)Math.Round(origin.X * scale),
            (int)Math.Round((origin.Y + top) * scale), (int)Math.Round(width * scale), (int)Math.Round(height * scale),
            Win32.SWP_NOACTIVATE);
    }

    private static string Format(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static partial class Win32
    {
        internal const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_DISABLED = 0x08000000,
            WS_CLIPSIBLINGS = 0x04000000;
        internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const int SW_HIDE = 0, SW_SHOWNA = 8;

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ShowWindow(IntPtr hwnd, int cmd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyWindow(IntPtr hwnd);
    }
}
