using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prive.Server.Http;

/// <summary>
///     Writes every <see cref="DateTime"/> the way Epic's own services do: exactly three fractional
///     digits, UTC, with a literal Z.
///     <para>
///         This is not cosmetic. <c>FDateTime::ParseIso8601</c> in the client refuses anything
///         longer - "should be no more than 3 digits ... return false" (DateTime.cpp) - and
///         System.Text.Json's default round-trip format emits seven. So every timestamp handed out
///         as a raw DateTime rather than through <see cref="Global.DateTimeFormat"/> was
///         unparseable, which for the party is fatal: <c>created_at</c> is a required field.
///     </para>
///     <para>
///         Registered globally rather than fixed per call site, because the call sites that got it
///         right did so by remembering to write <c>.ToString(DateTimeFormat)</c>, and the ones that
///         forgot are exactly the bugs. Handing out a DateTime is now safe by default.
///     </para>
/// </summary>
public sealed class EpicDateTimeConverter : JsonConverter<DateTime> {
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString(DateTimeFormat));
}
