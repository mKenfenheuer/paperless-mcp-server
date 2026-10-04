using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using PaperlessMcpServer.Paperless;

namespace PaperlessMcpServer.Tools;

/// <summary>Turns Paperless API objects into compact, name-resolved JSON for the model.</summary>
public sealed partial class DocumentFormatter(PaperlessClient client, MetadataResolver resolver)
{
    private const int ExcerptLength = 240;

    public sealed record Lookups(
        IReadOnlyDictionary<int, string> Tags,
        IReadOnlyDictionary<int, string> Correspondents,
        IReadOnlyDictionary<int, string> DocumentTypes,
        IReadOnlyDictionary<int, string> StoragePaths);

    public async Task<Lookups> LoadLookupsAsync(CancellationToken ct)
    {
        var tags = resolver.NamesAsync(MetadataKind.Tags, ct);
        var correspondents = resolver.NamesAsync(MetadataKind.Correspondents, ct);
        var types = resolver.NamesAsync(MetadataKind.DocumentTypes, ct);
        var paths = resolver.NamesAsync(MetadataKind.StoragePaths, ct);
        await Task.WhenAll(tags, correspondents, types, paths);
        return new Lookups(tags.Result, correspondents.Result, types.Result, paths.Result);
    }

    public JsonObject Summary(PaperlessDocument doc, Lookups lookups)
    {
        var result = new JsonObject
        {
            ["id"] = doc.Id,
            ["title"] = doc.Title,
            ["created"] = DateOnlyString(doc.Created),
            ["correspondent"] = Name(lookups.Correspondents, doc.Correspondent),
            ["document_type"] = Name(lookups.DocumentTypes, doc.DocumentType),
            ["tags"] = new JsonArray((doc.Tags ?? []).Select(t => (JsonNode?)Name(lookups.Tags, t)).ToArray()),
        };
        if (doc.StoragePath is not null) result["storage_path"] = Name(lookups.StoragePaths, doc.StoragePath);
        if (doc.ArchiveSerialNumber is not null) result["archive_serial_number"] = doc.ArchiveSerialNumber;
        if (doc.PageCount is not null) result["page_count"] = doc.PageCount;

        if (doc.SearchHit is { } hit)
        {
            if (hit.Score is not null) result["score"] = Math.Round(hit.Score.Value, 3);
            var highlights = CleanHighlights(hit.Highlights);
            if (!string.IsNullOrEmpty(highlights)) result["excerpt"] = highlights;
            var noteHighlights = CleanHighlights(hit.NoteHighlights);
            if (!string.IsNullOrEmpty(noteHighlights)) result["note_excerpt"] = noteHighlights;
        }
        else if (!string.IsNullOrWhiteSpace(doc.Content))
        {
            result["excerpt"] = Excerpt(doc.Content);
        }
        return result;
    }

    public async Task<JsonObject> DetailsAsync(PaperlessDocument doc, CancellationToken ct)
    {
        var lookups = await LoadLookupsAsync(ct);
        var result = Summary(doc with { SearchHit = null, Content = null }, lookups);
        result["created"] = doc.Created;
        result["added"] = doc.Added;
        result["modified"] = doc.Modified;
        result["original_file_name"] = doc.OriginalFileName;
        if (doc.ArchivedFileName is not null) result["archived_file_name"] = doc.ArchivedFileName;
        if (doc.MimeType is not null) result["mime_type"] = doc.MimeType;
        result["content_length"] = doc.Content?.Length ?? 0;
        result["url"] = client.DocumentUrl(doc.Id);

        if (doc.CustomFields is { Count: > 0 } fields)
        {
            var definitions = (await resolver.AllAsync(MetadataKind.CustomFields, ct)).ToDictionary(f => f.Id);
            result["custom_fields"] = new JsonArray(fields.Select(f =>
            {
                definitions.TryGetValue(f.Field, out var definition);
                return (JsonNode)new JsonObject
                {
                    ["field_id"] = f.Field,
                    ["name"] = definition?.Name ?? $"#{f.Field}",
                    ["data_type"] = definition?.Raw["data_type"]?.DeepClone(),
                    ["value"] = CustomFieldValues.Display(definition, f.Value),
                };
            }).ToArray());
        }

        if (doc.Notes is { Count: > 0 } notes)
        {
            result["notes"] = new JsonArray(notes.Select(n => (JsonNode)FormatNote(n)).ToArray());
        }
        return result;
    }

    public static JsonObject FormatNote(PaperlessNote note) => new()
    {
        ["id"] = note.Id,
        ["note"] = note.Note,
        ["created"] = note.Created,
        ["user"] = note.User switch
        {
            { ValueKind: JsonValueKind.Object } u when u.TryGetProperty("username", out var name) => name.GetString(),
            { ValueKind: JsonValueKind.Number } u => u.GetInt32(),
            _ => null,
        },
    };

    private static string? Name(IReadOnlyDictionary<int, string> names, int? id) =>
        id is null ? null : names.TryGetValue(id.Value, out var name) ? name : $"#{id}";

    private static string? DateOnlyString(string? value) =>
        value is { Length: >= 10 } && DateTimeOffset.TryParse(value, out _) ? value[..10] : value;

    private static string Excerpt(string content)
    {
        var text = WhitespaceRegex().Replace(content, " ").Trim();
        return text.Length <= ExcerptLength ? text : text[..ExcerptLength] + "…";
    }

    private static string? CleanHighlights(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        // Paperless wraps matches in <span class="match">; mark them with ** instead.
        var text = MatchSpanRegex().Replace(html, "**$1**");
        text = TagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length <= 400 ? text : text[..400] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("<span[^>]*class=\"match[^\"]*\"[^>]*>(.*?)</span>", RegexOptions.Singleline)]
    private static partial Regex MatchSpanRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
}

/// <summary>Conversion of custom field values between model-friendly and API representations.</summary>
public static class CustomFieldValues
{
    /// <summary>Converts a stored value for display (e.g. select option ids to labels).</summary>
    public static JsonNode? Display(MetadataItem? definition, JsonElement? value)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
        var node = JsonNode.Parse(value.Value.GetRawText());
        if (definition?.Raw["data_type"]?.GetValue<string>() != "select") return node;

        var options = SelectOptions(definition);
        var raw = value.Value.ValueKind == JsonValueKind.Number ? value.Value.GetRawText() : value.Value.ToString();
        var match = options.FirstOrDefault(o => o.Id == raw);
        return match.Label is not null ? JsonValue.Create(match.Label) : node;
    }

    /// <summary>Converts a user-supplied value to what the Paperless API expects for this field.</summary>
    public static JsonNode? ToApi(MetadataItem definition, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var dataType = definition.Raw["data_type"]?.GetValue<string>();
        switch (dataType)
        {
            case "select":
            {
                var options = SelectOptions(definition);
                var raw = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                var match = options.FirstOrDefault(o => string.Equals(o.Label, raw, StringComparison.OrdinalIgnoreCase));
                if (match.Label is null) match = options.FirstOrDefault(o => o.Id == raw);
                if (match.Label is null)
                {
                    throw new McpException($"\"{raw}\" is not an option of custom field \"{definition.Name}\". Options: {string.Join(", ", options.Select(o => o.Label))}");
                }
                return int.TryParse(match.Id, out var index) && match.IsIndex ? JsonValue.Create(index) : JsonValue.Create(match.Id);
            }
            case "integer" when value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var l):
                return JsonValue.Create(l);
            case "float" when value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d):
                return JsonValue.Create(d);
            case "boolean" when value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b):
                return JsonValue.Create(b);
            case "documentlink" when value.ValueKind == JsonValueKind.Number:
                return new JsonArray(value.GetInt32());
            case "monetary" when value.ValueKind == JsonValueKind.Number:
                return JsonValue.Create(value.GetDecimal().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            default:
                return JsonNode.Parse(value.GetRawText());
        }
    }

    /// <summary>
    /// Select options are objects with id and label (API v7+) or plain strings referenced by index (older versions).
    /// </summary>
    private static List<(string Id, string? Label, bool IsIndex)> SelectOptions(MetadataItem definition)
    {
        var list = new List<(string, string?, bool)>();
        if (definition.Raw["extra_data"]?["select_options"] is not JsonArray options) return list;
        for (var i = 0; i < options.Count; i++)
        {
            switch (options[i])
            {
                case JsonObject o:
                    list.Add((o["id"]?.ToString() ?? i.ToString(), o["label"]?.ToString(), false));
                    break;
                case JsonValue v:
                    list.Add((i.ToString(), v.ToString(), true));
                    break;
            }
        }
        return list;
    }
}
