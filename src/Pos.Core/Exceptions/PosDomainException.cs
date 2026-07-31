namespace Pos.Core.Exceptions;

/// <summary>
/// Base for every exception the domain throws deliberately.
/// </summary>
/// <remarks>
/// The API maps these to RFC 9457 <c>application/problem+json</c> in exactly one
/// place (see <c>Pos.Api/Errors</c>), which is only possible if they share a base
/// and carry a stable, machine-readable slug. Clients branch on
/// <see cref="ErrorType"/>; <see cref="Exception.Message"/> is human-facing and
/// may be reworded without warning.
/// </remarks>
public abstract class PosDomainException : Exception
{
    protected PosDomainException()
    {
    }

    protected PosDomainException(string message)
        : base(message)
    {
    }

    protected PosDomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Stable kebab-case slug identifying this failure, e.g. <c>cross-tenant-write</c>.
    /// Becomes the <c>type</c> of the problem+json response. Never change one of these
    /// without treating it as a breaking API change.
    /// </summary>
    public abstract string ErrorType { get; }
}
