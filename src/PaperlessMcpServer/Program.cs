using System.Net;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;
using PaperlessMcpServer.Auth;
using PaperlessMcpServer.Configuration;
using PaperlessMcpServer.Paperless;
using PaperlessMcpServer.Tools;

var builder = WebApplication.CreateBuilder(args);

// Plain environment variables (PUBLIC_URL, SECRET_KEY, ...) are read through IConfiguration.
using var startupLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
var options = ServerOptions.FromEnvironment(builder.Configuration, startupLoggerFactory.CreateLogger("PaperlessMcpServer"));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TokenSealer>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.MaxUploadBytes * 4 / 3 + 1024 * 1024);

builder.Services.AddHttpClient(PaperlessClient.HttpClientName, client =>
    {
        client.Timeout = options.PaperlessTimeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"paperless-mcp-server/{AppInfo.Version}");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // Redirects would drop the Authorization header; report them instead of following.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

// The Paperless client for the current request, built from the claims of the access token.
builder.Services.AddScoped(sp =>
{
    var user = sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
    var url = user?.FindFirst(SealedTokenAuthenticationHandler.PaperlessUrlClaim)?.Value;
    var token = user?.FindFirst(SealedTokenAuthenticationHandler.PaperlessTokenClaim)?.Value;
    if (url is null || token is null)
    {
        throw new McpException("Not authenticated with Paperless.");
    }
    return new PaperlessClient(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(PaperlessClient.HttpClientName),
        new Uri(url),
        token,
        sp.GetRequiredService<IMemoryCache>());
});
builder.Services.AddScoped<MetadataResolver>();
builder.Services.AddScoped<DocumentFormatter>();

builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultAuthenticateScheme = SealedTokenAuthenticationHandler.SchemeName;
        o.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
    })
    .AddScheme<AuthenticationSchemeOptions, SealedTokenAuthenticationHandler>(SealedTokenAuthenticationHandler.SchemeName, null)
    .AddMcp(o =>
    {
        // Protected resource metadata (RFC 9728), built per request because the public URL may be
        // derived from the request when PUBLIC_URL is not set.
        o.Events.OnResourceMetadataRequest = ctx =>
        {
            var request = ctx.HttpContext.Request;
            ctx.ResourceMetadata = new ProtectedResourceMetadata
            {
                Resource = options.GetMcpUrl(request).AbsoluteUri,
                AuthorizationServers = { options.GetIssuer(request) },
                ScopesSupported = [OAuthEndpoints.Scope],
                BearerMethodsSupported = ["header"],
                ResourceName = "Paperless-ngx",
                ResourceDocumentation = "https://github.com/mKenfenheuer/paperless-mcp-server",
            };
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

// Browser-based MCP clients (e.g. MCP Inspector) call metadata, OAuth and MCP endpoints cross-origin.
// No credentials (cookies) are involved, so allowing any origin is safe.
builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders("Mcp-Session-Id", "Mcp-Protocol-Version", "WWW-Authenticate"));
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Partition(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(Partition(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("register", ctx => RateLimitPartition.GetFixedWindowLimiter(Partition(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromHours(1) }));
    o.AddPolicy("token", ctx => RateLimitPartition.GetFixedWindowLimiter(Partition(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "paperless-mcp-server", Title = "Paperless-ngx", Version = AppInfo.Version };
        o.ServerInstructions = ServerInstructions.Text;
    })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<DocumentTools>(ToolJson.Options)
    .WithTools<MetadataTools>(ToolJson.Options)
    .WithTools<NoteTools>(ToolJson.Options);

var app = builder.Build();

if (options.EphemeralSecret)
{
    app.Logger.LogWarning("Running with an ephemeral SECRET_KEY: issued tokens become invalid on restart.");
}
app.Logger.LogInformation("Paperless MCP server {Version} – public URL: {PublicUrl} – mode: {Mode}",
    AppInfo.Version, options.PublicUrl?.AbsoluteUri ?? "derived from requests (Host / X-Forwarded-*)",
    options.PaperlessUrl is null ? "users provide Paperless URL and API token" : $"preconfigured instance {options.PaperlessUrl}");

if (options.TrustForwardedHeaders)
{
    // Behind a reverse proxy (Traefik, Caddy, nginx, ...): take client IP, scheme and host from X-Forwarded-*.
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
        ForwardLimit = null,
    };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (HttpContext http) => Results.Json(new
{
    name = "paperless-mcp-server",
    version = AppInfo.Version,
    mcp_endpoint = options.GetMcpUrl(http.Request).AbsoluteUri,
    documentation = "https://github.com/mKenfenheuer/paperless-mcp-server",
}));
app.MapGet("/healthz", () => Results.Text("ok"));
app.MapOAuthEndpoints();
app.MapMcp("/mcp").RequireAuthorization();

app.Run();

public partial class Program;

internal static class AppInfo
{
    public static readonly string Version =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
