using System;
using System.IO;
using System.Numerics;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.UI;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// Draws a video's still frame like any static image, then a play glyph centred on the canvas in screen space (it does
/// not zoom, pan or rotate with the still). Wraps a <see cref="StaticImageRenderer"/> rather than subclassing it:
/// its <c>Draw</c> is not virtual, and the wrapped renderer keeps owning the mip chain and the checkered brush.
/// Threads: as <see cref="StaticImageRenderer"/> (Draw and Dispose on the W2D thread).
/// </summary>
internal sealed partial class VideoStillRenderer : IRenderer
{
    /// <summary>Side of the centred glyph, in DIPs.</summary>
    public const double GlyphSize = 64;

    private readonly StaticImageRenderer _still;
    private readonly CanvasAnimatedControl _canvas;

    public VideoStillRenderer(CanvasAnimatedControl canvas, VideoHqDisplayItem item, Action invalidate)
    {
        _canvas = canvas;
        // A video has no alpha; the item owns the frame and the mip task leases it, as for any static HQ.
        _still = new StaticImageRenderer(canvas, item.Bitmap, false, invalidate, mipChainOwner: item);
    }

    /// <summary>The glyph's 64 DIP square, centred on a canvas of <paramref name="canvasDip"/>.</summary>
    public static Rect PlayGlyphRect(Windows.Foundation.Size canvasDip) =>
        new((canvasDip.Width - GlyphSize) / 2, (canvasDip.Height - GlyphSize) / 2, GlyphSize, GlyphSize);

    /// <summary>Whether <paramref name="p"/> (canvas DIPs) is on the glyph.</summary>
    public static bool HitsPlayGlyph(Windows.Foundation.Size canvasDip, Point p)
    {
        var r = PlayGlyphRect(canvasDip);
        return p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
    }

    public CanvasImageBrush CheckeredBrush { set => _still.CheckeredBrush = value; }

    /// <summary>W2D thread. The still, then the glyph with an identity transform in DIP units.</summary>
    public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality,
        bool isAnimating)
    {
        _still.Draw(session, viewState, quality, isAnimating);

        session.Units = CanvasUnits.Dips;
        session.Transform = Matrix3x2.Identity;
        session.Antialiasing = CanvasAntialiasing.Antialiased;
        PlayGlyph.Draw(session, PlayGlyphRect(_canvas.Size), PlayGlyph.LargeFormat);
    }

    public void HandleScalingMethodChange() => _still.HandleScalingMethodChange();

    public void Dispose() => _still.Dispose();
}

/// <summary>The play triangle in a translucent dark circle, for the centred still glyph and the strip badge.</summary>
internal static class PlayGlyph
{
    private const string Glyph = "";
    private static readonly Color Fill = Color.FromArgb(0x99, 0, 0, 0);
    private static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    // Made once, read-only after: the large one is used by the main canvas's thread, the small one by the strip's.
    public static CanvasTextFormat LargeFormat => field ??= CreateFormat((float)VideoStillRenderer.GlyphSize * 0.5f);
    public static CanvasTextFormat BadgeFormat => field ??= CreateFormat(16 * 0.55f);

    private static CanvasTextFormat CreateFormat(float fontSize)
    {
        var fontPath = PathResolver.IsPackagedApp
            ? "ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"
            : Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Segoe Fluent Icons.ttf") + "#Segoe Fluent Icons";
        return new CanvasTextFormat
        {
            FontFamily = fontPath,
            FontSize = fontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
    }

    /// <summary>Draws the glyph filling the square <paramref name="square"/>, in the session's current units.</summary>
    public static void Draw(CanvasDrawingSession ds, Rect square, CanvasTextFormat format)
    {
        var center = new Vector2((float)(square.X + square.Width / 2), (float)(square.Y + square.Height / 2));
        ds.FillCircle(center, (float)(square.Width / 2), Fill);
        ds.DrawText(Glyph, square, White, format);
    }
}
