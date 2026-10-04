using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PaperlessMcpServer.Paperless;

public sealed record Paged<T>(int Count, string? Next, string? Previous, List<T> Results);

public sealed record PaperlessUser(int Id, string Username, string? FirstName, string? LastName);

public sealed record PaperlessNote(int Id, string Note, string? Created, JsonElement? User);

public sealed record CustomFieldInstance(int Field, JsonElement? Value);

public sealed record SearchHit(double? Score, string? Highlights, string? NoteHighlights, int? Rank);

public sealed record PaperlessDocument(
    int Id,
    string Title,
    string? Content,
    int? Correspondent,
    int? DocumentType,
    int? StoragePath,
    int[]? Tags,
    string? Created,
    string? Modified,
    string? Added,
    int? ArchiveSerialNumber,
    string? OriginalFileName,
    string? ArchivedFileName,
    int? Owner,
    List<PaperlessNote>? Notes,
    List<CustomFieldInstance>? CustomFields,
    int? PageCount,
    string? MimeType,
    [property: JsonPropertyName("__search_hit__")] SearchHit? SearchHit);

public sealed record PaperlessTask(
    int Id,
    string TaskId,
    string? TaskFileName,
    string? TaskName,
    string? DateCreated,
    string? DateDone,
    string? Type,
    string? Status,
    string? Result,
    JsonElement? RelatedDocument)
{
    public int? RelatedDocumentId => RelatedDocument switch
    {
        { ValueKind: JsonValueKind.Number } e => e.GetInt32(),
        { ValueKind: JsonValueKind.String } e when int.TryParse(e.GetString(), out var id) => id,
        _ => null,
    };
}

/// <summary>A tag, correspondent, document type, storage path or custom field.</summary>
public sealed record MetadataItem(int Id, string Name, JsonObject Raw);

public sealed record DownloadedFile(byte[] Data, string ContentType, string FileName);

public enum MetadataKind
{
    Tags,
    Correspondents,
    DocumentTypes,
    StoragePaths,
    CustomFields,
}

public static class MetadataKindExtensions
{
    public static string ApiPath(this MetadataKind kind) => kind switch
    {
        MetadataKind.Tags => "tags",
        MetadataKind.Correspondents => "correspondents",
        MetadataKind.DocumentTypes => "document_types",
        MetadataKind.StoragePaths => "storage_paths",
        MetadataKind.CustomFields => "custom_fields",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Singular(this MetadataKind kind) => kind switch
    {
        MetadataKind.Tags => "tag",
        MetadataKind.Correspondents => "correspondent",
        MetadataKind.DocumentTypes => "document type",
        MetadataKind.StoragePaths => "storage path",
        MetadataKind.CustomFields => "custom field",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryParse(string? value, out MetadataKind kind)
    {
        switch (value?.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_'))
        {
            case "tag" or "tags":
                kind = MetadataKind.Tags;
                return true;
            case "correspondent" or "correspondents":
                kind = MetadataKind.Correspondents;
                return true;
            case "document_type" or "document_types" or "type" or "types":
                kind = MetadataKind.DocumentTypes;
                return true;
            case "storage_path" or "storage_paths":
                kind = MetadataKind.StoragePaths;
                return true;
            case "custom_field" or "custom_fields":
                kind = MetadataKind.CustomFields;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
