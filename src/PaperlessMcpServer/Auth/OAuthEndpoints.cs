using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using PaperlessMcpServer.Configuration;
using PaperlessMcpServer.Paperless;

namespace PaperlessMcpServer.Auth;

/// <summary>
/// A small, stateless OAuth 2.1 authorization server as required by the MCP authorization spec:
/// metadata (RFC 8414), dynamic client registration (RFC 7591), authorization code + PKCE,
/// refresh tokens and revocation (RFC 7009). The "login" step asks the user for their
/// Paperless credentials, which are validated against Paperless and sealed into the tokens.
/// </summary>
public static class OAuthEndpoints
{
    public const string Scope = "paperless";

    private static readonly TimeSpan PendingAuthorizationLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> ForbiddenRedirectSchemes = ["javascript", "data", "file", "vbscript", "about", "blob"];
    private static readonly HashSet<string> LoopbackHosts = ["localhost", "127.0.0.1", "[::1]", "::1"];

    public static void MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-authorization-server", GetMetadata);
        app.MapPost("/register", RegisterAsync).RequireRateLimiting("register");
        app.MapGet("/authorize", GetAuthorize);
        app.MapPost("/authorize", PostAuthorizeAsync).RequireRateLimiting("login").DisableAntiforgery();
        app.MapPost("/token", TokenAsync).RequireRateLimiting("token").DisableAntiforgery();
        app.MapPost("/revoke", RevokeAsync).RequireRateLimiting("token").DisableAntiforgery();
    }

    // ------------------------------------------------------------------- metadata

    private static IResult GetMetadata(ServerOptions options)
    {
        var issuer = options.Issuer;
        return Results.Json(new JsonObject
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = $"{issuer}/authorize",
            ["token_endpoint"] = $"{issuer}/token",
            ["registration_endpoint"] = $"{issuer}/register",
            ["revocation_endpoint"] = $"{issuer}/revoke",
            ["scopes_supported"] = new JsonArray(Scope),
            ["response_types_supported"] = new JsonArray("code"),
            ["response_modes_supported"] = new JsonArray("query"),
            ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
            ["token_endpoint_auth_methods_supported"] = new JsonArray("none", "client_secret_post", "client_secret_basic"),
            ["revocation_endpoint_auth_methods_supported"] = new JsonArray("none", "client_secret_post", "client_secret_basic"),
            ["code_challenge_methods_supported"] = new JsonArray("S256"),
            ["authorization_response_iss_parameter_supported"] = true,
        });
    }

    // --------------------------------------------------------- client registration

    private static async Task<IResult> RegisterAsync(HttpContext context, TokenSealer sealer, TimeProvider time)
    {
        context.Response.Headers.CacheControl = "no-store";
        JsonObject? body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<JsonObject>();
        }
        catch (Exception)
        {
            return RegistrationError("invalid_client_metadata", "Request body must be a JSON object.");
        }
        if (body is null) return RegistrationError("invalid_client_metadata", "Request body must be a JSON object.");

        if (body["redirect_uris"] is not JsonArray redirectArray || redirectArray.Count == 0)
        {
            return RegistrationError("invalid_redirect_uri", "redirect_uris is required.");
        }
        var redirectUris = new List<string>();
        foreach (var node in redirectArray)
        {
            var uri = node?.GetValueKind() == System.Text.Json.JsonValueKind.String ? node.GetValue<string>() : null;
            if (!IsAcceptableRedirectUri(uri))
            {
                return RegistrationError("invalid_redirect_uri", $"Invalid redirect URI: {uri}");
            }
            redirectUris.Add(uri!);
        }

        var authMethod = body["token_endpoint_auth_method"]?.GetValue<string>() ?? "client_secret_basic";
        if (authMethod is not ("none" or "client_secret_post" or "client_secret_basic"))
        {
            return RegistrationError("invalid_client_metadata", $"Unsupported token_endpoint_auth_method '{authMethod}'.");
        }

        if (body["grant_types"] is JsonArray grants &&
            grants.Select(g => g?.ToString()).Any(g => g is not ("authorization_code" or "refresh_token")))
        {
            return RegistrationError("invalid_client_metadata", "Only authorization_code and refresh_token grants are supported.");
        }

        var clientName = body["client_name"]?.ToString();
        if (clientName is { Length: > 100 }) clientName = clientName[..100];

        var issuedAt = time.GetUtcNow().ToUnixTimeSeconds();
        var registration = new ClientRegistration([.. redirectUris], clientName, authMethod, issuedAt);
        var clientId = sealer.Seal(SealPurpose.Client, registration);

        var response = new JsonObject
        {
            ["client_id"] = clientId,
            ["client_id_issued_at"] = issuedAt,
            ["redirect_uris"] = new JsonArray(redirectUris.Select(u => (JsonNode)u!).ToArray()),
            ["token_endpoint_auth_method"] = authMethod,
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["scope"] = Scope,
        };
        if (clientName is not null) response["client_name"] = clientName;
        if (!registration.IsPublic)
        {
            response["client_secret"] = ClientSecret(sealer, clientId);
            response["client_secret_expires_at"] = 0;
        }
        return Results.Json(response, statusCode: StatusCodes.Status201Created);
    }

    private static IResult RegistrationError(string error, string description) =>
        Results.Json(new JsonObject { ["error"] = error, ["error_description"] = description }, statusCode: 400);

    /// <summary>Client secrets are derived from the client id, so they need no storage.</summary>
    private static string ClientSecret(TokenSealer sealer, string clientId) => sealer.Hash("client-secret", clientId);

    private static string ClientKey(TokenSealer sealer, string clientId) => sealer.Hash("client-key", clientId)[..22];

    internal static bool IsAcceptableRedirectUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2000) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (!string.IsNullOrEmpty(uri.Fragment)) return false;
        return !ForbiddenRedirectSchemes.Contains(uri.Scheme.ToLowerInvariant());
    }

    /// <summary>Exact match, except that loopback redirect URIs may use any port (RFC 8252, section 7.3).</summary>
    internal static bool RedirectUriMatches(string requested, string registered)
    {
        if (requested == registered) return true;
        if (!Uri.TryCreate(requested, UriKind.Absolute, out var r) || !Uri.TryCreate(registered, UriKind.Absolute, out var g)) return false;
        return r.Scheme == Uri.UriSchemeHttp && g.Scheme == Uri.UriSchemeHttp
            && LoopbackHosts.Contains(r.Host) && r.Host == g.Host
            && r.PathAndQuery == g.PathAndQuery;
    }

    // ----------------------------------------------------------------- authorize

    private static IResult GetAuthorize(HttpContext context, TokenSealer sealer, ServerOptions options, TimeProvider time)
    {
        var query = context.Request.Query;
        string? Param(string name) => query[name].FirstOrDefault() is { Length: > 0 } v ? v : null;

        var clientId = Param("client_id");
        var client = sealer.Unseal<ClientRegistration>(SealPurpose.Client, clientId);
        if (client is null)
        {
            return ErrorPage(HttpStatusCode.BadRequest, "Unknown application",
                "This application is not registered with this server (or the server's SECRET_KEY changed). Remove and re-add the connection in your application.");
        }

        var redirectUri = Param("redirect_uri");
        if (redirectUri is null)
        {
            if (client.RedirectUris.Length != 1)
            {
                return ErrorPage(HttpStatusCode.BadRequest, "Invalid request", "redirect_uri is required.");
            }
            redirectUri = client.RedirectUris[0];
        }
        else if (!client.RedirectUris.Any(registered => RedirectUriMatches(redirectUri, registered)))
        {
            return ErrorPage(HttpStatusCode.BadRequest, "Invalid request", "The redirect_uri is not registered for this application.");
        }

        var state = Param("state");
        if (Param("response_type") != "code")
        {
            return ErrorRedirect(options, redirectUri, state, "unsupported_response_type", "Only response_type=code is supported.");
        }
        var codeChallenge = Param("code_challenge");
        if (codeChallenge is null || codeChallenge.Length is < 43 or > 128)
        {
            return ErrorRedirect(options, redirectUri, state, "invalid_request", "A PKCE code_challenge is required.");
        }
        if ((Param("code_challenge_method") ?? "plain") != "S256")
        {
            return ErrorRedirect(options, redirectUri, state, "invalid_request", "code_challenge_method must be S256.");
        }
        var resource = Param("resource");
        if (resource is not null && !IsOwnResource(options, resource))
        {
            return ErrorRedirect(options, redirectUri, state, "invalid_target", $"Unknown resource '{resource}'.");
        }

        var pending = new PendingAuthorization(
            clientId!, redirectUri, state, codeChallenge, Scope, resource,
            time.GetUtcNow().Add(PendingAuthorizationLifetime).ToUnixTimeSeconds());

        return LoginPageResult(new LoginPage.Model(
            sealer.Seal(SealPurpose.AuthorizationRequest, pending),
            DisplayName(client),
            RedirectHost(redirectUri),
            options.PaperlessUrl));
    }

    private static async Task<IResult> PostAuthorizeAsync(
        HttpContext context,
        TokenSealer sealer,
        ServerOptions options,
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("PaperlessMcpServer.Auth");
        var form = await context.Request.ReadFormAsync();
        string? Field(string name) => form[name].FirstOrDefault() is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var sealedRequest = Field("request");
        var pending = sealer.Unseal<PendingAuthorization>(SealPurpose.AuthorizationRequest, sealedRequest);
        if (pending is null || pending.ExpiresAt < time.GetUtcNow().ToUnixTimeSeconds())
        {
            return ErrorPage(HttpStatusCode.BadRequest, "Sign-in expired", "This sign-in request has expired. Please start again from your application.");
        }
        var client = sealer.Unseal<ClientRegistration>(SealPurpose.Client, pending.ClientId);
        if (client is null)
        {
            return ErrorPage(HttpStatusCode.BadRequest, "Unknown application", "This application is not registered with this server.");
        }

        if (Field("action") == "deny")
        {
            return ErrorRedirect(options, pending.RedirectUri, pending.State, "access_denied", "The user denied access.");
        }

        var mode = Field("mode") ?? "token";
        var prefillUrl = Field("url");
        var prefillUsername = Field("username");
        IResult Retry(string error) => LoginPageResult(new LoginPage.Model(
            sealedRequest!, DisplayName(client), RedirectHost(pending.RedirectUri), options.PaperlessUrl,
            error, prefillUrl, prefillUsername, TokenModeOpen: options.IsPreconfigured && mode == "token"), StatusCodes.Status400BadRequest);

        Uri? baseUrl;
        if (options.PaperlessUrl is not null)
        {
            baseUrl = options.PaperlessUrl;
        }
        else
        {
            baseUrl = PaperlessUrlHelper.Normalize(prefillUrl);
            if (baseUrl is null) return Retry("Please enter a valid Paperless URL, e.g. https://paperless.example.com.");
            if (!options.IsHostAllowed(baseUrl.Host)) return Retry($"Connecting to {baseUrl.Host} is not allowed on this server.");
        }

        var http = httpFactory.CreateClient(PaperlessClient.HttpClientName);
        string token;
        try
        {
            if (mode == "password")
            {
                if (options.PaperlessUrl is null) return Retry("Username and password sign-in is not available on this server.");
                var username = Field("username");
                var password = form["password"].FirstOrDefault();
                if (username is null || string.IsNullOrEmpty(password)) return Retry("Please enter your username and password.");
                token = await PaperlessClient.ObtainTokenAsync(http, baseUrl, username, password, context.RequestAborted);
            }
            else
            {
                token = Field("token") ?? "";
                if (token.Length == 0) return Retry("Please enter your Paperless API token.");
            }

            var paperless = new PaperlessClient(http, baseUrl, token, cache);
            var user = await paperless.GetCurrentUserAsync(context.RequestAborted);

            var code = new AuthorizationCode(
                Guid.NewGuid().ToString("N"),
                ClientKey(sealer, pending.ClientId),
                pending.RedirectUri,
                pending.CodeChallenge,
                pending.Scope,
                pending.Resource,
                new PaperlessGrant(baseUrl.AbsoluteUri, token, user.Username),
                time.GetUtcNow().Add(AuthorizationCodeLifetime).ToUnixTimeSeconds());

            logger.LogInformation("User {Username} authorized client {Client} for {PaperlessUrl}", user.Username, DisplayName(client), baseUrl);
            return Redirect(pending.RedirectUri, new Dictionary<string, string?>
            {
                ["code"] = sealer.Seal(SealPurpose.AuthorizationCode, code),
                ["state"] = pending.State,
                ["iss"] = options.Issuer,
            });
        }
        catch (PaperlessApiException e)
        {
            logger.LogInformation("Sign-in against {PaperlessUrl} failed: {Message}", baseUrl, e.Message);
            var message = e.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when mode == "token" => "Paperless rejected this API token.",
                HttpStatusCode.NotFound => $"{baseUrl} does not look like a Paperless-ngx instance.",
                _ => e.Message,
            };
            return Retry(message);
        }
    }

    private static string DisplayName(ClientRegistration client) =>
        string.IsNullOrWhiteSpace(client.ClientName) ? "An application" : client.ClientName!;

    private static string RedirectHost(string redirectUri) =>
        Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            ? (string.IsNullOrEmpty(uri.Authority) ? $"{uri.Scheme}:" : $"{uri.Scheme}://{uri.Authority}")
            : redirectUri;

    private static bool IsOwnResource(ServerOptions options, string resource)
    {
        static string Normalize(string value) => value.TrimEnd('/');
        var r = Normalize(resource);
        return string.Equals(r, Normalize(options.McpUrl.AbsoluteUri), StringComparison.OrdinalIgnoreCase)
            || string.Equals(r, options.Issuer, StringComparison.OrdinalIgnoreCase);
    }

    private static IResult LoginPageResult(LoginPage.Model model, int statusCode = StatusCodes.Status200OK) =>
        new HtmlResult(LoginPage.Render(model), statusCode);

    private static IResult ErrorPage(HttpStatusCode status, string title, string message) =>
        new HtmlResult(LoginPage.RenderError(title, message), (int)status);

    private static IResult ErrorRedirect(ServerOptions options, string redirectUri, string? state, string error, string description) =>
        Redirect(redirectUri, new Dictionary<string, string?>
        {
            ["error"] = error,
            ["error_description"] = description,
            ["state"] = state,
            ["iss"] = options.Issuer,
        });

    private static IResult Redirect(string redirectUri, Dictionary<string, string?> parameters) =>
        Results.Redirect(QueryHelpers.AddQueryString(redirectUri, parameters.Where(p => p.Value is not null)));

    // --------------------------------------------------------------------- token

    private static async Task<IResult> TokenAsync(
        HttpContext context,
        TokenSealer sealer,
        ServerOptions options,
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        TimeProvider time)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        if (!context.Request.HasFormContentType)
        {
            return TokenError("invalid_request", "Expected application/x-www-form-urlencoded body.");
        }
        var form = await context.Request.ReadFormAsync();
        string? Field(string name) => form[name].FirstOrDefault() is { Length: > 0 } v ? v : null;

        var (clientId, clientError) = AuthenticateClient(context, form, sealer);
        if (clientError is not null) return clientError;

        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var clientKey = ClientKey(sealer, clientId!);

        switch (Field("grant_type"))
        {
            case "authorization_code":
            {
                var code = sealer.Unseal<AuthorizationCode>(SealPurpose.AuthorizationCode, Field("code"));
                if (code is null || code.ExpiresAt < now || code.ClientKey != clientKey)
                {
                    return TokenError("invalid_grant", "The authorization code is invalid or expired.");
                }
                if (Field("redirect_uri") is { } redirectUri && redirectUri != code.RedirectUri)
                {
                    return TokenError("invalid_grant", "redirect_uri does not match the authorization request.");
                }
                var verifier = Field("code_verifier");
                if (verifier is null || !VerifyPkce(verifier, code.CodeChallenge))
                {
                    return TokenError("invalid_grant", "PKCE verification failed.");
                }
                if (Field("resource") is { } resource && !IsOwnResource(options, resource))
                {
                    return TokenError("invalid_target", $"Unknown resource '{resource}'.");
                }
                // Authorization codes are single use.
                var usedKey = $"used-code:{code.Id}";
                if (cache.TryGetValue(usedKey, out _))
                {
                    return TokenError("invalid_grant", "The authorization code has already been used.");
                }
                cache.Set(usedKey, true, AuthorizationCodeLifetime + TimeSpan.FromMinutes(1));

                return IssueTokens(sealer, options, time, clientKey, code.Scope, code.Resource, code.Grant);
            }

            case "refresh_token":
            {
                var refresh = sealer.Unseal<TokenPayload>(SealPurpose.RefreshToken, Field("refresh_token"));
                if (refresh is null || refresh.ExpiresAt < now || refresh.ClientKey != clientKey || IsRevoked(cache, refresh.Id))
                {
                    return TokenError("invalid_grant", "The refresh token is invalid or expired.");
                }

                // Make sure the Paperless token is still valid, so revoked tokens force a new sign-in.
                try
                {
                    var paperless = new PaperlessClient(httpFactory.CreateClient(PaperlessClient.HttpClientName),
                        new Uri(refresh.Grant.Url), refresh.Grant.Token, cache);
                    await paperless.GetCurrentUserAsync(context.RequestAborted);
                }
                catch (PaperlessApiException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return TokenError("invalid_grant", "The Paperless API token is no longer valid. Please sign in again.");
                }
                catch (PaperlessApiException)
                {
                    // Paperless is temporarily unreachable; don't force the user to sign in again for that.
                }

                return IssueTokens(sealer, options, time, clientKey, refresh.Scope, refresh.Resource, refresh.Grant);
            }

            default:
                return TokenError("unsupported_grant_type", "Supported grant types: authorization_code, refresh_token.");
        }
    }

    private static IResult IssueTokens(TokenSealer sealer, ServerOptions options, TimeProvider time, string clientKey, string scope, string? resource, PaperlessGrant grant)
    {
        var now = time.GetUtcNow();
        var access = new TokenPayload(Guid.NewGuid().ToString("N"), clientKey, scope, resource, grant,
            now.Add(options.AccessTokenLifetime).ToUnixTimeSeconds());
        var refresh = new TokenPayload(Guid.NewGuid().ToString("N"), clientKey, scope, resource, grant,
            now.Add(options.RefreshTokenLifetime).ToUnixTimeSeconds());

        return Results.Json(new JsonObject
        {
            ["access_token"] = sealer.Seal(SealPurpose.AccessToken, access),
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)options.AccessTokenLifetime.TotalSeconds,
            ["refresh_token"] = sealer.Seal(SealPurpose.RefreshToken, refresh),
            ["scope"] = scope,
        });
    }

    /// <summary>Authenticates the client via HTTP Basic or form parameters. Returns the client id or an error result.</summary>
    private static (string? ClientId, IResult? Error) AuthenticateClient(HttpContext context, IFormCollection form, TokenSealer sealer)
    {
        string? clientId = form["client_id"].FirstOrDefault();
        string? clientSecret = form["client_secret"].FirstOrDefault();

        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
                var separator = decoded.IndexOf(':');
                if (separator > 0)
                {
                    clientId = Uri.UnescapeDataString(decoded[..separator]);
                    clientSecret = Uri.UnescapeDataString(decoded[(separator + 1)..]);
                }
            }
            catch (FormatException)
            {
                return (null, TokenError("invalid_client", "Malformed Basic authorization header.", 401));
            }
        }

        var client = sealer.Unseal<ClientRegistration>(SealPurpose.Client, clientId);
        if (client is null)
        {
            return (null, TokenError("invalid_client", "Unknown client. Register the client again.", 401));
        }
        if (!client.IsPublic)
        {
            var expected = Encoding.UTF8.GetBytes(ClientSecret(sealer, clientId!));
            var actual = Encoding.UTF8.GetBytes(clientSecret ?? "");
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                return (null, TokenError("invalid_client", "Invalid client credentials.", 401));
            }
        }
        return (clientId, null);
    }

    internal static bool VerifyPkce(string verifier, string challenge)
    {
        if (verifier.Length is < 43 or > 128) return false;
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var computed = WebEncoders.Base64UrlEncode(hash);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    private static IResult TokenError(string error, string description, int status = 400) =>
        Results.Json(new JsonObject { ["error"] = error, ["error_description"] = description }, statusCode: status);

    // -------------------------------------------------------------------- revoke

    private static async Task<IResult> RevokeAsync(HttpContext context, TokenSealer sealer, IMemoryCache cache, TimeProvider time)
    {
        if (!context.Request.HasFormContentType) return TokenError("invalid_request", "Expected form body.");
        var form = await context.Request.ReadFormAsync();
        var token = form["token"].FirstOrDefault();

        var payload = sealer.Unseal<TokenPayload>(SealPurpose.AccessToken, token)
                      ?? sealer.Unseal<TokenPayload>(SealPurpose.RefreshToken, token);
        if (payload is not null)
        {
            var remaining = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt) - time.GetUtcNow();
            if (remaining > TimeSpan.Zero) cache.Set($"revoked:{payload.Id}", true, remaining);
        }
        // RFC 7009: respond with 200 even for unknown tokens.
        return Results.Ok();
    }

    internal static bool IsRevoked(IMemoryCache cache, string tokenId) => cache.TryGetValue($"revoked:{tokenId}", out _);

    private sealed class HtmlResult(string html, int statusCode) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/html; charset=utf-8";
            var headers = context.Response.Headers;
            headers.CacheControl = "no-store";
            headers["X-Frame-Options"] = "DENY";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; frame-ancestors 'none'; base-uri 'none'";
            return context.Response.WriteAsync(html);
        }
    }
}
