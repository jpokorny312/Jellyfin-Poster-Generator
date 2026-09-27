using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Jellyfin.Plugin.PosterLogoComposer.ImageComposition;

/// <summary>
/// Composites a textless poster with a logo: the bottom band of the poster is blurred so
/// the logo reads clearly. Contrast between the logo and the band is primarily handled by
/// <see cref="PosterScorer"/> picking a poster whose band already contrasts well with this
/// specific logo. When even the best available poster still falls short of a minimum
/// contrast gap (e.g. because TMDb has no better-contrasting candidate for this title), a
/// soft, heavily-feathered backdrop is drawn — but only directly behind the logo's own
/// footprint, not across the whole band — so it never turns into the old flat, hard-edged
/// rectangle over a large solid-color area.
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

        // 3. Getting good logo/background contrast is primarily handled by PosterScorer
        // picking a poster whose (blurred) band already contrasts well with this specific
        // logo, before this method ever runs. But if TMDb simply has no better-contrasting
        // candidate for this title, the best available poster can still fall short — so as
        // a last resort, top up the contrast with a soft backdrop confined to the logo's
        // own footprint (never the old whole-band tint, which looked like a flat, oddly
        // colored rectangle over a solid-color background).
        if (config.MinRenderContrastGap > 0 && config.MaxLocalizedShadowAlpha > 0)
        {
            var bandStats = PosterScorer.GetBottomBandStats(poster, config.FadeBandHeightPercent);
            var logoLuminance = PosterScorer.GetLogoAverageLuminance(logo);
            var actualGap = Math.Abs(bandStats.Mean - logoLuminance);

            if (actualGap < config.MinRenderContrastGap)
            {
                // Move away from the logo's own brightness, whichever direction widens the
                // gap — darken behind a light/white logo, lighten behind a dark logo.
                byte fillLuminance = logoLuminance >= 128 ? (byte)0 : (byte)255;
                var shortfall = Math.Clamp((config.MinRenderContrastGap - actualGap) / config.MinRenderContrastGap, 0.0, 1.0);
                var alpha = (byte)Math.Clamp(shortfall * config.MaxLocalizedShadowAlpha, 0, 255);

                if (alpha > 0)
                {
                    DrawLocalizedContrastBackdrop(poster, new Rectangle(logoX, logoY, logo.Width, logo.Height), fillLuminance, alpha);
                }
            }
        }

        poster.Mutate(ctx => ctx.DrawImage(logo, new Point(logoX, logoY), 1f));

        using var output = new MemoryStream();
        poster.Save(output, new JpegEncoder { Quality = 92 });
        return output.ToArray();
    }

    /// <summary>
    /// Draws a soft, elliptical, heavily-feathered backdrop scoped tightly to
    /// <paramref name="logoBounds"/> (plus generous padding) — full strength directly
    /// behind the logo, smoothly falling off to nothing by the padded edge. Unlike a
    /// whole-band tint, this never reads as a hard-edged rectangle because it always
    /// follows the logo's own footprint and fades out well before the band's edges.
    /// </summary>
    private static void DrawLocalizedContrastBackdrop(Image<Rgba32> poster, Rectangle logoBounds, byte fillLuminance, byte maxAlpha)
    {
        var paddingX = Math.Max(1, (int)(logoBounds.Width * 0.35));
        var paddingY = Math.Max(1, (int)(logoBounds.Height * 0.7));
        var centerX = logoBounds.X + (logoBounds.Width / 2.0);
        var centerY = logoBounds.Y + (logoBounds.Height / 2.0);
        var radiusX = (logoBounds.Width / 2.0) + paddingX;
        var radiusY = (logoBounds.Height / 2.0) + paddingY;

        var minX = Math.Max(0, (int)Math.Floor(centerX - radiusX));
        var maxX = Math.Min(poster.Width - 1, (int)Math.Ceiling(centerX + radiusX));
        var minY = Math.Max(0, (int)Math.Floor(centerY - radiusY));
        var maxY = Math.Min(poster.Height - 1, (int)Math.Ceiling(centerY + radiusY));
        if (minX > maxX || minY > maxY)
        {
            return;
        }

        var region = new Rectangle(minX, minY, (maxX - minX) + 1, (maxY - minY) + 1);
        using (var regionLayer = poster.Clone(ctx => ctx.Crop(region)))
        {
            regionLayer.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var absoluteY = minY + y;
                    var dy = (absoluteY - centerY) / radiusY;
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        var absoluteX = minX + x;
                        var dx = (absoluteX - centerX) / radiusX;
                        var normalizedDistance = Math.Sqrt((dx * dx) + (dy * dy));
                        if (normalizedDistance >= 1.0)
                        {
                            continue;
                        }

                        var t = 1.0 - SmoothstepUnit(normalizedDistance);
                        var alpha = t * maxAlpha / 255.0;
                        var p = row[x];
                        row[x] = new Rgba32(
                            (byte)Math.Clamp((p.R * (1 - alpha)) + (fillLuminance * alpha), 0, 255),
                            (byte)Math.Clamp((p.G * (1 - alpha)) + (fillLuminance * alpha), 0, 255),
                            (byte)Math.Clamp((p.B * (1 - alpha)) + (fillLuminance * alpha), 0, 255),
                            p.A);
                    }
                }
            });

            poster.Mutate(ctx => ctx.DrawImage(regionLayer, new Point(minX, minY), 1f));
        }
    }

    /// <summary>
    /// Smoothstep eased falloff for a value already normalized to [0, 1].
    /// </summary>
    private static double SmoothstepUnit(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3 - (2 * t));
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
