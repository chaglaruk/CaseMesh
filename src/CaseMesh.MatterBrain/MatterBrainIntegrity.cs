using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CaseMesh.MatterBrain;

internal static class MatterBrainIntegrity
{
    internal static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string CanonicalizeJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string Fingerprint(
        StructuredExtractionProviderDescriptor descriptor,
        IEnumerable<Guid> sourceIds) => Digest(CanonicalizeJson(JsonSerializer.Serialize(new
        {
            descriptor.Provider,
            descriptor.Model,
            descriptor.ExtractionVersion,
            descriptor.PromptVersion,
            descriptor.SchemaVersion,
            SourceSpanIds = sourceIds.Order().Select(id => id.ToString("N")).ToArray()
        })));

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
