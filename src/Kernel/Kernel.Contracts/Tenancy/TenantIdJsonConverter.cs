using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Kernel.Contracts.Tenancy;

/// <summary>
/// Reads and writes <see cref="TenantId"/> as a plain GUID string.
/// </summary>
/// <remarks>
/// Without it, System.Text.Json builds a struct through its implicit parameterless constructor and
/// skips the get-only <see cref="TenantId.Value"/>, so every tenant id would come back as
/// <see cref="Guid.Empty"/>, bypassing the constructor's check. Invalid input throws
/// <see cref="JsonException"/>, the exception deserialization callers already handle.
/// </remarks>
public sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    public override TenantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var value) || value == Guid.Empty)
        {
            throw new JsonException("A tenant id must be a non-empty GUID string.");
        }

        return new TenantId(value);
    }

    public override void Write(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Fail when the event is stored, not later when a dispatcher cannot read it back.
        if (value.Value == Guid.Empty)
        {
            throw new JsonException("default(TenantId) cannot be serialized.");
        }

        writer.WriteStringValue(value.Value);
    }

    /// <summary>For dictionaries keyed by tenant.</summary>
    public override TenantId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.TryParse(reader.GetString(), out var value) && value != Guid.Empty
            ? new TenantId(value)
            : throw new JsonException("A tenant id must be a non-empty GUID string.");

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value.Value == Guid.Empty)
        {
            throw new JsonException("default(TenantId) cannot be serialized.");
        }

        writer.WritePropertyName(value.Value.ToString());
    }
}
