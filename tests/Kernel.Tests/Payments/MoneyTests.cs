using Platform.Kernel.Contracts.Payments;

namespace Platform.Kernel.Tests.Payments;

public sealed class MoneyTests
{
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
    public void Equality_IsByAmountAndCurrency()
    {
        Assert.Equal(Money.Of(500, "GEL"), Money.Of(500, "GEL"));
        Assert.NotEqual(Money.Of(500, "GEL"), Money.Of(500, "EUR"));
        Assert.NotEqual(Money.Of(500, "GEL"), Money.Of(501, "GEL"));
    }
}
