using System.Security.Cryptography;

namespace PaperlessMcpServer.Configuration;

/// <summary>
/// Runtime configuration, read from environment variables (see README).
/// </summary>
public sealed class ServerOptions
{
    /// <summary>
    /// Public base URL of this server, used as OAuth issuer and to build the MCP resource URL.
    /// When null, it is derived from each request (honoring X-Forwarded-Proto / X-Forwarded-Host).
    /// </summary>
    public Uri? PublicUrl { get; init; }

    /// <summary>Whether X-Forwarded-For / -Proto / -Host headers from reverse proxies are trusted.</summary>
    public bool TrustForwardedHeaders { get; init; } = true;

    /// <summary>Secret used to encrypt client registrations, authorization codes and tokens.</summary>
    public required string SecretKey { get; init; }

    /// <summary>True if no SECRET_KEY was configured and a random one was generated at startup.</summary>
    public bool EphemeralSecret { get; init; }

    /// <summary>
    /// Preconfigured Paperless-ngx instance. When set, users sign in with username and password
    /// (or an API token) for this instance instead of entering an instance URL.
    /// </summary>
    public Uri? PaperlessUrl { get; init; }

    /// <summary>Optional allowlist of Paperless hostnames users may connect to (only used without PAPERLESS_URL).</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(30);

    public TimeSpan PaperlessTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum size of documents returned by download_document.</summary>
    public long MaxDownloadBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>Maximum size of documents accepted by upload_document.</summary>
    public long MaxUploadBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>The public base URL for a request: PUBLIC_URL if configured, otherwise scheme and host of the request.</summary>
    public Uri GetPublicUrl(HttpRequest request) =>
        PublicUrl ?? new Uri($"{request.Scheme}://{request.Host.Value}/");

    /// <summary>The URL of the MCP endpoint, which is the OAuth protected resource.</summary>
    public Uri GetMcpUrl(HttpRequest request) => new(GetPublicUrl(request), "mcp");

    /// <summary>The OAuth issuer identifier (public URL without trailing slash).</summary>
    public string GetIssuer(HttpRequest request) => GetPublicUrl(request).AbsoluteUri.TrimEnd('/');

    public bool IsPreconfigured => PaperlessUrl is not null;

    public static ServerOptions FromEnvironment(IConfiguration configuration, ILogger? logger = null)
    {
        string? Get(string key) => string.IsNullOrWhiteSpace(configuration[key]) ? null : configuration[key]!.Trim();

        Uri? publicUrl = null;
        if (Get("PUBLIC_URL") is { } publicUrlRaw)
        {
            if (!Uri.TryCreate(publicUrlRaw, UriKind.Absolute, out publicUrl) ||
                (publicUrl.Scheme != Uri.UriSchemeHttp && publicUrl.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException($"PUBLIC_URL must be an absolute http(s) URL, got '{publicUrlRaw}'.");
            }
            if (publicUrl.AbsolutePath != "/")
            {
                throw new InvalidOperationException(
                    "PUBLIC_URL must not contain a path. Serve the MCP server on its own (sub)domain, e.g. https://paperless-mcp.example.com.");
            }
        }

        var secret = Get("SECRET_KEY");
        var ephemeral = false;
        if (secret is null)
        {
            secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            ephemeral = true;
            logger?.LogWarning("SECRET_KEY is not set. A random key was generated; all clients must sign in again after a restart.");
        }
        else if (secret.Length < 32)
        {
            throw new InvalidOperationException("SECRET_KEY must be at least 32 characters long (e.g. `openssl rand -base64 48`).");
        }

        Uri? paperlessUrl = null;
        if (Get("PAPERLESS_URL") is { } paperlessRaw)
        {
            paperlessUrl = PaperlessUrlHelper.Normalize(paperlessRaw)
                ?? throw new InvalidOperationException($"PAPERLESS_URL is not a valid http(s) URL: '{paperlessRaw}'.");
        }

        var allowedHosts = (Get("PAPERLESS_ALLOWED_HOSTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.ToLowerInvariant())
            .ToArray();

        return new ServerOptions
        {
            PublicUrl = publicUrl,
            TrustForwardedHeaders = !string.Equals(Get("TRUST_FORWARDED_HEADERS"), "false", StringComparison.OrdinalIgnoreCase),
            SecretKey = secret,
            EphemeralSecret = ephemeral,
            PaperlessUrl = paperlessUrl,
            AllowedHosts = allowedHosts,
            AccessTokenLifetime = TimeSpan.FromSeconds(GetInt(configuration, "ACCESS_TOKEN_TTL_SECONDS", 3600)),
            RefreshTokenLifetime = TimeSpan.FromSeconds(GetInt(configuration, "REFRESH_TOKEN_TTL_SECONDS", 30 * 24 * 3600)),
            PaperlessTimeout = TimeSpan.FromSeconds(GetInt(configuration, "PAPERLESS_TIMEOUT_SECONDS", 60)),
            MaxDownloadBytes = GetInt(configuration, "MAX_DOWNLOAD_MB", 25) * 1024L * 1024L,
            MaxUploadBytes = GetInt(configuration, "MAX_UPLOAD_MB", 50) * 1024L * 1024L,
        };
    }

    private static int GetInt(IConfiguration configuration, string key, int fallback)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, out var value) || value <= 0)
        {
            throw new InvalidOperationException($"{key} must be a positive integer, got '{raw}'.");
        }
        return value;
    }

    /// <summary>Checks a hostname against PAPERLESS_ALLOWED_HOSTS (supports "*.example.com" wildcards).</summary>
    public bool IsHostAllowed(string host)
    {
        if (AllowedHosts.Count == 0) return true;
        host = host.ToLowerInvariant();
        return AllowedHosts.Any(entry => entry.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(entry[1..], StringComparison.Ordinal)
            : host == entry);
    }
}

public static class PaperlessUrlHelper
{
    /// <summary>
    /// Normalizes a user-supplied Paperless URL: requires http(s), drops query/fragment,
    /// a trailing "/api" and trailing slashes. Returns the base URL ending in "/".
    /// </summary>
    public static Uri? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.Port, path + "/");
        return builder.Uri;
    }
}
