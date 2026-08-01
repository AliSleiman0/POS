namespace Pos.Api.Common;

/// <summary>
/// One page of a list endpoint's results, and where to resume.
/// </summary>
/// <remarks>
/// The three fields docs/API.md promises, and no more. In particular there is no total
/// count: producing one means a second aggregate over the whole filtered set on every page,
/// and it would be stale the moment it was computed on a catalog being edited.
/// </remarks>
/// <param name="Items">This page's rows, in the endpoint's sort order.</param>
/// <param name="NextCursor">
/// Opaque. Pass it back as <c>?cursor=</c> to get the next page; <c>null</c> on the last
/// one. Clients must never parse it — its contents are an implementation detail and the
/// format carries a version so it can change.
/// </param>
/// <param name="HasMore">
/// Whether a further page exists. Redundant with <see cref="NextCursor"/> being non-null and
/// kept because "is there more?" is the question UI code actually asks, and answering it by
/// null-checking a string reads like a mistake.
/// </param>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);
