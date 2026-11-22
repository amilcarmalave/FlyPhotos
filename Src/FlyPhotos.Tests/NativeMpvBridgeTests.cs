using System.Diagnostics;
using FlyPhotos.Infra.Interop;

namespace FlyPhotos.Tests;

[TestClass]
public sealed class NativeMpvBridgeTests
{
    private const string Lavfi = "av://lavfi:testsrc=duration=2:size=160x90:rate=10";

    private static MpvHandle CreateHeadless(bool paused = true)
    {
        Assert.IsTrue(MpvHandle.TryLoad());
        return MpvHandle.Create(new Dictionary<string, string>
        {
            ["vo"] = "null",
            ["ao"] = "null",
            ["pause"] = paused ? "yes" : "no",
        });
    }

    [TestMethod]
    public void TryLoad_succeeds_with_the_bundled_dll() => Assert.IsTrue(MpvHandle.TryLoad());

    [TestMethod]
    public void Lavfi_source_reports_its_duration()
    {
        // Unpaused: a paused lavfi source never reads ahead, so its duration estimate stays at the first 0.3 s probed.
        using var mpv = CreateHeadless(paused: false);
        mpv.Command("loadfile", Lavfi);

        double? duration = null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            duration = mpv.GetDouble("duration");
            // A lavfi source reports a growing estimate while the demuxer reads ahead, so wait for it to settle.
            if (duration.HasValue && Math.Abs(duration.Value - 2.0) <= 0.2) break;
            Thread.Sleep(20);
        }

        Assert.IsNotNull(duration, "duration never became available");
        Assert.AreEqual(2.0, duration.Value, 0.2);
        Assert.AreEqual(160.0, mpv.GetDouble("width"));
    }

    [TestMethod]
    public void Bad_command_throws_MpvException()
    {
        using var mpv = CreateHeadless();
        Assert.ThrowsExactly<MpvException>(() => mpv.Command("no-such-command"));
    }

    [TestMethod]
    public void Observed_time_pos_changes_while_playing()
    {
        Assert.IsTrue(MpvHandle.TryLoad());
        using var mpv = MpvHandle.Create(new Dictionary<string, string> { ["vo"] = "null", ["ao"] = "null" });
        using var got = new ManualResetEventSlim();
        mpv.DoublePropertyChanged += (name, value) =>
        {
            if (name == "time-pos" && value > 0) got.Set();
        };
        mpv.Observe(1, "time-pos");
        mpv.StartEventLoop();
        mpv.Command("loadfile", Lavfi);

        Assert.IsTrue(got.Wait(TimeSpan.FromSeconds(5)), "no time-pos > 0 notification");
    }

    [TestMethod]
    public void Dispose_twice_is_safe()
    {
        var mpv = CreateHeadless();
        mpv.StartEventLoop();
        mpv.Dispose();
        mpv.Dispose();
    }
}
