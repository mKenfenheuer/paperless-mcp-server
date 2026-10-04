using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PaperlessMcpServer.Paperless;

namespace PaperlessMcpServer.Tools;

[McpServerToolType]
public sealed class NoteTools(PaperlessClient client)
{
    [McpServerTool(Name = "list_document_notes", Title = "List document notes", ReadOnly = true, OpenWorld = false)]
    [Description("List the notes attached to a document.")]
    public async Task<JsonObject> ListDocumentNotes([Description("Document ID.")] int document_id, CancellationToken ct = default)
    {
        var notes = await client.GetNotesAsync(document_id, ct);
        return new JsonObject
        {
            ["document_id"] = document_id,
            ["notes"] = new JsonArray(notes.Select(n => (JsonNode)DocumentFormatter.FormatNote(n)).ToArray()),
        };
    }

    [McpServerTool(Name = "add_document_note", Title = "Add note to document", Destructive = false, OpenWorld = false)]
    [Description("Add a note to a document. Notes are visible in the Paperless web UI and searchable.")]
    public async Task<JsonObject> AddDocumentNote(
        [Description("Document ID.")] int document_id,
        [Description("Note text.")] string note,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(note)) throw new McpException("note must not be empty.");
        var notes = await client.AddNoteAsync(document_id, note.Trim(), ct);
        return new JsonObject
        {
            ["document_id"] = document_id,
            ["notes"] = new JsonArray(notes.Select(n => (JsonNode)DocumentFormatter.FormatNote(n)).ToArray()),
        };
    }

    [McpServerTool(Name = "delete_document_note", Title = "Delete document note", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Delete a note from a document.")]
    public async Task<JsonObject> DeleteDocumentNote(
        [Description("Document ID.")] int document_id,
        [Description("Note ID (see get_document or list_document_notes).")] int note_id,
        CancellationToken ct = default)
    {
        await client.DeleteNoteAsync(document_id, note_id, ct);
        return new JsonObject { ["deleted"] = true, ["document_id"] = document_id, ["note_id"] = note_id };
    }
}
