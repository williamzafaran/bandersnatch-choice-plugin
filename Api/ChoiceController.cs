using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BandersnatchChoice.Api
{
    /// <summary>
    /// API controller serving metadata, config, assets, diagnostics,
    /// and the chapter-mark generation task for the Bandersnatch plugin.
    /// </summary>
    [ApiController]
    [Route("BandersnatchChoice")]
    public class ChoiceController : ControllerBase
    {
        private readonly ILogger<ChoiceController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IChapterManager _chapterManager;

        // Duration of the full interactive cut (ms) and tolerance for matching
        private const long BandersnatchDurationMs  = 18_734_000;
        private const long DurationToleranceMs      = 60_000;

        public ChoiceController(
            ILogger<ChoiceController> logger,
            ILibraryManager libraryManager,
            IChapterManager chapterManager)
        {
            _logger         = logger;
            _libraryManager = libraryManager;
            _chapterManager = chapterManager;
        }

        // ---------------------------------------------------------------
        // GET /BandersnatchChoice/Metadata
        // ---------------------------------------------------------------

        /// <summary>Gets the full segment / choice-point metadata JSON.</summary>
        [HttpGet("Metadata")]
        [AllowAnonymous]
        [Produces("application/json")]
        public ActionResult GetMetadata()
            => ServeEmbeddedResource(
                "Jellyfin.Plugin.BandersnatchChoice.Web.bandersnatch-metadata.json",
                "application/json");

        // ---------------------------------------------------------------
        // GET /BandersnatchChoice/Config
        // ---------------------------------------------------------------

        /// <summary>Gets the current plugin configuration as JSON for the injected JS.</summary>
        [HttpGet("Config")]
        [AllowAnonymous]
        [Produces("application/json")]
        public ActionResult GetConfig()
        {
            try
            {
                var config = BandersnatchChoicePlugin.Instance?.Configuration;
                if (config == null)
                {
                    return Ok(new { enabled = true, leadInSeconds = 10, autoDetect = true, manualItemId = (string?)null });
                }

                return Content(JsonSerializer.Serialize(new
                {
                    enabled       = config.Enabled,
                    leadInSeconds = config.LeadInSeconds,
                    autoDetect    = config.AutoDetect,
                    manualItemId  = config.ManualItemId
                }), "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BandersnatchChoice] Error serving config");
                return StatusCode(500, "Internal server error");
            }
        }

        // ---------------------------------------------------------------
        // GET /BandersnatchChoice/Assets/{filename}
        // ---------------------------------------------------------------

        /// <summary>Gets a static web asset (JS or CSS) from embedded resources.</summary>
        [HttpGet("Assets/{filename}")]
        [AllowAnonymous]
        public ActionResult GetAsset([FromRoute] string filename)
        {
            if (filename != "bandersnatch.js" && filename != "bandersnatch.css")
                return NotFound();

            var contentType = filename.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                ? "application/javascript"
                : "text/css";

            return ServeEmbeddedResource(
                $"Jellyfin.Plugin.BandersnatchChoice.Web.{filename}",
                contentType);
        }

        // ---------------------------------------------------------------
        // POST /BandersnatchChoice/GenerateChapters[?itemId=...]
        // Writes the Bandersnatch segment map as Jellyfin chapter markers
        // on the specified (or auto-detected) item.
        // ---------------------------------------------------------------

        /// <summary>
        /// Generates Jellyfin chapter markers for the Bandersnatch interactive item
        /// using the embedded segment metadata. Optionally accepts an explicit item ID;
        /// otherwise falls back to the configured ManualItemId or auto-detection.
        /// </summary>
        [HttpPost("GenerateChapters")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult> GenerateChapters([FromQuery] string? itemId)
        {
            try
            {
                // ── 1. Resolve the target item ────────────────────────────────────
                var config = BandersnatchChoicePlugin.Instance?.Configuration;

                var resolvedId = itemId?.Trim()
                    ?? config?.ManualItemId?.Trim();

                BaseItem? item = null;

                if (!string.IsNullOrEmpty(resolvedId) && Guid.TryParse(resolvedId, out var guid))
                {
                    item = _libraryManager.GetItemById(guid);
                    if (item == null)
                    {
                        return NotFound($"Item '{resolvedId}' not found in the library.");
                    }
                }
                else
                {
                    // Auto-detect: find movies containing "Bandersnatch" with the right duration
                    var allMovies = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        Recursive         = true,
                        IncludeItemTypes  = new[] { BaseItemKind.Movie }
                    });

                    item = allMovies.FirstOrDefault(i =>
                    {
                        if (!(i.Name ?? string.Empty).Contains("Bandersnatch", StringComparison.OrdinalIgnoreCase))
                            return false;
                        var durationMs = (i.RunTimeTicks ?? 0) / 10_000L;
                        return Math.Abs(durationMs - BandersnatchDurationMs) < DurationToleranceMs;
                    });

                    if (item == null)
                    {
                        return NotFound(
                            "Could not auto-detect the Bandersnatch interactive cut. " +
                            "Set a Manual Item ID in the plugin settings and try again.");
                    }
                }

                _logger.LogInformation("[BandersnatchChoice] Generating chapter marks for '{Name}' ({Id})", item.Name, item.Id);

                // ── 2. Load the embedded metadata JSON ───────────────────────────
                var metadataJson = GetEmbeddedResourceText(
                    "Jellyfin.Plugin.BandersnatchChoice.Web.bandersnatch-metadata.json");

                if (metadataJson == null)
                {
                    return StatusCode(500, "Could not load embedded segment metadata.");
                }

                var metadata = JsonSerializer.Deserialize<MetadataDoc>(metadataJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (metadata?.Segments == null || metadata.Segments.Count == 0)
                {
                    return StatusCode(500, "Segment metadata is empty or could not be parsed.");
                }

                // ── 3. Build ChapterInfo list ─────────────────────────────────────
                var chapters = metadata.Segments
                    .Select(kvp => new ChapterInfo
                    {
                        // Ticks = ms × 10,000
                        StartPositionTicks = kvp.Value.StartMs * 10_000L,
                        Name = kvp.Key   // e.g. "1A", "3Vfs", "SS13"
                    })
                    .OrderBy(c => c.StartPositionTicks)
                    .ToList();

                // ── 4. Save to Jellyfin ───────────────────────────────────────────
                _chapterManager.SaveChapters(item.Id, chapters);

                _logger.LogInformation(
                    "[BandersnatchChoice] Saved {Count} chapter marks for '{Name}'.",
                    chapters.Count, item.Name);

                return Ok(new
                {
                    success      = true,
                    itemId       = item.Id.ToString(),
                    itemName     = item.Name,
                    chaptersWritten = chapters.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BandersnatchChoice] Error generating chapter marks");
                return StatusCode(500, ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // GET /BandersnatchChoice/Status   — diagnostic
        // ---------------------------------------------------------------

        /// <summary>Returns a JSON diagnostic report for debugging.</summary>
        [HttpGet("Status")]
        [AllowAnonymous]
        [Produces("application/json")]
        public ActionResult GetStatus()
        {
            try
            {
                var assembly      = Assembly.GetExecutingAssembly();
                var resourceNames = assembly.GetManifestResourceNames();
                var config        = BandersnatchChoicePlugin.Instance?.Configuration;

                string? injectedInto = null;
                var webCandidates = new[]
                {
                    @"C:\Program Files\Jellyfin\Server\jellyfin-web\index.html",
                    "/usr/share/jellyfin/web/index.html",
                    "/usr/lib/jellyfin-web/index.html",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jellyfin-web", "index.html"),
                    Path.Combine(
                        Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? string.Empty,
                        "jellyfin-web", "index.html"),
                };
                foreach (var p in webCandidates)
                {
                    try
                    {
                        if (System.IO.File.Exists(p) &&
                            System.IO.File.ReadAllText(p)
                                .Contains("bandersnatch.js", StringComparison.OrdinalIgnoreCase))
                        {
                            injectedInto = p;
                            break;
                        }
                    }
                    catch { }
                }

                return Content(JsonSerializer.Serialize(new
                {
                    plugin            = "Bandersnatch Interactive",
                    version           = assembly.GetName().Version?.ToString() ?? "unknown",
                    pluginLoaded      = BandersnatchChoicePlugin.Instance != null,
                    scriptInjected    = injectedInto != null,
                    injectedInto,
                    embeddedResources = resourceNames,
                    config            = config == null ? null : new object[]
                    {
                        new { config.Enabled, config.LeadInSeconds, config.AutoDetect, config.ManualItemId }
                    }
                }, new JsonSerializerOptions { WriteIndented = true }), "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BandersnatchChoice] Error in Status endpoint");
                return StatusCode(500, ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private ActionResult ServeEmbeddedResource(string resourceName, string contentType)
        {
            var text = GetEmbeddedResourceText(resourceName);
            if (text == null)
            {
                _logger.LogWarning(
                    "[BandersnatchChoice] Embedded resource not found: {Resource}. Available: {Available}",
                    resourceName,
                    string.Join(", ", Assembly.GetExecutingAssembly().GetManifestResourceNames()));
                return NotFound($"Resource '{resourceName}' not found.");
            }

            return Content(text, contentType);
        }

        private static string? GetEmbeddedResourceText(string resourceName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) return null;
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------
        // Private DTOs for metadata deserialization
        // ---------------------------------------------------------------

        private sealed record SegmentDto(
            [property: JsonPropertyName("startMs")] long StartMs,
            [property: JsonPropertyName("endMs")]   long EndMs,
            [property: JsonPropertyName("choicePointId")] string? ChoicePointId
        );

        private sealed record MetadataDoc(
            [property: JsonPropertyName("segments")] Dictionary<string, SegmentDto>? Segments
        );
    }
}
