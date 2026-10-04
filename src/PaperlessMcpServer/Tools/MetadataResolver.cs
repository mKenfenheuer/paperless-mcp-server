using System.Text.Json.Nodes;
using ModelContextProtocol;
using PaperlessMcpServer.Paperless;

namespace PaperlessMcpServer.Tools;

/// <summary>
/// Translates between names and IDs of tags, correspondents, document types, storage paths
/// and custom fields, so tools can accept human-readable names.
/// </summary>
public sealed class MetadataResolver(PaperlessClient client)
{
    public Task<IReadOnlyList<MetadataItem>> AllAsync(MetadataKind kind, CancellationToken ct) => client.ListAllAsync(kind, ct);

    public async Task<IReadOnlyDictionary<int, string>> NamesAsync(MetadataKind kind, CancellationToken ct) =>
        (await client.ListAllAsync(kind, ct)).ToDictionary(i => i.Id, i => i.Name);

    public async Task<MetadataItem?> FindAsync(MetadataKind kind, string value, CancellationToken ct)
    {
        value = value.Trim();
        var all = await client.ListAllAsync(kind, ct);
        // Names win over IDs, so a tag literally named "2024" is found by name.
        return all.FirstOrDefault(i => string.Equals(i.Name, value, StringComparison.OrdinalIgnoreCase))
               ?? (int.TryParse(value, out var id) ? all.FirstOrDefault(i => i.Id == id) : null);
    }

    /// <summary>Resolves a name or ID. Optionally creates the object if no match exists.</summary>
    public async Task<MetadataItem> ResolveAsync(MetadataKind kind, string value, bool createMissing, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new McpException($"Empty {kind.Singular()} name.");
        }
        var found = await FindAsync(kind, value, ct);
        if (found is not null) return found;

        if (createMissing && kind != MetadataKind.CustomFields && !int.TryParse(value, out _))
        {
            return await client.CreateObjectAsync(kind, new JsonObject { ["name"] = value.Trim() }, ct);
        }

        var all = await client.ListAllAsync(kind, ct);
        var suggestions = all
            .Where(i => i.Name.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase) || value.Contains(i.Name, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Name)
            .Take(10)
            .ToList();
        var hint = suggestions.Count > 0
            ? $" Did you mean: {string.Join(", ", suggestions.Select(s => $"\"{s}\""))}?"
            : $" Use list_metadata with kind \"{kind.ApiPath()}\" to see available values"
              + (kind == MetadataKind.CustomFields ? "." : ", or set create_missing_metadata to create it.");
        throw new McpException($"Unknown {kind.Singular()} \"{value}\".{hint}");
    }

    public async Task<int[]> ResolveManyAsync(MetadataKind kind, IEnumerable<string>? values, bool createMissing, CancellationToken ct)
    {
        if (values is null) return [];
        var ids = new List<int>();
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            ids.Add((await ResolveAsync(kind, value, createMissing, ct)).Id);
        }
        return [.. ids.Distinct()];
    }

    public async Task<int?> ResolveOptionalAsync(MetadataKind kind, string? value, bool createMissing, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(value) ? null : (await ResolveAsync(kind, value, createMissing, ct)).Id;
}
