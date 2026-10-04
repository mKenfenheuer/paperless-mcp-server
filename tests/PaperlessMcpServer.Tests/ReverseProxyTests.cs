using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PaperlessMcpServer.Tests;

/// <summary>Without PUBLIC_URL, URLs are derived from X-Forwarded-Proto / X-Forwarded-Host set by a reverse proxy.</summary>
public class ReverseProxyTests : IClassFixture<ReverseProxyTests.Factory>
{
    private readonly Factory _factory;

    public ReverseProxyTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("SECRET_KEY", "integration-test-secret-0123456789abcdef");
    }

    private HttpClient CreateProxiedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "mcp.example.com");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");
        return client;
    }

    [Fact]
    public async Task MetadataUsesForwardedSchemeAndHost()
    {
        var client = CreateProxiedClient();

        var authorizationServer = await client.GetFromJsonAsync<JsonObject>("/.well-known/oauth-authorization-server");
        Assert.Equal("https://mcp.example.com", authorizationServer!["issuer"]!.ToString());
        Assert.Equal("https://mcp.example.com/authorize", authorizationServer["authorization_endpoint"]!.ToString());

        var resource = await client.GetFromJsonAsync<JsonObject>("/.well-known/oauth-protected-resource/mcp");
        Assert.Equal("https://mcp.example.com/mcp", resource!["resource"]!.ToString());
        Assert.Equal("https://mcp.example.com", resource["authorization_servers"]![0]!.ToString());

        var response = await client.PostAsync("/mcp", JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("https://mcp.example.com/.well-known/oauth-protected-resource/mcp", response.Headers.WwwAuthenticate.ToString());
    }
}
