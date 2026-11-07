using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hic.AgentBridge;

public static class CanonicalJson
{
    public const int MaxBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 16
    };

    public static T Parse<T>(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxBytes)
            throw new BridgeException("PAYLOAD_SIZE");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            Check(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Options)
                ?? throw new BridgeException("INVALID_JSON");
        }
        catch (JsonException)
        {
            throw new BridgeException("INVALID_JSON");
        }
    }

    private static void Check(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new BridgeException("DUPLICATE_FIELD");
                Check(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null)
                    throw new BridgeException("NULL_COLLECTION_ITEM");
                Check(item);
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            Safety.CheckString(element.GetString()!);
        }
    }

    public static byte[] Serialize<T>(T value, int limit = MaxBytes)
    {
        var element = JsonSerializer.SerializeToElement(value, Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Write(writer, element);
        if (stream.Length > limit)
            throw new BridgeException("PAYLOAD_SIZE");
        return stream.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
                Write(writer, item);
            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }

    public static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
