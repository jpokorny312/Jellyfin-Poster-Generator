using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PosterLogoComposer;

/// <summary>
/// Configuration for the Poster Logo Composer plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether movies should be processed.
    /// </summary>
    public bool ProcessMovies { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether series should be processed.
    /// </summary>
    public bool ProcessSeries { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an existing image should be overwritten.
    /// If false, only images that are currently missing (per image type: Primary, Logo,
    /// Backdrop) are filled in.
    /// </summary>
    public bool ReplaceExistingImages { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the logo used to build the Primary image
    /// should also be saved as the item's own Logo image.
    /// </summary>
    public bool SaveLogoImage { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a Backdrop image should also be selected
    /// and saved, choosing the highest resolution, best rated TMDb backdrop available.
    /// </summary>
    public bool SaveBackdropImage { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the logo language should be taken
    /// automatically from each item's library (its "Preferred metadata language" library
    /// setting, falling back to the server's default language), instead of always using
    /// <see cref="PreferredLogoLanguage"/>. If the library has no language configured,
    /// <see cref="PreferredLogoLanguage"/> is used as a fallback.
    /// </summary>
    public bool AutoDetectLogoLanguage { get; set; } = true;

    /// <summary>
    /// Gets or sets the preferred logo language (ISO 639-1 code, e.g. "en"). This is the
    /// language used both for the logo composited onto the Primary image and for the
    /// standalone Logo image, either directly (when <see cref="AutoDetectLogoLanguage"/>
    /// is off) or as a fallback when the library's language can't be determined. If no
    /// logo is available in the resolved language, a language-neutral logo is used, then
    /// an English logo, then (only if <see cref="AllowAnyLanguageLogoFallback"/> is on)
    /// any logo regardless of language.
    /// </summary>
    public string PreferredLogoLanguage { get; set; } = "en";

    /// <summary>
    /// Gets or sets a value indicating whether, as a last resort, a logo in any language
    /// may be used when no logo matches the resolved language, a language-neutral logo,
    /// or English. Off by default so an item never ends up with a logo in an unrelated
    /// language (e.g. a Chinese logo showing up in a German library) just because it
    /// happened to have the highest resolution among the leftovers — the item is simply
    /// skipped instead.
    /// </summary>
    public bool AllowAnyLanguageLogoFallback { get; set; }

    /// <summary>
    /// Gets or sets the minimum logo width (in pixels) that is treated as "good enough".
    /// Among candidates at or above this width, the one with the best community
    /// rating/vote count wins rather than the highest resolution one; resolution is only
    /// used as the final tiebreaker. If no candidate reaches this width, the width
    /// requirement is dropped (rather than skipping the item) and the same rating-first
    /// selection runs over every candidate.
    /// </summary>
    public int MinLogoWidth { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the weight applied to a logo candidate's community rating (0-10 scale)
    /// when choosing between candidates that already meet <see cref="MinLogoWidth"/>.
    /// </summary>
    public double LogoRatingWeight { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the weight applied to a logo candidate's (log-scaled) vote count when
    /// choosing between candidates that already meet <see cref="MinLogoWidth"/>.
    /// </summary>
    public double LogoVoteCountWeight { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the maximum number of textless poster candidates to download and
    /// analyze per item. Higher values improve the chance of finding a poster where the
    /// logo will be clearly visible but increase provider API and bandwidth usage.
    /// </summary>
    public int MaxPosterCandidates { get; set; } = 12;

    /// <summary>
    /// Gets or sets the height of the bottom band that gets blurred to make the logo stand
    /// out, as a fraction of the poster's total height. Readability against the logo is
    /// primarily down to picking a poster whose band already has good natural contrast
    /// (see <see cref="ContrastWeight"/>); see also <see cref="MinRenderContrastGap"/> for
    /// the last-resort fallback when even the best poster falls short. Kept fairly small
    /// by default so the blur only touches the bottom portion of the poster instead of a
    /// large chunk of the artwork.
    /// </summary>
    public double FadeBandHeightPercent { get; set; } = 0.22;

    /// <summary>
    /// Gets or sets the height of the bottom region, as a fraction of the poster's total
    /// height, that poster candidates are actually *scored* on for flatness/contrast —
    /// independent of, and normally taller than, <see cref="FadeBandHeightPercent"/> (which
    /// controls how much is actually blurred/rendered). Baked-in text/branding positioned
    /// just above the smaller render band — close enough to visually clash with the
    /// composited logo, but outside the band ever measured or touched for the render itself
    /// — would otherwise go undetected: the scorer would see only the plain, genuinely flat
    /// backdrop below it and rate the candidate as ideal. Widening the scoring window (while
    /// leaving the actual blur/render extent alone) catches this without changing the look
    /// of well-behaved posters at all.
    /// </summary>
    public double PosterScoreScanHeightPercent { get; set; } = 0.45;

    /// <summary>
    /// Gets or sets the Gaussian blur sigma applied to the fade band.
    /// </summary>
    public double BlurSigma { get; set; } = 12.0;

    /// <summary>
    /// Gets or sets how much of the fade band (as a fraction of the band's height) is
    /// used to smoothly ramp the blur up to full strength before it reaches the logo's
    /// top edge, instead of an abrupt sharp-to-blurry transition.
    /// </summary>
    public double GradientFeatherPercent { get; set; } = 0.25;

    /// <summary>
    /// Gets or sets the minimum acceptable luminance gap (0-255) between the logo and the
    /// poster band actually behind it, checked against the poster actually chosen (after
    /// <see cref="ContrastWeight"/>-based selection already tried to pick a well-matched
    /// one). If TMDb simply has no better-contrasting candidate for this title and the gap
    /// still falls short, a soft backdrop is drawn — but only directly behind the logo's
    /// own footprint, never across the whole band — to close it. Set to 0 to disable this
    /// fallback entirely and rely purely on poster selection.
    /// </summary>
    public double MinRenderContrastGap { get; set; } = 70;

    /// <summary>
    /// Gets or sets the maximum opacity (0-255) of the localized backdrop drawn behind the
    /// logo when <see cref="MinRenderContrastGap"/> isn't naturally met. Scoped tightly to
    /// the logo's own footprint and heavily feathered, so — unlike the old whole-band tint
    /// — it never reads as a flat rectangle over a solid-color background. Set to 0 to
    /// disable.
    /// </summary>
    public int MaxLocalizedShadowAlpha { get; set; } = 180;

    /// <summary>
    /// Gets or sets a hard ceiling (0-255) on the bottom band's luminance std-dev for even
    /// the best-scoring textless poster candidate. TMDb occasionally mistags a poster as
    /// textless (<c>language == null</c>) when it actually has baked-in text/logos/branding
    /// (e.g. a foreign streaming service's own release image) — <see cref="FlatnessWeight"/>
    /// already prefers calmer candidates during selection, but with nothing better
    /// available the "best of a bad set" can still be unusable. If the chosen candidate's
    /// std-dev exceeds this value, Primary generation is skipped for that item entirely
    /// rather than compositing the logo onto a busy/textful background. Set to 0 to disable
    /// and never skip (the historical behavior). Off by default since a sensible cutoff
    /// depends on how busy your library's legitimate posters normally are; try ~95-110 if
    /// you hit a case like this.
    /// </summary>
    public double MaxPosterBandStdDev { get; set; }

    /// <summary>
    /// Gets or sets the maximum logo width, as a fraction of the poster width.
    /// </summary>
    public double LogoMaxWidthPercent { get; set; } = 0.62;

    /// <summary>
    /// Gets or sets the maximum logo height, as a fraction of the poster's total height
    /// (not the fade band height, so shrinking <see cref="FadeBandHeightPercent"/> doesn't
    /// automatically shrink the logo too). The logo is free to extend above the fade band
    /// itself; only the portion that overlaps the band gets the blur/fade treatment.
    /// </summary>
    public double LogoMaxHeightPercent { get; set; } = 0.19;

    /// <summary>
    /// Gets or sets the bottom margin below the logo, as a fraction of the poster height.
    /// </summary>
    public double LogoBottomMarginPercent { get; set; } = 0.05;

    /// <summary>
    /// Gets or sets the weight given to "flatness" (low contrast/variance) of a poster's
    /// bottom band when scoring candidates for logo visibility.
    /// </summary>
    public double FlatnessWeight { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the weight given to actual logo/background contrast when scoring
    /// poster candidates: how far apart the band's real (unaltered — only blur is ever
    /// applied to it) luminance and the specific logo's own luminance are. This is what
    /// avoids e.g. a white logo landing on a poster whose band is already too bright.
    /// </summary>
    public double ContrastWeight { get; set; } = 1.5;

    /// <summary>
    /// Gets or sets the weight given to raw resolution when scoring poster candidates,
    /// used as a secondary factor after flatness/contrast.
    /// </summary>
    public double ResolutionWeight { get; set; } = 0.2;

    /// <summary>
    /// Gets or sets the weight given to a poster candidate's TMDb community rating (0-10)
    /// when scoring poster candidates. Normalized to roughly the same 0-1 scale as
    /// flatness/contrast so it nudges a near-tie toward the more popular/vetted candidate
    /// rather than dominating — e.g. an obscure, unrated, very-low-resolution upload can
    /// otherwise end up scoring deceptively "flat" simply because downscaling blurred
    /// whatever text/branding it had baked in, even though better-reviewed alternatives
    /// with equal contrast exist.
    /// </summary>
    public double PosterRatingWeight { get; set; } = 0.5;

    /// <summary>
    /// Gets or sets the weight given to a poster candidate's (log-scaled) TMDb vote count.
    /// Kept modest by default since most legitimate poster uploads have few or no votes —
    /// this should break near-ties, not penalize an otherwise-good, simply unvoted poster.
    /// </summary>
    public double PosterVoteCountWeight { get; set; } = 0.3;

    /// <summary>
    /// Gets or sets the relative resolution difference (as a fraction, e.g. 0.1 = 10%)
    /// below which two backdrop candidates are treated as the same resolution tier, so
    /// rating and vote count decide between them instead of raw pixel count.
    /// </summary>
    public double BackdropResolutionTierTolerance { get; set; } = 0.1;

    /// <summary>
    /// Gets or sets the weight applied to a backdrop candidate's community rating
    /// (0-10 scale) when comparing candidates within the same resolution tier.
    /// </summary>
    public double BackdropRatingWeight { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the weight applied to a backdrop candidate's (log-scaled) vote count
    /// when comparing candidates within the same resolution tier.
    /// </summary>
    public double BackdropVoteCountWeight { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the maximum number of Backdrop images to keep. Every time the
    /// Backdrop image is (re)generated for an item, its existing Backdrop images are
    /// replaced with up to this many best-scoring TMDb candidates, so the count never
    /// grows unbounded across repeated task runs.
    /// </summary>
    public int MaxBackdrops { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether TMDb image URLs are rewritten to request
    /// the original (full) resolution before downloading, overriding whatever size the
    /// TMDb provider's own configuration would otherwise hand back (some setups return a
    /// downscaled preview URL, e.g. 500x750, even though the source poster is 1500x3000
    /// or larger).
    /// </summary>
    public bool UseOriginalTmdbResolution { get; set; } = true;
}
