using System.Text.Json;

namespace Dotnetarium.Tool;

// Bounded proof from a validated restore, not a package-name allowlist. Never
// infer pruning from the package name or the missing parent edge alone.
internal static class FrameworkPackagePruning
{
    internal static RestoredAssetsState ValidatePolicy(string path, JsonElement assets, string framework, string? policy)
    {
        var settings = assets.GetProperty("project").GetProperty("frameworks").GetProperty(framework);
        var hasPruning = settings.TryGetProperty("packagesToPrune", out var pruning) && pruning.EnumerateObject().Any();
        if (policy != null && !bool.TryParse(policy, out _))
            return new(path, "unverified", "Unsupported RestoreEnablePackagePruning value.");
        if (bool.TryParse(policy, out var enabled) && enabled != hasPruning)
            return new(path, "stale", "Package pruning policy differs from the saved restore metadata.");
        return new(path, "matched");
    }

    // Direct pruning can privatize the child's request; a parent's framework
    // can also prune a package still needed by a lower-framework child.
    internal static string? TryOmit(JsonElement assets, string framework, string id, string request, out bool omitted)
    {
        omitted = false;
        var settings = assets.GetProperty("project").GetProperty("frameworks").GetProperty(framework);
        if (!settings.TryGetProperty("packagesToPrune", out var pruning)) return null;
        var entries = pruning.EnumerateObject().Where(item => item.Name.Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length == 0) return null;
        if (entries.Length != 1) return $"Ambiguous pruning metadata for {id}.";
        var bound = entries[0].Value.GetString() ?? "";
        var normalized = RestoredAssetsValidator.NormalizeRange(request);
        // SDK stable pruning bounds have no minimum and an inclusive maximum.
        // Prereleases/floating requests/custom ranges are deliberately unverified.
        if (!bound.StartsWith("(,", StringComparison.Ordinal) || !bound.EndsWith(']') ||
            StableVersion(bound[2..^1]) is not { } maximum || normalized == null ||
            StableVersion(normalized[1..].Split(',')[0]) is not { } minimum)
            return $"Unsupported pruning range or version request for {id}.";
        if (minimum > maximum || (minimum == maximum && normalized[0] == '(')) return null;
        if (!assets.TryGetProperty("targets", out var targets) || !targets.TryGetProperty(framework, out var target))
            return $"Pruning target metadata is unavailable for {id}.";
        var nodes = target.EnumerateObject().Where(item =>
            item.Name[..item.Name.LastIndexOf('/')].Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (nodes.Length > 1) return $"Ambiguous resolved pruning identity for {id}.";
        if (nodes.Length == 1)
        {
            var node = nodes[0];
            if (!node.Value.TryGetProperty("type", out var type) || type.GetString() != "package" ||
                StableVersion(node.Name[(node.Name.LastIndexOf('/') + 1)..]) is not { } resolved || resolved > maximum)
                return $"Resolved pruning version or package identity is unverified for {id}.";
            foreach (var kind in new[] { "compile", "runtime", "native", "runtimeTargets", "build", "buildTransitive", "buildMultiTargeting", "contentFiles", "analyzers" })
                if (node.Value.TryGetProperty(kind, out var files) && files.EnumerateObject().Any(file =>
                    !file.Name.Replace('\\', '/').EndsWith("/_._", StringComparison.Ordinal) && file.Name != "_._"))
                    return $"Package {id} is in the pruning range but still has active resolved assets.";
        }
        omitted = true;
        return null;
    }

    private static Version? StableVersion(string value)
    {
        var parts = value.Trim().Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var numbers = new int[4];
        for (var index = 0; index < parts.Length; index++)
            if (!int.TryParse(parts[index], out numbers[index]) || numbers[index] < 0 ||
                parts[index].Any(character => !char.IsAsciiDigit(character))) return null;
        return new(numbers[0], numbers[1], numbers[2], numbers[3]);
    }
}
