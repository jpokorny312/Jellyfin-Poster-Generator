## Poster Logo Composer (Jellyfin Plugin)

Generates a Primary (poster) image per movie/series by compositing a **textless** TMDb
poster with the item's **logo**, so the library shows title-branded artwork instead of a
generic poster or a poster with baked-in foreign text. It also saves that same logo as the
item's own Logo image, and picks/saves the highest resolution, best rated TMDb backdrop as
the Backdrop image.

## How it works

1. Pulls every available textless poster (`language == null`) and every available logo for
   the item from the TMDb metadata provider.
2. Downloads up to a configurable number of poster candidates and scores each one on how
   "calm" (low contrast/variance) its bottom area is — a busy or high-contrast background
   there would fight with the logo — *and* on how much natural contrast this specific logo
   already has against that band, so a white logo isn't matched with a poster that's too
   bright, and vice versa. Resolution is a secondary tiebreaker.
3. Blurs the bottom band of the chosen poster, smoothly cross-fading from the sharp,
   untouched poster above it (no hard sharp-to-blurry seam). The same rule then runs for
   every item to decide the fade: if the logo and the band already contrast enough on their
   own, no darkening/lightening is applied at all; otherwise the band is faded toward
   whichever of black/white increases the gap, by just enough to reach a configurable
   target — so a well-matched poster/logo pairing gets little to no fade, instead of a
   fixed-strength overlay that could turn into a visible block. The logo is then centered on
   top, scaled to fit within configurable width/height limits, and the result is saved as
   the Primary image.
4. Saves the same logo as the item's Logo image (can be turned off).
5. Independently, fetches all TMDb backdrops and picks the one with the highest
   resolution, using community rating and vote count as a tiebreaker among similarly sized
   candidates (same logic as Best Image Selector) — saved as the Backdrop image (can be
   turned off).

Primary, Logo and Backdrop are each generated/replaced independently based on whether the
item is missing that image type (or always, if "Replace existing images" is enabled) — an
item that already has a Backdrop but no Logo will just get the Logo filled in, for example.

**Logo language:** by default (`AutoDetectLogoLanguage` = on), each item's own library's
"Preferred metadata language" setting is used (falling back to the server's default
language); the `PreferredLogoLanguage` setting (default `en`) is only used as a fallback
if that can't be determined, or directly if auto-detection is turned off. Selection order
within the resolved language: a logo in that language → a language-neutral logo → an
English logo → (only if "Allow any-language logo as a last resort" is on) any available
logo — in each case picking the highest resolution one available. By default, if none of
the first three tiers has a match, the item is skipped rather than falling back to an
unrelated-language logo. The same choice
is used both for the logo composited onto the Primary image and for the standalone Logo
image.

## Build

Requires the .NET 9 SDK (Jellyfin 10.11.x targets .NET 9).

```bash
cd Jellyfin.Plugin.PosterLogoComposer
dotnet publish -c Release -f net9.0 --no-self-contained
```

Unlike Best Image Selector, this plugin has one extra dependency beyond the Jellyfin SDK:
`SixLabors.ImageSharp`. Jellyfin does **not** resolve NuGet dependencies for plugins at
runtime — it only loads what's physically in the plugin's folder — so a plain
`dotnet build` output (just the plugin DLL) will fail to load silently (no error dialog,
the plugin just won't appear in the Plugins list or show a settings gear). Use
`dotnet publish` and take `Jellyfin.Plugin.PosterLogoComposer.dll` **and**
`SixLabors.ImageSharp.dll` from `bin/Release/net9.0/publish/`.

Do **not** copy the other DLLs from the `publish/` output (`MediaBrowser.*`,
`Jellyfin.*`, `Microsoft.Extensions.*`, EF Core, etc.) — those are already provided by the
Jellyfin server itself, and shipping a second copy alongside the plugin can cause type or
version conflicts. Only ship assemblies that aren't part of the Jellyfin server.

Built against Jellyfin.Controller/Jellyfin.Model 10.11.11. If your server is on the
10.10.x line instead (.NET 8), change the `TargetFramework` in the `.csproj` back to
`net8.0` and the package versions to `10.10.7`, and `targetAbi`/`framework` in
`meta.json` accordingly (then publish with `-f net8.0`).

## Install (recommended: via plugin repository)

Every push to `main` that bumps the version in `meta.json` is built, packaged and
published as a GitHub Release by `.github/workflows/release.yml`, which also updates
`manifest.json` at the repository root. Adding that manifest as a plugin repository in
Jellyfin means future updates show up on the normal Plugins → Catalog page — no more
manually deleting/copying files.

1. **Dashboard → Plugins → Repositories → Add Repository.**
2. Repository name: anything you like (e.g. `Poster Logo Composer`).
3. Repository URL:
   ```
   https://raw.githubusercontent.com/jpokorny312/Jellyfin-Poster-Generator/main/manifest.json
   ```
4. Save, then go to **Dashboard → Plugins → Catalog**, find **Poster Logo Composer**
   under your repository, and install it. From then on, new releases appear as a normal
   update on the Plugins page.

### Releasing a new version

1. Bump `version` in `meta.json` and prepend the new entry to `changelog`.
2. Commit and push to `main`.
3. The workflow builds, tags (`vX.Y.Z.W`), creates a GitHub Release with the plugin zip
   attached, and commits the updated `manifest.json` back to `main` — nothing else to do
   manually.

## Install (manual, fallback)

1. Build the plugin (see above) or download the zip from the
   [latest release](../../releases/latest).
2. Create a folder named `Poster Logo Composer` inside your Jellyfin server's plugin
   directory.
3. Copy `Jellyfin.Plugin.PosterLogoComposer.dll`, `SixLabors.ImageSharp.dll` and
   `meta.json` into that folder.
4. Restart the Jellyfin server.
5. Make sure the **TheMovieDb** metadata provider is enabled for the relevant libraries
   (Dashboard → Libraries → your library → metadata providers), since this plugin only
   uses TMDb-sourced posters, logos and backdrops.
6. Go to **Dashboard → Plugins → Poster Logo Composer** to configure item types, fade/logo
   geometry, backdrop selection, and whether existing images should be replaced.
7. Go to **Dashboard → Scheduled Tasks → Library** and run **Compose Poster Logos**.
   The task has no default trigger (it downloads and processes a lot of images), so run it
   manually or add a schedule from the scheduled tasks page.

## Configuration

| Setting | Description |
|---|---|
| Movies / Series | Which item types to process. |
| Replace existing images | If off, only images that are currently missing are (re)generated — Primary, Logo and Backdrop are each checked independently. |
| Also save the logo as the item's Logo image | Saves the same logo used in the Primary composite as the standalone Logo image. |
| Also select and save a Backdrop image | Picks the highest resolution, best rated TMDb backdrop and saves it as the Backdrop image. |
| Automatically use each library's preferred metadata language for the logo | When on (default), uses each item's library language setting instead of a fixed one. |
| Fallback / manual logo language | ISO 639-1 code (e.g. `en`); used directly when auto-detection is off, or as a fallback. Falls back further to a language-neutral logo, then an English logo. |
| Allow any-language logo as a last resort | Off by default: if no logo matches the resolved language, a language-neutral logo, or English, the item is skipped rather than using an unrelated-language logo (e.g. Chinese in a German library). |
| Max poster candidates | How many textless posters are downloaded/scored per item to find the best fit for the logo. |
| Flatness weight | Weight given to how uniform/calm a poster's bottom band is. |
| Contrast weight | Weight given to the natural contrast between this specific logo and the poster's bottom band — this is what keeps a white logo off a poster that's too bright to begin with. |
| Resolution weight | Weight given to raw poster resolution, used as a tiebreaker after flatness/contrast. |
| Fade band height | Height of the blurred band at the bottom, as a fraction of poster height. Kept fairly small by default (22%) so the fade only touches the bottom portion of the poster. |
| Blur sigma | Strength of the Gaussian blur applied to the fade band. |
| Target contrast gap | The fade tries to reach at least this much luminance gap (0-255) between the logo and the band. If the pairing already has enough natural contrast, no fade is applied at all. |
| Max gradient strength | A ceiling (0-255 alpha) on how strong/opaque the fade is ever allowed to get, even if more would be needed to reach the target contrast gap. |
| Gradient feather | How much of the fade band is used to smoothly ramp up to full strength around the logo (on all four sides), instead of an abrupt transition. |
| Max logo width / height | Size limits for the logo, as fractions of poster width / poster height (independent of the fade band height — the logo may extend above the band). |
| Logo bottom margin | Space below the logo, as a fraction of poster height. |
| Max backdrops to keep | How many Backdrop images to keep. Existing Backdrops are replaced with up to this many best-scoring candidates every run, so the count stays fixed instead of growing forever. |
| Backdrop resolution tier tolerance | Relative pixel-count difference (0–1) below which two backdrop candidates are treated as equal resolution. |
| Backdrop rating weight / vote count weight | Weights applied to a backdrop candidate's rating/(log-scaled) vote count when comparing similarly sized candidates. |
| Always download TMDb images at original resolution | Rewrites TMDb image URLs to request the "original" size before downloading. Fixes setups where the TMDb provider otherwise hands back a downscaled preview (e.g. 500x750 instead of the source's 1500x3000). On by default. |
