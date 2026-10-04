using PaperlessMcpServer.Auth;
using PaperlessMcpServer.Configuration;

namespace PaperlessMcpServer.Tests;

public class TokenSealerTests
{
    private sealed record Payload(string Value, int Number);

    private readonly TokenSealer _sealer = new("unit-test-secret-0123456789abcdefghijklmnop");

    [Fact]
    public void RoundTrips()
    {
        var sealedValue = _sealer.Seal(SealPurpose.AccessToken, new Payload("hello", 42));
        Assert.StartsWith("pmat_", sealedValue);
        Assert.Equal(new Payload("hello", 42), _sealer.Unseal<Payload>(SealPurpose.AccessToken, sealedValue));
    }

    [Fact]
    public void RejectsOtherPurpose()
    {
        var refresh = _sealer.Seal(SealPurpose.RefreshToken, new Payload("x", 1));
        // Even with a forged prefix, the key and associated data differ.
        var forged = "pmat_" + refresh["pmrt_".Length..];
        Assert.Null(_sealer.Unseal<Payload>(SealPurpose.AccessToken, refresh));
        Assert.Null(_sealer.Unseal<Payload>(SealPurpose.AccessToken, forged));
    }

    [Fact]
    public void RejectsTamperedValue()
    {
        var value = _sealer.Seal(SealPurpose.Client, new Payload("x", 1));
        var chars = value.ToCharArray();
        chars[^5] = chars[^5] == 'A' ? 'B' : 'A';
        Assert.Null(_sealer.Unseal<Payload>(SealPurpose.Client, new string(chars)));
        Assert.Null(_sealer.Unseal<Payload>(SealPurpose.Client, "pmc_not-base64!"));
        Assert.Null(_sealer.Unseal<Payload>(SealPurpose.Client, null));
    }

    [Fact]
    public void RejectsOtherSecret()
    {
        var other = new TokenSealer("another-secret-0123456789abcdefghijklmnop");
        var value = other.Seal(SealPurpose.AccessToken, new Payload("x", 1));
        Assert.Null(_sealer.Unseal<Payload>(SealPurpose.AccessToken, value));
    }
}

public class OAuthHelperTests
{
    [Fact]
    public void VerifiesPkceS256() // RFC 7636, appendix B
    {
        Assert.True(OAuthEndpoints.VerifyPkce("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
        Assert.False(OAuthEndpoints.VerifyPkce("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXx", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("http://localhost:51234/callback", "http://localhost:6274/callback", true)]
    [InlineData("http://127.0.0.1:1/cb", "http://127.0.0.1/cb", true)]
    [InlineData("http://localhost:51234/other", "http://localhost:6274/callback", false)]
    [InlineData("https://evil.example/cb", "https://good.example/cb", false)]
    [InlineData("http://example.com:8080/cb", "http://example.com/cb", false)]
    public void MatchesRedirectUris(string requested, string registered, bool expected) =>
        Assert.Equal(expected, OAuthEndpoints.RedirectUriMatches(requested, registered));

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("http://localhost:3000/cb", true)]
    [InlineData("cursor://anysphere.cursor-retrieval/oauth/callback", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://example.com/cb#fragment", false)]
    [InlineData("/relative", false)]
    public void ValidatesRedirectUris(string uri, bool expected) =>
        Assert.Equal(expected, OAuthEndpoints.IsAcceptableRedirectUri(uri));
}

public class ConfigurationTests
{
    [Theory]
    [InlineData("https://paperless.example.com", "https://paperless.example.com/")]
    [InlineData("https://paperless.example.com/api/", "https://paperless.example.com/")]
    [InlineData("paperless.example.com", "https://paperless.example.com/")]
    [InlineData("http://nas:8000/paperless/", "http://nas:8000/paperless/")]
    [InlineData("https://example.com/paperless/api?x=1#y", "https://example.com/paperless/")]
    public void NormalizesPaperlessUrls(string input, string expected) =>
        Assert.Equal(expected, PaperlessUrlHelper.Normalize(input)!.AbsoluteUri);

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("")]
    public void RejectsInvalidPaperlessUrls(string input) => Assert.Null(PaperlessUrlHelper.Normalize(input));

    [Fact]
    public void ChecksAllowedHosts()
    {
        var options = new ServerOptions { PublicUrl = new Uri("https://mcp.example.com"), SecretKey = "x", AllowedHosts = ["paperless.example.com", "*.home.arpa"] };
        Assert.True(options.IsHostAllowed("paperless.example.com"));
        Assert.True(options.IsHostAllowed("docs.home.arpa"));
        Assert.False(options.IsHostAllowed("home.arpa.evil.com"));
        Assert.False(options.IsHostAllowed("169.254.169.254"));
    }
}
