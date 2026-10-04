using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol;

namespace PaperlessMcpServer.Paperless;

/// <summary>
/// Error returned by (or while talking to) Paperless. Derives from <see cref="McpException"/>
/// so the message is passed through to the MCP client as a tool error.
/// </summary>
public sealed class PaperlessApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : McpException(message, inner!)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>Thin client for the Paperless-ngx REST API, authenticated with a user's API token.</summary>
public sealed class PaperlessClient
{
    public const string HttpClientName = "paperless";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private static readonly TimeSpan MetadataCacheDuration = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly IMemoryCache _cache;
    private readonly string _cachePrefix;

    public PaperlessClient(HttpClient http, Uri baseUrl, string token, IMemoryCache cache)
    {
        _http = http;
        BaseUrl = baseUrl;
        _token = token;
        _cache = cache;
        _cachePrefix = $"paperless|{baseUrl}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16]}|";
    }

    /// <summary>Base URL of the Paperless instance, ending with "/".</summary>
    public Uri BaseUrl { get; }

    /// <summary>Paperless version as reported by the X-Version response header, if seen.</summary>
    public string? ServerVersion { get; private set; }

    /// <summary>REST API version as reported by the X-Api-Version response header, if seen.</summary>
    public int? ApiVersion { get; private set; }

    /// <summary>Link to a document in the Paperless web UI.</summary>
    public string DocumentUrl(int id) => new Uri(BaseUrl, $"documents/{id}/details").AbsoluteUri;

    /// <summary>Exchanges username and password for the user's API token (POST /api/token/).</summary>
    public static async Task<string> ObtainTokenAsync(HttpClient http, Uri baseUrl, string username, string password, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "api/token/"))
        {
            Content = JsonContent(new { username, password }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new PaperlessApiException($"Could not reach Paperless at {baseUrl}: {e.Message}", inner: e);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new PaperlessApiException("Invalid username or password.", response.StatusCode);
            }
            await EnsureSuccessAsync(response, request);
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            return body?["token"]?.GetValue<string>()
                ?? throw new PaperlessApiException("Paperless did not return an API token.");
        }
    }

    // ------------------------------------------------------------------ general

    public async Task<PaperlessUser> GetCurrentUserAsync(CancellationToken ct)
    {
        var settings = await GetAsync<JsonObject>("api/ui_settings/", null, ct);
        var user = settings["user"]?.Deserialize<PaperlessUser>(JsonOptions)
            ?? throw new PaperlessApiException("Paperless did not return user information.");
        return user;
    }

    public Task<JsonObject> GetStatisticsAsync(CancellationToken ct) => GetAsync<JsonObject>("api/statistics/", null, ct);

    // ---------------------------------------------------------------- documents

    public Task<Paged<PaperlessDocument>> ListDocumentsAsync(IDictionary<string, string?> query, CancellationToken ct) =>
        GetAsync<Paged<PaperlessDocument>>("api/documents/", query, ct);

    public Task<PaperlessDocument> GetDocumentAsync(int id, CancellationToken ct) =>
        GetAsync<PaperlessDocument>($"api/documents/{id}/", null, ct);

    public Task<PaperlessDocument> UpdateDocumentAsync(int id, JsonObject patch, CancellationToken ct) =>
        SendJsonAsync<PaperlessDocument>(HttpMethod.Patch, $"api/documents/{id}/", patch, ct);

    public async Task DeleteDocumentAsync(int id, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, $"api/documents/{id}/", null, null, ct);
    }

    public Task<JsonNode?> BulkEditAsync(IReadOnlyList<int> documents, string method, JsonObject parameters, CancellationToken ct) =>
        SendJsonAsync<JsonNode?>(HttpMethod.Post, "api/documents/bulk_edit/", new JsonObject
        {
            ["documents"] = new JsonArray(documents.Select(d => (JsonNode)d).ToArray()),
            ["method"] = method,
            ["parameters"] = parameters,
        }, ct);

    public async Task<DownloadedFile> DownloadDocumentAsync(int id, bool original, long maxBytes, CancellationToken ct)
    {
        var query = original ? new Dictionary<string, string?> { ["original"] = "true" } : null;
        using var response = await SendAsync(HttpMethod.Get, $"api/documents/{id}/download/", query, null, ct, HttpCompletionOption.ResponseHeadersRead);
        return await ReadFileAsync(response, $"document-{id}", maxBytes, ct);
    }

    public async Task<DownloadedFile> GetThumbnailAsync(int id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/documents/{id}/thumb/", null, null, ct, HttpCompletionOption.ResponseHeadersRead);
        return await ReadFileAsync(response, $"thumbnail-{id}", 10 * 1024 * 1024, ct);
    }

    /// <summary>Uploads a document for consumption. Returns the consumption task id.</summary>
    public async Task<string> UploadDocumentAsync(byte[] data, string fileName, string? contentType, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/octet-stream");
        form.Add(file, "document", fileName);
        foreach (var (key, value) in fields)
        {
            form.Add(new StringContent(value), key);
        }

        using var response = await SendAsync(HttpMethod.Post, "api/documents/post_document/", null, form, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var node = JsonNode.Parse(text);
            return node switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonObject o when o["task_id"] is { } id => id.ToString(),
                _ => text.Trim('"'),
            };
        }
        catch (JsonException)
        {
            return text.Trim().Trim('"');
        }
    }

    public async Task<PaperlessTask?> GetTaskAsync(string taskId, CancellationToken ct)
    {
        var node = await GetAsync<JsonNode>("api/tasks/", new Dictionary<string, string?> { ["task_id"] = taskId }, ct);
        var array = node as JsonArray ?? node["results"] as JsonArray;
        return array is { Count: > 0 } ? array[0].Deserialize<PaperlessTask>(JsonOptions) : null;
    }

    // -------------------------------------------------------------------- notes

    public async Task<List<PaperlessNote>> GetNotesAsync(int documentId, CancellationToken ct)
    {
        var node = await GetAsync<JsonNode>($"api/documents/{documentId}/notes/", null, ct);
        var array = node as JsonArray ?? node["results"] as JsonArray ?? [];
        return array.Deserialize<List<PaperlessNote>>(JsonOptions) ?? [];
    }

    public Task<List<PaperlessNote>> AddNoteAsync(int documentId, string note, CancellationToken ct) =>
        SendJsonAsync<List<PaperlessNote>>(HttpMethod.Post, $"api/documents/{documentId}/notes/", new JsonObject { ["note"] = note }, ct);

    public async Task DeleteNoteAsync(int documentId, int noteId, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, $"api/documents/{documentId}/notes/",
            new Dictionary<string, string?> { ["id"] = noteId.ToString() }, null, ct);
    }

    // ----------------------------------------------------------------- metadata

    /// <summary>Returns all objects of a metadata kind. Results are cached for a short time.</summary>
    public async Task<IReadOnlyList<MetadataItem>> ListAllAsync(MetadataKind kind, CancellationToken ct)
    {
        var key = _cachePrefix + kind;
        if (_cache.TryGetValue(key, out IReadOnlyList<MetadataItem>? cached) && cached is not null) return cached;

        var items = new List<MetadataItem>();
        for (var page = 1; ; page++)
        {
            var result = await GetAsync<Paged<JsonObject>>($"api/{kind.ApiPath()}/",
                new Dictionary<string, string?> { ["page"] = page.ToString(), ["page_size"] = "1000" }, ct);
            items.AddRange(result.Results.Select(ToItem));
            if (result.Next is null || result.Results.Count == 0) break;
        }

        _cache.Set(key, (IReadOnlyList<MetadataItem>)items, MetadataCacheDuration);
        return items;
    }

    public async Task<MetadataItem> CreateObjectAsync(MetadataKind kind, JsonObject data, CancellationToken ct)
    {
        var created = await SendJsonAsync<JsonObject>(HttpMethod.Post, $"api/{kind.ApiPath()}/", data, ct);
        Invalidate(kind);
        return ToItem(created);
    }

    public async Task<MetadataItem> UpdateObjectAsync(MetadataKind kind, int id, JsonObject data, CancellationToken ct)
    {
        var updated = await SendJsonAsync<JsonObject>(HttpMethod.Patch, $"api/{kind.ApiPath()}/{id}/", data, ct);
        Invalidate(kind);
        return ToItem(updated);
    }

    public async Task DeleteObjectAsync(MetadataKind kind, int id, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, $"api/{kind.ApiPath()}/{id}/", null, null, ct);
        Invalidate(kind);
    }

    public void Invalidate(MetadataKind kind) => _cache.Remove(_cachePrefix + kind);

    private static MetadataItem ToItem(JsonObject obj) =>
        new(obj["id"]!.GetValue<int>(), obj["name"]?.GetValue<string>() ?? "", obj);

    // ------------------------------------------------------------------ plumbing

    private async Task<T> GetAsync<T>(string path, IDictionary<string, string?>? query, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, query, null, ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, JsonNode body, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, null, JsonContent(body), ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) return default!;
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)!;
        }
        catch (JsonException e)
        {
            throw new PaperlessApiException($"Unexpected response from Paperless ({response.RequestMessage?.RequestUri?.AbsolutePath}): {e.Message}", inner: e);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        IDictionary<string, string?>? query,
        HttpContent? content,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var url = new Uri(BaseUrl, path).AbsoluteUri;
        if (query is not null)
        {
            url = QueryHelpers.AddQueryString(url, query.Where(kv => !string.IsNullOrEmpty(kv.Value)));
        }

        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, ct);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new PaperlessApiException($"Request to Paperless timed out ({method} {request.RequestUri!.AbsolutePath}).", inner: e);
        }
        catch (HttpRequestException e)
        {
            throw new PaperlessApiException($"Could not reach Paperless at {BaseUrl}: {e.Message}", inner: e);
        }

        if (response.Headers.TryGetValues("X-Version", out var versions))
        {
            ServerVersion = versions.FirstOrDefault();
        }
        if (response.Headers.TryGetValues("X-Api-Version", out var apiVersions) && int.TryParse(apiVersions.FirstOrDefault(), out var apiVersion))
        {
            ApiVersion = apiVersion;
        }

        try
        {
            await EnsureSuccessAsync(response, request);
        }
        catch
        {
            response.Dispose();
            throw;
        }
        return response;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, HttpRequestMessage request)
    {
        if (response.IsSuccessStatusCode) return;

        var status = (int)response.StatusCode;
        var path = $"{request.Method} {request.RequestUri?.AbsolutePath}";
        if (status is >= 300 and < 400)
        {
            throw new PaperlessApiException(
                $"Paperless redirected {path} to {response.Headers.Location}. Check the configured Paperless URL (scheme, host and path).",
                response.StatusCode);
        }

        var detail = DescribeError(await response.Content.ReadAsStringAsync());
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Paperless rejected the API token (HTTP 401). Sign in to the MCP server again.",
            HttpStatusCode.Forbidden => $"Permission denied by Paperless (HTTP 403) for {path}{(detail is null ? "" : $": {detail}")}",
            HttpStatusCode.NotFound => $"Not found in Paperless (HTTP 404): {path}",
            _ => $"Paperless returned HTTP {status} for {path}{(detail is null ? "" : $": {detail}")}",
        };
        throw new PaperlessApiException(message, response.StatusCode);
    }

    private static string? DescribeError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject obj)
            {
                if (obj["detail"] is JsonValue detail) return detail.ToString();
                return string.Join("; ", obj.Select(kv => $"{kv.Key}: {Flatten(kv.Value)}"));
            }
            return Truncate(Flatten(node), 500);
        }
        catch (JsonException)
        {
            // Probably an HTML error page.
            return body.TrimStart().StartsWith('<') ? null : Truncate(body, 500);
        }

        static string Flatten(JsonNode? n) => n switch
        {
            JsonArray a => string.Join(" ", a.Select(Flatten)),
            JsonObject o => string.Join("; ", o.Select(kv => $"{kv.Key}: {Flatten(kv.Value)}")),
            null => "null",
            _ => n.ToString(),
        };
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private static async Task<DownloadedFile> ReadFileAsync(HttpResponseMessage response, string fallbackName, long maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            throw new PaperlessApiException($"The file is {length / 1024 / 1024.0:F1} MB, which exceeds the limit of {maxBytes / 1024 / 1024} MB.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                throw new PaperlessApiException($"The file exceeds the limit of {maxBytes / 1024 / 1024} MB.");
            }
        }

        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"') ?? fallbackName;
        return new DownloadedFile(buffer.ToArray(), response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", fileName);
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");
}
