namespace PaperlessMcpServer.Tools;

internal static class ServerInstructions
{
    public const string Text = """
        This server gives access to a Paperless-ngx document management system on behalf of the signed-in user.

        - Find documents with search_documents (full-text query plus filters). Results are summaries; read details
          with get_document and the text with get_document_content.
        - Tags, correspondents, document types, storage paths and custom fields can be passed by name or ID.
          Use list_metadata to see what exists before inventing new names.
        - upload_document waits for Paperless to process the file and returns the new document. Paperless may assign
          tags, correspondent and type automatically; check the result before changing them.
        - update_document only changes the fields you pass. Use add_tags/remove_tags to keep other tags.
        - delete_document, bulk_edit_documents (delete) and delete_metadata change data permanently or move it to the
          trash. Confirm with the user before using them.
        - When referring to documents, mention their title and ID. get_document returns a link to the Paperless web UI.
        """;
}
