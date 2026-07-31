using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pos.Data.Configurations;

/// <summary>
/// Shared configuration helpers, so every entity expresses the same conventions the
/// same way instead of each one re-deciding.
/// </summary>
internal static class EntityTypeBuilderExtensions
{
    /// <summary>
    /// Maps a string property as Postgres <c>text</c> with a length <b>check constraint</b>,
    /// per docs/DATA-MODEL.md#conventions.
    /// </summary>
    /// <remarks>
    /// Not <c>varchar(n)</c>. In Postgres the two perform identically, but widening a
    /// <c>varchar(n)</c> is a schema migration on a table that may be large and busy,
    /// whereas a check constraint is dropped and recreated cheaply. The limit exists to
    /// stop a runaway input, not to describe the domain.
    /// </remarks>
    /// <param name="columnName">
    /// The snake_case column name, stated rather than derived. If it is wrong the
    /// migration fails immediately with "column does not exist", which is the loud
    /// failure we want from a mistyped constraint.
    /// </param>
    public static EntityTypeBuilder<TEntity> HasBoundedText<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, string?>> property,
        string columnName,
        int maxLength)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        builder.Property(property).HasColumnType("text");

        var table = builder.Metadata.GetTableName()
            ?? throw new InvalidOperationException(
                $"{typeof(TEntity).Name} has no table name; call ToTable() before HasBoundedText().");

        builder.ToTable(t => t.HasCheckConstraint(
            $"ck_{table}_{columnName}_length",
            FormattableString.Invariant($"length(\"{columnName}\") <= {maxLength}")));

        return builder;
    }

    /// <summary>
    /// Stores an enum as text rather than an integer, bounded by a check constraint.
    /// </summary>
    /// <remarks>
    /// Text because a report, a psql session or a support question is unreadable when the
    /// answer is "3". Not a Postgres <c>ENUM</c> type: adding or renaming a value in one is
    /// a migration with real teeth, and these enums are owned by C#.
    /// </remarks>
    public static EntityTypeBuilder<TEntity> HasEnumAsText<TEntity, TEnum>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, TEnum>> property,
        string columnName)
        where TEntity : class
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        builder.Property(property)
            .HasConversion<string>()
            .HasColumnType("text");

        var table = builder.Metadata.GetTableName()
            ?? throw new InvalidOperationException(
                $"{typeof(TEntity).Name} has no table name; call ToTable() before HasEnumAsText().");

        var allowed = string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"));

        // The constraint is what stops a row arriving through raw SQL with a value C# has
        // no case for — the enum's exhaustiveness is only a compile-time guarantee.
        builder.ToTable(t => t.HasCheckConstraint(
            $"ck_{table}_{columnName}_allowed",
            string.Create(CultureInfo.InvariantCulture, $"\"{columnName}\" IN ({allowed})")));

        return builder;
    }
}
