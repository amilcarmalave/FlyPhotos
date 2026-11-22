using FlyPhotos.Display.ImageRendering;
using Windows.Foundation;

namespace FlyPhotos.Tests.Display;

[TestClass]
public sealed class VideoStillRendererTests
{
    [TestMethod]
    public void PlayGlyphRect_is_centred_64_dip()
    {
        Assert.AreEqual(new Rect(368, 268, 64, 64), VideoStillRenderer.PlayGlyphRect(new Size(800, 600)));
    }

    [TestMethod]
    public void HitsPlayGlyph_is_true_only_inside_the_square()
    {
        var canvas = new Size(800, 600);
        Assert.IsTrue(VideoStillRenderer.HitsPlayGlyph(canvas, new Point(400, 300)));
        Assert.IsTrue(VideoStillRenderer.HitsPlayGlyph(canvas, new Point(370, 270)));
        Assert.IsFalse(VideoStillRenderer.HitsPlayGlyph(canvas, new Point(360, 300)));
        Assert.IsFalse(VideoStillRenderer.HitsPlayGlyph(canvas, new Point(400, 340)));
        Assert.IsFalse(VideoStillRenderer.HitsPlayGlyph(canvas, new Point(0, 0)));
    }
}
