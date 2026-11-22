using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;

namespace FlyPhotos.Infra.Interop;

internal sealed class MpvException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Raw libmpv client API. Struct layouts and enum values checked against mpv/client.h (client API 2.x).</summary>
internal static partial class NativeMpv
{
    internal const string Lib = "libmpv-2.dll";

    internal const int EventNone = 0;
    internal const int EventShutdown = 1;
    internal const int EventStartFile = 6;
    internal const int EventEndFile = 7;
    internal const int EventPropertyChange = 22;
    internal const int FormatFlag = 3;
    internal const int FormatDouble = 5;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEvent
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    /// <summary>The start of mpv_event_end_file: why the file ended, and the error for <see cref="EndFileError"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventEndFile
    {
        public int Reason;
        public int Error;
    }

    internal const int EndFileEof = 0, EndFileStop = 2, EndFileQuit = 3, EndFileError = 4, EndFileRedirect = 5;

    /// <summary>mpv_end_file_reason as text, for the log.</summary>
    internal static string EndFileReasonName(int reason) => reason switch
    {
        EndFileEof => "eof",
        EndFileStop => "stop",
        EndFileQuit => "quit",
        EndFileError => "error",
        EndFileRedirect => "redirect",
        _ => reason.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventProperty
    {
        public IntPtr Name;
        public int Format;
        public IntPtr Data;
    }

    [LibraryImport(Lib)] internal static partial IntPtr mpv_create();
    [LibraryImport(Lib)] internal static partial int mpv_initialize(IntPtr ctx);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_option_string(IntPtr ctx, string name, string data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_property_string(IntPtr ctx, string name, string data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_get_property(IntPtr ctx, string name, int format, out double data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr mpv_get_property_string(IntPtr ctx, string name);

    [LibraryImport(Lib)] internal static partial void mpv_free(IntPtr data);
    [LibraryImport(Lib)] internal static partial int mpv_command(IntPtr ctx, IntPtr[] args);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_observe_property(IntPtr ctx, ulong replyUserdata, string name, int format);

    [LibraryImport(Lib)] internal static unsafe partial MpvEvent* mpv_wait_event(IntPtr ctx, double timeout);
    [LibraryImport(Lib)] internal static partial void mpv_wakeup(IntPtr ctx);
    [LibraryImport(Lib)] internal static partial void mpv_terminate_destroy(IntPtr ctx);
    [LibraryImport(Lib)] internal static partial IntPtr mpv_error_string(int error);

    internal static string Error(int code) => Marshal.PtrToStringUTF8(mpv_error_string(code)) ?? code.ToString();
}

/// <summary>
/// A typed wrapper over one libmpv handle. Events are raised on the dedicated event-loop thread.
/// <see cref="TryLoad"/> is the only call that may load the DLL; everything else assumes it returned true.
/// </summary>
internal sealed class MpvHandle : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly Lock LoadLock = new();
    private static bool? _loaded;

    private IntPtr _ctx;
    private Thread _thread;
    private volatile bool _stop;
    private int _disposed;

    public event Action<string, double> DoublePropertyChanged;
    public event Action<string, bool> FlagPropertyChanged;
    /// <summary>A file ended (mpv's END_FILE): its mpv_end_file_reason and, for an error, mpv's error code.</summary>
    public event Action<int, int> EndFile;
    /// <summary>A file starts loading (mpv's START_FILE), before any of its property changes.</summary>
    public event Action StartFile;

    private MpvHandle(IntPtr ctx) => _ctx = ctx;

    public static bool TryLoad()
    {
        lock (LoadLock)
        {
            if (_loaded.HasValue) return _loaded.Value;
            try
            {
                // The handle is intentionally kept: the library stays loaded for the process lifetime.
                _loaded = NativeLibrary.TryLoad(NativeMpv.Lib, typeof(MpvHandle).Assembly, null, out _);
                if (!_loaded.Value) Logger.Warn("{0} could not be loaded", NativeMpv.Lib);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "{0} could not be loaded", NativeMpv.Lib);
                _loaded = false;
            }
            return _loaded.Value;
        }
    }

    public static MpvHandle Create(IReadOnlyDictionary<string, string> options)
    {
        var ctx = NativeMpv.mpv_create();
        if (ctx == IntPtr.Zero) throw new MpvException("mpv_create failed", -1);
        try
        {
            foreach (var (name, value) in options)
            {
                var rc = NativeMpv.mpv_set_option_string(ctx, name, value);
                if (rc < 0) throw new MpvException($"option {name}={value}: {NativeMpv.Error(rc)}", rc);
            }
            var init = NativeMpv.mpv_initialize(ctx);
            if (init < 0) throw new MpvException($"mpv_initialize: {NativeMpv.Error(init)}", init);
        }
        catch
        {
            NativeMpv.mpv_terminate_destroy(ctx);
            throw;
        }
        return new MpvHandle(ctx);
    }

    public void Command(params string[] args)
    {
        var ctx = Ctx();
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            var rc = NativeMpv.mpv_command(ctx, ptrs);
            if (rc < 0) throw new MpvException($"{args[0]}: {NativeMpv.Error(rc)}", rc);
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    public double? GetDouble(string property) =>
        NativeMpv.mpv_get_property(Ctx(), property, NativeMpv.FormatDouble, out var v) < 0 ? null : v;

    public string GetString(string property)
    {
        var p = NativeMpv.mpv_get_property_string(Ctx(), property);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { NativeMpv.mpv_free(p); }
    }

    public void SetString(string property, string value)
    {
        var rc = NativeMpv.mpv_set_property_string(Ctx(), property, value);
        if (rc < 0) throw new MpvException($"set {property}={value}: {NativeMpv.Error(rc)}", rc);
    }

    /// <summary>"pause" is observed as a flag; every other property as a double.</summary>
    public void Observe(ulong id, string property)
    {
        var format = property == "pause" ? NativeMpv.FormatFlag : NativeMpv.FormatDouble;
        var rc = NativeMpv.mpv_observe_property(Ctx(), id, property, format);
        if (rc < 0) throw new MpvException($"observe {property}: {NativeMpv.Error(rc)}", rc);
    }

    public void StartEventLoop()
    {
        var ctx = Ctx();
        if (_thread != null) return;
        _thread = new Thread(() => Loop(ctx)) { IsBackground = true, Name = "mpv-events" };
        _thread.Start();
    }

    private unsafe void Loop(IntPtr ctx)
    {
        while (!_stop)
        {
            var ev = NativeMpv.mpv_wait_event(ctx, -1);
            if (_stop || ev == null) break;
            try
            {
                switch (ev->EventId)
                {
                    case NativeMpv.EventShutdown:
                        return;
                    case NativeMpv.EventStartFile:
                        StartFile?.Invoke();
                        break;
                    case NativeMpv.EventEndFile:
                        var end = (NativeMpv.MpvEventEndFile*)ev->Data;
                        EndFile?.Invoke(end == null ? -1 : end->Reason, end == null ? 0 : end->Error);
                        break;
                    case NativeMpv.EventPropertyChange:
                        RaiseProperty((NativeMpv.MpvEventProperty*)ev->Data);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "mpv event handler failed");
            }
        }
    }

    private unsafe void RaiseProperty(NativeMpv.MpvEventProperty* prop)
    {
        if (prop == null || prop->Data == IntPtr.Zero) return; // format NONE: property unavailable
        var name = Marshal.PtrToStringUTF8(prop->Name);
        if (name == null) return;
        switch (prop->Format)
        {
            case NativeMpv.FormatDouble:
                DoublePropertyChanged?.Invoke(name, *(double*)prop->Data);
                break;
            case NativeMpv.FormatFlag:
                FlagPropertyChanged?.Invoke(name, *(int*)prop->Data != 0);
                break;
        }
    }

    private IntPtr Ctx()
    {
        var ctx = _ctx;
        return ctx == IntPtr.Zero || _disposed != 0 ? throw new ObjectDisposedException(nameof(MpvHandle)) : ctx;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var ctx = _ctx;
        _stop = true;
        var joined = true;
        if (_thread != null)
        {
            NativeMpv.mpv_wakeup(ctx);
            // Disposing from inside an event handler: joining ourselves would deadlock.
            if (Thread.CurrentThread != _thread) joined = _thread.Join(TimeSpan.FromSeconds(2));
            if (!joined) Logger.Warn("mpv event thread did not stop within 2 s");
        }
        _ctx = IntPtr.Zero;
        NativeMpv.mpv_terminate_destroy(ctx);
    }
}
