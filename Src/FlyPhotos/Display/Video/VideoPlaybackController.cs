using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.Video;

/// <summary>
/// Plays one window's video in an mpv-rendered child HWND kept over the canvas, between the title bar and the bottom
/// panel. A thin translation of <see cref="VideoPlaybackState"/> transitions into mpv commands and ShowWindow.
/// <para>
/// One mpv handle and one child window per window lifetime, both made on the first Play and destroyed only in
/// <see cref="Dispose"/>: an item change unloads the file (<c>stop</c>) and hides the child, and the next video is a
/// <c>loadfile … replace</c> on the same handle. Destroying embedded mpv instances while the window lives (more than
/// one over its lifetime) made WinUI fail-fast in CoreMessaging when the window closed.
/// </para>
/// All public members are called on the UI thread; events are raised on it.
/// </summary>
internal sealed partial class VideoPlaybackController : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const ulong ObserveTimePos = 1, ObserveDuration = 2, ObservePause = 3;

    // How long after the last volume or mute change the settings are saved: a held Shift+Up repeats ~30 times a
    // second, and one save per repeat could also land out of order and keep a stale volume. The window's volume
    // readout shows for as long.
    private static readonly TimeSpan SettingsSaveDelay = TimeSpan.FromMilliseconds(1500);

    private readonly FrameworkElement _canvas;
    private readonly FrameworkElement _titleBar;
    private readonly FrameworkElement _bottomPanel;
    private readonly DispatcherQueue _ui;
    private readonly IntPtr _parent;

    private XamlRoot _xamlRoot;
    private IntPtr _child;
    private MpvHandle _mpv;
    // Files mpv has started (START_FILE count), written on the mpv event thread. Each event is tagged with it, so a
    // late event from an earlier file never applies to the current one: mpv delivers the old file's END_FILE and
    // property changes before the next START_FILE.
    private int _startedFiles;
    // The START_FILE count the file now playing will have (bumped by every loadfile). UI thread.
    private int _currentFile;
    // Completed when mpv reports the end of a file; lets a caller wait until the file is closed. Any thread.
    private TaskCompletionSource _fileClosed;
    // The first position report of each file runs a keyboard-focus check: mpv creates its render window then.
    private bool _focusCheckPending;
    // While the player exists: mpv's render window lives on mpv's thread and can take Win32 focus at any moment (its
    // input queue is attached to ours), so focus is checked a few times a second, not only at known points.
    private DispatcherQueueTimer _focusTimer;
    private double _position;
    private double _duration;
    private bool _disposed;
    // Restarted by each volume or mute change; saves the settings once the changes stop. Made on first use.
    private DispatcherQueueTimer _saveTimer;
    private bool _savePending;
    // The file mpv could not play, until another item is shown (ItemChanged): playing it again would only flash the
    // empty player over the still and fail the same way.
    private string _failedPath;

    public VideoPlaybackState State { get; } = new();

    /// <summary>(position, duration) in seconds, on the UI thread.</summary>
    public event Action<double, double> PositionChanged;

    /// <summary>On the UI thread.</summary>
    public event Action<VideoPhase> PhaseChanged;

    /// <summary>
    /// On the UI thread: keyboard focus had fallen onto the top-level window or the player (mpv's window coming and
    /// going moves Win32 focus) and was handed back to the XAML island; the host makes sure a XAML element has it.
    /// </summary>
    public event Action FocusReturned;

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

    /// <summary>The volume (0–<see cref="VideoPlaybackState.MaxVolume"/>) every video plays at, kept across files.</summary>
    public int Volume => ClampVolume(AppConfig.Settings.VideoVolume);

    /// <summary>Whether every video plays muted, kept across files.</summary>
    public bool Muted => AppConfig.Settings.VideoMuted;

    public void Play(string path)
    {
        if (_disposed) return;
        if (State.Phase == VideoPhase.Idle && string.Equals(path, _failedPath, StringComparison.Ordinal))
        {
            Logger.Debug("Not playing {0}: it failed to play", path);
            return;
        }
        var before = State.Phase;
        var action = State.Play(path);
        try
        {
            switch (action)
            {
                case PlayAction.Load:
                    Load(path);
                    break;
                default:
                    Apply(action);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not play {0}", path);
            Unload();
            State.Stop();
        }
        RaiseIfChanged(before);
        PostFocusCheck();
    }

    public void TogglePause() => Transition(() => Apply(State.TogglePause()));

    public void Seek(double deltaSeconds)
    {
        if (_mpv == null || State.Phase == VideoPhase.Idle) return;
        RunSeek(() => _mpv.Command("seek", Format(deltaSeconds), "relative+exact"));
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
        ScheduleSave();
    }

    public void ToggleMute()
    {
        var muted = !AppConfig.Settings.VideoMuted;
        AppConfig.Settings.VideoMuted = muted;
        SetProperty("mute", muted ? "yes" : "no");
        ScheduleSave();
    }

    // The settings already hold the change; the file is written once the changes stop (or on Dispose).
    private void ScheduleSave()
    {
        if (_disposed) return;
        _savePending = true;
        if (_saveTimer == null)
        {
            _saveTimer = _ui.CreateTimer();
            _saveTimer.Interval = SettingsSaveDelay;
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) =>
            {
                if (!_savePending) return;
                _savePending = false;
                _ = AppConfig.SaveAsync();
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void OverlayOpened() => Transition(() =>
    {
        var action = State.OverlayOpened();
        UpdateVisibility();
        Apply(action);
    });

    public void OverlayClosed()
    {
        Transition(() =>
        {
            State.OverlayClosed();
            Reposition();
            UpdateVisibility();
        });
        PostFocusCheck();
    }

    public void Minimized() => Transition(() => Apply(State.Minimized()));

    /// <summary>
    /// Unloads the file and hides the player; the handle and child window stay for the next video. Called on every
    /// item change, so it is cheap and does nothing when idle.
    /// </summary>
    public void Stop()
    {
        if (_disposed || State.Phase == VideoPhase.Idle) return;
        var before = State.Phase;
        Unload();
        State.Stop();
        RaiseIfChanged(before);
    }

    /// <summary>
    /// The window shows <paramref name="path"/> (every item change): stops a video of another file, and lets a file
    /// that failed to play be tried again once the viewer has moved off it.
    /// </summary>
    public void ItemChanged(string path)
    {
        if (!string.Equals(path, _failedPath, StringComparison.Ordinal)) _failedPath = null;
        if (State.Path != null && path != State.Path) Stop();
    }

    /// <summary>
    /// <see cref="Stop"/>, then waits (up to 2 s) until mpv has closed the file, so the caller can delete or rename
    /// it: mpv opens files without FILE_SHARE_DELETE.
    /// </summary>
    public async Task StopAndReleaseFileAsync()
    {
        if (_disposed || State.Phase == VideoPhase.Idle) return;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _fileClosed, closed);
        Stop();
        if (await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(2))) != closed.Task)
            Logger.Warn("mpv did not report the file closed within 2 s");
    }

    public void Dispose()
    {
        if (_disposed) return;
        var before = State.Phase;
        State.Stop();
        RaiseIfChanged(before);
        _disposed = true;
        _saveTimer?.Stop();
        _saveTimer = null;
        // The window is closing: a volume or mute change still waiting for its save is written now.
        if (_savePending)
        {
            _savePending = false;
            AppConfig.Save();
        }
        Teardown();
        _canvas.SizeChanged -= OnLayoutChanged;
        _titleBar.SizeChanged -= OnLayoutChanged;
        _bottomPanel.SizeChanged -= OnLayoutChanged;
        if (_xamlRoot != null) _xamlRoot.Changed -= OnXamlRootChanged;
        _xamlRoot = null;
        PositionChanged = null;
        PhaseChanged = null;
        FocusReturned = null;
    }

    // ---- player lifetime ----

    // The handle and the child window, made once.
    private void EnsurePlayer()
    {
        if (_mpv != null) return;
        if (!MpvHandle.TryLoad()) throw new InvalidOperationException("libmpv is not available");

        TrackXamlRoot();
        // WS_DISABLED: the child never takes focus or clicks, so the keyboard stays with the WinUI window. Created
        // hidden; Load shows it.
        var style = Win32.WS_CHILD | Win32.WS_DISABLED | Win32.WS_CLIPSIBLINGS;
        _child = Win32.CreateWindowEx(0, "STATIC", "", style, 0, 0, 16, 16, _parent, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero);
        if (_child == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        HookParent();

        try
        {
            var mpv = MpvHandle.Create(new Dictionary<string, string>
            {
                ["wid"] = _child.ToInt64().ToString(CultureInfo.InvariantCulture),
                ["input-default-bindings"] = "no",
                ["input-vo-keyboard"] = "no",
                ["input-cursor"] = "no",
                ["osc"] = "no",
                ["osd-level"] = "0",
                ["hwdec"] = "auto-safe",
                // At the end mpv keeps the last frame and pauses (no END_FILE); see OnFlag. END_FILE then means
                // the file could not be played (OnEndFile).
                ["keep-open"] = "yes",
                // Keep mpv's render window between files, so it is made once rather than per video.
                ["force-window"] = "yes",
                ["idle"] = "yes",
                ["volume"] = ClampVolume(AppConfig.Settings.VideoVolume).ToString(CultureInfo.InvariantCulture),
                ["mute"] = AppConfig.Settings.VideoMuted ? "yes" : "no",
            });
            mpv.StartFile += () => Interlocked.Increment(ref _startedFiles);
            mpv.DoublePropertyChanged += (name, value) => Post(() => OnDouble(name, value));
            mpv.FlagPropertyChanged += (name, value) => Post(() => OnFlag(name, value));
            mpv.EndFile += (reason, error) =>
            {
                Interlocked.Exchange(ref _fileClosed, null)?.TrySetResult();
                Post(() => OnEndFile(reason, error));
            };
            mpv.Observe(ObserveTimePos, "time-pos");
            mpv.Observe(ObserveDuration, "duration");
            mpv.Observe(ObservePause, "pause");
            mpv.StartEventLoop();
            _mpv = mpv;
            _focusTimer = _ui.CreateTimer();
            _focusTimer.Interval = TimeSpan.FromMilliseconds(250);
            _focusTimer.Tick += (_, _) => ReturnFocusIfLost();
            _focusTimer.Start();
        }
        catch
        {
            UnhookParent();
            Win32.DestroyWindow(_child);
            _child = IntPtr.Zero;
            throw;
        }
    }

    // ---- cursor over the video ----
    // The child is disabled (so it never takes focus), and nothing sets the cursor over it: it kept whatever shape it
    // last had (the start-up wait wheel, or a resize arrow from crossing the window edge). WM_SETCURSOR bubbles from
    // mpv's render window through the child to the top-level window, so the top-level is subclassed to answer it.

    // Top-level window → (owner, original window proc). The entry stays after unhooking (Owner null) so a message
    // already on its way still reaches the original proc. Touched only on the UI thread.
    private static readonly Dictionary<IntPtr, (VideoPlaybackController Owner, IntPtr OldProc)> Subclassed = new();
    private static readonly IntPtr ArrowCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW);

    private unsafe void HookParent()
    {
        if (Subclassed.TryGetValue(_parent, out var entry) && entry.Owner != null) return;
        delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> proc = &ParentProc;
        if (entry.OldProc != IntPtr.Zero && Win32.GetWindowLongPtr(_parent, Win32.GWLP_WNDPROC) == (IntPtr)proc)
        {
            Subclassed[_parent] = (this, entry.OldProc); // still ours from an earlier hook
            return;
        }
        var old = Win32.SetWindowLongPtr(_parent, Win32.GWLP_WNDPROC, (IntPtr)proc);
        if (old == IntPtr.Zero)
        {
            Logger.Warn("Subclassing the window for the video cursor failed: {0}", Marshal.GetLastPInvokeError());
            return;
        }
        Subclassed[_parent] = (this, old);
    }

    private void UnhookParent()
    {
        if (!Subclassed.TryGetValue(_parent, out var entry) || entry.Owner != this) return;
        // Leave the proc in place (routing straight to the original) rather than restoring it: another subclass may
        // have chained after ours, and restoring would cut it off.
        Subclassed[_parent] = (null, entry.OldProc);
    }

    [UnmanagedCallersOnly]
    private static IntPtr ParentProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!Subclassed.TryGetValue(hwnd, out var entry)) return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        try
        {
            if (msg == Win32.WM_SETCURSOR && entry.Owner != null && entry.Owner.IsOverVideo(wParam, lParam))
            {
                Win32.SetCursor(ArrowCursor);
                return 1;
            }
        }
        catch (Exception ex) { Logger.Warn(ex, "Video cursor hook failed"); }
        return Win32.CallWindowProc(entry.OldProc, hwnd, msg, wParam, lParam);
    }

    // Whether the cursor is over the shown video: the window under it is the child or mpv's window inside it, or the
    // top-level reports its client area at a point inside the child. Resize borders keep their own cursors.
    private bool IsOverVideo(IntPtr hitWindow, IntPtr lParam)
    {
        if (_child == IntPtr.Zero || _disposed || !Win32.IsWindowVisible(_child)) return false;
        if (hitWindow == _child || Win32.IsChild(_child, hitWindow)) return true;
        var hit = (short)(lParam.ToInt64() & 0xFFFF);
        if (hit != Win32.HTCLIENT && hit != Win32.HTNOWHERE && hit != Win32.HTERROR) return false;
        return Win32.GetCursorPos(out var p) && Win32.GetWindowRect(_child, out var r) &&
               p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    private void Load(string path)
    {
        EnsurePlayer();
        _position = 0;
        _duration = 0;
        _focusCheckPending = true;
        // A paused previous file leaves pause set; the new one starts playing.
        _mpv.SetString("pause", "no");
        // Counted only for a loadfile mpv accepted: every accepted one starts a file (START_FILE), even a broken one.
        _currentFile++;
        try { _mpv.Command("loadfile", path, "replace"); }
        catch
        {
            _currentFile--;
            throw;
        }
        Reposition();
        UpdateVisibility();
        Logger.Debug("Playing {0}", path);
    }

    // Unloads the file and hides the child. The handle stays.
    private void Unload()
    {
        if (_child != IntPtr.Zero) Win32.ShowWindow(_child, Win32.SW_HIDE);
        if (_mpv == null) return;
        try { _mpv.Command("stop"); }
        catch (Exception ex) { Logger.Warn(ex, "mpv stop failed"); }
    }

    private void Teardown()
    {
        _focusTimer?.Stop();
        _focusTimer = null;
        UnhookParent();
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

    // Shown while a file is loaded and no overlay covers it.
    private void UpdateVisibility()
    {
        if (_child == IntPtr.Zero) return;
        var show = State.Phase != VideoPhase.Idle && !State.Hidden;
        Win32.ShowWindow(_child, show ? Win32.SW_SHOWNA : Win32.SW_HIDE);
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

    // ---- keyboard focus ----

    // Posted, so it runs after whatever moved focus in this turn.
    private void PostFocusCheck() => _ui.TryEnqueue(ReturnFocusIfLost);

    // Win32 focus on the top-level window or inside the player means no XAML element gets keys: hand it back to the
    // island's input window. Focus anywhere else (a popup, another window) is left alone.
    private void ReturnFocusIfLost()
    {
        if (_disposed || Win32.GetForegroundWindow() != _parent) return;
        var focus = Win32.GetFocus();
        var lost = focus == _parent || (_child != IntPtr.Zero && (focus == _child || Win32.IsChild(_child, focus)));
        if (!lost) return;
        var input = FindDescendant(_parent, "InputSiteWindowClass");
        if (input == IntPtr.Zero) return;
        Win32.SetFocus(input);
        Logger.Debug("Keyboard focus returned to the viewer (it was on {0})", focus == _parent ? "the window" : "the player");
        FocusReturned?.Invoke();
    }

    private static IntPtr FindDescendant(IntPtr parent, string className)
    {
        for (var child = Win32.FindWindowEx(parent, IntPtr.Zero, null, null); child != IntPtr.Zero;
             child = Win32.FindWindowEx(parent, child, null, null))
        {
            if (Win32.ClassNameIs(child, className)) return child;
            var deeper = FindDescendant(child, className);
            if (deeper != IntPtr.Zero) return deeper;
        }
        return IntPtr.Zero;
    }

    // ---- mpv events (marshalled) ----

    // Runs on the mpv event thread: never block here (Dispose joins this thread from the UI thread), just queue,
    // tagged with the file the event belongs to.
    private void Post(Action action)
    {
        var file = Volatile.Read(ref _startedFiles);
        _ui.TryEnqueue(() =>
        {
            // From an earlier file, an unloaded one, or a disposed player.
            if (_disposed || file != _currentFile || State.Phase == VideoPhase.Idle) return;
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
        if (_focusCheckPending)
        {
            _focusCheckPending = false;
            PostFocusCheck();
        }
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

    // The current file ended without keep-open holding it: a load or decode error (or a stop mpv made itself). Spec
    // section 4: log it, stop, and keep showing the still. Unloading hides the player, which would otherwise stay
    // over the still as a black box (force-window keeps mpv's window), and Idle makes Space load the file afresh.
    private void OnEndFile(int reason, int error)
    {
        if (reason == NativeMpv.EndFileError)
            Logger.Warn("Could not play {0}: {1}", State.Path, NativeMpv.Error(error));
        else
            Logger.Info("Playback of {0} ended ({1})", State.Path, NativeMpv.EndFileReasonName(reason));
        var path = State.Path;
        Stop();
        _failedPath = path;
    }

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
        internal const uint WS_CHILD = 0x40000000, WS_DISABLED = 0x08000000, WS_CLIPSIBLINGS = 0x04000000;
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

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetFocus();

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        internal static partial IntPtr SetFocus(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsChild(IntPtr parent, IntPtr hwnd);

        [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);

        internal const uint WM_SETCURSOR = 0x0020;
        internal const short HTERROR = -2, HTNOWHERE = 0, HTCLIENT = 1;
        internal const int GWLP_WNDPROC = -4;
        internal const int IDC_ARROW = 32512;

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static partial IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static partial IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
        internal static partial IntPtr CallWindowProc(IntPtr proc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static partial IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
        internal static partial IntPtr LoadCursor(IntPtr instance, int name);

        [LibraryImport("user32.dll")]
        internal static partial IntPtr SetCursor(IntPtr cursor);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindowVisible(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetCursorPos(out POINT point);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
        private static unsafe partial int GetClassName(IntPtr hwnd, char* buffer, int max);

        internal static unsafe bool ClassNameIs(IntPtr hwnd, string name)
        {
            var buffer = stackalloc char[256];
            var length = GetClassName(hwnd, buffer, 256);
            return new ReadOnlySpan<char>(buffer, length).SequenceEqual(name);
        }
    }
}
