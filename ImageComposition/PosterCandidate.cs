using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.PosterLogoComposer.ImageComposition;

/// <summary>
/// A downloaded, scored textless poster candidate.
/// </summary>
/// <param name="Info">The remote image metadata this candidate came from.</param>
/// <param name="Bytes">The raw downloaded image bytes.</param>
/// <param name="BandStats">The candidate's bottom-band luminance statistics.</param>
/// <param name="PeakLocalVariance">The candidate's peak-vs-median strip variance from <see cref="PosterScorer.GetPeakLocalVariance"/>.</param>
public sealed record PosterCandidate(RemoteImageInfo Info, byte[] Bytes, PosterScorer.BandStats BandStats, double PeakLocalVariance);
