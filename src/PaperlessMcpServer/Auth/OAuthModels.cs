using System.Text.Json.Serialization;

namespace PaperlessMcpServer.Auth;

// Payloads sealed with TokenSealer. Property names are kept short because they end up inside tokens.

/// <summary>A dynamically registered OAuth client. The sealed form of this record is the client_id.</summary>
public sealed record ClientRegistration(
    [property: JsonPropertyName("r")] string[] RedirectUris,
    [property: JsonPropertyName("n")] string? ClientName,
    [property: JsonPropertyName("m")] string AuthMethod,
    [property: JsonPropertyName("i")] long IssuedAt)
{
    public bool IsPublic => AuthMethod == "none";
}

/// <summary>The Paperless credentials a token grants access to.</summary>
public sealed record PaperlessGrant(
    [property: JsonPropertyName("u")] string Url,
    [property: JsonPropertyName("t")] string Token,
    [property: JsonPropertyName("n")] string Username);

/// <summary>A pending /authorize request, carried through the login form.</summary>
public sealed record PendingAuthorization(
    [property: JsonPropertyName("c")] string ClientId,
    [property: JsonPropertyName("r")] string RedirectUri,
    [property: JsonPropertyName("s")] string? State,
    [property: JsonPropertyName("cc")] string CodeChallenge,
    [property: JsonPropertyName("sc")] string Scope,
    [property: JsonPropertyName("res")] string? Resource,
    [property: JsonPropertyName("e")] long ExpiresAt);

public sealed record AuthorizationCode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("c")] string ClientKey,
    [property: JsonPropertyName("r")] string RedirectUri,
    [property: JsonPropertyName("cc")] string CodeChallenge,
    [property: JsonPropertyName("sc")] string Scope,
    [property: JsonPropertyName("res")] string? Resource,
    [property: JsonPropertyName("g")] PaperlessGrant Grant,
    [property: JsonPropertyName("e")] long ExpiresAt);

/// <summary>Payload of both access and refresh tokens (they are sealed with different keys).</summary>
public sealed record TokenPayload(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("c")] string ClientKey,
    [property: JsonPropertyName("sc")] string Scope,
    [property: JsonPropertyName("res")] string? Resource,
    [property: JsonPropertyName("g")] PaperlessGrant Grant,
    [property: JsonPropertyName("e")] long ExpiresAt);
