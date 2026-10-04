using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace PaperlessMcpServer.Auth;

/// <summary>Validates bearer access tokens issued by <see cref="OAuthEndpoints"/>.</summary>
public sealed class SealedTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TokenSealer sealer,
    IMemoryCache cache,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PaperlessBearer";

    public const string PaperlessUrlClaim = "paperless_url";
    public const string PaperlessTokenClaim = "paperless_token";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = sealer.Unseal<TokenPayload>(SealPurpose.AccessToken, header[7..].Trim());
        if (token is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid access token."));
        }
        if (token.ExpiresAt < time.GetUtcNow().ToUnixTimeSeconds())
        {
            return Task.FromResult(AuthenticateResult.Fail("Access token expired."));
        }
        if (OAuthEndpoints.IsRevoked(cache, token.Id))
        {
            return Task.FromResult(AuthenticateResult.Fail("Access token revoked."));
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, $"{token.Grant.Username}@{token.Grant.Url}"),
            new Claim(ClaimTypes.Name, token.Grant.Username),
            new Claim(PaperlessUrlClaim, token.Grant.Url),
            new Claim(PaperlessTokenClaim, token.Grant.Token),
            new Claim("scope", token.Scope),
        ], SchemeName);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
