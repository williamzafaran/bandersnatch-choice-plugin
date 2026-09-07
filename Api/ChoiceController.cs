using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BandersnatchChoice.Api
{
    /// <summary>
    /// API controller serving metadata, config, assets, and diagnostics for the
    /// Bandersnatch interactive choice overlay.
    /// </summary>
    [ApiController]
    [Route("BandersnatchChoice")]
    public class ChoiceController : ControllerBase
    {
        private readonly ILogger<ChoiceController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChoiceController"/> class.
        /// </summary>
        public ChoiceController(ILogger<ChoiceController> logger)
        {
            _logger = logger;
        }

        // ---------------------------------------------------------------
        // GET /BandersnatchChoice/Metadata
        // ---------------------------------------------------------------

        /// <summary>Gets the Bandersnatch segment and choice-point metadata.</summary>
        [HttpGet("Metadata")]
        [AllowAnonymous]
        [Produces("application/json")]
        public ActionResult GetMetadata()
        {
            return ServeEmbeddedResource(
                "Jellyfin.Plugin.BandersnatchChoice.Web.bandersnatch-metadata.json",
                "application/json");
        }

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

                var result = new
                {
                    enabled       = config.Enabled,
                    leadInSeconds = config.LeadInSeconds,
                    autoDetect    = config.AutoDetect,
                    manualItemId  = config.ManualItemId
                };

                return Content(JsonSerializer.Serialize(result), "application/json");
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
            // Whitelist — no path traversal possible
            if (filename != "bandersnatch.js" && filename != "bandersnatch.css")
            {
                return NotFound();
            }

            var resourceName = $"Jellyfin.Plugin.BandersnatchChoice.Web.{filename}";
            var contentType  = filename.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                ? "application/javascript"
                : "text/css";

            return ServeEmbeddedResource(resourceName, contentType);
        }

        // ---------------------------------------------------------------
        // GET /BandersnatchChoice/Status
        // Diagnostic endpoint — visit in browser to verify plugin is working
        // ---------------------------------------------------------------

        /// <summary>
        /// Returns a JSON diagnostic report: plugin version, available embedded resources,
        /// and whether index.html was already injected.
        /// </summary>
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

                // Check if index.html has the injection (scan common paths)
                string? injectedInto = null;
                var webCandidates = new[]
                {
                    @"C:\Program Files\Jellyfin\Server\jellyfin-web\index.html",
                    "/usr/share/jellyfin/web/index.html",
                    "/usr/lib/jellyfin-web/index.html",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jellyfin-web", "index.html"),
                    Path.Combine(
                        Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? "",
                        "jellyfin-web", "index.html"),
                };
                foreach (var p in webCandidates)
                {
                    try
                    {
                        if (System.IO.File.Exists(p) &&
                            System.IO.File.ReadAllText(p).Contains("bandersnatch.js", StringComparison.OrdinalIgnoreCase))
                        {
                            injectedInto = p;
                            break;
                        }
                    }
                    catch { /* skip */ }
                }

                var report = new
                {
                    plugin          = "Bandersnatch Interactive",
                    version         = assembly.GetName().Version?.ToString() ?? "unknown",
                    pluginLoaded    = BandersnatchChoicePlugin.Instance != null,
                    scriptInjected  = injectedInto != null,
                    injectedInto,
                    embeddedResources = resourceNames,
                    config          = config == null ? null : new
                    {
                        config.Enabled,
                        config.LeadInSeconds,
                        config.AutoDetect,
                        config.ManualItemId
                    }
                };

                return Content(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                    "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BandersnatchChoice] Error in Status endpoint");
                return StatusCode(500, ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // Shared helper
        // ---------------------------------------------------------------

        private ActionResult ServeEmbeddedResource(string resourceName, string contentType)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    _logger.LogWarning(
                        "[BandersnatchChoice] Embedded resource not found: {Resource}. Available: {Available}",
                        resourceName,
                        string.Join(", ", assembly.GetManifestResourceNames()));
                    return NotFound($"Resource '{resourceName}' not found.");
                }

                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                return Content(content, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BandersnatchChoice] Error serving resource {Resource}", resourceName);
                return StatusCode(500, "Internal server error");
            }
        }
    }
}
