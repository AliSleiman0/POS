namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when setting a category's parent would make the category its own ancestor.
/// </summary>
/// <remarks>
/// No SQL constraint can prevent this. <c>fk_category_parent</c> guarantees the parent row
/// exists and belongs to the same tenant, and nothing more — reachability is not something
/// a foreign key can express. So the check lives in the write path, and this is what it
/// throws.
/// <para>
/// A 400 rather than a 409: the request names a parent that cannot be that category's
/// parent, which is a statement about the body, not about a race with someone else's write.
/// </para>
/// </remarks>
public sealed class CategoryCycleException : PosDomainException
{
    public CategoryCycleException()
        : base("That parent would put the category inside its own subtree.")
    {
    }

    public CategoryCycleException(string message)
        : base(message)
    {
    }

    public CategoryCycleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "category-cycle";
}
