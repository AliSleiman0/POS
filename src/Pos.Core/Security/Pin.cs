namespace Pos.Core.Security;

/// <summary>Rules for a cashier PIN.</summary>
public static class Pin
{
    public const int MinLength = 4;
    public const int MaxLength = 6;

    /// <summary>
    /// Whether <paramref name="candidate"/> is a syntactically acceptable PIN.
    /// </summary>
    /// <remarks>
    /// Deliberately no "not 1234" rule. A blocklist of obvious PINs pushes staff toward
    /// writing the awkward one on the drawer, and the security of this scheme rests on the
    /// device token and the lockout, not on the PIN being unguessable.
    /// </remarks>
    public static bool IsWellFormed(string? candidate)
        => candidate is { Length: >= MinLength and <= MaxLength }
           && candidate.All(char.IsAsciiDigit);
}
