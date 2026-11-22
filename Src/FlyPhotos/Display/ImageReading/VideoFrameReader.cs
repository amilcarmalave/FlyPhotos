#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Reads one still frame of a video for the decoder chain, through a headless libmpv handle (no window, no audio):
/// load paused, seek to <see cref="FrameTime"/>, <c>screenshot-to-file</c> to a temp PNG, load that into Win2D.
/// </summary>
internal static class VideoFrameReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>The preview's longer side, as for PDF and SVG.</summary>
    private const int PreviewMaxDimension = 800;

    /// <summary>How long one decode may take once it has a slot; then it fails.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>How often mpv's properties are polled while it loads and seeks.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>Past this long a file with a picture but no known duration is read at its first frame.</summary>
    private static readonly TimeSpan DurationGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A seek that never showed <c>seeking</c> nor moved <c>time-pos</c> is taken as landed on the frame already
    /// shown after this long (a target in the first frame's span).
    /// </summary>
    private static readonly TimeSpan SeekGrace = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How many decodes may run at once: each runs a whole mpv player (demuxer, decoder threads). The prefetch
    /// planner sizes its video lane to it (<c>JobLimits.Videos</c>).
    /// </summary>
    internal const int MaxDecodes = 2;

    /// <summary>
    /// Lets <see cref="MaxDecodes"/> decodes run at once. Internal for the cancellation tests, which hold its slots.
    /// </summary>
    internal static readonly SemaphoreSlim Gate = new(MaxDecodes);

    /// <summary>The headless player: no video or audio output, no audio decode, paused, exact seeks.</summary>
    private static readonly IReadOnlyDictionary<string, string> Options = new Dictionary<string, string>
    {
        ["vo"] = "null",
        ["ao"] = "null",
        ["pause"] = "yes",
        ["hr-seek"] = "yes",
        ["audio"] = "no",
        // A temp file read once: fast compression, and 8 bits per channel whatever the source.
        ["screenshot-png-compression"] = "1",
        ["screenshot-high-bit-depth"] = "no",
    };

    private static int _running;

    /// <summary>Test hook: the most decodes that have run at once since <see cref="ResetPeakConcurrency"/>.</summary>
    internal static int PeakConcurrency;

    /// <summary>Test hook: starts counting <see cref="PeakConcurrency"/> afresh.</summary>
    internal static void ResetPeakConcurrency() => Interlocked.Exchange(ref PeakConcurrency, 0);

    /// <summary>
    /// Where the still frame is taken: the start of a clip under a second, else 10% in (past a fade from black),
    /// but never more than 10 s in, so a long film doesn't make mpv read far.
    /// </summary>
    internal static double FrameTime(double durationSeconds) =>
        durationSeconds < 1 ? 0 : Math.Min(durationSeconds * 0.1, 10);

    /// <summary>A frame at most 800 px on the longer side; its metadata reports the video's full frame size.</summary>
    public static async Task<(bool, PreviewDisplayItem)> GetResized(ICanvasResourceCreatorWithDpi ctrl, string path,
        CancellationToken ct = default)
    {
        if (await ReadFrame(ctrl, path, ct) is not { } frame) return (false, PreviewDisplayItem.Empty());
        try
        {
            var bitmap = Downscale(ctrl.Device, frame.Bitmap, PreviewMaxDimension);
            var metadata = new ImageMetadata(frame.Info.Width, frame.Info.Height);
            return (true, new PreviewDisplayItem(bitmap, Origin.Disk, metadata, frame.Rotation));
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Video preview failed: {Path}", path);
            return (false, PreviewDisplayItem.Empty());
        }
    }

    /// <summary>The full-size frame, with the video's duration and size.</summary>
    public static async Task<(bool, HqDisplayItem)> GetHq(ICanvasResourceCreatorWithDpi ctrl, string path,
        CancellationToken ct = default) =>
        await ReadFrame(ctrl, path, ct) is { } frame
            ? (true, new VideoHqDisplayItem(frame.Bitmap, Origin.Disk, frame.Info, frame.Rotation))
            : (false, HqDisplayItem.Empty());

    /// <summary>A decoded frame, owned by the caller, and how to show it.</summary>
    private sealed record Frame(CanvasBitmap Bitmap, VideoInfo Info, int Rotation);

    /// <summary>
    /// The still frame, or null when libmpv is missing, the file can't be played, <paramref name="ct"/> is
    /// cancelled, or the decode takes longer than <see cref="Timeout"/>. Never throws.
    /// </summary>
    private static async Task<Frame?> ReadFrame(ICanvasResourceCreatorWithDpi ctrl, string path, CancellationToken ct)
    {
        if (!MpvHandle.TryLoad()) return null;
        try
        {
            await Gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var png = Path.Combine(Path.GetTempPath(), $"fly_video_frame_{Guid.NewGuid():N}.png");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        EnterDecode();
        try
        {
            var grab = await Task.Run(() => GrabToFile(path, png, timeout.Token), timeout.Token);
            // Through a stream we close: loaded by file name, Win2D keeps the file open and it can't be deleted.
            CanvasBitmap bitmap;
            await using (var stream = File.OpenRead(png))
                bitmap = await CanvasBitmap.LoadAsync(ctrl, stream.AsRandomAccessStream()).AsTask(timeout.Token);
            var (width, height) = ((int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
            return new Frame(bitmap, new VideoInfo(grab.Duration, width, height),
                StillToRotate(grab.Rotation, grab.Width, grab.Height, width, height));
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) Logger.Warn("Video frame timed out after {0}: {1}", Timeout, path);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Video frame failed: {Path}", path);
            return null;
        }
        finally
        {
            ExitDecode();
            Gate.Release();
            TryDelete(png);
        }
    }

    /// <summary>
    /// Plays <paramref name="path"/> paused in a fresh mpv, seeks to <see cref="FrameTime"/> and saves that frame
    /// as <paramref name="png"/>. Returns the duration, the video's rotation and its decoded (unrotated) size.
    /// Blocks; throws on failure or cancellation.
    /// </summary>
    private static (double Duration, int Rotation, int Width, int Height) GrabToFile(string path, string png,
        CancellationToken ct)
    {
        using var mpv = MpvHandle.Create(Options);
        var ended = 0;
        // Paused, a playable file never ends; one that can't be played ends at once.
        mpv.EndFile += (_, _) => Volatile.Write(ref ended, 1);
        mpv.StartEventLoop();
        mpv.Command("loadfile", path);

        // Loaded: the first frame has been decoded once the output's size is known.
        var loading = System.Diagnostics.Stopwatch.StartNew();
        double? duration;
        while (true)
        {
            ThrowIfEnded();
            // "auto" while opening; once open, the selected track or "no": audio only, nothing to show.
            if (mpv.GetString("vid") == "no") throw new MpvException($"{path} has no video track", -1);
            duration = mpv.GetDouble("duration");
            if (mpv.GetDouble("video-params/w") is > 0 && (duration.HasValue || loading.Elapsed > DurationGrace)) break;
            Wait();
        }

        var target = FrameTime(duration ?? 0);
        if (target > 0) SeekAndSettle(target);

        // A frame may be decoded a moment before the output has it; retry until it does.
        while (true)
        {
            try
            {
                mpv.Command("screenshot-to-file", png, "video");
                break;
            }
            catch (MpvException)
            {
                ThrowIfEnded();
                Wait();
            }
        }

        var rotate = NormaliseRotation((int)(mpv.GetDouble("video-params/rotate") ?? 0));
        var width = (int)(mpv.GetDouble("video-params/w") ?? 0);
        var height = (int)(mpv.GetDouble("video-params/h") ?? 0);
        return (duration ?? 0, rotate, width, height);

        void ThrowIfEnded()
        {
            if (Volatile.Read(ref ended) != 0) throw new MpvException($"{path} ended before a frame was read", -1);
        }

        void Wait()
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(PollInterval);
        }

        // An exact seek, done once mpv has started it (seen seeking, or time-pos moved; a seek the poll missed
        // entirely within SeekGrace), is no longer seeking and time-pos holds still between two polls. Paused, the
        // landed frame stays put, so this ends as soon as the seek does, whatever pts the frame has (VFR files land
        // well off the target when frames are sparse). Never compares time-pos with the target.
        void SeekAndSettle(double target)
        {
            var before = mpv.GetDouble("time-pos");
            mpv.Command("seek", target.ToString(CultureInfo.InvariantCulture), "absolute+exact");
            var sinceSeek = System.Diagnostics.Stopwatch.StartNew();
            bool started = false;
            double? last = null;
            while (true)
            {
                ThrowIfEnded();
                var seeking = mpv.GetString("seeking") != "no";
                var now = mpv.GetDouble("time-pos");
                started |= seeking || now != before || sinceSeek.Elapsed > SeekGrace;
                if (started && !seeking && now.HasValue && now == last) return;
                last = now;
                Wait();
            }
        }
    }

    /// <summary>
    /// The rotation left for the canvas. mpv may already have turned the frame (it rotates in a filter for outputs
    /// that can't): a quarter turn whose screenshot already has the video's sides swapped is not applied twice.
    /// </summary>
    private static int StillToRotate(int rotation, int videoWidth, int videoHeight, int frameWidth, int frameHeight) =>
        rotation is 90 or 270 && videoWidth != videoHeight && (frameWidth, frameHeight) == (videoHeight, videoWidth)
            ? 0
            : rotation;

    /// <summary>mpv's clockwise degrees as one of 0, 90, 180, 270, the angles the canvas draws.</summary>
    private static int NormaliseRotation(int degrees) => (((degrees % 360) + 360) % 360 + 45) / 90 % 4 * 90;

    /// <summary>
    /// The frame fitted within <paramref name="maxDimension"/>, owned by the caller. Returns the frame itself when
    /// it already fits; otherwise disposes it.
    /// </summary>
    private static CanvasBitmap Downscale(CanvasDevice device, CanvasBitmap frame, int maxDimension)
    {
        var (w, h) = (frame.SizeInPixels.Width, frame.SizeInPixels.Height);
        var longest = Math.Max(w, h);
        if (longest <= maxDimension) return frame;

        var scale = (double)maxDimension / longest;
        var (sw, sh) = (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
        using (frame)
        {
            var target = new CanvasRenderTarget(device, sw, sh, 96);
            try
            {
                using var ds = target.CreateDrawingSession();
                ds.DrawImage(frame, new Rect(0, 0, sw, sh), new Rect(0, 0, w, h), 1,
                    CanvasImageInterpolation.HighQualityCubic);
                return target;
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }
    }

    private static void EnterDecode()
    {
        var now = Interlocked.Increment(ref _running);
        int peak;
        while (now > (peak = Volatile.Read(ref PeakConcurrency)) &&
               Interlocked.CompareExchange(ref PeakConcurrency, now, peak) != peak)
        {
        }
    }

    private static void ExitDecode() => Interlocked.Decrement(ref _running);

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not delete the temp frame {0}", file);
        }
    }
}
