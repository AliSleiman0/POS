using System.Globalization;

namespace Pos.Core.Monetary;

/// <summary>
/// An amount of the tenant's currency. The type every price, total and tender is expressed in.
/// </summary>
/// <remarks>
/// <b>There is deliberately no <c>operator +(Money, decimal)</c>.</b> That absence is
/// CLAUDE.md invariant 3 enforced by the compiler rather than by review: <c>total + 1.005m</c>
/// does not compile, so raw decimal arithmetic on a price has to be written as an explicit
/// conversion, which is visible in a diff. Multiplication and division by a bare
/// <see cref="decimal"/> <i>are</i> offered, because those factors are dimensionless — a
/// quantity, a tax rate, a share — and money times money is meaningless anyway.
/// <para>
/// <b>It does not validate on construction, and that is the point.</b> Rule 3 says line
/// extensions stay at full precision: 0.3333 kg at €1.2340 is €0.41129220, eight decimals.
/// A type that threw on more than four decimal places would force a round on every
/// intermediate value, which <i>is</i> the per-line-rounding bug this phase exists to
/// prevent. Storability is a boundary question, asked once by <see cref="IsStorable"/> and
/// answered by <see cref="RoundToStorage"/> at the point of save.
/// </para>
/// <para>
/// No currency code. <c>Tenant.CurrencyCode</c> is display-only and there is no FX in this
/// system, so carrying a currency on every amount would add a comparison nothing can fail
/// and a conversion nothing can perform.
/// </para>
/// </remarks>
public readonly record struct Money(decimal Amount) : IComparable<Money>, IComparable, IFormattable
{
    /// <summary>No money. Equal to <c>default</c>, so an unassigned amount is zero.</summary>
    public static Money Zero => default;

    /// <summary>Named alternate for the explicit conversion from <see cref="decimal"/>.</summary>
    public static Money From(decimal amount) => new(amount);

    /// <summary>
    /// Explicit, never implicit: turning a bare decimal into money is the boundary this type
    /// exists to make visible.
    /// </summary>
    public static explicit operator Money(decimal amount) => new(amount);

    /// <summary>Explicit, for the same reason as the conversion in.</summary>
    public static explicit operator decimal(Money money) => money.Amount;

    /// <summary>Named alternate for the explicit conversion to <see cref="decimal"/>.</summary>
    public decimal ToDecimal() => Amount;

    public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount);

    /// <summary>Named alternate for <c>operator +</c>.</summary>
    public static Money Add(Money left, Money right) => left + right;

    public static Money operator -(Money left, Money right) => new(left.Amount - right.Amount);

    /// <summary>Named alternate for the binary <c>operator -</c>.</summary>
    public static Money Subtract(Money left, Money right) => left - right;

    public static Money operator -(Money value) => new(-value.Amount);

    /// <summary>Named alternate for the unary <c>operator -</c>.</summary>
    public static Money Negate(Money value) => -value;

    /// <summary>Scales an amount by a dimensionless factor — a quantity, a rate, a share.</summary>
    public static Money operator *(Money left, decimal factor) => new(left.Amount * factor);

    /// <summary>Named alternate for <c>operator *</c>.</summary>
    public static Money Multiply(Money left, decimal factor) => left * factor;

    /// <summary>Divides an amount by a dimensionless divisor.</summary>
    public static Money operator /(Money left, decimal divisor) => new(left.Amount / divisor);

    /// <summary>Named alternate for <c>operator /</c>.</summary>
    public static Money Divide(Money left, decimal divisor) => left / divisor;

    /// <summary>
    /// The dimensionless ratio of one amount to another — money divided by money is a share,
    /// not an amount, which is why this is not <c>operator /</c>.
    /// </summary>
    /// <remarks>
    /// Exists for discount apportionment, where a line's share of a cart discount is its
    /// share of the cart. Kept at full precision: the caller rounds, once, at the end.
    /// </remarks>
    public static decimal Ratio(Money numerator, Money denominator) =>
        numerator.Amount / denominator.Amount;

    public static bool operator <(Money left, Money right) => left.Amount < right.Amount;

    public static bool operator <=(Money left, Money right) => left.Amount <= right.Amount;

    public static bool operator >(Money left, Money right) => left.Amount > right.Amount;

    public static bool operator >=(Money left, Money right) => left.Amount >= right.Amount;

    public int CompareTo(Money other) => Amount.CompareTo(other.Amount);

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        Money other => CompareTo(other),
        _ => throw new ArgumentException($"Cannot compare a {nameof(Money)} to a {obj.GetType()}.", nameof(obj)),
    };

    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    public bool IsPositive => Amount > 0m;

    public Money Abs() => new(System.Math.Abs(Amount));

    /// <summary>
    /// Rounds to the payable scale — the amount a person hands over.
    /// </summary>
    /// <remarks>
    /// Call this <b>once</b>, on a total, at the end of a pipeline. Calling it per line and
    /// summing is the bug described on <see cref="Rounding"/>.
    /// </remarks>
    public Money Round(int scale = Rounding.DisplayScale) => new(Rounding.To(Amount, scale));

    /// <summary>
    /// Rounds to what the column holds. The call every writer makes before <c>SaveChanges</c>.
    /// </summary>
    /// <remarks>
    /// Explicit rather than done by the value converter, because rounding on write would put
    /// a rounding rule in a layer nobody reads, and Postgres would have rounded silently
    /// anyway — the point is that the decision is taken somewhere a reviewer can see it.
    /// </remarks>
    public Money RoundToStorage() => new(Rounding.To(Amount, Rounding.StorageScale));

    /// <summary>Whether <c>numeric(19,4)</c> would hold this amount without changing it.</summary>
    public bool IsStorable => Rounding.IsStorable(Amount);

    /// <summary>
    /// Adds a sequence of amounts at full precision.
    /// </summary>
    /// <remarks>
    /// Written out because LINQ's <c>Sum</c> has no overload for a custom struct, and the
    /// obvious workaround — <c>Sum(m =&gt; m.Amount)</c> — is exactly the raw-decimal
    /// arithmetic the missing <c>operator +(Money, decimal)</c> is there to prevent.
    /// </remarks>
    public static Money Sum(IEnumerable<Money> amounts)
    {
        ArgumentNullException.ThrowIfNull(amounts);

        var total = 0m;

        foreach (var amount in amounts)
        {
            total += amount.Amount;
        }

        return new Money(total);
    }

    public override string ToString() => ToString(format: null, CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Amount.ToString(format ?? "0.00##", formatProvider ?? CultureInfo.InvariantCulture);
}
