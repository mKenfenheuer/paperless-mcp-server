using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PaperlessMcpServer.Tools;

/// <summary>JSON settings for tool arguments and results: snake_case names, nulls omitted.</summary>
public static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        // Results are consumed by language models, not embedded in HTML: keep text readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
