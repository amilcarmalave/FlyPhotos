using System.Text.Json;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;

namespace FlyPhotos.Tests;

[TestClass]
public sealed class VideoFormatsTests
{
    private static readonly string[] SpecExtensions =
    [
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".mpg", ".mpeg", ".m2ts", ".mts", ".ts", ".3gp", ".flv", ".ogv"
    ];

    [TestMethod]
    public void Every_spec_extension_maps_to_Video()
    {
        Assert.AreEqual(15, VideoFormats.Extensions.Count);
        foreach (var e in SpecExtensions)
        {
            Assert.IsTrue(VideoFormats.IsVideoExtension(e), e);
            Assert.AreEqual(ImageFormat.Video, ImageFormats.FromExtension(e.ToLowerInvariant()), e);
            Assert.AreEqual(ImageFormat.Video, ImageFormats.FromExtension(e.ToUpperInvariant()), e);
        }
        Assert.IsFalse(VideoFormats.IsVideoExtension(".jpg"));
        Assert.IsFalse(VideoFormats.IsVideoExtension(""));
    }

    [TestMethod]
    public void Photo_IsVideo_follows_extension()
    {
        Assert.IsTrue(new Photo(@"C:\x\a.MKV").IsVideo);
        Assert.IsFalse(new Photo(@"C:\x\a.jpg").IsVideo);
    }

    [TestMethod]
    public void Supported_extensions_include_videos_when_libmpv_is_present()
    {
        Assert.IsTrue(VideoFormats.LibMpvPresent);
        Assert.IsTrue(CodecDiscovery.SupportedExtensions.Contains(".webm"));
    }

    [TestMethod]
    public void Magick_no_longer_claims_video_extensions()
    {
        foreach (var e in new[] { ".mp4", ".mkv", ".mov", ".mpeg" })
            Assert.IsFalse(CodecDiscovery.Support.Magick(e), e);
    }

    [TestMethod]
    public void Settings_defaults()
    {
        var s = new AppSettings();
        Assert.IsTrue(s.ShowVideos);
        Assert.AreEqual(100, s.VideoVolume);
        Assert.IsFalse(s.VideoMuted);

        var json = JsonSerializer.Serialize(new SettingsWrapper { Settings = new AppSettings { ShowVideos = false, VideoVolume = 37, VideoMuted = true } },
            JsonSourceGenerationContext.Default.SettingsWrapper);
        var back = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.SettingsWrapper)!.Settings;
        Assert.IsFalse(back.ShowVideos);
        Assert.AreEqual(37, back.VideoVolume);
        Assert.IsTrue(back.VideoMuted);
    }
}
