using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BandersnatchChoice.Api
{
    /// <summary>
    /// API controller serving metadata, config, and static assets for the
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
        // Returns the full segment/choice-point metadata JSON consumed by bandersnatch.js
        // ---------------------------------------------------------------

        /// <summary>Gets the Bandersnatch segment and choice-point metadata.</summary>
        [HttpGet("Metadata")]
        [AllowAnonymous]
        [Produces("application/json")]
        public ActionResult GetMetadata()
        {
            return ServeEmbeddedResource("Jellyfin.Plugin.BandersnatchChoice.Web.bandersnatch-metadata.json", "application/json");
        }

        // ---------------------------------------------------------------
        // GET /BandersnatchChoice/Config
        // Returns the current plugin configuration as JSON for the injected JS
        // ---------------------------------------------------------------

        /// <summary>Gets the current plugin configuration as JSON.</summary>
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
                    enabled = config.Enabled,
                    leadInSeconds = config.LeadInSeconds,
                    autoDetect = config.AutoDetect,
                    manualItemId = config.ManualItemId
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
        // Serves bandersnatch.js and bandersnatch.css from embedded resources
        // ---------------------------------------------------------------

        /// <summary>Gets a static web asset (JS or CSS) from embedded resources.</summary>
        [HttpGet("Assets/{filename}")]
        [AllowAnonymous]
        public ActionResult GetAsset([FromRoute] string filename)
        {
            // Only allow known files — no path traversal
            if (filename != "bandersnatch.js" && filename != "bandersnatch.css")
            {
                return NotFound();
            }

            var resourceName = $"Jellyfin.Plugin.BandersnatchChoice.Web.{filename}";
            var contentType = filename.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                ? "application/javascript"
                : "text/css";

            return ServeEmbeddedResource(resourceName, contentType);
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
                    _logger.LogWarning("[BandersnatchChoice] Embedded resource not found: {Resource}", resourceName);
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
