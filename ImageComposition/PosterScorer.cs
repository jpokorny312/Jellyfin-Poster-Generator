using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Jellyfin.Plugin.PosterLogoComposer.ImageComposition;

/// <summary>
/// Scores poster candidates by how suitable their bottom band is for a logo overlay to be
/// composited on top of. A "flat" (low variance / low contrast) bottom band means the
/// eventual blur+fade will produce a clean, evenly shaded backdrop that makes the logo pop,
/// rather than one competing with busy artwork, faces or text underneath it. A poster is
/// also scored on how much contrast the fade (always the same everywhere, so it stays a
/// consistent, subtle look across the library) is predicted to leave between the band and
/// this specific logo's own color, so a light poster isn't picked for a light logo (or a
/// dark poster for a dark logo) even though the fade alone can't fix that.
/// </summary>
public static class PosterScorer
{
    private const int SampleStep = 4;

    /// <summary>
    /// A bottom band's luminance statistics.
    /// </summary>
    /// <param name="Mean">Average luminance (0-255).</param>
    /// <param name="StdDev">Luminance standard deviation (0-255); lower means flatter.</param>
    public readonly record struct BandStats(double Mean, double StdDev);

    /// <summary>
    /// Computes the mean and standard deviation of pixel luminance within the bottom band
    /// of the image (the region the logo fade will be drawn over).
    /// </summary>
    /// <param name="poster">The decoded poster image.</param>
    /// <param name="bandHeightPercent">Height of the bottom band, as a fraction of total image height.</param>
    /// <returns>The band's luminance statistics.</returns>
    public static BandStats GetBottomBandStats(Image<Rgba32> poster, double bandHeightPercent)
    {
        var bandHeight = Math.Max(1, (int)(poster.Height * bandHeightPercent));
        var startY = Math.Max(0, poster.Height - bandHeight);

        double sum = 0;
        double sumSquares = 0;
        long count = 0;

        poster.ProcessPixelRows(accessor =>
        {
            for (var y = startY; y < accessor.Height; y += SampleStep)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x += SampleStep)
                {
                    var pixel = row[x];
                    var luminance = (0.299 * pixel.R) + (0.587 * pixel.G) + (0.114 * pixel.B);
                    sum += luminance;
                    sumSquares += luminance * luminance;
                    count++;
                }
            }
        });

        if (count == 0)
        {
            return new BandStats(0, 0);
        }

        var mean = sum / count;
        var variance = (sumSquares / count) - (mean * mean);
        return new BandStats(mean, Math.Sqrt(Math.Max(variance, 0)));
    }

    /// <summary>
    /// Computes the alpha-weighted average luminance (0-255) of a logo's visible pixels,
    /// ignoring near-fully-transparent ones so the large transparent margin typical of a
    /// logo PNG doesn't skew the result.
    /// </summary>
    /// <param name="logo">The decoded logo image.</param>
    /// <returns>The logo's average luminance (0-255).</returns>
    public static double GetLogoAverageLuminance(Image<Rgba32> logo)
    {
        double weightedSum = 0;
        double weightSum = 0;

        logo.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    if (pixel.A < 10)
                    {
                        continue;
                    }

                    var weight = pixel.A / 255.0;
                    var luminance = (0.299 * pixel.R) + (0.587 * pixel.G) + (0.114 * pixel.B);
                    weightedSum += luminance * weight;
                    weightSum += weight;
                }
            }
        });

        // A fully transparent "logo" shouldn't happen; treat it as neutral gray so it
        // doesn't skew candidate scoring one way or the other.
        return weightSum > 0 ? weightedSum / weightSum : 128;
    }

    /// <summary>
    /// Computes a combined score (higher is better) for a poster candidate: a flatness
    /// term (inverse of luminance std-dev in the bottom band), a contrast term measuring
    /// how well this specific logo will read against the band's actual (unaltered)
    /// luminance — <see cref="LogoPosterComposer"/> only blurs the band, it does not
    /// darken/lighten it — a smaller resolution term, and a popularity term from TMDb's own
    /// community rating/vote count.
    /// </summary>
    /// <param name="bandStats">The candidate's bottom-band luminance statistics.</param>
    /// <param name="logoLuminance">The logo's own average luminance (0-255).</param>
    /// <param name="pixelCount">Total pixel count of the candidate (width * height).</param>
    /// <param name="maxPixelCountInSet">The largest pixel count among all candidates being compared, used to normalize.</param>
    /// <param name="communityRating">The candidate's TMDb community rating (0-10), if any.</param>
    /// <param name="voteCount">The candidate's TMDb vote count, if any.</param>
    /// <param name="flatnessWeight">Configured weight for the flatness term.</param>
    /// <param name="contrastWeight">Configured weight for the logo-contrast term.</param>
    /// <param name="resolutionWeight">Configured weight for the resolution term.</param>
    /// <param name="ratingWeight">Configured weight for the community-rating term.</param>
    /// <param name="voteCountWeight">Configured weight for the (log-scaled) vote-count term.</param>
    /// <returns>A combined score; higher means better suited for this logo.</returns>
    public static double GetScore(
        BandStats bandStats,
        double logoLuminance,
        long pixelCount,
        long maxPixelCountInSet,
        double? communityRating,
        int? voteCount,
        double flatnessWeight,
        double contrastWeight,
        double resolutionWeight,
        double ratingWeight,
        double voteCountWeight)
    {
        // Std-dev of luminance across a real photo/poster region rarely exceeds ~110-120,
        // so this normalizes to roughly a 0-1 "flatness" score without needing a second pass.
        var flatness = 1.0 - Math.Clamp(bandStats.StdDev / 120.0, 0.0, 1.0);

        // The band is only blurred, never darkened/lightened, so its actual mean luminance
        // (not some hypothetical faded value) is what the logo will actually sit against.
        // A poster whose band luminance is already close to the logo's own (e.g. a bright
        // band under a bright/white logo) is penalized in favor of one with a wide natural
        // gap (e.g. a bright band, dark logo).
        var contrast = Math.Abs(bandStats.Mean - logoLuminance) / 255.0;

        var normalizedResolution = maxPixelCountInSet <= 0
            ? 0.0
            : Math.Clamp((double)pixelCount / maxPixelCountInSet, 0.0, 1.0);

        // Normalized to roughly 0-1, like the other terms, so this nudges a near-tie toward
        // the more popular/vetted candidate rather than dominating flatness/contrast — e.g.
        // an obscure, unrated, very-low-resolution upload can end up scoring deceptively
        // "flat" simply because downscaling blurred whatever text/branding it had baked in.
        var normalizedRating = Math.Clamp((communityRating ?? 0) / 10.0, 0.0, 1.0);
        var normalizedVoteCount = Math.Clamp(Math.Log10((voteCount ?? 0) + 1) / 3.0, 0.0, 1.0);

        return (flatness * flatnessWeight)
            + (contrast * contrastWeight)
            + (normalizedResolution * resolutionWeight)
            + (normalizedRating * ratingWeight)
            + (normalizedVoteCount * voteCountWeight);
    }
}
