using System.Text;
using System.Text.Json;

namespace ApiTester.Core;

/// <summary>Builds a GraphQL request body — <c>{ "query": …, "variables": … }</c> — sent as
/// application/json. Keeps the query correctly escaped and validates the variables JSON.</summary>
public static class GraphQL
{
    /// <summary>Build the JSON body for a GraphQL query, optionally with a JSON object of variables.</summary>
    /// <exception cref="JsonException">The variables text is not a JSON object.</exception>
    public static string BuildBody(string query, string? variablesJson = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("query", query ?? "");
            if (!string.IsNullOrWhiteSpace(variablesJson))
            {
                using var doc = JsonDocument.Parse(variablesJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("GraphQL variables must be a JSON object, e.g. {\"id\":1}.");
                writer.WritePropertyName("variables");
                doc.RootElement.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
