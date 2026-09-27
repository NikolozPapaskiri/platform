using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Kernel.Contracts.Payments;

/// <summary>
/// Reads and writes <see cref="Money"/> as <c>{"amountMinor":2500,"currency":"GEL"}</c>.
/// </summary>
/// <remarks>
/// Without it, System.Text.Json builds the struct through its implicit parameterless constructor
/// and skips the get-only properties, so every amount would come back as <c>default(Money)</c>.
/// Names are written fixed, whatever the serializer's naming policy, because stored events must stay
/// readable; they are read case-insensitively. Unknown properties are ignored, repeated ones rejected.
/// Reading validates through <see cref="Money.Of"/>; invalid input throws <see cref="JsonException"/>,
/// the exception deserialization callers already handle.
/// </remarks>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    private const string AmountMinorName = "amountMinor";
    private const string CurrencyName = "currency";

    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Money must be a JSON object.");
        }

        long? amountMinor = null;
        string? currency = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, AmountMinorName, StringComparison.OrdinalIgnoreCase))
            {
                // A repeated key is rejected, not resolved: parsers disagree on which copy wins
                // (PostgreSQL jsonb keeps the last), and money must mean one thing everywhere.
                amountMinor = amountMinor is null && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var amount)
                    ? amount
                    : throw new JsonException($"'{AmountMinorName}' must appear once, as a whole number.");
            }
            else if (string.Equals(name, CurrencyName, StringComparison.OrdinalIgnoreCase))
            {
                currency = currency is null && reader.TokenType == JsonTokenType.String
                    ? reader.GetString()
                    : throw new JsonException($"'{CurrencyName}' must appear once, as a string.");
            }
            else if (!reader.TrySkip())
            {
                // The serializer buffers a custom converter's whole value before calling it, so this
                // should always succeed, even when reading a stream. If it ever cannot, callers get a
                // JsonException, which they handle, not the InvalidOperationException Skip() throws.
                throw new JsonException("Money has an unknown property that could not be skipped.");
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || amountMinor is null || currency is null)
        {
            throw new JsonException($"Money needs '{AmountMinorName}' and '{CurrencyName}'.");
        }

        try
        {
            return Money.Of(amountMinor.Value, currency);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Fail when the event is stored, not later when a dispatcher cannot read it back.
        if (value.Currency is null)
        {
            throw new JsonException("default(Money) has no currency and cannot be serialized.");
        }

        writer.WriteStartObject();
        writer.WriteNumber(AmountMinorName, value.AmountMinor);
        writer.WriteString(CurrencyName, value.Currency);
        writer.WriteEndObject();
    }
}
