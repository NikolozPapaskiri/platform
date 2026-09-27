using System.Text.Json;
using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Tests.Tenancy;

public sealed class TenantIdTests
{
    private static readonly JsonSerializerOptions _web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Json_IsAPlainGuidString_AndRoundTrips()
    {
        var tenant = TenantId.New();

        var json = JsonSerializer.Serialize(tenant, _web);

        Assert.Equal($"\"{tenant.Value}\"", json);
        Assert.Equal(tenant, JsonSerializer.Deserialize<TenantId>(json, _web));
        Assert.Equal(tenant, JsonSerializer.Deserialize<TenantId>(json));
    }

    [Theory]
    [InlineData("\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("\"not-a-guid\"")]
    [InlineData("null")]
    [InlineData("{\"value\":\"0190a1b2-0000-7000-8000-000000000001\"}")]
    public void Json_EmptyOrInvalid_ThrowsJsonException(string json)
    {
        // An empty tenant id must never come out of deserialization: it would bypass the constructor.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TenantId>(json, _web));
    }

    [Fact]
    public void Json_AsDictionaryKey_RoundTrips()
    {
        var totals = new Dictionary<TenantId, int> { [TenantId.New()] = 3 };

        var read = JsonSerializer.Deserialize<Dictionary<TenantId, int>>(JsonSerializer.Serialize(totals, _web), _web);

        Assert.Equal(totals, read);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Dictionary<TenantId, int>>(
            "{\"00000000-0000-0000-0000-000000000000\":3}", _web));
    }

    [Fact]
    public void Json_DefaultTenantId_CannotBeWritten()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(default(TenantId), _web));
    }
}
