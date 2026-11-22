using System;

namespace FlyPhotos.Display.Video;

internal enum VideoPhase { Idle, Playing, Paused, Ended }

/// <summary>What the player has to do to carry out a transition.</summary>
internal enum PlayAction
{
    None,
    /// <summary>Load the (new) path and start playing.</summary>
    Load,
    /// <summary>Seek to 0 and unpause (the same path ended).</summary>
    Restart,
    Pause,
    Resume,
}

/// <summary>
/// The playback state of one window's video, with no Win32 or mpv in it, so every transition is unit-tested.
/// <see cref="VideoPlaybackController"/> turns the returned <see cref="PlayAction"/>s into mpv commands.
/// </summary>
internal sealed class VideoPlaybackState
{
    public const int MaxVolume = 130;

    public VideoPhase Phase { get; private set; } = VideoPhase.Idle;

    /// <summary>True while an overlay covers the video; the child window is hidden.</summary>
    public bool Hidden { get; private set; }

    public string Path { get; private set; }

    public static int ClampVolume(int volume) => Math.Clamp(volume, 0, MaxVolume);

    /// <summary>Idle, Ended or another path → Playing (from the start); Paused on the same path → Playing.</summary>
    public PlayAction Play(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        if (!string.Equals(path, Path, StringComparison.Ordinal) || Phase == VideoPhase.Idle)
        {
            Path = path;
            Phase = VideoPhase.Playing;
            return PlayAction.Load;
        }
        switch (Phase)
        {
            case VideoPhase.Paused:
                Phase = VideoPhase.Playing;
                return PlayAction.Resume;
            case VideoPhase.Ended:
                Phase = VideoPhase.Playing;
                return PlayAction.Restart;
            default:
                return PlayAction.None;
        }
    }

    /// <summary>Playing ↔ Paused; Ended → Playing from 0; Idle → no-op.</summary>
    public PlayAction TogglePause()
    {
        switch (Phase)
        {
            case VideoPhase.Playing:
                Phase = VideoPhase.Paused;
                return PlayAction.Pause;
            case VideoPhase.Paused:
                Phase = VideoPhase.Playing;
                return PlayAction.Resume;
            case VideoPhase.Ended:
                Phase = VideoPhase.Playing;
                return PlayAction.Restart;
            default:
                return PlayAction.None;
        }
    }

    /// <summary>Playing → Paused; always Hidden. Paused and Ended keep their phase.</summary>
    public PlayAction OverlayOpened()
    {
        Hidden = true;
        return PauseIfPlaying();
    }

    /// <summary>Ended → Paused: after seeking back from the end, a toggle resumes from there, not from 0.</summary>
    public void Seeked()
    {
        if (Phase == VideoPhase.Ended) Phase = VideoPhase.Paused;
    }

    /// <summary>Reveals the video; the phase is unchanged (a video paused by the overlay stays paused).</summary>
    public void OverlayClosed() => Hidden = false;

    /// <summary>Playing → Paused.</summary>
    public PlayAction Minimized() => PauseIfPlaying();

    /// <summary>Playing → Ended (mpv reached the end of the file).</summary>
    public void Ended()
    {
        if (Phase == VideoPhase.Playing) Phase = VideoPhase.Ended;
    }

    /// <summary>→ Idle, no path, not hidden.</summary>
    public void Stop()
    {
        Phase = VideoPhase.Idle;
        Path = null;
        Hidden = false;
    }

    private PlayAction PauseIfPlaying()
    {
        if (Phase != VideoPhase.Playing) return PlayAction.None;
        Phase = VideoPhase.Paused;
        return PlayAction.Pause;
    }
}
