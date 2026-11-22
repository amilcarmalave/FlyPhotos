using System;
using System.Globalization;

namespace FlyPhotos.Display.Video;

/// <summary>The time shown on the video bar: <c>m:ss</c>, or <c>h:mm:ss</c> from one hour.</summary>
internal static class VideoTimeFormat
{
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var total = (long)Math.Floor(seconds);
        var h = total / 3600;
        var m = total / 60 % 60;
        var s = total % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, s);
    }
}
