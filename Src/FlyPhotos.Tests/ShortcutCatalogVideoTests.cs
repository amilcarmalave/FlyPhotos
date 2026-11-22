using Windows.System;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Localization;
using FlyPhotos.UI.Views;

namespace FlyPhotos.Tests;

/// <summary>The video commands' place in the shortcut catalog: their default chords, no chord clash with the
/// existing commands, and an en-US name for the settings page.</summary>
[TestClass]
public sealed class ShortcutCatalogVideoTests
{
    private static readonly CommandId[] VideoCommands =
    [
        CommandId.VideoPlayPause, CommandId.VideoSeekForward, CommandId.VideoSeekBack,
        CommandId.VideoVolumeUp, CommandId.VideoVolumeDown, CommandId.VideoMute
    ];

    // Defaults only: Resolve overlays whatever AppConfig.Settings carries.
    private static Dictionary<KeyChord, CommandId> DefaultRoutes()
    {
        AppConfig.Settings = new AppSettings();
        return ShortcutCatalog.Resolve();
    }

    private static KeyChord K(VirtualKey key, bool shift = false) => new(key, false, false, shift);

    [TestMethod]
    public void Video_commands_have_the_spec_chords()
    {
        var routes = DefaultRoutes();

        Assert.AreEqual(CommandId.VideoPlayPause, routes[K(VirtualKey.Space)]);
        Assert.AreEqual(CommandId.VideoSeekForward, routes[K(VirtualKey.Right, shift: true)]);
        Assert.AreEqual(CommandId.VideoSeekBack, routes[K(VirtualKey.Left, shift: true)]);
        Assert.AreEqual(CommandId.VideoVolumeUp, routes[K(VirtualKey.Up, shift: true)]);
        Assert.AreEqual(CommandId.VideoVolumeDown, routes[K(VirtualKey.Down, shift: true)]);
        Assert.IsFalse(routes.ContainsValue(CommandId.VideoMute), "VideoMute ships with no chord.");

        Assert.IsTrue(ShortcutCatalog.Has(CommandId.VideoPlayPause, CommandFlags.Suppress),
            "Space must not also click the focused button.");
        Assert.IsFalse(ShortcutCatalog.Has(CommandId.VideoPlayPause, CommandFlags.Repeat));
        foreach (var id in new[] { CommandId.VideoSeekForward, CommandId.VideoSeekBack,
                                   CommandId.VideoVolumeUp, CommandId.VideoVolumeDown })
            Assert.IsTrue(ShortcutCatalog.Has(id, CommandFlags.Repeat), $"{id} should repeat while held.");
    }

    [TestMethod]
    public void No_chord_is_bound_twice()
    {
        AppConfig.Settings = new AppSettings();
        var owners = new Dictionary<KeyChord, CommandId>();
        foreach (var id in Enum.GetValues<CommandId>())
        foreach (var chord in ShortcutCatalog.DefaultChords(id))
            Assert.IsTrue(owners.TryAdd(chord, id),
                $"{chord.Format()} is a default for both {id} and {owners.GetValueOrDefault(chord)}.");
    }

    [TestMethod]
    public void Every_video_command_has_an_en_US_name()
    {
        var group = L.Get("ShortcutGroup_Video");
        Assert.IsFalse(string.IsNullOrEmpty(group));
        Assert.AreNotEqual("ShortcutGroup_Video", group);

        foreach (var id in VideoCommands)
        {
            var key = $"ShortcutName_{id}";
            var name = L.Get(key);
            Assert.IsFalse(string.IsNullOrEmpty(name), $"{key} is missing.");
            Assert.AreNotEqual(key, name);
        }
    }
}
