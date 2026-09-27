using System.Text.Json.Serialization;

namespace Platform.Kernel.Contracts.Payments;

/// <summary>
/// An amount of money: an integer number of the currency's minor units (cents, tetri) plus an
/// ISO 4217 currency code. Integers make money arithmetic exact: no binary floating-point rounding,
/// and no ambiguity about how many decimals an amount has.
/// </summary>
/// <remarks>
/// Money in different currencies never combines: adding or subtracting them throws. There is no
/// conversion to or from <see cref="decimal"/> here, because that needs the currency's minor-unit
/// exponent (2 for GEL and EUR, 0 for JPY, 3 for KWD), which is presentation, not arithmetic.
/// <c>default(Money)</c> has no currency and is invalid; create values with <see cref="Of"/>.
/// JSON goes through <see cref="MoneyJsonConverter"/>, which validates on the way in.
/// </remarks>
[JsonConverter(typeof(MoneyJsonConverter))]
public readonly record struct Money
{
    private Money(long amountMinor, string currency)
    {
        AmountMinor = amountMinor;
        Currency = currency;
    }

    /// <summary>The amount in minor units, for example 1250 for 12.50 GEL.</summary>
    public long AmountMinor { get; }

    /// <summary>ISO 4217 alphabetic code: three upper-case letters, for example <c>GEL</c>.</summary>
    public string Currency { get; }

    /// <summary>Creates an amount. Only the code's shape is checked, not membership in ISO 4217.</summary>
    /// <exception cref="ArgumentException">The currency is not three upper-case ASCII letters.</exception>
    public static Money Of(long amountMinor, string currency)
    {
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException(
                $"'{currency}' is not an ISO 4217 currency code (three upper-case letters).", nameof(currency));
        }

        return new Money(amountMinor, currency);
    }

    public static Money Zero(string currency) => Of(0, currency);

    public bool IsZero => AmountMinor == 0;

    public bool IsPositive => AmountMinor > 0;

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.AmountMinor + right.AmountMinor), left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.AmountMinor - right.AmountMinor), left.Currency);
    }

    public Money Add(Money other) => this + other;

    public Money Subtract(Money other) => this - other;

    /// <summary>Invariant, unambiguous form for logs and diagnostics, for example <c>1250 GEL (minor units)</c>.</summary>
    public override string ToString() => $"{AmountMinor} {Currency} (minor units)";

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency is null || right.Currency is null)
        {
            throw new InvalidOperationException("default(Money) has no currency; create values with Money.Of.");
        }

        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot combine {left.Currency} and {right.Currency}.");
        }
    }
}
