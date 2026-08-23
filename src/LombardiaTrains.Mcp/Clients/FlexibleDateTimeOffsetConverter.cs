using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LombardiaTrains.Mcp.Clients;

/// <summary>
/// Reads timestamps whose UTC offset has no colon.
///
/// transport.opendata.ch returns "2026-08-29T09:40:00+0200". That is not valid
/// ISO 8601 — the offset should be "+02:00" — and System.Text.Json refuses it,
/// throwing rather than returning a default. Caught by a broad handler that
/// converts failures into "nothing found", it turns into an empty result with
/// no explanation, which is a slow thing to diagnose.
///
/// Both spellings are accepted here so the payload can change back without
/// breaking anything.
/// </summary>
public sealed class FlexibleDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:sszzz",      // +02:00
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFK",
        "yyyy-MM-dd'T'HH:mm:sszz00"      // +0200
    ];

    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var direct))
            return direct;

        // Insert the missing colon: "+0200" becomes "+02:00".
        var repaired = System.Text.RegularExpressions.Regex.Replace(
            text, @"([+-]\d{2})(\d{2})$", "$1:$2");

        if (DateTimeOffset.TryParse(repaired, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var fixedUp))
            return fixedUp;

        return DateTimeOffset.TryParseExact(text, Formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var exact) ? exact : null;
    }

    public override void Write(
        Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value);
    }
}
