namespace Pos.Core;

// TEMPORARY - verifies that CI fails on a warning. Reverted immediately after.
internal static class CiBreakCheck
{
    internal static int Probe()
    {
        int unused = 42; // CS0219: assigned but never used -> warning -> error
        return 0;
    }
}
