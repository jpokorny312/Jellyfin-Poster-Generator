using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PosterLogoComposer.ImageComposition;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Jellyfin.Plugin.PosterLogoComposer.ScheduledTasks;

/// <summary>
/// Scheduled task that generates a Primary image for movies/series by compositing a
/// textless TMDb poster with the item's logo, blurred and faded in at the bottom.
/// </summary>
public class PosterLogoComposerTask : IScheduledTask
{
    private const string TmdbProviderNameFragment = "themoviedb";

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IDirectoryService _directoryService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PosterLogoComposerTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterLogoComposerTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="directoryService">Instance of the <see cref="IDirectoryService"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{PosterLogoComposerTask}"/> interface.</param>
    public PosterLogoComposerTask(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IDirectoryService directoryService,
        IHttpClientFactory httpClientFactory,
        ILogger<PosterLogoComposerTask> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _directoryService = directoryService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Compose Poster Logos";

    /// <inheritdoc />
    public string Key => "PosterLogoComposerTask";

    /// <inheritdoc />
    public string Description =>
        "Generates a Primary image per movie/series from a textless TMDb poster with the " +
        "logo overlaid on a blurred, darkened fade, choosing the poster that shows the " +
        "logo off best.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // No default trigger; this generates significant provider API and CPU usage.
        return Array.Empty<TaskTriggerInfo>();
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        var itemKinds = GetConfiguredItemKinds(config).ToArray();
        if (itemKinds.Length == 0)
        {
            _logger.LogInformation("No item types are enabled in the plugin configuration, nothing to do");
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = itemKinds,
            SourceTypes = new[] { SourceType.Library }
        });

        _logger.LogInformation("Poster Logo Composer processing {ItemCount} items", items.Count);

        var processed = 0;
        var updated = 0;
        var alreadyComplete = 0;
        var attemptedButNoImageFound = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (needsPrimary, needsLogo, needsBackdrop) = GetNeededWork(item, config);
            if (!needsPrimary && !needsLogo && !needsBackdrop)
            {
                // Already has every image type this plugin manages (or the corresponding
                // "also save" option is off) and "Replace existing images" is disabled, so
                // there is nothing to do — skipped without contacting TMDb at all.
                alreadyComplete++;
                processed++;
                progress.Report(100.0 * processed / Math.Max(items.Count, 1));
                continue;
            }

            try
            {
                if (await ProcessItemAsync(item, config, needsPrimary, needsLogo, needsBackdrop, cancellationToken).ConfigureAwait(false))
                {
                    updated++;
                }
                else
                {
                    attemptedButNoImageFound++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compose Primary image for {ItemName} ({ItemId})", item.Name, item.Id);
                attemptedButNoImageFound++;
            }

            processed++;
            progress.Report(100.0 * processed / Math.Max(items.Count, 1));
        }

        _logger.LogInformation(
            "Poster Logo Composer finished: {Processed} items processed, {Updated} images generated, " +
            "{AlreadyComplete} already had every managed image type (skipped, enable \"Replace existing " +
            "images\" to reprocess them), {NoImageFound} attempted but no suitable TMDb image was found",
            processed,
            updated,
            alreadyComplete,
            attemptedButNoImageFound);
    }

    private static (bool NeedsPrimary, bool NeedsLogo, bool NeedsBackdrop) GetNeededWork(BaseItem item, PluginConfiguration config)
    {
        var needsPrimary = !item.HasImage(ImageType.Primary, 0) || config.ReplaceExistingImages;
        var needsLogo = config.SaveLogoImage && (!item.HasImage(ImageType.Logo, 0) || config.ReplaceExistingImages);
        var needsBackdrop = config.SaveBackdropImage && (!item.HasImage(ImageType.Backdrop, 0) || config.ReplaceExistingImages);
        return (needsPrimary, needsLogo, needsBackdrop);
    }

    private static IEnumerable<BaseItemKind> GetConfiguredItemKinds(PluginConfiguration config)
    {
        if (config.ProcessMovies)
        {
            yield return BaseItemKind.Movie;
        }

        if (config.ProcessSeries)
        {
            yield return BaseItemKind.Series;
        }
    }

    private async Task<bool> ProcessItemAsync(
        BaseItem item,
        PluginConfiguration config,
        bool needsPrimary,
        bool needsLogo,
        bool needsBackdrop,
        CancellationToken cancellationToken)
    {
        // A single call to the TMDb provider returns every image type (Primary, Backdrop,
        // Logo, ...) it has for the item, so fetch once and slice locally instead of
        // querying per image type.
        var tmdbImages = await GetTmdbImagesAsync(item, cancellationToken).ConfigureAwait(false);

        var httpClient = _httpClientFactory.CreateClient();
        var anyUpdated = false;

        // The composited Primary image needs the logo too, so select it whenever either
        // the Primary or the standalone Logo image is being (re)generated.
        RemoteImageInfo? chosenLogoInfo = null;
        byte[]? logoBytes = null;
        if (needsPrimary || needsLogo)
        {
            var logoCandidates = tmdbImages.Where(r => r.Type == ImageType.Logo).ToList();
            var logoLanguage = GetLogoLanguage(item, config);
            chosenLogoInfo = SelectLogo(logoCandidates, logoLanguage, config, item);

            if (chosenLogoInfo is null)
            {
                _logger.LogDebug("No TMDb logo available for {ItemName} ({ItemId})", item.Name, item.Id);
            }
            else
            {
                try
                {
                    var logoUrl = GetOriginalResolutionUrl(chosenLogoInfo.Url, config);
                    logoBytes = await httpClient.GetByteArrayAsync(logoUrl, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to download logo for {ItemName} ({ItemId})", item.Name, item.Id);
                }
            }
        }

        if (needsPrimary && logoBytes is not null)
        {
            var posterCandidates = tmdbImages.Where(r => r.Type == ImageType.Primary).ToList();
            if (await ComposePrimaryImageAsync(item, config, httpClient, posterCandidates, logoBytes, chosenLogoInfo!.Url, cancellationToken).ConfigureAwait(false))
            {
                anyUpdated = true;
            }
        }

        if (needsLogo && logoBytes is not null && chosenLogoInfo is not null)
        {
            _logger.LogInformation(
                "Saving Logo image for {ItemName} ({ItemId}) from {LogoUrl}",
                item.Name,
                item.Id,
                chosenLogoInfo.Url);

            using (var stream = new System.IO.MemoryStream(logoBytes))
            {
                await _providerManager
                    .SaveImage(item, stream, GuessMimeType(chosenLogoInfo.Url), ImageType.Logo, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            anyUpdated = true;
        }

        if (needsBackdrop)
        {
            var backdropCandidates = tmdbImages.Where(r => r.Type == ImageType.Backdrop).ToList();
            if (await SaveBestBackdropAsync(item, config, backdropCandidates, cancellationToken).ConfigureAwait(false))
            {
                anyUpdated = true;
            }
        }

        if (anyUpdated)
        {
            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        }

        return anyUpdated;
    }

    private async Task<bool> ComposePrimaryImageAsync(
        BaseItem item,
        PluginConfiguration config,
        HttpClient httpClient,
        IReadOnlyList<RemoteImageInfo> posterCandidatesInfo,
        byte[] logoBytes,
        string logoUrl,
        CancellationToken cancellationToken)
    {
        var textlessPosters = posterCandidatesInfo
            .Where(c => string.IsNullOrEmpty(c.Language))
            .OrderByDescending(c => (long)(c.Width ?? 0) * (c.Height ?? 0))
            .Take(Math.Max(config.MaxPosterCandidates, 1))
            .ToList();

        if (textlessPosters.Count == 0)
        {
            var languages = posterCandidatesInfo
                .Select(c => string.IsNullOrEmpty(c.Language) ? "(none)" : c.Language)
                .Distinct()
                .ToList();
            _logger.LogInformation(
                "{ItemName} ({ItemId}): no textless Primary/poster candidate. TMDb returned {Total} Primary " +
                "candidate(s) in total, with languages: {Languages}",
                item.Name,
                item.Id,
                posterCandidatesInfo.Count,
                languages.Count == 0 ? "(none returned at all)" : string.Join(", ", languages));
            return false;
        }

        double logoLuminance;
        try
        {
            using var decodedLogo = Image.Load<Rgba32>(logoBytes);
            logoLuminance = PosterScorer.GetLogoAverageLuminance(decodedLogo);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to decode logo for contrast scoring for {ItemName} ({ItemId}); assuming neutral gray", item.Name, item.Id);
            logoLuminance = 128;
        }

        var candidates = new List<PosterCandidate>();
        foreach (var posterInfo in textlessPosters)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] posterBytes;
            try
            {
                var posterUrl = GetOriginalResolutionUrl(posterInfo.Url, config);
                posterBytes = await httpClient.GetByteArrayAsync(posterUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to download poster candidate for {ItemName} ({ItemId})", item.Name, item.Id);
                continue;
            }

            PosterScorer.BandStats bandStats;
            try
            {
                using var decoded = Image.Load<Rgba32>(posterBytes);
                bandStats = PosterScorer.GetBottomBandStats(decoded, config.FadeBandHeightPercent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to decode poster candidate for {ItemName} ({ItemId})", item.Name, item.Id);
                continue;
            }

            candidates.Add(new PosterCandidate(posterInfo, posterBytes, bandStats));
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        var maxPixelCount = candidates.Max(c => (long)(c.Info.Width ?? 0) * (c.Info.Height ?? 0));
        var best = candidates
            .OrderByDescending(c => PosterScorer.GetScore(
                c.BandStats,
                logoLuminance,
                (long)(c.Info.Width ?? 0) * (c.Info.Height ?? 0),
                maxPixelCount,
                config.FlatnessWeight,
                config.ContrastWeight,
                config.ResolutionWeight))
            .First();

        byte[] composed;
        try
        {
            composed = LogoPosterComposer.Compose(best.Bytes, logoBytes, config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compose Primary image for {ItemName} ({ItemId})", item.Name, item.Id);
            return false;
        }

        _logger.LogInformation(
            "Generating composed Primary image for {ItemName} ({ItemId}) from poster {PosterUrl} and logo {LogoUrl}",
            item.Name,
            item.Id,
            best.Info.Url,
            logoUrl);

        using (var stream = new System.IO.MemoryStream(composed))
        {
            await _providerManager
                .SaveImage(item, stream, "image/jpeg", ImageType.Primary, null, cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> SaveBestBackdropAsync(
        BaseItem item,
        PluginConfiguration config,
        IReadOnlyList<RemoteImageInfo> backdrops,
        CancellationToken cancellationToken)
    {
        if (backdrops.Count == 0)
        {
            _logger.LogDebug("No TMDb backdrop available for {ItemName} ({ItemId})", item.Name, item.Id);
            return false;
        }

        var maxBackdrops = Math.Max(config.MaxBackdrops, 1);

        // Order worst-to-best so the best-scoring candidates are the last (maxBackdrops)
        // entries, then reverse just that slice to get best-first.
        var chosen = backdrops
            .OrderBy(c => c, Comparer<RemoteImageInfo>.Create((a, b) => BackdropScorer.Compare(
                a,
                b,
                config.BackdropResolutionTierTolerance,
                config.BackdropRatingWeight,
                config.BackdropVoteCountWeight)))
            .TakeLast(maxBackdrops)
            .Reverse()
            .ToList();

        // Replace whatever Backdrop images the item currently has so the count stays
        // fixed at maxBackdrops instead of growing by one every time this task runs
        // (SaveImage with a null index always appends a new Backdrop rather than
        // overwriting one).
        var existingBackdropCount = item.GetImages(ImageType.Backdrop).Count();
        for (var i = existingBackdropCount - 1; i >= 0; i--)
        {
            await item.DeleteImageAsync(ImageType.Backdrop, i).ConfigureAwait(false);
        }

        for (var i = 0; i < chosen.Count; i++)
        {
            var candidate = chosen[i];
            var url = GetOriginalResolutionUrl(candidate.Url, config);

            _logger.LogInformation(
                "Saving Backdrop image {Index} for {ItemName} ({ItemId}): {Width}x{Height} from {Url}",
                i,
                item.Name,
                item.Id,
                candidate.Width,
                candidate.Height,
                url);

            // Let the provider manager download the backdrop directly; no local
            // processing is needed for it, unlike Primary (composited) and Logo
            // (re-encoded) images.
            await _providerManager
                .SaveImage(item, url, ImageType.Backdrop, i, cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Rewrites a TMDb image URL (https://image.tmdb.org/t/p/{size}/{path}) to request the
    /// "original" (full) resolution, since some TMDb provider configurations hand back a
    /// downscaled preview size (e.g. "w500") even when a much larger source image exists.
    /// </summary>
    private static string GetOriginalResolutionUrl(string url, PluginConfiguration config)
    {
        if (!config.UseOriginalTmdbResolution)
        {
            return url;
        }

        const string sizeMarker = "/t/p/";
        var markerIndex = url.IndexOf(sizeMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return url;
        }

        var sizeStart = markerIndex + sizeMarker.Length;
        var pathStart = url.IndexOf('/', sizeStart);
        if (pathStart < 0)
        {
            return url;
        }

        return string.Concat(url.AsSpan(0, sizeStart), "original", url.AsSpan(pathStart));
    }

    private static string GuessMimeType(string url)
    {
        if (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return "image/png";
        }

        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return "image/webp";
        }

        if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return "image/svg+xml";
        }

        return "image/jpeg";
    }

    /// <summary>
    /// Resolves the logo language to use for an item: either the library's own preferred
    /// metadata language (falling back to the server default), or the configured static
    /// language, depending on <see cref="PluginConfiguration.AutoDetectLogoLanguage"/>.
    /// </summary>
    private static string GetLogoLanguage(BaseItem item, PluginConfiguration config)
    {
        if (!config.AutoDetectLogoLanguage)
        {
            return config.PreferredLogoLanguage;
        }

        var libraryLanguage = item.GetPreferredMetadataLanguage();
        return string.IsNullOrWhiteSpace(libraryLanguage) ? config.PreferredLogoLanguage : libraryLanguage;
    }

    /// <summary>
    /// Fetches every image (all types combined) directly from the TMDb image provider(s)
    /// registered for this item, bypassing the per-library "image fetcher" enable/disable
    /// list entirely and without touching any other (possibly broken or disabled)
    /// provider such as TheTVDB.
    /// </summary>
    private async Task<IReadOnlyList<RemoteImageInfo>> GetTmdbImagesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        IEnumerable<IImageProvider> providers;
        try
        {
            var refreshOptions = new ImageRefreshOptions(_directoryService);
            providers = _providerManager.GetImageProviders(item, refreshOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate image providers for {ItemName} ({ItemId})", item.Name, item.Id);
            return Array.Empty<RemoteImageInfo>();
        }

        var tmdbProviders = providers
            .OfType<IRemoteImageProvider>()
            .Where(p => p.Name.Contains(TmdbProviderNameFragment, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Supports(item))
            .ToList();

        foreach (var provider in tmdbProviders)
        {
            IEnumerable<ImageType> supported;
            try
            {
                supported = provider.GetSupportedImages(item);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to query supported image types for provider \"{ProviderName}\" on {ItemName} ({ItemId})", provider.Name, item.Name, item.Id);
                continue;
            }

            if (!supported.Contains(ImageType.Primary))
            {
                _logger.LogInformation(
                    "{ItemName} ({ItemId}): TMDb provider \"{ProviderName}\" does not list Primary among its " +
                    "supported image types for this item (supports: {Supported})",
                    item.Name,
                    item.Id,
                    provider.Name,
                    string.Join(", ", supported));
            }
        }

        if (tmdbProviders.Count == 0)
        {
            _logger.LogInformation(
                "{ItemName} ({ItemId}): no TMDb image provider is registered for this item type. Make sure " +
                "the TheMovieDb plugin is installed and enabled.",
                item.Name,
                item.Id);
            return Array.Empty<RemoteImageInfo>();
        }

        var results = new List<RemoteImageInfo>();
        foreach (var provider in tmdbProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var images = await provider.GetImages(item, cancellationToken).ConfigureAwait(false);
                results.AddRange(images);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TMDb image provider \"{ProviderName}\" failed for {ItemName} ({ItemId})", provider.Name, item.Name, item.Id);
            }
        }

        if (results.Count == 0)
        {
            _logger.LogInformation(
                "{ItemName} ({ItemId}): the TMDb image provider returned no images at all. The item may be " +
                "missing a TMDb ID — try refreshing its metadata.",
                item.Name,
                item.Id);
        }

        return results;
    }

    private RemoteImageInfo? SelectLogo(IReadOnlyList<RemoteImageInfo> logos, string preferredLanguage, PluginConfiguration config, BaseItem item)
    {
        if (logos.Count == 0)
        {
            return null;
        }

        var byLanguage = ByLanguage(logos, preferredLanguage, config);
        if (byLanguage is not null)
        {
            return byLanguage;
        }

        var languageNeutral = SelectBestLogo(logos.Where(l => string.IsNullOrEmpty(l.Language)), config);
        if (languageNeutral is not null)
        {
            return languageNeutral;
        }

        // English is the most broadly legible, commonly-available fallback on TMDb —
        // try it before giving up, unless it was already the requested language above.
        if (!string.Equals(preferredLanguage, "en", StringComparison.OrdinalIgnoreCase))
        {
            var english = ByLanguage(logos, "en", config);
            if (english is not null)
            {
                return english;
            }
        }

        if (config.AllowAnyLanguageLogoFallback)
        {
            return SelectBestLogo(logos, config);
        }

        _logger.LogDebug(
            "{ItemName} ({ItemId}): no logo in \"{Language}\", language-neutral, or English was found among " +
            "{Count} candidate(s); skipping rather than using an unrelated-language logo (e.g. Chinese in a " +
            "German library). Enable \"Allow any-language logo as a last resort\" to change this.",
            item.Name,
            item.Id,
            preferredLanguage,
            logos.Count);

        return null;
    }

    private static RemoteImageInfo? ByLanguage(IReadOnlyList<RemoteImageInfo> logos, string language, PluginConfiguration config)
    {
        return SelectBestLogo(logos.Where(l => string.Equals(l.Language, language, StringComparison.OrdinalIgnoreCase)), config);
    }

    /// <summary>
    /// Picks the best logo from a pool of same-language candidates: resolution is only used
    /// as a floor (<see cref="PluginConfiguration.MinLogoWidth"/>) and a final tiebreaker,
    /// not as the deciding factor — among candidates that clear the floor, the one with the
    /// best community rating/vote count wins. If none clear the floor, the floor is dropped
    /// rather than the item being skipped, and the same rating-first pick runs over every
    /// candidate in the pool.
    /// </summary>
    private static RemoteImageInfo? SelectBestLogo(IEnumerable<RemoteImageInfo> candidates, PluginConfiguration config)
    {
        var pool = candidates as IReadOnlyList<RemoteImageInfo> ?? candidates.ToList();
        if (pool.Count == 0)
        {
            return null;
        }

        var wideEnough = pool.Where(l => (l.Width ?? 0) >= config.MinLogoWidth).ToList();
        var scoringPool = wideEnough.Count > 0 ? wideEnough : pool;

        return scoringPool
            .OrderByDescending(l => BackdropScorer.GetSecondaryScore(l, config.LogoRatingWeight, config.LogoVoteCountWeight))
            .ThenByDescending(l => (long)(l.Width ?? 0) * (l.Height ?? 0))
            .First();
    }
}
