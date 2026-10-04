using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using PaperlessMcpServer.Paperless;

namespace PaperlessMcpServer.Tests;

/// <summary>Runs the full OAuth flow and MCP tool calls against a fake Paperless instance.</summary>
public partial class OAuthFlowTests : IClassFixture<OAuthFlowTests.Factory>
{
    private const string RedirectUri = "http://localhost:33418/callback";
    private const string PaperlessUrl = "https://paperless.test";
    private const string ValidToken = "valid-paperless-token";

    private readonly Factory _factory;

    public OAuthFlowTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("PUBLIC_URL", "http://localhost");
            builder.UseSetting("SECRET_KEY", "integration-test-secret-0123456789abcdef");
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(PaperlessClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakePaperless()));
        }
    }

    [Fact]
    public async Task McpEndpointRequiresAuthentication()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/mcp", JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata=", response.Headers.WwwAuthenticate.ToString());

        var metadata = await client.GetFromJsonAsync<JsonObject>("/.well-known/oauth-protected-resource/mcp");
        Assert.Equal("http://localhost/mcp", metadata!["resource"]!.ToString());
        var asMetadata = await client.GetFromJsonAsync<JsonObject>("/.well-known/oauth-authorization-server");
        Assert.Equal("http://localhost/token", asMetadata!["token_endpoint"]!.ToString());
    }

    [Fact]
    public async Task FullFlowIssuesTokensThatWorkForTools()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var (clientId, verifier, sealedRequest) = await StartAuthorizationAsync(client);

        // Wrong token: the login page is shown again with an error.
        var bad = await PostLoginAsync(client, sealedRequest, "wrong-token");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("rejected", await bad.Content.ReadAsStringAsync());

        var ok = await PostLoginAsync(client, sealedRequest, ValidToken);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var query = QueryHelpers.ParseQuery(ok.Headers.Location!.Query);
        Assert.Equal("abc", query["state"].ToString());
        var code = query["code"].ToString();

        // Wrong PKCE verifier is rejected.
        var wrongPkce = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
            ["code_verifier"] = new string('a', 50), ["redirect_uri"] = RedirectUri,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, wrongPkce.StatusCode);

        var tokenResponse = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
            ["code_verifier"] = verifier, ["redirect_uri"] = RedirectUri,
        }));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokens = (await tokenResponse.Content.ReadFromJsonAsync<JsonObject>())!;
        var accessToken = tokens["access_token"]!.ToString();

        // Codes are single use.
        var replay = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId, ["code_verifier"] = verifier,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        // Tools work with the access token.
        var result = await CallToolAsync(client, accessToken, "search_documents", new JsonObject { ["tags"] = new JsonArray("Invoice") });
        var text = result["result"]!["content"]![0]!["text"]!.ToString();
        var payload = JsonNode.Parse(text)!;
        Assert.Equal(1, payload["total"]!.GetValue<int>());
        Assert.Equal("Invoice INV-100", payload["documents"]![0]!["title"]!.ToString());
        Assert.Equal("Contoso Ltd.", payload["documents"]![0]!["correspondent"]!.ToString());

        // Refresh works and yields a new access token.
        var refreshed = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = tokens["refresh_token"]!.ToString(), ["client_id"] = clientId,
        }));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        // Revoked access tokens stop working.
        await client.PostAsync("/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = accessToken, ["client_id"] = clientId }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task DenyRedirectsWithAccessDenied()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var (_, _, sealedRequest) = await StartAuthorizationAsync(client);
        var response = await client.PostAsync("/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["request"] = sealedRequest, ["action"] = "deny",
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=access_denied", response.Headers.Location!.Query);
    }

    [Fact]
    public async Task UnknownRedirectUriIsNotFollowed()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var registration = await RegisterAsync(client);
        var response = await client.GetAsync(QueryHelpers.AddQueryString("/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = registration, ["redirect_uri"] = "https://evil.example/cb",
            ["code_challenge"] = new string('a', 43), ["code_challenge_method"] = "S256",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<string> RegisterAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/register", new
        {
            redirect_uris = new[] { RedirectUri },
            client_name = "Test Client",
            token_endpoint_auth_method = "none",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.ToString();
    }

    private static async Task<(string ClientId, string Verifier, string SealedRequest)> StartAuthorizationAsync(HttpClient client)
    {
        var clientId = await RegisterAsync(client);
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var page = await client.GetStringAsync(QueryHelpers.AddQueryString("/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = RedirectUri,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = "abc",
            ["resource"] = "http://localhost/mcp",
        }));
        Assert.Contains("Test Client", page);
        var sealedRequest = WebUtility.HtmlDecode(RequestFieldRegex().Match(page).Groups[1].Value);
        return (clientId, verifier, sealedRequest);
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string sealedRequest, string token) =>
        client.PostAsync("/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["request"] = sealedRequest, ["mode"] = "token", ["url"] = PaperlessUrl, ["token"] = token, ["action"] = "approve",
        }));

    private static async Task<JsonNode> CallToolAsync(HttpClient client, string accessToken, string name, JsonObject arguments)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var json = body.TrimStart().StartsWith('{')
            ? body
            : body.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))[5..];
        return JsonNode.Parse(json)!;
    }

    [GeneratedRegex("name=\"request\" value=\"([^\"]+)\"")]
    private static partial Regex RequestFieldRegex();

    /// <summary>Minimal stand-in for the Paperless REST API.</summary>
    private sealed class FakePaperless : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Parameter != ValidToken)
            {
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"detail":"Invalid token."}"""));
            }
            var path = request.RequestUri!.AbsolutePath;
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            return Task.FromResult(path switch
            {
                "/api/ui_settings/" => Json(HttpStatusCode.OK, """{"user":{"id":4,"username":"steve"},"settings":{}}"""),
                "/api/tags/" => Json(HttpStatusCode.OK, """{"count":1,"next":null,"previous":null,"results":[{"id":1,"name":"Invoice","document_count":1}]}"""),
                "/api/correspondents/" => Json(HttpStatusCode.OK, """{"count":1,"next":null,"previous":null,"results":[{"id":2,"name":"Contoso Ltd."}]}"""),
                "/api/document_types/" or "/api/storage_paths/" or "/api/custom_fields/" => Json(HttpStatusCode.OK, """{"count":0,"next":null,"previous":null,"results":[]}"""),
                "/api/documents/" when query["tags__id__all"] == "1" => Json(HttpStatusCode.OK, """
                    {"count":1,"next":null,"previous":null,"results":[{"id":1,"title":"Invoice INV-100","content":"CONTOSO LTD. INVOICE","correspondent":2,"document_type":null,"storage_path":null,"tags":[1],"created":"2026-02-23","archive_serial_number":null}]}
                    """),
                _ => Json(HttpStatusCode.NotFound, """{"detail":"Not found."}"""),
            });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
