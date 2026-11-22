// THROWAWAY SPIKE — answers one question: can an embedded libmpv child window sit over the Win2D canvas,
// follow its size, and leave keyboard focus with the WinUI window (so Left/Right still navigate photos)?
// Active only when FLY_SPIKE_VIDEO is set (to a file path or an mpv URL such as av://lavfi:testsrc).
using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.Video;

internal static partial class Mpv
{
    private const string Lib = "libmpv-2.dll";
    [LibraryImport(Lib)] internal static partial IntPtr mpv_create();
    [LibraryImport(Lib)] internal static partial int mpv_initialize(IntPtr ctx);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_option_string(IntPtr ctx, string name, string data);
    [LibraryImport(Lib)] internal static partial int mpv_command(IntPtr ctx, IntPtr[] args);
    [LibraryImport(Lib)] internal static partial void mpv_terminate_destroy(IntPtr ctx);
    [LibraryImport(Lib)] internal static partial IntPtr mpv_error_string(int error);

    internal static int Command(IntPtr ctx, params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            return mpv_command(ctx, ptrs);
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    internal static string Error(int code) => Marshal.PtrToStringUTF8(mpv_error_string(code));
}

internal static partial class SpikeWin32
{
    internal const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_DISABLED = 0x08000000,
        WS_CLIPSIBLINGS = 0x04000000;
    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
    internal const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hwnd);
}

/// <summary>Spike: an mpv-rendered child HWND kept over a XAML element's bounds.</summary>
internal sealed class VideoSpike
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly FrameworkElement _host;
    private readonly FrameworkElement _bottomPanel;
    private readonly IntPtr _parent;
    private IntPtr _child;
    private IntPtr _mpv;

    private VideoSpike(Window window, FrameworkElement host, FrameworkElement bottomPanel)
    {
        _host = host;
        _bottomPanel = bottomPanel;
        _parent = WinRT.Interop.WindowNative.GetWindowHandle(window);
        host.Loaded += (_, _) => Start(Environment.GetEnvironmentVariable("FLY_SPIKE_VIDEO"));
        host.SizeChanged += (_, _) => Reposition();
        bottomPanel.SizeChanged += (_, _) => Reposition();
        window.Closed += (_, _) => Stop();
    }

    public static VideoSpike TryAttach(Window window, FrameworkElement host, FrameworkElement bottomPanel) =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLY_SPIKE_VIDEO"))
            ? null
            : new VideoSpike(window, host, bottomPanel);

    private void Start(string source)
    {
        try
        {
            // WS_DISABLED: the child never takes focus or clicks, so the keyboard stays with the WinUI window.
            _child = SpikeWin32.CreateWindowEx(0, "STATIC", "", SpikeWin32.WS_CHILD | SpikeWin32.WS_VISIBLE |
                SpikeWin32.WS_DISABLED | SpikeWin32.WS_CLIPSIBLINGS, 0, 0, 16, 16, _parent, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero);
            Reposition();

            _mpv = Mpv.mpv_create();
            Set("wid", _child.ToInt64().ToString());
            Set("input-default-bindings", "no");
            Set("input-vo-keyboard", "no");
            Set("input-cursor", "no");
            Set("osc", "no");
            Set("hwdec", "auto-safe");
            Set("loop-file", "inf");
            Set("keep-open", "yes");
            var rc = Mpv.mpv_initialize(_mpv);
            Logger.Info("Spike: mpv_initialize -> {0}", rc);
            rc = Mpv.Command(_mpv, "loadfile", source);
            Logger.Info("Spike: loadfile {0} -> {1} {2}", source, rc, Mpv.Error(rc));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spike: mpv start failed");
        }
    }

    private void Set(string name, string value)
    {
        var rc = Mpv.mpv_set_option_string(_mpv, name, value);
        if (rc < 0) Logger.Warn("Spike: option {0}={1} -> {2}", name, value, Mpv.Error(rc));
    }

    // Keeps the child over the canvas, minus the bottom panel (thumbnail strip + buttons stay visible).
    private void Reposition()
    {
        if (_child == IntPtr.Zero || _host.XamlRoot == null) return;
        var scale = _host.XamlRoot.RasterizationScale;
        var origin = _host.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var height = Math.Max(1, _host.ActualHeight - _bottomPanel.ActualHeight);
        SpikeWin32.SetWindowPos(_child, SpikeWin32.HWND_TOP, (int)(origin.X * scale), (int)(origin.Y * scale),
            (int)(_host.ActualWidth * scale), (int)(height * scale), SpikeWin32.SWP_NOACTIVATE | SpikeWin32.SWP_SHOWWINDOW);
        Logger.Info("Spike: child at {0},{1} {2}x{3}", (int)(origin.X * scale), (int)(origin.Y * scale),
            (int)(_host.ActualWidth * scale), (int)(height * scale));
    }

    private void Stop()
    {
        if (_mpv != IntPtr.Zero) { Mpv.mpv_terminate_destroy(_mpv); _mpv = IntPtr.Zero; }
        if (_child != IntPtr.Zero) { SpikeWin32.DestroyWindow(_child); _child = IntPtr.Zero; }
    }
}
