namespace Pos.Seed;

/// <summary>
/// A failure the operator can act on — an unreachable database, a schema behind its
/// migrations, an Identity validation error.
/// </summary>
/// <remarks>
/// Separated from everything else so that <c>Program</c> can print these as a plain
/// message and exit 1, while a genuine bug still comes out as an unhandled exception with
/// its stack trace intact. A tool that swallows both looks the same in either case.
/// </remarks>
public sealed class SeedException : Exception
{
    public SeedException()
    {
    }

    public SeedException(string message)
        : base(message)
    {
    }

    public SeedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
