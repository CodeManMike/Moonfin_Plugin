using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Moonfin.Server.Api;

/// <summary>
/// Controller to serve Moonfin web files.
/// </summary>
[ApiController]
[Route("Moonfin/Web")]
public class MoonfinWebController : ControllerBase
{
    private readonly Assembly _assembly;
    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".htm"] = "text/html; charset=utf-8",
            [".js"] = "application/javascript",
            [".mjs"] = "text/javascript",
            [".css"] = "text/css",
            [".json"] = "application/json",
            [".map"] = "application/json",
            [".wasm"] = "application/wasm",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".ico"] = "image/x-icon",
            [".txt"] = "text/plain; charset=utf-8",
            [".xml"] = "application/xml",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".ttf"] = "font/ttf",
            [".otf"] = "font/otf",
        };

    public MoonfinWebController()
    {
        _assembly = typeof(MoonfinWebController).Assembly;
    }

    /// <summary>
    /// Legacy entrypoint redirect to Moonfin web app.
    /// </summary>
    [HttpGet("plugin.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult RedirectLegacyPluginJs()
    {
        const string script =
            "(function(){if(window.location.pathname.toLowerCase().indexOf('/moonfin/web/')===-1){window.location.href='/Moonfin/Web/';}})();";
        return Content(script, "application/javascript");
    }

    /// <summary>
    /// Legacy entrypoint redirect to Moonfin web app.
    /// </summary>
    [HttpGet("plugin.css")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult RedirectLegacyPluginCss()
    {
        return Content("/* legacy moonfin css stub */", "text/css");
    }

    /// <summary>
    /// Serves the Moonfin loader script bridge for stock Jellyfin web.
    /// </summary>
    /// <returns>The loader.js file.</returns>
    [HttpGet("loader.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetLoaderJs()
    {
        var resourceName = "Moonfin.Server.Web.loader.js";
        var stream = _assembly.GetManifestResourceStream(resourceName);

        if (stream == null)
        {
            return NotFound(new { Error = "loader.js not found" });
        }

        return File(stream, "application/javascript");
    }

    /// <summary>
    /// Serves runtime config consumed by Moonfin web plugin mode.
    /// </summary>
    [HttpGet("config.json")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRuntimeConfig()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var runtimeBaseUrl = ResolveRuntimeBaseUrl();
        var payload = new
        {
            schemaVersion = 1,
            defaultServerUrl = ResolveDefaultServerUrl(config?.WebDefaultServerUrl, runtimeBaseUrl),
            discoveryProxyUrl = ResolveDiscoveryProxyUrl(runtimeBaseUrl),
            enableWebRtcScan = config?.WebEnableWebRtcScan ?? true,
            brandingName = "Moonfin",
            pluginMode = true,
            forcedServerUrl = NormalizeConfiguredServerUrl(config?.WebForcedServerUrl)
        };

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        return new JsonResult(payload);
    }

    /// <summary>
    /// Serves Moonfin web static files from disk with index fallback for SPA routes.
    /// </summary>
    [HttpGet("{**path}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetWebAsset([FromRoute] string? path)
    {
        var webRoot = ResolveWebRoot();
        if (string.IsNullOrWhiteSpace(webRoot) || !Directory.Exists(webRoot))
        {
            return NotFound(new { Error = "Moonfin web root not found", Path = webRoot });
        }

        var requestedPath = string.IsNullOrWhiteSpace(path) ? "index.html" : path;
        if (!TryResolvePath(webRoot, requestedPath, out var fullPath))
        {
            return NotFound();
        }

        if (System.IO.File.Exists(fullPath))
        {
            return PhysicalFile(fullPath, GetContentType(fullPath));
        }

        if (Directory.Exists(fullPath))
        {
            var nestedIndexPath = Path.Combine(fullPath, "index.html");
            if (System.IO.File.Exists(nestedIndexPath))
            {
                return PhysicalFile(nestedIndexPath, "text/html; charset=utf-8");
            }
        }

        if (Path.HasExtension(requestedPath))
        {
            return NotFound();
        }

        var indexPath = Path.Combine(webRoot, "index.html");
        if (!System.IO.File.Exists(indexPath))
        {
            return NotFound(new { Error = "Moonfin web entrypoint missing", Path = indexPath });
        }

        return PhysicalFile(indexPath, "text/html; charset=utf-8");
    }

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private string ResolveRuntimeBaseUrl()
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}".Trim();
        return baseUrl.TrimEnd('/');
    }

    private static string ResolveDiscoveryProxyUrl(string runtimeBaseUrl)
    {
        return $"{runtimeBaseUrl}/Moonfin/Discovery/";
    }

    private static string ResolveDefaultServerUrl(string? configuredServerUrl, string runtimeBaseUrl)
    {
        return NormalizeConfiguredServerUrl(configuredServerUrl) ?? runtimeBaseUrl;
    }

    private static string? NormalizeConfiguredServerUrl(string? value)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized == null)
        {
            return null;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return normalized.TrimEnd('/');
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var count = segments.Count;

        if (count >= 2 &&
            segments[count - 2].Equals("web", StringComparison.OrdinalIgnoreCase) &&
            segments[count - 1].Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveRange(count - 2, 2);
        }
        else if (count >= 1 && segments[count - 1].Equals("web", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(count - 1);
        }

        var path = segments.Count == 0 ? string.Empty : "/" + string.Join('/', segments);
        var builder = new UriBuilder(uri)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private string ResolveWebRoot()
    {
        var overridePath = NormalizeOptionalText(Environment.GetEnvironmentVariable("MOONFIN_WEB_ROOT"));
        if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var assemblyDir = Path.GetDirectoryName(_assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDir))
        {
            var localFrontend = Path.Combine(assemblyDir, "frontend");
            if (Directory.Exists(localFrontend))
            {
                return Path.GetFullPath(localFrontend);
            }

            var localWeb = Path.Combine(assemblyDir, "web");
            if (Directory.Exists(localWeb))
            {
                return Path.GetFullPath(localWeb);
            }

            // Local development fallback when running from the source tree.
            var repoFrontend = Path.GetFullPath(
                Path.Combine(assemblyDir, "..", "..", "..", "..", "frontend"));
            if (Directory.Exists(repoFrontend))
            {
                return repoFrontend;
            }
        }

        var dataFolder = MoonfinPlugin.Instance?.DataFolderPath;
        if (!string.IsNullOrWhiteSpace(dataFolder))
        {
            var dataWeb = Path.Combine(dataFolder, "web");
            if (Directory.Exists(dataWeb))
            {
                return Path.GetFullPath(dataWeb);
            }

            return Path.GetFullPath(dataWeb);
        }

        return string.Empty;
    }

    private static bool TryResolvePath(string rootPath, string requestPath, out string fullPath)
    {
        var normalizedRequest = requestPath.Replace('\\', '/').TrimStart('/');
        var candidate = Path.Combine(
            rootPath,
            normalizedRequest.Replace('/', Path.DirectorySeparatorChar));
        fullPath = Path.GetFullPath(candidate);

        var normalizedRoot = Path.GetFullPath(rootPath);
        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return fullPath.StartsWith(rootWithSeparator, comparison) ||
               string.Equals(fullPath, normalizedRoot, comparison);
    }

    private static string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (ContentTypes.TryGetValue(extension, out var contentType))
        {
            return contentType;
        }

        return "application/octet-stream";
    }
}
