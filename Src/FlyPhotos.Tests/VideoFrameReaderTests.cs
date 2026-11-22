using System.Diagnostics;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Interop;
using FlyPhotos.Tests.Display.Harness;
using Microsoft.Graphics.Canvas;
using D = FlyPhotos.Display.ImageReading.DecodeStep;

namespace FlyPhotos.Tests;

/// <summary>
/// Still frames of a video through libmpv, on a headless Win2D device. The fixture, a 2 s 160×90 MPEG-4 clip, is
/// encoded at test time by libmpv itself from a lavfi test source, so no binary is committed.
/// </summary>
[TestClass]
public sealed class VideoFrameReaderTests
{
    private static readonly string Fixture = Path.Combine(Path.GetTempPath(), "fly_video_fixture.mp4");
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "FlyPhotosVideoTests", Guid.NewGuid().ToString("N"));
    private static readonly string AudioOnly = Path.Combine(Dir, "audio_only.mp4");
    private static readonly CanvasRenderTarget Ctrl = new(TestGpu.Device, 1, 1, 96);

    [ClassInitialize]
    public static void EncodeFixtures(TestContext _)
    {
        Directory.CreateDirectory(Dir);
        Assert.IsTrue(MpvHandle.TryLoad(), "libmpv-2.dll is not next to the tests");
        File.Delete(Fixture);
        Encode(Fixture, "av://lavfi:testsrc=duration=2:size=160x90:rate=10", ("ovc", "mpeg4"), ("oac", "no"));
        // A 2 s tone in an MP4 with no video stream at all.
        Encode(AudioOnly, "av://lavfi:sine=duration=2", ("oac", "aac"));
    }

    /// <summary>Encodes 2 s of <paramref name="source"/> into <paramref name="output"/> with libmpv's encoder.</summary>
    private static void Encode(string output, string source, params (string Name, string Value)[] codecs)
    {
        var options = new Dictionary<string, string> { ["o"] = output, ["vo"] = "null", ["ao"] = "null", ["end"] = "2" };
        foreach (var (name, value) in codecs) options[name] = value;
        using (var mpv = MpvHandle.Create(options))
        {
            using var ended = new ManualResetEventSlim();
            mpv.EndFile += ended.Set;
            mpv.StartEventLoop();
            mpv.Command("loadfile", source);
            Assert.IsTrue(ended.Wait(TimeSpan.FromSeconds(20)), $"encoding {output} never ended");
        }
        // The encoder finishes the file when the handle is destroyed.
        Assert.IsTrue(new FileInfo(output).Length > 1000, $"{output} was not written");
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        try { File.Delete(Fixture); } catch (IOException) { }
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }

    [TestMethod]
    public async Task Hq_returns_a_full_size_frame_with_duration()
    {
        var (ok, item) = await VideoFrameReader.GetHq(Ctrl, Fixture);
        try
        {
            Assert.IsTrue(ok);
            var video = item as VideoHqDisplayItem;
            Assert.IsNotNull(video, $"expected a VideoHqDisplayItem, got {item?.GetType().Name}");
            Assert.AreEqual(160u, video.Bitmap.SizeInPixels.Width);
            Assert.AreEqual(90u, video.Bitmap.SizeInPixels.Height);
            Assert.AreEqual(2.0, video.Info.DurationSeconds, 0.3);
            Assert.AreEqual((160, 90), (video.Info.Width, video.Info.Height));
            Assert.AreEqual(0, video.Rotation);
        }
        finally
        {
            item.Release();
        }
    }

    [TestMethod]
    public async Task The_temp_frame_is_deleted()
    {
        string[] TempFrames() => Directory.GetFiles(Path.GetTempPath(), "fly_video_frame_*.png");
        var before = TempFrames();

        var (ok, item) = await VideoFrameReader.GetHq(Ctrl, Fixture);
        item.Release();

        Assert.IsTrue(ok);
        CollectionAssert.AreEquivalent(before, TempFrames());
    }

    [TestMethod]
    public async Task Resized_never_exceeds_800()
    {
        var (ok, item) = await VideoFrameReader.GetResized(Ctrl, Fixture);
        try
        {
            Assert.IsTrue(ok);
            Assert.IsNotNull(item.Bitmap);
            var size = item.Bitmap.SizeInPixels;
            Assert.IsTrue(Math.Max(size.Width, size.Height) <= 800);
            Assert.AreEqual((160u, 90u), (size.Width, size.Height));
            Assert.AreEqual((160.0, 90.0), (item.Metadata.FullWidth, item.Metadata.FullHeight));
        }
        finally
        {
            item.Release();
        }
    }

    [TestMethod]
    public async Task Corrupt_file_fails_cleanly()
    {
        var bad = Path.Combine(Dir, "bad.mp4");
        var bytes = new byte[1024];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(bad, bytes);

        var sw = Stopwatch.StartNew();
        var (okHq, _) = await VideoFrameReader.GetHq(Ctrl, bad);
        var (okResized, _) = await VideoFrameReader.GetResized(Ctrl, bad);

        Assert.IsFalse(okHq);
        Assert.IsFalse(okResized);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [TestMethod]
    public async Task Cancelled_token_returns_promptly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        var (ok, _) = await VideoFrameReader.GetHq(Ctrl, Fixture, cts.Token);

        Assert.IsFalse(ok);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.Elapsed}");
    }

    [TestMethod]
    public async Task Audio_only_file_fails_fast()
    {
        var sw = Stopwatch.StartNew();
        var (ok, _) = await VideoFrameReader.GetHq(Ctrl, AudioOnly);

        Assert.IsFalse(ok);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }

    [TestMethod]
    public async Task Cancelling_decodes_that_wait_for_a_slot_frees_them_at_once()
    {
        // The real pipeline below the prefetch cache: PhotoDecoder -> ImageReader -> VideoFrameReader, with both
        // decode slots taken (two slow videos elsewhere).
        AppConfig.Settings = new AppSettings();
        var decoder = new PhotoDecoder(Ctrl);
        var photo = new Photo(Fixture);
        Assert.IsTrue(VideoFrameReader.Gate.Wait(0) && VideoFrameReader.Gate.Wait(0), "both slots free at the start");
        try
        {
            using var cts = new CancellationTokenSource();
            var preview = decoder.LoadPreview(photo, Purpose.PreviewHeavy, cts.Token);
            var hq = decoder.LoadHq(photo, allowLarge: false, cts.Token);
            await Task.Delay(200);
            Assert.IsFalse(preview.IsCompleted || hq.IsCompleted, "they wait for a slot");

            var sw = Stopwatch.StartNew();
            await cts.CancelAsync();
            var all = Task.WhenAll(preview, hq);
            Assert.AreSame(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(1))), "cancelling ends them");
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.Elapsed}");
            Assert.IsNull(await preview, "a cancelled preview is nothing, not the error item");
            Assert.IsNull(await hq, "a cancelled HQ is nothing, not the error screen");
            Assert.IsNull(photo.Slots.Hq);
        }
        finally
        {
            VideoFrameReader.Gate.Release(2);
            photo.Slots.Retire();
        }
        Assert.AreEqual(2, VideoFrameReader.Gate.CurrentCount, "no slot is kept by a cancelled decode");
    }

    [TestMethod]
    public async Task Cancelling_a_running_decode_frees_its_slot_at_once()
    {
        for (int delayMs = 0; delayMs <= 40; delayMs += 10)
        {
            using var cts = new CancellationTokenSource();
            var decode = VideoFrameReader.GetHq(Ctrl, Fixture, cts.Token);
            await Task.Delay(delayMs);
            var sw = Stopwatch.StartNew();
            await cts.CancelAsync();
            var (ok, item) = await decode;
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.Elapsed} after {delayMs} ms");
            if (ok) item.Release(); // finished before the cancel landed
            Assert.AreEqual(2, VideoFrameReader.Gate.CurrentCount, $"slot freed (cancelled after {delayMs} ms)");
        }
    }

    [TestMethod]
    public void FrameTime_rules()
    {
        Assert.AreEqual(0.0, VideoFrameReader.FrameTime(0.5));
        Assert.AreEqual(2.0, VideoFrameReader.FrameTime(20), 1e-9);
        Assert.AreEqual(10.0, VideoFrameReader.FrameTime(600));
    }

    [TestMethod]
    public void Chains_choose_video_steps()
    {
        var codecs = new CodecSupport(
            wic: Set(".jpg", ".mkv", ".mp4"), wicRaw: Set(), magick: Set(".jpg", ".mkv", ".mp4"), magickRaw: Set(), rawler: Set(".mkv"));
        var options = new ReadOptions(false, [RawDecoder.WIC, RawDecoder.Rawler, RawDecoder.ImageMagick]);
        List<D> Chain(Purpose purpose) => DecoderChains.For(ImageFormats.FromExtension(".mkv"), ".mkv", purpose, options, codecs);

        // Even with every codec claiming .mkv, only the mpv steps are tried.
        CollectionAssert.AreEqual(new[] { D.VideoFrameResized }, Chain(Purpose.Preview));
        CollectionAssert.AreEqual(new[] { D.VideoFrameResized }, Chain(Purpose.FirstPreview));
        CollectionAssert.AreEqual(new[] { D.VideoFrameHq }, Chain(Purpose.Hq));
        Assert.AreEqual(0, Chain(Purpose.Large).Count);
        // A still runs a whole mpv player: heavy, so background loading gives it the heavy lane, never a light slot.
        Assert.IsTrue(DecoderChains.IsHeavy(D.VideoFrameResized, ImageFormat.Video, ".mkv"));
        Assert.AreEqual(0, Chain(Purpose.PreviewLight).Count);
        CollectionAssert.AreEqual(new[] { D.VideoFrameResized }, Chain(Purpose.PreviewHeavy));
    }

    [TestMethod]
    public async Task No_more_than_two_decodes_run_at_once()
    {
        VideoFrameReader.ResetPeakConcurrency();

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Task.Run(() => VideoFrameReader.GetResized(Ctrl, Fixture))));
        try
        {
            Assert.IsTrue(results.All(r => r.Item1), "every decode succeeds");
            Assert.IsTrue(VideoFrameReader.PeakConcurrency is >= 1 and <= 2, $"peak {VideoFrameReader.PeakConcurrency}");
        }
        finally
        {
            foreach (var (_, item) in results) item.Release();
        }
    }

    private static HashSet<string> Set(params string[] e) => new(e, StringComparer.OrdinalIgnoreCase);
}
