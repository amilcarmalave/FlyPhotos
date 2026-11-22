#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace FlyPhotos.Display.ImageReading;

/// <summary>The video extensions Fly plays through libmpv. Pure: no native loading.</summary>
internal static class VideoFormats
{
    public static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".mpg", ".mpeg", ".m2ts", ".mts", ".ts", ".3gp", ".flv", ".ogv"
    };

    public static bool IsVideoExtension(string? ext) => !string.IsNullOrEmpty(ext) && Extensions.Contains(ext);

    /// <summary>libmpv-2.dll sits next to the exe. A file check only: CodecDiscovery must not load native DLLs.</summary>
    public static bool LibMpvPresent { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"));
}
