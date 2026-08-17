using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.VaultVisionTV.Domain;

// channels.json carries `genre` as either a single string or an array of
// strings (the VBO movie-tier channels sweep several genres at once) —
// mirrors channels.js's own `[].concat(channel.genre)` normalization.
public class StringOrArrayConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new List<string> { reader.GetString()! };
        }

        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            list.Add(reader.GetString()!);
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var v in value)
        {
            writer.WriteStringValue(v);
        }

        writer.WriteEndArray();
    }
}
