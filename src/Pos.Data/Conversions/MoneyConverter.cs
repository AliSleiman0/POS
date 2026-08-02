using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pos.Core.Monetary;

namespace Pos.Data.Conversions;

/// <summary>
/// Maps <see cref="Money"/> to the <c>numeric(19,4)</c> column underneath it.
/// </summary>
/// <remarks>
/// <b>It does not round.</b> Rounding on write would put a rounding rule in a layer nobody
/// reads, and the writer's explicit <c>RoundToStorage()</c> before <c>SaveChanges</c> is
/// where that decision is meant to be visible. Postgres would silently round a fifth decimal
/// place anyway; the point of keeping the converter dumb is that the value reaching it has
/// already been decided somewhere a reviewer can see.
/// <para>
/// Applied model-wide from <see cref="AppDbContext.ConfigureConventions"/>, alongside — not
/// instead of — the existing <c>decimal</c> sweep. <c>Properties&lt;decimal&gt;()</c> matches
/// on the CLR property type, so a <see cref="Money"/> property is <i>not</i> covered by it and
/// would otherwise map at the provider default of <c>numeric(18,2)</c>, silently truncating
/// the third and fourth decimals of a unit price.
/// </para>
/// </remarks>
internal sealed class MoneyConverter : ValueConverter<Money, decimal>
{
    public MoneyConverter()
        : base(money => money.Amount, amount => new Money(amount))
    {
    }
}
