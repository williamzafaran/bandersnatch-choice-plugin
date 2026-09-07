using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BandersnatchChoice
{
    /// <summary>
    /// Bandersnatch Interactive Choice Plugin.
    /// Injects interactive choice UI into the native Jellyfin player,
    /// inspired by the intro-skipper injection pattern.
    /// </summary>
    public class BandersnatchChoicePlugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly ILogger<BandersnatchChoicePlugin> _logger;
        private readonly IApplicationPaths _applicationPaths;

        // Unique plugin GUID — do not change after release
        private static readonly Guid PluginGuid = Guid.Parse("c7d8e9f0-a1b2-4c3d-8e5f-6a7b8c9d0e1f");

        /// <summary>
        /// Initializes a new instance of the <see cref="BandersnatchChoicePlugin"/> class.
        /// </summary>
        public BandersnatchChoicePlugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger<BandersnatchChoicePlugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _logger = logger;
            _applicationPaths = applicationPaths;

            // Inject script and stylesheet tags into index.html on startup
            InjectWebAssets();
        }

        /// <inheritdoc />
        public override string Name => "Bandersnatch Interactive";

        /// <inheritdoc />
        public override Guid Id => PluginGuid;

        /// <inheritdoc />
        public override string Description => "Interactive choice overlay for Black Mirror: Bandersnatch in the native Jellyfin player.";

        /// <summary>
        /// Gets the current plugin instance (singleton).
        /// </summary>
        public static BandersnatchChoicePlugin? Instance { get; private set; }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name            = this.Name,
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                    // Show in the Jellyfin admin side-panel under Plugins
                    EnableInMainMenu = true,
                    MenuIcon        = "movie",
                    MenuSection     = "server"
                }
            };
        }

        // ---------------------------------------------------------------
        // index.html injection
        // ---------------------------------------------------------------

        private void InjectWebAssets()
        {
            try
            {
                var webPath = FindWebDirectory();
                if (webPath == null)
                {
                    _logger.LogWarning("[BandersnatchChoice] Could not locate Jellyfin web directory. Script injection skipped.");
                    return;
                }

                var indexPath = Path.Combine(webPath, "index.html");
                if (!File.Exists(indexPath))
                {
                    _logger.LogWarning("[BandersnatchChoice] index.html not found at {Path}. Injection skipped.", indexPath);
                    return;
                }

                var content = File.ReadAllText(indexPath);

                // Guard: already injected
                if (content.Contains("bandersnatch.js", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[BandersnatchChoice] Script already present in index.html.");
                    return;
                }

                var injection =
                    "\n    <link rel=\"stylesheet\" href=\"/BandersnatchChoice/Assets/bandersnatch.css\">" +
                    "\n    <script src=\"/BandersnatchChoice/Assets/bandersnatch.js\"></script>";

                content = content.Replace("</body>", injection + "\n</body>", StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(indexPath, content);

                _logger.LogInformation("[BandersnatchChoice] Successfully injected interactive scripts into {Path}.", indexPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BandersnatchChoice] Failed to inject web assets.");
            }
        }

        /// <summary>
        /// Tries to locate Jellyfin's web content directory across common installation paths.
        /// </summary>
        private string? FindWebDirectory()
        {
            // 1. Try IApplicationPaths.WebPath (Jellyfin 10.9+)
            var webPathProp = _applicationPaths.GetType().GetProperty("WebPath",
                BindingFlags.Public | BindingFlags.Instance);
            if (webPathProp != null)
            {
                var webPath = webPathProp.GetValue(_applicationPaths) as string;
                if (!string.IsNullOrEmpty(webPath) && IsValidWebDir(webPath))
                {
                    _logger.LogInformation("[BandersnatchChoice] Found web dir via IApplicationPaths.WebPath: {Path}", webPath);
                    return webPath;
                }
            }

            // 2. Build candidate list from every sensible location
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var entryDir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()?.Location) ?? baseDir;

            var candidates = new[]
            {
                // Relative to entry assembly (most reliable on all platforms)
                Path.Combine(entryDir, "jellyfin-web"),
                Path.Combine(entryDir, "web"),
                Path.Combine(entryDir, "..", "jellyfin-web"),

                // Relative to AppDomain base
                Path.Combine(baseDir, "jellyfin-web"),
                Path.Combine(baseDir, "web"),
                Path.Combine(baseDir, "..", "jellyfin-web"),

                // Windows: standard installer layout
                @"C:\Program Files\Jellyfin\Server\jellyfin-web",
                @"C:\Program Files\Jellyfin\jellyfin-web",
                @"C:\ProgramData\Jellyfin\Server\jellyfin-web",

                // Linux package paths
                "/usr/share/jellyfin/web",
                "/usr/lib/jellyfin-web",
                "/opt/jellyfin/web",

                // Docker default
                "/jellyfin/jellyfin-web",
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    var normalised = Path.GetFullPath(candidate);
                    if (IsValidWebDir(normalised))
                    {
                        _logger.LogInformation("[BandersnatchChoice] Found web dir: {Path}", normalised);
                        return normalised;
                    }
                }
                catch { /* path may be invalid on this OS — ignore */ }
            }

            _logger.LogWarning(
                "[BandersnatchChoice] Could not find Jellyfin web directory. " +
                "Tried baseDir={BaseDir}, entryDir={EntryDir}. " +
                "Script injection skipped — add your web path via the plugin config page once the settings page is working.",
                baseDir, entryDir);

            return null;
        }

        private static bool IsValidWebDir(string path)
        {
            return Directory.Exists(path) && File.Exists(Path.Combine(path, "index.html"));
        }
    }
}
