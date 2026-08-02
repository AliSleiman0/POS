namespace Pos.Core.Entities;

/// <summary>Whether a register's drawer is still trading.</summary>
public enum ShiftStatus
{
    /// <summary>Open for business. At most one per register, enforced by a filtered unique index.</summary>
    Open = 0,

    /// <summary>Counted and reconciled. A sale against a closed shift is refused.</summary>
    Closed = 1,
}
