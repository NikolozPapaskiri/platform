using System.Text.Json;
using Platform.Kernel.Contracts.Payments;

namespace Platform.Kernel.Tests.Payments;

public sealed class MoneyTests
{
    private static readonly JsonSerializerOptions _web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("GEL")]
    [InlineData("EUR")]
    [InlineData("JPY")]
    public void Of_AcceptsIso4217ShapedCodes(string currency)
    {
        var money = Money.Of(1250, currency);

        Assert.Equal(1250, money.AmountMinor);
        Assert.Equal(currency, money.Currency);
    }

    [Theory]
    [InlineData("gel")]
    [InlineData("GE")]
    [InlineData("GELL")]
    [InlineData("G1L")]
    [InlineData("")]
    [InlineData(null)]
    public void Of_RejectsAnythingElse(string? currency)
    {
        Assert.Throws<ArgumentException>(() => Money.Of(100, currency!));
    }

    [Fact]
    public void Arithmetic_InOneCurrency_IsExact()
    {
        // 0.1 + 0.2 in binary floating point is 0.30000000000000004; minor units have no such drift.
        var total = Money.Of(10, "GEL") + Money.Of(20, "GEL");

        Assert.Equal(Money.Of(30, "GEL"), total);
        Assert.Equal(Money.Of(-10, "GEL"), Money.Of(10, "GEL") - Money.Of(20, "GEL"));
    }

    [Fact]
    public void Arithmetic_AcrossCurrencies_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Money.Of(100, "GEL") + Money.Of(100, "EUR"));
        Assert.Throws<InvalidOperationException>(() => Money.Of(100, "GEL") - Money.Of(100, "EUR"));
    }

    [Fact]
    public void Arithmetic_WithDefaultMoney_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Money.Of(100, "GEL") + default(Money));
    }

    [Fact]
    public void Arithmetic_Overflow_Throws()
    {
        Assert.Throws<OverflowException>(() => Money.Of(long.MaxValue, "GEL") + Money.Of(1, "GEL"));
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var money = Money.Of(2500, "GEL");

        var json = JsonSerializer.Serialize(money, _web);

        Assert.Equal("""{"amountMinor":2500,"currency":"GEL"}""", json);
        Assert.Equal(money, JsonSerializer.Deserialize<Money>(json, _web));
        Assert.Equal(money, JsonSerializer.Deserialize<Money>(json));
    }

    [Theory]
    [InlineData("""{"amountMinor":2500,"currency":"gel"}""")]
    [InlineData("""{"amountMinor":2500}""")]
    [InlineData("""{"currency":"GEL"}""")]
    [InlineData("""{"amountMinor":"2500","currency":"GEL"}""")]
    [InlineData("""{"amountMinor":25.5,"currency":"GEL"}""")]
    [InlineData("""{"amountMinor":2500,"currency":null}""")]
    [InlineData("""{}""")]
    [InlineData("""2500""")]
    [InlineData("""{"amountMinor":2500,"currency":"GEL","amountMinor":1}""")]
    [InlineData("""{"amountMinor":2500,"currency":"GEL","Currency":"EUR"}""")]
    public void Json_Invalid_ThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Money>(json, _web));
    }

    [Theory]
    [InlineData("""{"amountMinor":2500,"meta":{"amountMinor":999,"currency":"EUR"},"currency":"GEL"}""")]
    [InlineData("""{"note":"x","amountMinor":2500,"tags":[{"amountMinor":1}],"currency":"GEL","n":7}""")]
    public void Json_UnknownProperties_AreSkipped_EvenNestedOnesThatLookLikeMoney(string json)
    {
        Assert.Equal(Money.Of(2500, "GEL"), JsonSerializer.Deserialize<Money>(json, _web));
    }

    [Fact]
    public async Task Json_FromAStreamInSmallChunks_SkipsUnknownProperties()
    {
        // A stream is read in small buffers, as ASP.NET Core reads request bodies; the unknown nested
        // property spans several of them.
        var json = """{"amountMinor":2500,"meta":{"deeply":{"nested":[1,2,3,4,5,6,7,8,9]}},"currency":"GEL"}"""u8.ToArray();
        await using var stream = new MemoryStream(json);

        var money = await JsonSerializer.DeserializeAsync<Money>(stream, new JsonSerializerOptions(_web) { DefaultBufferSize = 16 }, TestContext.Current.CancellationToken);

        Assert.Equal(Money.Of(2500, "GEL"), money);
    }

    [Fact]
    public void Json_DefaultMoney_CannotBeWritten()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(default(Money), _web));
    }

    [Fact]
    public void Equality_IsByAmountAndCurrency()
    {
        Assert.Equal(Money.Of(500, "GEL"), Money.Of(500, "GEL"));
        Assert.NotEqual(Money.Of(500, "GEL"), Money.Of(500, "EUR"));
        Assert.NotEqual(Money.Of(500, "GEL"), Money.Of(501, "GEL"));
    }
}
