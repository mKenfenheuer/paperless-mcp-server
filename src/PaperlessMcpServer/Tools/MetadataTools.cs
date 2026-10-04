using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PaperlessMcpServer.Paperless;

namespace PaperlessMcpServer.Tools;

[McpServerToolType]
public sealed class MetadataTools(PaperlessClient client, MetadataResolver resolver)
{
    private const string KindDescription = "One of: tags, correspondents, document_types, storage_paths, custom_fields.";

    private static readonly Dictionary<string, int> MatchingAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = 0,
        ["any"] = 1,
        ["all"] = 2,
        ["exact"] = 3,
        ["regex"] = 4,
        ["fuzzy"] = 5,
        ["auto"] = 6,
    };

    private static readonly HashSet<string> CustomFieldTypes =
        ["string", "longtext", "url", "date", "boolean", "integer", "float", "monetary", "documentlink", "select"];

    [McpServerTool(Name = "list_metadata", Title = "List tags, correspondents, types, …", ReadOnly = true, OpenWorld = false)]
    [Description("List tags, correspondents, document types, storage paths or custom fields with their IDs and document counts.")]
    public async Task<JsonObject> ListMetadata(
        [Description(KindDescription)] string kind,
        [Description("Only items whose name contains this text (case-insensitive).")] string? name_contains = null,
        CancellationToken ct = default)
    {
        var metadataKind = ParseKind(kind);
        var items = (await resolver.AllAsync(metadataKind, ct))
            .Where(i => string.IsNullOrWhiteSpace(name_contains) || i.Name.Contains(name_contains.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        IReadOnlyDictionary<int, string>? tagNames = metadataKind == MetadataKind.Tags ? items.ToDictionary(i => i.Id, i => i.Name) : null;

        return new JsonObject
        {
            ["kind"] = metadataKind.ApiPath(),
            ["count"] = items.Count,
            ["items"] = new JsonArray(items.Select(i => (JsonNode)Format(metadataKind, i, tagNames)).ToArray()),
        };
    }

    [McpServerTool(Name = "create_metadata", Title = "Create tag, correspondent, type, …", Destructive = false, OpenWorld = false)]
    [Description("""
        Create a tag, correspondent, document type, storage path or custom field.
        Storage paths need `path` (a Paperless path template such as "{{ correspondent }}/{{ created_year }}/{{ title }}").
        Custom fields need `data_type` (string, longtext, url, date, boolean, integer, float, monetary, documentlink, select);
        select fields also need `select_options`.
        """)]
    public async Task<JsonObject> CreateMetadata(
        [Description(KindDescription)] string kind,
        [Description("Name of the new item.")] string name,
        [Description("Tags only: color as hex, e.g. \"#a6cee3\".")] string? color = null,
        [Description("Tags only: mark as inbox tag (added to all new documents).")] bool? is_inbox_tag = null,
        [Description("Tags only: parent tag name or ID (Paperless 2.19+ tag hierarchy).")] string? parent_tag = null,
        [Description("Storage paths only: path template.")] string? path = null,
        [Description("Custom fields only: data type.")] string? data_type = null,
        [Description("Custom fields of type select only: option labels.")] string[]? select_options = null,
        [Description("Automatic assignment: none, any, all, exact, regex, fuzzy or auto (machine learning). Not for custom fields.")] string? matching_algorithm = null,
        [Description("Match pattern used by the matching algorithm.")] string? match = null,
        [Description("Whether matching is case-insensitive.")] bool? case_insensitive = null,
        CancellationToken ct = default)
    {
        var metadataKind = ParseKind(kind);
        if (string.IsNullOrWhiteSpace(name)) throw new McpException("name is required.");
        if (await resolver.FindAsync(metadataKind, name, ct) is { } existing && existing.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException($"A {metadataKind.Singular()} named \"{existing.Name}\" already exists (id {existing.Id}).");
        }

        var data = new JsonObject { ["name"] = name.Trim() };
        await ApplyCommonAsync(metadataKind, data, color, is_inbox_tag, parent_tag, path, matching_algorithm, match, case_insensitive, ct);
        if (metadataKind == MetadataKind.StoragePaths && string.IsNullOrWhiteSpace(path))
        {
            throw new McpException("path is required for storage paths.");
        }
        if (metadataKind == MetadataKind.CustomFields)
        {
            var type = data_type?.Trim().ToLowerInvariant();
            if (type is null || !CustomFieldTypes.Contains(type))
            {
                throw new McpException($"data_type is required for custom fields. Use one of: {string.Join(", ", CustomFieldTypes)}.");
            }
            data["data_type"] = type;
            if (type == "select")
            {
                if (select_options is not { Length: > 0 }) throw new McpException("select_options is required for select fields.");
                data["extra_data"] = new JsonObject { ["select_options"] = SelectOptionsPayload(select_options, null) };
            }
        }

        var created = await client.CreateObjectAsync(metadataKind, data, ct);
        return new JsonObject { ["created"] = Format(metadataKind, created, null) };
    }

    [McpServerTool(Name = "update_metadata", Title = "Update tag, correspondent, type, …", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rename or change a tag, correspondent, document type, storage path or custom field. Only the given properties change.")]
    public async Task<JsonObject> UpdateMetadata(
        [Description(KindDescription)] string kind,
        [Description("Current name or ID of the item.")] string item,
        [Description("New name.")] string? new_name = null,
        [Description("Tags only: color as hex, e.g. \"#a6cee3\".")] string? color = null,
        [Description("Tags only: mark as inbox tag.")] bool? is_inbox_tag = null,
        [Description("Tags only: parent tag name or ID; \"none\" removes the parent.")] string? parent_tag = null,
        [Description("Storage paths only: path template.")] string? path = null,
        [Description("Custom fields of type select only: the complete list of option labels. Existing labels keep their identity.")] string[]? select_options = null,
        [Description("Automatic assignment: none, any, all, exact, regex, fuzzy or auto.")] string? matching_algorithm = null,
        [Description("Match pattern used by the matching algorithm.")] string? match = null,
        [Description("Whether matching is case-insensitive.")] bool? case_insensitive = null,
        CancellationToken ct = default)
    {
        var metadataKind = ParseKind(kind);
        var current = await resolver.ResolveAsync(metadataKind, item, false, ct);

        var data = new JsonObject();
        if (!string.IsNullOrWhiteSpace(new_name)) data["name"] = new_name.Trim();
        await ApplyCommonAsync(metadataKind, data, color, is_inbox_tag, parent_tag, path, matching_algorithm, match, case_insensitive, ct);
        if (select_options is { Length: > 0 })
        {
            if (metadataKind != MetadataKind.CustomFields || current.Raw["data_type"]?.GetValue<string>() != "select")
            {
                throw new McpException("select_options only applies to custom fields of type select.");
            }
            var extra = current.Raw["extra_data"]?.DeepClone() as JsonObject ?? new JsonObject();
            extra["select_options"] = SelectOptionsPayload(select_options, current.Raw["extra_data"]?["select_options"] as JsonArray);
            data["extra_data"] = extra;
        }
        if (data.Count == 0) throw new McpException("Nothing to update.");

        var updated = await client.UpdateObjectAsync(metadataKind, current.Id, data, ct);
        return new JsonObject { ["updated"] = Format(metadataKind, updated, null) };
    }

    [McpServerTool(Name = "delete_metadata", Title = "Delete tag, correspondent, type, …", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Delete a tag, correspondent, document type, storage path or custom field. Documents keep existing but lose this assignment. Ask the user for confirmation first.")]
    public async Task<JsonObject> DeleteMetadata(
        [Description(KindDescription)] string kind,
        [Description("Name or ID of the item.")] string item,
        CancellationToken ct = default)
    {
        var metadataKind = ParseKind(kind);
        var current = await resolver.ResolveAsync(metadataKind, item, false, ct);
        await client.DeleteObjectAsync(metadataKind, current.Id, ct);
        return new JsonObject
        {
            ["deleted"] = true,
            ["kind"] = metadataKind.ApiPath(),
            ["id"] = current.Id,
            ["name"] = current.Name,
            ["affected_documents"] = current.Raw["document_count"]?.DeepClone(),
        };
    }

    [McpServerTool(Name = "get_instance_info", Title = "Paperless instance info", ReadOnly = true, OpenWorld = false)]
    [Description("Show the connected Paperless instance, the signed-in user, the Paperless version and statistics (document counts, inbox, file types).")]
    public async Task<JsonObject> GetInstanceInfo(CancellationToken ct = default)
    {
        var user = await client.GetCurrentUserAsync(ct);
        var statistics = await client.GetStatisticsAsync(ct);
        return new JsonObject
        {
            ["url"] = client.BaseUrl.AbsoluteUri,
            ["user"] = new JsonObject
            {
                ["id"] = user.Id,
                ["username"] = user.Username,
                ["name"] = $"{user.FirstName} {user.LastName}".Trim() is { Length: > 0 } n ? n : null,
            },
            ["paperless_version"] = client.ServerVersion,
            ["api_version"] = client.ApiVersion,
            ["statistics"] = statistics,
        };
    }

    // ------------------------------------------------------------------ helpers

    private async Task ApplyCommonAsync(
        MetadataKind kind, JsonObject data, string? color, bool? is_inbox_tag, string? parent_tag, string? path,
        string? matching_algorithm, string? match, bool? case_insensitive, CancellationToken ct)
    {
        if (kind != MetadataKind.Tags && (color is not null || is_inbox_tag is not null || parent_tag is not null))
        {
            throw new McpException("color, is_inbox_tag and parent_tag only apply to tags.");
        }
        if (kind != MetadataKind.StoragePaths && path is not null)
        {
            throw new McpException("path only applies to storage paths.");
        }
        if (kind == MetadataKind.CustomFields && (matching_algorithm is not null || match is not null || case_insensitive is not null))
        {
            throw new McpException("Custom fields have no matching settings.");
        }

        if (color is not null) data["color"] = color.Trim();
        if (is_inbox_tag is not null) data["is_inbox_tag"] = is_inbox_tag;
        if (parent_tag is not null)
        {
            data["parent"] = parent_tag.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
                ? null
                : (await resolver.ResolveAsync(MetadataKind.Tags, parent_tag, false, ct)).Id;
        }
        if (path is not null) data["path"] = path;
        if (matching_algorithm is not null)
        {
            if (!MatchingAlgorithms.TryGetValue(matching_algorithm.Trim(), out var algorithm))
            {
                throw new McpException($"Unknown matching_algorithm \"{matching_algorithm}\". Use one of: {string.Join(", ", MatchingAlgorithms.Keys)}.");
            }
            data["matching_algorithm"] = algorithm;
        }
        if (match is not null) data["match"] = match;
        if (case_insensitive is not null) data["is_insensitive"] = case_insensitive;
    }

    /// <summary>
    /// Builds select options in the format of the server's API version: objects with id/label (API v7+)
    /// or plain strings. Existing option ids are kept for labels that already exist.
    /// </summary>
    private JsonArray SelectOptionsPayload(string[] labels, JsonArray? existing)
    {
        var useObjects = client.ApiVersion is null or >= 7 || existing?.FirstOrDefault() is JsonObject;
        if (!useObjects) return new JsonArray(labels.Select(l => (JsonNode)l.Trim()).ToArray());

        var existingIds = (existing ?? [])
            .OfType<JsonObject>()
            .Where(o => o["label"] is not null)
            .GroupBy(o => o["label"]!.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()["id"]?.DeepClone(), StringComparer.OrdinalIgnoreCase);
        return new JsonArray(labels.Select(l =>
        {
            var option = new JsonObject { ["label"] = l.Trim() };
            if (existingIds.TryGetValue(l.Trim(), out var id) && id is not null) option["id"] = id;
            return (JsonNode)option;
        }).ToArray());
    }

    private static JsonObject Format(MetadataKind kind, MetadataItem item, IReadOnlyDictionary<int, string>? tagNames)
    {
        var raw = item.Raw;
        var result = new JsonObject { ["id"] = item.Id, ["name"] = item.Name };
        if (raw["document_count"] is { } count) result["document_count"] = count.DeepClone();

        switch (kind)
        {
            case MetadataKind.Tags:
                if (raw["color"] is { } color) result["color"] = color.DeepClone();
                if (raw["is_inbox_tag"]?.GetValue<bool>() == true) result["is_inbox_tag"] = true;
                if (raw["parent"] is JsonValue parent && parent.TryGetValue<int>(out var parentId))
                {
                    result["parent"] = tagNames is not null && tagNames.TryGetValue(parentId, out var parentName) ? parentName : parentId;
                }
                break;
            case MetadataKind.StoragePaths:
                result["path"] = raw["path"]?.DeepClone();
                break;
            case MetadataKind.CustomFields:
                result["data_type"] = raw["data_type"]?.DeepClone();
                if (raw["extra_data"]?["select_options"] is JsonArray options && options.Count > 0)
                {
                    result["select_options"] = new JsonArray(options
                        .Select(o => (JsonNode?)(o is JsonObject obj ? obj["label"]?.ToString() : o?.ToString()))
                        .ToArray());
                }
                if (raw["extra_data"]?["default_currency"] is JsonValue currency) result["default_currency"] = currency.DeepClone();
                break;
        }

        if (kind != MetadataKind.CustomFields && raw["matching_algorithm"] is JsonValue algorithm && algorithm.TryGetValue<int>(out var a))
        {
            var name = MatchingAlgorithms.FirstOrDefault(kv => kv.Value == a).Key;
            if (name is not null && name != "none") result["matching"] = name;
            if (raw["match"]?.ToString() is { Length: > 0 } match) result["match"] = match;
        }
        return result;
    }

    private static MetadataKind ParseKind(string kind) =>
        MetadataKindExtensions.TryParse(kind, out var parsed)
            ? parsed
            : throw new McpException($"Unknown kind \"{kind}\". {KindDescription}");
}
