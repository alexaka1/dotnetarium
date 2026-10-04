using Microsoft.CodeAnalysis;

namespace Dotnetarium.Tool;

internal static class SourceLocationSpan
{
    // Razor's #line directives map generated operations back to markup. Keep
    // hidden/unmapped generated sections at their physical C# locations.
    internal static FileLinePositionSpan GetDisplaySpan(Location location) =>
        location.SourceTree?.GetLineVisibility(location.SourceSpan.Start) == LineVisibility.Hidden
            ? location.GetLineSpan() : location.GetMappedLineSpan();
}
