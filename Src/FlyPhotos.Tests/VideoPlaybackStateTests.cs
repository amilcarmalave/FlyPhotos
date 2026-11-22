using FlyPhotos.Display.Video;

namespace FlyPhotos.Tests;

[TestClass]
public sealed class VideoPlaybackStateTests
{
    private static VideoPlaybackState Playing(string path = "a.mp4")
    {
        var s = new VideoPlaybackState();
        s.Play(path);
        Assert.AreEqual(VideoPhase.Playing, s.Phase);
        return s;
    }

    [TestMethod]
    public void New_state_is_idle()
    {
        var s = new VideoPlaybackState();
        Assert.AreEqual(VideoPhase.Idle, s.Phase);
        Assert.IsNull(s.Path);
        Assert.IsFalse(s.Hidden);
    }

    [TestMethod]
    public void Play_then_toggle_pauses_and_resumes()
    {
        var s = Playing();
        Assert.AreEqual("a.mp4", s.Path);
        s.TogglePause();
        Assert.AreEqual(VideoPhase.Paused, s.Phase);
        s.TogglePause();
        Assert.AreEqual(VideoPhase.Playing, s.Phase);
    }

    [TestMethod]
    public void Overlay_pauses_and_hides_and_close_reveals_paused()
    {
        var s = Playing();
        s.OverlayOpened();
        Assert.AreEqual(VideoPhase.Paused, s.Phase);
        Assert.IsTrue(s.Hidden);
        s.OverlayClosed();
        Assert.AreEqual(VideoPhase.Paused, s.Phase);
        Assert.IsFalse(s.Hidden);
    }

    [TestMethod]
    public void Overlay_while_paused_stays_paused()
    {
        var s = Playing();
        s.TogglePause();
        s.OverlayOpened();
        Assert.AreEqual(VideoPhase.Paused, s.Phase);
        Assert.IsTrue(s.Hidden);
        s.OverlayClosed();
        Assert.AreEqual(VideoPhase.Paused, s.Phase);
        Assert.IsFalse(s.Hidden);
    }

    [TestMethod]
    public void Overlay_while_ended_stays_ended()
    {
        var s = Playing();
        s.Ended();
        s.OverlayOpened();
        Assert.AreEqual(VideoPhase.Ended, s.Phase);
        Assert.IsTrue(s.Hidden);
    }

    [TestMethod]
    public void Minimize_pauses()
    {
        var s = Playing();
        s.Minimized();
        Assert.AreEqual(VideoPhase.Paused, s.Phase);
        Assert.IsFalse(s.Hidden);
    }

    [TestMethod]
    public void Minimize_when_idle_or_ended_is_noop()
    {
        var idle = new VideoPlaybackState();
        idle.Minimized();
        Assert.AreEqual(VideoPhase.Idle, idle.Phase);

        var ended = Playing();
        ended.Ended();
        ended.Minimized();
        Assert.AreEqual(VideoPhase.Ended, ended.Phase);
    }

    [TestMethod]
    public void Ended_then_toggle_restarts()
    {
        var s = Playing();
        s.Ended();
        Assert.AreEqual(VideoPhase.Ended, s.Phase);
        s.TogglePause();
        Assert.AreEqual(VideoPhase.Playing, s.Phase);
        Assert.AreEqual("a.mp4", s.Path);
    }

    [TestMethod]
    public void Ended_only_applies_while_playing()
    {
        var idle = new VideoPlaybackState();
        idle.Ended();
        Assert.AreEqual(VideoPhase.Idle, idle.Phase);

        var paused = Playing();
        paused.TogglePause();
        paused.Ended();
        Assert.AreEqual(VideoPhase.Paused, paused.Phase);
    }

    [TestMethod]
    public void Stop_resets()
    {
        var s = Playing();
        s.OverlayOpened();
        s.Stop();
        Assert.AreEqual(VideoPhase.Idle, s.Phase);
        Assert.IsNull(s.Path);
        Assert.IsFalse(s.Hidden);
    }

    [TestMethod]
    public void A_file_that_fails_to_load_stops_rather_than_ending()
    {
        // mpv's END_FILE (keep-open: only a load or decode error) stops the player, so the bar resets and nothing
        // is left to restart or toggle: a toggle is a no-op and Play loads the file afresh.
        var s = Playing();
        s.Stop();
        Assert.AreEqual(VideoPhase.Idle, s.Phase);
        Assert.AreEqual(PlayAction.None, s.TogglePause());
        Assert.AreEqual(PlayAction.Load, s.Play("a.mp4"));
    }

    [TestMethod]
    public void Play_other_path_restarts()
    {
        var s = Playing("a.mp4");
        s.TogglePause();
        s.Play("b.mp4");
        Assert.AreEqual(VideoPhase.Playing, s.Phase);
        Assert.AreEqual("b.mp4", s.Path);
    }

    [TestMethod]
    public void Play_same_path_while_paused_resumes()
    {
        var s = Playing("a.mp4");
        s.TogglePause();
        s.Play("a.mp4");
        Assert.AreEqual(VideoPhase.Playing, s.Phase);
        Assert.AreEqual("a.mp4", s.Path);
    }

    [TestMethod]
    public void Play_after_ended_restarts()
    {
        var s = Playing("a.mp4");
        s.Ended();
        s.Play("a.mp4");
        Assert.AreEqual(VideoPhase.Playing, s.Phase);
    }

    [TestMethod]
    public void Toggle_when_idle_is_noop()
    {
        var s = new VideoPlaybackState();
        s.TogglePause();
        Assert.AreEqual(VideoPhase.Idle, s.Phase);
        Assert.IsNull(s.Path);
    }

    [TestMethod]
    public void Play_reports_whether_it_restarts_from_the_beginning()
    {
        var s = new VideoPlaybackState();
        Assert.AreEqual(PlayAction.Load, s.Play("a.mp4"));
        Assert.AreEqual(PlayAction.None, s.Play("a.mp4"));        // already playing
        s.TogglePause();
        Assert.AreEqual(PlayAction.Resume, s.Play("a.mp4"));
        s.Ended();                                                  // no-op while paused
        s.TogglePause();
        s.Ended();
        Assert.AreEqual(PlayAction.Restart, s.Play("a.mp4"));
        Assert.AreEqual(PlayAction.Load, s.Play("b.mp4"));
    }

    [TestMethod]
    public void Toggle_reports_the_action()
    {
        var s = new VideoPlaybackState();
        Assert.AreEqual(PlayAction.None, s.TogglePause());
        s.Play("a.mp4");
        Assert.AreEqual(PlayAction.Pause, s.TogglePause());
        Assert.AreEqual(PlayAction.Resume, s.TogglePause());
        s.Ended();
        Assert.AreEqual(PlayAction.Restart, s.TogglePause());
    }

    [TestMethod]
    public void Seek_after_end_pauses_so_toggle_resumes_instead_of_restarting()
    {
        var s = Playing();
        s.Ended();
        s.Seeked();
        Assert.AreEqual(VideoPhase.Paused, s.Phase);
        Assert.AreEqual(PlayAction.Resume, s.TogglePause());

        s.Seeked(); // while playing: unchanged
        Assert.AreEqual(VideoPhase.Playing, s.Phase);
    }

    [TestMethod]
    public void Volume_is_clamped_to_0_130()
    {
        Assert.AreEqual(0, VideoPlaybackState.ClampVolume(-5));
        Assert.AreEqual(130, VideoPlaybackState.ClampVolume(500));
        Assert.AreEqual(100, VideoPlaybackState.ClampVolume(100));
        Assert.AreEqual(0, VideoPlaybackState.ClampVolume(0));
        Assert.AreEqual(130, VideoPlaybackState.ClampVolume(130));
    }
}
