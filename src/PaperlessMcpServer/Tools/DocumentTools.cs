using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PaperlessMcpServer.Configuration;
using PaperlessMcpServer.Paperless;

namespace PaperlessMcpServer.Tools;

[McpServerToolType]
public sealed class DocumentTools(
    PaperlessClient client,
    MetadataResolver resolver,
    DocumentFormatter formatter,
    ServerOptions options,
    TimeProvider time)
{
    private const int DefaultContentLength = 20_000;

    private static readonly Dictionary<string, string> SortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["created"] = "created",
        ["added"] = "added",
        ["modified"] = "modified",
        ["title"] = "title",
        ["correspondent"] = "correspondent__name",
        ["document_type"] = "document_type__name",
        ["archive_serial_number"] = "archive_serial_number",
        ["page_count"] = "page_count",
    };

    [McpServerTool(Name = "search_documents", Title = "Search documents", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Search and filter documents. Combine a full-text query with filters; all filters are optional.
        Returns compact summaries (id, title, created date, correspondent, type, tags, excerpt).
        Use get_document / get_document_content for details and full text.
        The full-text query supports Paperless/Whoosh syntax, e.g. `invoice AND 2024`, `"exact phrase"`,
        `title:receipt`, `correspondent:acme`, `tag:tax`, `created:[2024-01-01 to 2024-12-31]`, `notes:urgent`.
        """)]
    public async Task<JsonObject> SearchDocuments(
        [Description("Full-text query over content, title, notes and metadata.")] string? query = null,
        [Description("Only documents whose title contains this text (case-insensitive).")] string? title_contains = null,
        [Description("Documents must have ALL of these tags (names or IDs).")] string[]? tags = null,
        [Description("Documents must have AT LEAST ONE of these tags (names or IDs).")] string[]? any_tags = null,
        [Description("Exclude documents with any of these tags (names or IDs).")] string[]? exclude_tags = null,
        [Description("Correspondent name or ID.")] string? correspondent = null,
        [Description("Document type name or ID.")] string? document_type = null,
        [Description("Storage path name or ID.")] string? storage_path = null,
        [Description("Created on or after this date (YYYY-MM-DD).")] string? created_from = null,
        [Description("Created on or before this date (YYYY-MM-DD).")] string? created_to = null,
        [Description("Added to Paperless on or after this date (YYYY-MM-DD).")] string? added_from = null,
        [Description("Added to Paperless on or before this date (YYYY-MM-DD).")] string? added_to = null,
        [Description("Exact archive serial number (ASN).")] int? archive_serial_number = null,
        [Description("Only documents in the inbox (having an inbox tag).")] bool inbox_only = false,
        [Description("Find documents similar to this document ID.")] int? similar_to_document_id = null,
        [Description("Sort field: created, added, modified, title, correspondent, document_type, archive_serial_number, page_count. Prefix with '-' for descending. Defaults to relevance for full-text queries, otherwise -created.")] string? sort = null,
        [Description("Page number, starting at 1.")] int page = 1,
        [Description("Results per page (1-100).")] int page_size = 25,
        CancellationToken ct = default)
    {
        page_size = Math.Clamp(page_size, 1, 100);
        page = Math.Max(page, 1);
        var fullText = !string.IsNullOrWhiteSpace(query) || similar_to_document_id is not null;

        var q = new Dictionary<string, string?>
        {
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["page_size"] = page_size.ToString(CultureInfo.InvariantCulture),
            ["query"] = string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            ["more_like_id"] = similar_to_document_id?.ToString(CultureInfo.InvariantCulture),
            ["title__icontains"] = title_contains,
            ["archive_serial_number"] = archive_serial_number?.ToString(CultureInfo.InvariantCulture),
            ["is_in_inbox"] = inbox_only ? "true" : null,
            ["created__date__gte"] = ParseDate(created_from, nameof(created_from)),
            ["created__date__lte"] = ParseDate(created_to, nameof(created_to)),
            ["added__date__gte"] = ParseDate(added_from, nameof(added_from)),
            ["added__date__lte"] = ParseDate(added_to, nameof(added_to)),
            ["truncate_content"] = "true",
            ["fields"] = "id,title,content,correspondent,document_type,storage_path,tags,created,archive_serial_number,page_count"
                         + (fullText ? ",__search_hit__" : ""),
        };

        if (tags is { Length: > 0 }) q["tags__id__all"] = Join(await resolver.ResolveManyAsync(MetadataKind.Tags, tags, false, ct));
        if (any_tags is { Length: > 0 }) q["tags__id__in"] = Join(await resolver.ResolveManyAsync(MetadataKind.Tags, any_tags, false, ct));
        if (exclude_tags is { Length: > 0 }) q["tags__id__none"] = Join(await resolver.ResolveManyAsync(MetadataKind.Tags, exclude_tags, false, ct));
        if (correspondent is not null) q["correspondent__id"] = (await resolver.ResolveAsync(MetadataKind.Correspondents, correspondent, false, ct)).Id.ToString(CultureInfo.InvariantCulture);
        if (document_type is not null) q["document_type__id"] = (await resolver.ResolveAsync(MetadataKind.DocumentTypes, document_type, false, ct)).Id.ToString(CultureInfo.InvariantCulture);
        if (storage_path is not null) q["storage_path__id"] = (await resolver.ResolveAsync(MetadataKind.StoragePaths, storage_path, false, ct)).Id.ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(sort))
        {
            var descending = sort.Trim().StartsWith('-');
            var key = sort.Trim().TrimStart('-');
            if (!SortFields.TryGetValue(key, out var field))
            {
                throw new McpException($"Unknown sort field \"{key}\". Use one of: {string.Join(", ", SortFields.Keys)}.");
            }
            q["ordering"] = (descending ? "-" : "") + field;
        }
        else if (!fullText)
        {
            q["ordering"] = "-created";
        }

        var result = await client.ListDocumentsAsync(q, ct);
        var lookups = await formatter.LoadLookupsAsync(ct);
        return new JsonObject
        {
            ["total"] = result.Count,
            ["page"] = page,
            ["page_size"] = page_size,
            ["has_more"] = result.Next is not null,
            ["documents"] = new JsonArray(result.Results.Select(d => (JsonNode)formatter.Summary(d, lookups)).ToArray()),
        };
    }

    [McpServerTool(Name = "get_document", Title = "Get document details", ReadOnly = true, OpenWorld = false)]
    [Description("Get all metadata of a document: title, dates, correspondent, type, tags, storage path, ASN, custom fields, notes, file names and a link to the Paperless web UI. Optionally includes the beginning of the text content.")]
    public async Task<JsonObject> GetDocument(
        [Description("Document ID.")] int id,
        [Description("Also include the OCR/text content (first 20,000 characters; use get_document_content for paging).")] bool include_content = false,
        CancellationToken ct = default)
    {
        var doc = await client.GetDocumentAsync(id, ct);
        var details = await formatter.DetailsAsync(doc, ct);
        if (include_content)
        {
            var content = doc.Content ?? "";
            details["content"] = content.Length > DefaultContentLength ? content[..DefaultContentLength] : content;
            details["content_truncated"] = content.Length > DefaultContentLength;
        }
        return details;
    }

    [McpServerTool(Name = "get_document_content", Title = "Get document text", ReadOnly = true, OpenWorld = false)]
    [Description("Get the text content (OCR result) of a document. Long texts are returned in chunks; use offset with next_offset to continue reading.")]
    public async Task<JsonObject> GetDocumentContent(
        [Description("Document ID.")] int id,
        [Description("Character offset to start from.")] int offset = 0,
        [Description("Maximum number of characters to return (1-100000).")] int max_length = DefaultContentLength,
        CancellationToken ct = default)
    {
        var doc = await client.GetDocumentAsync(id, ct);
        var content = doc.Content ?? "";
        offset = Math.Clamp(offset, 0, content.Length);
        max_length = Math.Clamp(max_length, 1, 100_000);
        var length = Math.Min(max_length, content.Length - offset);
        var end = offset + length;

        var result = new JsonObject
        {
            ["id"] = doc.Id,
            ["title"] = doc.Title,
            ["total_length"] = content.Length,
            ["offset"] = offset,
            ["truncated"] = end < content.Length,
        };
        if (end < content.Length) result["next_offset"] = end;
        result["content"] = content.Substring(offset, length);
        return result;
    }

    [McpServerTool(Name = "download_document", Title = "Download document file", ReadOnly = true, OpenWorld = false)]
    [Description("Download the document file (archived PDF by default, or the original upload) as an embedded resource. Prefer get_document_content to read text; use this when the actual file is needed.")]
    public async Task<CallToolResult> DownloadDocument(
        [Description("Document ID.")] int id,
        [Description("Return the originally uploaded file instead of the archived (OCR'd PDF) version.")] bool original = false,
        CancellationToken ct = default)
    {
        var file = await client.DownloadDocumentAsync(id, original, options.MaxDownloadBytes, ct);
        var uri = new Uri(client.BaseUrl, $"api/documents/{id}/download/{(original ? "?original=true" : "")}").AbsoluteUri;
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = $"Document {id}: {file.FileName} ({file.ContentType}, {file.Data.Length:N0} bytes)" },
                new EmbeddedResourceBlock { Resource = BlobResourceContents.FromBytes(file.Data, uri, file.ContentType) },
            ],
        };
    }

    [McpServerTool(Name = "get_document_thumbnail", Title = "Get document thumbnail", ReadOnly = true, OpenWorld = false)]
    [Description("Get a small preview image of the first page of a document.")]
    public async Task<CallToolResult> GetDocumentThumbnail([Description("Document ID.")] int id, CancellationToken ct = default)
    {
        var file = await client.GetThumbnailAsync(id, ct);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(file.Data, file.ContentType)] };
    }

    [McpServerTool(Name = "upload_document", Title = "Upload document", Destructive = false, OpenWorld = false)]
    [Description("""
        Upload a new document to Paperless. Provide the file as base64 (content_base64) or plain text (text_content, stored as a .txt document).
        Metadata can be given by name or ID. Paperless processes uploads asynchronously (OCR, classification);
        by default this tool waits for processing and returns the new document. Duplicates are rejected by Paperless.
        """)]
    public async Task<JsonObject> UploadDocument(
        [Description("File name including extension, e.g. \"invoice-2024-03.pdf\". Determines the file type.")] string file_name,
        [Description("File content, base64 encoded (a data: URL prefix is allowed).")] string? content_base64 = null,
        [Description("Plain text content, as an alternative to content_base64.")] string? text_content = null,
        [Description("Document title. Defaults to a title derived by Paperless.")] string? title = null,
        [Description("Correspondent name or ID.")] string? correspondent = null,
        [Description("Document type name or ID.")] string? document_type = null,
        [Description("Storage path name or ID.")] string? storage_path = null,
        [Description("Tag names or IDs.")] string[]? tags = null,
        [Description("Created date (YYYY-MM-DD or ISO 8601 date-time).")] string? created = null,
        [Description("Archive serial number (ASN).")] int? archive_serial_number = null,
        [Description("Custom field values by field name or ID, e.g. {\"Amount\": \"EUR12.50\", \"Due\": \"2024-05-01\"}. Applied after processing (requires wait_for_completion).")] Dictionary<string, JsonElement>? custom_fields = null,
        [Description("Create tags, correspondents, document types or storage paths that don't exist yet.")] bool create_missing_metadata = false,
        [Description("Wait until Paperless has processed the document and return it.")] bool wait_for_completion = true,
        [Description("Maximum seconds to wait for processing (1-300).")] int timeout_seconds = 90,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file_name)) throw new McpException("file_name is required.");
        file_name = Path.GetFileName(file_name.Trim());

        byte[] data;
        if (!string.IsNullOrEmpty(content_base64))
        {
            var base64 = content_base64.Trim();
            var comma = base64.IndexOf(',');
            if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0) base64 = base64[(comma + 1)..];
            try
            {
                data = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                throw new McpException("content_base64 is not valid base64.");
            }
        }
        else if (text_content is not null)
        {
            data = Encoding.UTF8.GetBytes(text_content);
            if (Path.GetExtension(file_name).Length == 0) file_name += ".txt";
        }
        else
        {
            throw new McpException("Provide either content_base64 or text_content.");
        }

        if (data.Length == 0) throw new McpException("The document is empty.");
        if (data.Length > options.MaxUploadBytes)
        {
            throw new McpException($"The document exceeds the upload limit of {options.MaxUploadBytes / 1024 / 1024} MB.");
        }
        if (custom_fields is { Count: > 0 } && !wait_for_completion)
        {
            throw new McpException("custom_fields can only be applied when wait_for_completion is true. Use update_document afterwards instead.");
        }

        var fields = new List<KeyValuePair<string, string>>();
        void Add(string key, object? value)
        {
            if (value is not null) fields.Add(new(key, Convert.ToString(value, CultureInfo.InvariantCulture)!));
        }
        Add("title", string.IsNullOrWhiteSpace(title) ? null : title.Trim());
        Add("created", created is null ? null : ParseDateTime(created, nameof(created)));
        Add("correspondent", await resolver.ResolveOptionalAsync(MetadataKind.Correspondents, correspondent, create_missing_metadata, ct));
        Add("document_type", await resolver.ResolveOptionalAsync(MetadataKind.DocumentTypes, document_type, create_missing_metadata, ct));
        Add("storage_path", await resolver.ResolveOptionalAsync(MetadataKind.StoragePaths, storage_path, create_missing_metadata, ct));
        foreach (var tagId in await resolver.ResolveManyAsync(MetadataKind.Tags, tags, create_missing_metadata, ct)) Add("tags", tagId);
        Add("archive_serial_number", archive_serial_number);

        // Resolve custom fields before uploading so mistakes surface before anything is created.
        var customFieldValues = custom_fields is { Count: > 0 } ? await ResolveCustomFieldsAsync(custom_fields, ct) : null;

        var task_id = await client.UploadDocumentAsync(data, file_name, GuessContentType(file_name), fields, ct);
        if (!wait_for_completion)
        {
            return new JsonObject
            {
                ["status"] = "queued",
                ["task_id"] = task_id,
                ["message"] = "Document queued for processing. Use get_task_status to check the result.",
            };
        }

        var deadline = time.GetUtcNow().AddSeconds(Math.Clamp(timeout_seconds, 1, 300));
        PaperlessTask? task = null;
        while (time.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), time, ct);
            task = await client.GetTaskAsync(task_id, ct);
            if (task?.Status is "SUCCESS" or "FAILURE" or "REVOKED") break;
        }

        if (task?.Status == "SUCCESS" && task.RelatedDocumentId is { } document_id)
        {
            var doc = customFieldValues is not null
                ? await client.UpdateDocumentAsync(document_id, new JsonObject { ["custom_fields"] = customFieldValues }, ct)
                : await client.GetDocumentAsync(document_id, ct);
            var details = await formatter.DetailsAsync(doc, ct);
            return new JsonObject { ["status"] = "success", ["task_id"] = task_id, ["document"] = details };
        }
        if (task?.Status is "FAILURE" or "REVOKED")
        {
            throw new McpException($"Paperless could not process the document: {task.Result ?? task.Status}");
        }
        return new JsonObject
        {
            ["status"] = task?.Status?.ToLowerInvariant() ?? "pending",
            ["task_id"] = task_id,
            ["message"] = "Document is still being processed. Use get_task_status with this task_id to check later."
                          + (customFieldValues is not null ? " Custom fields were not applied yet; use update_document afterwards." : ""),
        };
    }

    [McpServerTool(Name = "get_task_status", Title = "Get processing task status", ReadOnly = true, OpenWorld = false)]
    [Description("Check the status of a Paperless processing task, e.g. a document upload. Returns the document ID once processing succeeded.")]
    public async Task<JsonObject> GetTaskStatus([Description("Task ID returned by upload_document.")] string task_id, CancellationToken ct = default)
    {
        var task = await client.GetTaskAsync(task_id.Trim(), ct) ?? throw new McpException($"Task \"{task_id}\" not found.");
        return new JsonObject
        {
            ["task_id"] = task.TaskId,
            ["status"] = task.Status,
            ["file_name"] = task.TaskFileName,
            ["result"] = task.Result,
            ["document_id"] = task.RelatedDocumentId,
            ["created"] = task.DateCreated,
            ["done"] = task.DateDone,
        };
    }

    [McpServerTool(Name = "update_document", Title = "Update document metadata", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Update metadata of a document. Only the given fields change. Metadata can be given by name or ID.
        Tags: use `tags` to replace all tags, or `add_tags` / `remove_tags` to change some.
        Custom fields: values by field name or ID; fields not mentioned stay unchanged.
        To clear a field, list it in `clear` (correspondent, document_type, storage_path, archive_serial_number, tags, custom_fields).
        """)]
    public async Task<JsonObject> UpdateDocument(
        [Description("Document ID.")] int id,
        [Description("New title.")] string? title = null,
        [Description("Correspondent name or ID.")] string? correspondent = null,
        [Description("Document type name or ID.")] string? document_type = null,
        [Description("Storage path name or ID.")] string? storage_path = null,
        [Description("Created date (YYYY-MM-DD).")] string? created = null,
        [Description("Archive serial number (ASN).")] int? archive_serial_number = null,
        [Description("Replace all tags with these (names or IDs).")] string[]? tags = null,
        [Description("Tags to add (names or IDs).")] string[]? add_tags = null,
        [Description("Tags to remove (names or IDs).")] string[]? remove_tags = null,
        [Description("Custom field values by field name or ID. Select fields accept the option label.")] Dictionary<string, JsonElement>? custom_fields = null,
        [Description("Custom fields to remove from the document (names or IDs).")] string[]? remove_custom_fields = null,
        [Description("Fields to clear: correspondent, document_type, storage_path, archive_serial_number, tags, custom_fields.")] string[]? clear = null,
        [Description("Create tags, correspondents, document types or storage paths that don't exist yet.")] bool create_missing_metadata = false,
        CancellationToken ct = default)
    {
        var patch = new JsonObject();
        var clearSet = new HashSet<string>((clear ?? []).Select(c => c.Trim().ToLowerInvariant()));

        if (!string.IsNullOrWhiteSpace(title)) patch["title"] = title.Trim();
        if (created is not null) patch["created"] = ParseDate(created, nameof(created));
        if (archive_serial_number is not null) patch["archive_serial_number"] = archive_serial_number;
        if (correspondent is not null) patch["correspondent"] = (await resolver.ResolveAsync(MetadataKind.Correspondents, correspondent, create_missing_metadata, ct)).Id;
        if (document_type is not null) patch["document_type"] = (await resolver.ResolveAsync(MetadataKind.DocumentTypes, document_type, create_missing_metadata, ct)).Id;
        if (storage_path is not null) patch["storage_path"] = (await resolver.ResolveAsync(MetadataKind.StoragePaths, storage_path, create_missing_metadata, ct)).Id;

        foreach (var field in clearSet)
        {
            switch (field)
            {
                case "correspondent" or "document_type" or "storage_path" or "archive_serial_number":
                    patch[field] = null;
                    break;
                case "tags":
                    patch["tags"] = new JsonArray();
                    break;
                case "custom_fields":
                    patch["custom_fields"] = new JsonArray();
                    break;
                default:
                    throw new McpException($"Cannot clear \"{field}\". Allowed: correspondent, document_type, storage_path, archive_serial_number, tags, custom_fields.");
            }
        }

        PaperlessDocument? current = null;
        async Task<PaperlessDocument> Current() => current ??= await client.GetDocumentAsync(id, ct);

        if (tags is not null || add_tags is { Length: > 0 } || remove_tags is { Length: > 0 })
        {
            var tagIds = tags is not null
                ? (await resolver.ResolveManyAsync(MetadataKind.Tags, tags, create_missing_metadata, ct)).ToList()
                : clearSet.Contains("tags") ? [] : [.. (await Current()).Tags ?? []];
            tagIds.AddRange(await resolver.ResolveManyAsync(MetadataKind.Tags, add_tags, create_missing_metadata, ct));
            var remove = await resolver.ResolveManyAsync(MetadataKind.Tags, remove_tags, false, ct);
            patch["tags"] = new JsonArray(tagIds.Distinct().Except(remove).Select(t => (JsonNode)t).ToArray());
        }

        if (custom_fields is { Count: > 0 } || remove_custom_fields is { Length: > 0 })
        {
            var merged = new Dictionary<int, JsonNode?>();
            if (!clearSet.Contains("custom_fields"))
            {
                foreach (var existing in (await Current()).CustomFields ?? [])
                {
                    merged[existing.Field] = existing.Value is { } v ? JsonNode.Parse(v.GetRawText()) : null;
                }
            }
            if (custom_fields is { Count: > 0 })
            {
                foreach (var item in await ResolveCustomFieldsAsync(custom_fields, ct))
                {
                    merged[item!["field"]!.GetValue<int>()] = item["value"]?.DeepClone();
                }
            }
            foreach (var name in remove_custom_fields ?? [])
            {
                merged.Remove((await resolver.ResolveAsync(MetadataKind.CustomFields, name, false, ct)).Id);
            }
            patch["custom_fields"] = new JsonArray(merged.Select(kv => (JsonNode)new JsonObject { ["field"] = kv.Key, ["value"] = kv.Value }).ToArray());
        }

        if (patch.Count == 0) throw new McpException("Nothing to update. Provide at least one field to change.");

        var updated = await client.UpdateDocumentAsync(id, patch, ct);
        return new JsonObject
        {
            ["updated_fields"] = new JsonArray(patch.Select(kv => (JsonNode)kv.Key).ToArray()),
            ["document"] = await formatter.DetailsAsync(updated, ct),
        };
    }

    [McpServerTool(Name = "delete_document", Title = "Delete document", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Delete a document. Depending on the Paperless configuration it is moved to the trash (restorable in the web UI for a limited time) or removed permanently. Ask the user for confirmation before deleting.")]
    public async Task<JsonObject> DeleteDocument([Description("Document ID.")] int id, CancellationToken ct = default)
    {
        var doc = await client.GetDocumentAsync(id, ct);
        await client.DeleteDocumentAsync(id, ct);
        return new JsonObject
        {
            ["deleted"] = true,
            ["id"] = id,
            ["title"] = doc.Title,
            ["message"] = "Document deleted. If the trash is enabled in Paperless, it can be restored from the trash in the web UI.",
        };
    }

    [McpServerTool(Name = "bulk_edit_documents", Title = "Bulk edit documents", Destructive = true, OpenWorld = false)]
    [Description("""
        Apply one action to many documents at once.
        Actions: modify_tags (use add_tags / remove_tags), set_correspondent, set_document_type, set_storage_path
        (use value; "none" clears it), delete (ask the user for confirmation first), reprocess (re-run OCR/consumption).
        """)]
    public async Task<JsonObject> BulkEditDocuments(
        [Description("IDs of the documents to change.")] int[] document_ids,
        [Description("One of: modify_tags, set_correspondent, set_document_type, set_storage_path, delete, reprocess.")] string action,
        [Description("Tags to add (modify_tags).")] string[]? add_tags = null,
        [Description("Tags to remove (modify_tags).")] string[]? remove_tags = null,
        [Description("Name or ID for set_correspondent / set_document_type / set_storage_path; \"none\" clears the field.")] string? value = null,
        [Description("Create missing tags, correspondents, document types or storage paths.")] bool create_missing_metadata = false,
        CancellationToken ct = default)
    {
        if (document_ids is not { Length: > 0 }) throw new McpException("document_ids must not be empty.");
        var ids = document_ids.Distinct().ToArray();
        var parameters = new JsonObject();
        string method;

        async Task<JsonNode?> Target(MetadataKind kind)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new McpException($"value is required for {action} (use \"none\" to clear).");
            if (value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) && await resolver.FindAsync(kind, value, ct) is null) return null;
            return (await resolver.ResolveAsync(kind, value, create_missing_metadata, ct)).Id;
        }

        switch (action.Trim().ToLowerInvariant())
        {
            case "modify_tags" or "add_tags" or "remove_tags":
                method = "modify_tags";
                var add = await resolver.ResolveManyAsync(MetadataKind.Tags, add_tags, create_missing_metadata, ct);
                var remove = await resolver.ResolveManyAsync(MetadataKind.Tags, remove_tags, false, ct);
                if (add.Length == 0 && remove.Length == 0) throw new McpException("Provide add_tags and/or remove_tags.");
                parameters["add_tags"] = new JsonArray(add.Select(t => (JsonNode)t).ToArray());
                parameters["remove_tags"] = new JsonArray(remove.Select(t => (JsonNode)t).ToArray());
                break;
            case "set_correspondent":
                method = "set_correspondent";
                parameters["correspondent"] = await Target(MetadataKind.Correspondents);
                break;
            case "set_document_type":
                method = "set_document_type";
                parameters["document_type"] = await Target(MetadataKind.DocumentTypes);
                break;
            case "set_storage_path":
                method = "set_storage_path";
                parameters["storage_path"] = await Target(MetadataKind.StoragePaths);
                break;
            case "delete":
                method = "delete";
                break;
            case "reprocess":
                method = "reprocess";
                break;
            default:
                throw new McpException($"Unknown action \"{action}\". Use modify_tags, set_correspondent, set_document_type, set_storage_path, delete or reprocess.");
        }

        await client.BulkEditAsync(ids, method, parameters, ct);
        return new JsonObject
        {
            ["action"] = method,
            ["document_ids"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()),
            ["parameters"] = parameters.DeepClone(),
            ["status"] = "ok",
        };
    }

    // ------------------------------------------------------------------ helpers

    private async Task<JsonArray> ResolveCustomFieldsAsync(Dictionary<string, JsonElement> values, CancellationToken ct)
    {
        var result = new JsonArray();
        foreach (var (key, value) in values)
        {
            var definition = await resolver.ResolveAsync(MetadataKind.CustomFields, key, false, ct);
            result.Add(new JsonObject { ["field"] = definition.Id, ["value"] = CustomFieldValues.ToApi(definition, value) });
        }
        return result;
    }

    private static string Join(IEnumerable<int> ids) => string.Join(',', ids);

    private static string? ParseDate(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            return dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        throw new McpException($"{ToSnakeCase(parameter)} must be a date in the format YYYY-MM-DD, got \"{value}\".");
    }

    private static string ParseDateTime(string value, string parameter)
    {
        value = value.Trim();
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return value;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)) return dto.ToString("O", CultureInfo.InvariantCulture);
        throw new McpException($"{ToSnakeCase(parameter)} must be a date (YYYY-MM-DD) or ISO 8601 date-time, got \"{value}\".");
    }

    private static string ToSnakeCase(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    private static string GuessContentType(string file_name) => Path.GetExtension(file_name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        ".bmp" => "image/bmp",
        ".heic" => "image/heic",
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        ".csv" => "text/csv",
        ".html" or ".htm" => "text/html",
        ".eml" => "message/rfc822",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".doc" => "application/msword",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".xls" => "application/vnd.ms-excel",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".odt" => "application/vnd.oasis.opendocument.text",
        ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
        ".odp" => "application/vnd.oasis.opendocument.presentation",
        ".rtf" => "application/rtf",
        _ => "application/octet-stream",
    };
}
