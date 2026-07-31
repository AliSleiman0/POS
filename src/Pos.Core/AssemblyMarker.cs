namespace Pos.Core;

/// <summary>
/// Stable anchor for reflecting over this assembly (see ArchitectureTests).
/// Exists so the architecture test does not have to name a real domain type,
/// which would couple it to code that gets renamed or moved.
/// </summary>
public sealed class AssemblyMarker
{
    private AssemblyMarker() { }
}
