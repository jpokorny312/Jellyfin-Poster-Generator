using System;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.PosterLogoComposer.ImageComposition;

/// <summary>
/// Scores and compares backdrop candidates: resolution first, community rating and vote
/// count as a tiebreaker among candidates whose resolution is roughly equal.
/// </summary>
public static class BackdropScorer
{
    /// <summary>
    /// Gets the effective pixel count (width * height) of a candidate, or 0 if unknown.
    /// </summary>
    /// <param name="image">The candidate image.</param>
    /// <returns>The pixel count.</returns>
    public static long GetPixelCount(RemoteImageInfo image)
    {
        return (long)(image.Width ?? 0) * (image.Height ?? 0);
    }

    /// <summary>
    /// Determines whether two pixel counts are close enough to be treated as the same
    /// resolution tier, given a relative tolerance.
    /// </summary>
    /// <param name="pixelsA">Pixel count of the first candidate.</param>
    /// <param name="pixelsB">Pixel count of the second candidate.</param>
    /// <param name="tolerance">Relative tolerance (e.g. 0.1 for 10%).</param>
    /// <returns>True if the two pixel counts are within tolerance of each other.</returns>
    public static bool IsSameResolutionTier(long pixelsA, long pixelsB, double tolerance)
    {
        var larger = Math.Max(pixelsA, pixelsB);
        if (larger == 0)
        {
            return true;
        }

        return Math.Abs(pixelsA - pixelsB) / (double)larger <= tolerance;
    }

    /// <summary>
    /// Computes a secondary score from community rating and (log-scaled) vote count, used
    /// as a tiebreaker between candidates whose resolution is roughly equal.
    /// </summary>
    /// <param name="image">The candidate image.</param>
    /// <param name="ratingWeight">Weight applied to the community rating (0-10).</param>
    /// <param name="voteCountWeight">Weight applied to the log-scaled vote count.</param>
    /// <returns>The secondary score.</returns>
    public static double GetSecondaryScore(RemoteImageInfo image, double ratingWeight, double voteCountWeight)
    {
        var rating = image.CommunityRating ?? 0;
        var voteScore = Math.Log10((image.VoteCount ?? 0) + 1);
        return (rating * ratingWeight) + (voteScore * voteCountWeight);
    }

    /// <summary>
    /// Compares two candidates: resolution first (outside the configured tolerance), then
    /// the combined rating/vote-count score.
    /// </summary>
    /// <param name="a">The first candidate.</param>
    /// <param name="b">The second candidate.</param>
    /// <param name="tolerance">Resolution tier tolerance.</param>
    /// <param name="ratingWeight">Weight applied to the community rating.</param>
    /// <param name="voteCountWeight">Weight applied to the log-scaled vote count.</param>
    /// <returns>A negative number if <paramref name="a"/> is worse than <paramref name="b"/>, positive if better, 0 if equal.</returns>
    public static int Compare(RemoteImageInfo a, RemoteImageInfo b, double tolerance, double ratingWeight, double voteCountWeight)
    {
        var pixelsA = GetPixelCount(a);
        var pixelsB = GetPixelCount(b);

        if (!IsSameResolutionTier(pixelsA, pixelsB, tolerance))
        {
            return pixelsA.CompareTo(pixelsB);
        }

        return GetSecondaryScore(a, ratingWeight, voteCountWeight)
            .CompareTo(GetSecondaryScore(b, ratingWeight, voteCountWeight));
    }
}
