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
    /// item detection, and chapter-mark generation.
    /// </summary>
    [ApiController]
    [Route("BandersnatchChoice")]
    public class ChoiceController : ControllerBase
    {
        private readonly ILogger<ChoiceController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IChapterManager _chapterManager;

        // Full interactive cut: ~5h 12m 14s
        private const long BandersnatchDurationMs = 18_734_000;
        private const long DurationToleranceMs    = 60_000;

        public ChoiceController(
            ILogger<ChoiceController> logger,
            ILibraryManager libraryManager,
            IChapterManager chapterManager)
        {
            _logger         = logger;
            _libraryManager = libraryManager;
            _chapterManager = chapterManager;
        }

        // ── GET /BandersnatchChoice/Metadata ────────────────────────────────

        [HttpGet("Metadata")]
        [AllowAnonymous]
        [Produces("application/json")]
        public ActionResult GetMetadata()
            => ServeEmbeddedResource(
                "Jellyfin.Plugin.BandersnatchChoice.Web.bandersnatch-metadata.json",
                "application/json");

        // ── GET /BandersnatchChoice/Config ───────────────────────────────────

        [HttpGet("Config")]
        [AllowAnonymous]
        [Produces("application/json")]
        public ActionResult GetConfig()
        {
            var config = BandersnatchChoicePlugin.Instance?.Configuration;
            return Content(JsonSerializer.Serialize(new
            {
                enabled       = config?.Enabled       ?? true,
                leadInSeconds = config?.LeadInSeconds ?? 10,
                autoDetect    = config?.AutoDetect    ?? true,
                manualItemId  = config?.ManualItemId
            }), "application/json");
        }

        // ── GET /BandersnatchChoice/Assets/{filename} ────────────────────────

        [HttpGet("Assets/{filename}")]
        [AllowAnonymous]
        public ActionResult GetAsset([FromRoute] string filename)
        {
            if (filename != "bandersnatch.js" && filename != "bandersnatch.css")
                return NotFound();

            return ServeEmbeddedResource(
                $"Jellyfin.Plugin.BandersnatchChoice.Web.{filename}",
                filename.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                    ? "application/javascript" : "text/css");
        }

        // ── GET /BandersnatchChoice/DetectItem ───────────────────────────────
        // Searches the Jellyfin library for the Bandersnatch interactive cut.
        // Returns item info (id, name) so the settings page can auto-fill the
        // Manual Item ID field and show a poster preview.

        [HttpGet("DetectItem")]
        [Authorize(Policy = "RequiresElevation")]
        [Produces("application/json")]
        public ActionResult DetectItem()
        {
            try
            {
                var config = BandersnatchChoicePlugin.Instance?.Configuration;

                // 1. Try the manually configured item ID first
                if (!string.IsNullOrEmpty(config?.ManualItemId) &&
                    Guid.TryParse(config.ManualItemId, out var manualGuid))
                {
                    var manualItem = _libraryManager.GetItemById(manualGuid);
                    if (manualItem != null)
                    {
                        return Ok(new
                        {
                            found  = true,
                            itemId = manualItem.Id.ToString("N"),
                            name   = manualItem.Name,
                            source = "manual"
                        });
                    }
                }

                // 2. Auto-detect: search all items (not just Movies — could be a Video)
                var item = _libraryManager
                    .GetItemList(new InternalItemsQuery { Recursive = true })
                    .FirstOrDefault(i =>
                    {
                        if (!(i.Name ?? string.Empty)
                            .Contains("Bandersnatch", StringComparison.OrdinalIgnoreCase))
                            return false;

                        var durationMs = (i.RunTimeTicks ?? 0) / 10_000L;
                        return Math.Abs(durationMs - BandersnatchDurationMs) < DurationToleranceMs;
                    });

                if (item == null)
                {
                    return Ok(new { found = false });
                }

                return Ok(new
                {
                    found  = true,
                    itemId = item.Id.ToString("N"),
                    name   = item.Name,
                    source = "auto"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BandersnatchChoice] DetectItem error");
                return StatusCode(500, ex.Message);
            }
        }

        // ── POST /BandersnatchChoice/GenerateChapters[?itemId=...] ───────────
        // Writes the Bandersnatch segment map as Jellyfin chapter markers.

        [HttpPost("GenerateChapters")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult> GenerateChapters([FromQuery] string? itemId)
        {
            try
            {
                var config = BandersnatchChoicePlugin.Instance?.Configuration;
                var resolvedId = (itemId?.Trim())
                    ?? config?.ManualItemId?.Trim();

                BaseItem? item = null;

                // Try explicit ID (query param or config)
                if (!string.IsNullOrEmpty(resolvedId) && Guid.TryParse(resolvedId, out var guid))
                {
                    item = _libraryManager.GetItemById(guid);
                }

                // Fall back to auto-detection (search all item types, not just Movie)
                if (item == null)
                {
                    item = _libraryManager
                        .GetItemList(new InternalItemsQuery { Recursive = true })
                        .FirstOrDefault(i =>
                        {
                            if (!(i.Name ?? string.Empty)
                                .Contains("Bandersnatch", StringComparison.OrdinalIgnoreCase))
                                return false;
                            var durationMs = (i.RunTimeTicks ?? 0) / 10_000L;
                            return Math.Abs(durationMs - BandersnatchDurationMs) < DurationToleranceMs;
                        });
                }

                if (item == null)
                {
                    return NotFound(
                        "Could not find the Bandersnatch interactive cut. " +
                        "Set the Manual Item ID in plugin settings or ensure the item is in your library.");
                }

                _logger.LogInformation(
                    "[BandersnatchChoice] Generating chapter marks for '{Name}' ({Id})",
                    item.Name, item.Id);

                // Load embedded metadata
                var metadataJson = GetEmbeddedResourceText(
                    "Jellyfin.Plugin.BandersnatchChoice.Web.bandersnatch-metadata.json");
                if (metadataJson == null)
                    return StatusCode(500, "Could not load embedded segment metadata.");

                var metadata = JsonSerializer.Deserialize<MetadataDoc>(metadataJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (metadata?.Segments == null || metadata.Segments.Count == 0)
                    return StatusCode(500, "Segment metadata is empty.");

                var chapters = metadata.Segments
                    .Select(kvp => new ChapterInfo
                    {
                        StartPositionTicks = kvp.Value.StartMs * 10_000L,
                        Name = kvp.Key   // e.g. "1A", "3M", "SS17"
                    })
                    .OrderBy(c => c.StartPositionTicks)
                    .ToList();

                _chapterManager.SaveChapters(item.Id, chapters);

                _logger.LogInformation(
                    "[BandersnatchChoice] Saved {Count} chapter marks for '{Name}'.",
                    chapters.Count, item.Name);

                return Ok(new
                {
                    success         = true,
                    itemId          = item.Id.ToString("N"),
                    itemName        = item.Name,
                    chaptersWritten = chapters.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BandersnatchChoice] GenerateChapters error");
                return StatusCode(500, ex.Message);
            }
        }

        // ── GET /BandersnatchChoice/Status ───────────────────────────────────

        [HttpGet("Status")]
        [AllowAnonymous]
        [Produces("application/json")]
        public ActionResult GetStatus()
        {
            var assembly      = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();
            var config        = BandersnatchChoicePlugin.Instance?.Configuration;

            string? injectedInto = null;
            foreach (var p in new[]
            {
                @"C:\Program Files\Jellyfin\Server\jellyfin-web\index.html",
                "/usr/share/jellyfin/web/index.html",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jellyfin-web", "index.html"),
            })
            {
                try
                {
                    if (System.IO.File.Exists(p) &&
                        System.IO.File.ReadAllText(p)
                            .Contains("bandersnatch.js", StringComparison.OrdinalIgnoreCase))
                    {
                        injectedInto = p; break;
                    }
                }
                catch { }
            }

            return Content(JsonSerializer.Serialize(new
            {
                plugin            = "Bandersnatch Interactive",
                version           = assembly.GetName().Version?.ToString(),
                pluginLoaded      = BandersnatchChoicePlugin.Instance != null,
                scriptInjected    = injectedInto != null,
                injectedInto,
                embeddedResources = resourceNames,
                config            = config == null ? null : new
                {
                    config.Enabled,
                    config.LeadInSeconds,
                    config.AutoDetect,
                    config.ManualItemId
                }
            }, new JsonSerializerOptions { WriteIndented = true }), "application/json");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private ActionResult ServeEmbeddedResource(string resourceName, string contentType)
        {
            var text = GetEmbeddedResourceText(resourceName);
            if (text == null)
            {
                _logger.LogWarning(
                    "[BandersnatchChoice] Resource not found: {Resource}. Available: {Available}",
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
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(resourceName);
                if (stream == null) return null;
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch { return null; }
        }

        // ── Private DTOs ─────────────────────────────────────────────────────

        private sealed record SegmentDto(
            [property: JsonPropertyName("startMs")] long StartMs,
            [property: JsonPropertyName("endMs")]   long EndMs
        );

        private sealed record MetadataDoc(
            [property: JsonPropertyName("segments")] Dictionary<string, SegmentDto>? Segments
        );
    }
}
