namespace Pos.Core.Entities;

/// <summary>
/// Which product a tenant is running: a shop counter, or a dining room.
/// </summary>
/// <remarks>
/// <b>This is a switch between two front-of-house models, not a feature flag.</b> Retail rings a
/// cart and takes the money in one motion; a restaurant opens an order against a table, adds to
/// it over an hour, sends it to a kitchen in rounds and settles it at the end. The money path
/// underneath is the same — a bill becomes an ordinary <c>Sale</c> through the same pricing
/// engine and the same writer — but everything in front of it differs, which is why
/// <c>DECISIONS.md</c> refused to force one schema for both.
/// <para>
/// <b>Not immutable, unlike <see cref="TaxMode"/>.</b> Flipping a tax mode reinterprets every
/// price already stored; flipping this changes which screens a shop sees and which endpoints
/// answer. Nothing historical is reinterpreted — a <c>Sale</c> written in restaurant mode reads
/// identically afterwards, because it is the same row it always was. A shop that opens a café
/// alongside its counter should be able to say so.
/// </para>
/// <para>
/// <b>What it costs a restaurant tenant is offline trading.</b> An order lives on the server so
/// a second tablet can see the table; the retail cart lives in <c>sessionStorage</c> so it can
/// be rung with the line down. Phase 9's outbox queues sales and knows nothing about orders, so
/// restaurant mode cannot take an order while offline and the app says so rather than letting a
/// service discover it.
/// </para>
/// </remarks>
public enum ServiceMode
{
    /// <summary>A counter. Scan, tender, done — the MVP's model, and the default.</summary>
    Retail = 0,

    /// <summary>A dining room: tables, orders held open, courses, split bills.</summary>
    Restaurant = 1,
}
