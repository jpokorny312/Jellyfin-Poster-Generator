using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Jellyfin.Plugin.PosterLogoComposer.ImageComposition;

/// <summary>
/// Composites a textless poster with a logo: the bottom band of the poster is blurred and,
/// only as much as actually needed, faded toward black or white — whichever increases the
/// gap between the band's brightness and the logo's own — so the logo reads clearly. The
/// same rule runs for every item, so a poster/logo pairing that already contrasts well
/// (helped along by <see cref="PosterScorer"/> picking a suitable poster to begin with)
/// gets little to no fade, instead of an always-on, fixed-strength overlay that could turn
/// into a visible, hard-edged block over an already high-contrast area.
/// </summary>
public static class LogoPosterComposer
{
    /// <summary>
    /// Produces the composited Primary image.
    /// </summary>
    /// <param name="posterBytes">Raw bytes of the textless poster.</param>
    /// <param name="logoBytes">Raw bytes of the (transparent) logo.</param>
    /// <param name="config">The plugin configuration controlling fade/logo geometry.</param>
    /// <returns>Encoded JPEG bytes of the composited image.</returns>
    public static byte[] Compose(byte[] posterBytes, byte[] logoBytes, PluginConfiguration config)
    {
        using var poster = Image.Load<Rgba32>(posterBytes);
        using var logo = Image.Load<Rgba32>(logoBytes);

        var bandHeight = Math.Clamp((int)(poster.Height * config.FadeBandHeightPercent), 1, poster.Height);
        var bandY = poster.Height - bandHeight;
        var vFeather = Math.Max(1, (int)(bandHeight * config.GradientFeatherPercent));

        // 1. Blur the bottom band for a frosted-glass backdrop, cross-fading smoothly from
        // the untouched, sharp poster above into the fully blurred band — instead of
        // blurring a hard-edged rectangle, which left a visible seam (sharp-to-blurry cut)
        // exactly at the band's top edge, independent of the darkening fade below.
        var blurRegionTop = Math.Max(0, bandY - vFeather);
        var blurRegionRect = new Rectangle(0, blurRegionTop, poster.Width, poster.Height - blurRegionTop);
        using (var blurredLayer = poster.Clone(ctx => ctx.Crop(blurRegionRect)))
        {
            blurredLayer.Mutate(ctx => ctx.GaussianBlur((float)config.BlurSigma));

            blurredLayer.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var absoluteY = blurRegionTop + y;
                    var t = Smoothstep(absoluteY, blurRegionTop, bandY, poster.Height, poster.Height);
                    var alpha = (byte)Math.Clamp(t * 255, 0, 255);
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        var p = row[x];
                        row[x] = new Rgba32(p.R, p.G, p.B, alpha);
                    }
                }
            });

            poster.Mutate(ctx => ctx.DrawImage(blurredLayer, new Point(0, blurRegionTop), 1f));
        }

        // 2. Resize the logo to fit within the configured width/height budget (without
        // upscaling) *before* building the fade, so the fade can be shaped around where
        // the logo will actually land instead of a generic top-to-bottom ramp.
        var maxLogoWidth = poster.Width * config.LogoMaxWidthPercent;
        var maxLogoHeight = poster.Height * config.LogoMaxHeightPercent;
        var scale = Math.Min(maxLogoWidth / logo.Width, maxLogoHeight / logo.Height);
        scale = Math.Min(scale, 1.0);

        var targetWidth = Math.Max(1, (int)Math.Round(logo.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(logo.Height * scale));
        logo.Mutate(ctx => ctx.Resize(targetWidth, targetHeight));

        var logoX = (poster.Width - logo.Width) / 2;
        var bottomMargin = (int)(poster.Height * config.LogoBottomMarginPercent);
        var logoY = poster.Height - logo.Height - bottomMargin;

        // 3. No color tint is added at all — the blur above is the entire "fade". A
        // black/white overlay (even one only applied where actually needed) turned into a
        // visible, oddly-colored block whenever the band was a flat, saturated area with no
        // texture (e.g. a solid-color poster background), since there was nothing to blend
        // the tint into. Getting good logo/background contrast is handled entirely by
        // PosterScorer picking a poster whose (blurred) band already contrasts well with
        // this specific logo, before this method ever runs.
        poster.Mutate(ctx => ctx.DrawImage(logo, new Point(logoX, logoY), 1f));

        using var output = new MemoryStream();
        poster.Save(output, new JpegEncoder { Quality = 92 });
        return output.ToArray();
    }

    /// <summary>
    /// Computes a 0-1 falloff factor along one axis: 0 before <paramref name="rampStart"/>,
    /// smoothly easing up to 1 across [<paramref name="rampStart"/>, <paramref name="coreStart"/>],
    /// staying at 1 across [<paramref name="coreStart"/>, <paramref name="coreEnd"/>] (the
    /// "core" zone directly behind the logo), then easing back down to 0 across
    /// [<paramref name="coreEnd"/>, <paramref name="rampEnd"/>].
    /// </summary>
    private static double Smoothstep(int position, int rampStart, int coreStart, int coreEnd, int rampEnd)
    {
        double t;
        if (position < rampStart || position > rampEnd)
        {
            t = 0.0;
        }
        else if (position < coreStart)
        {
            t = (double)(position - rampStart) / Math.Max(coreStart - rampStart, 1);
        }
        else if (position <= coreEnd)
        {
            t = 1.0;
        }
        else
        {
            t = 1.0 - ((double)(position - coreEnd) / Math.Max(rampEnd - coreEnd, 1));
        }

        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3 - (2 * t));
    }
}
