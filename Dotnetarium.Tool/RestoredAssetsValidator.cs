using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dotnetarium.Tool;

internal sealed record RestoredAssetsState(string Path, string Status, string? Reason = null);
internal sealed record PackageInput(string Version, string Aliases, string IncludeAssets, string ExcludeAssets, string? PrivateAssets);

// Validate the restore request, not file timestamps or the selected version alone.
// Matching requests still cannot prove that all custom SDK/import inputs match.
internal static class RestoredAssetsValidator
{
    internal static RestoredAssetsState Validate(string assetsPath, JsonElement assets, string projectPath, string framework,
        IReadOnlyDictionary<string, PackageInput> packages, IEnumerable<string> projectReferences, bool incomplete)
    {
        RestoredAssetsState Stale(string reason) => new(assetsPath, "stale", reason);
        RestoredAssetsState Unknown(string reason) => new(assetsPath, "unverified", reason);
        if (!assets.TryGetProperty("project", out var project) || !project.TryGetProperty("restore", out var restore) ||
            !restore.TryGetProperty("projectPath", out var restoredPath) ||
            !project.TryGetProperty("frameworks", out var frameworks) || !frameworks.TryGetProperty(framework, out var settings))
            return Unknown("Restore request metadata is absent or does not describe this target framework.");
        if (!ProjectLoader.PathComparer.Equals(Path.GetFullPath(restoredPath.GetString()!), projectPath))
            return Stale("The assets file belongs to another project path.");
        if (incomplete) return Unknown("Unsupported project inputs prevent full restore-request validation.");
        var restoredPackages = settings.TryGetProperty("dependencies", out var dependencies)
            ? dependencies.EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (!restoredPackages.TryGetValue(package.Key, out var restored))
                return Stale($"Package {package.Key} was added after restore.");
            var requestedRange = NormalizeRange(package.Value.Version);
            var restoredRange = NormalizeRange(String(restored, "version"));
            if (requestedRange == null || restoredRange == null)
                return Unknown($"Package {package.Key} has an unsupported, floating or unresolved version request.");
            if (!requestedRange.Equals(restoredRange, StringComparison.OrdinalIgnoreCase))
                return Stale($"Package {package.Key} has a different version request than the last restore.");
            if (Tokens(package.Value.Aliases, "global") != Tokens(String(restored, "aliases"), "global") ||
                AssetFlags(package.Value.IncludeAssets, "All") != AssetFlags(String(restored, "include"), "All") ||
                AssetFlags(package.Value.ExcludeAssets, "None") != AssetFlags(String(restored, "exclude"), "None") ||
                AssetFlags(package.Value.PrivateAssets ?? "ContentFiles,Analyzers,Build", "ContentFiles,Analyzers,Build") !=
                    AssetFlags(String(restored, "suppressParent"), "ContentFiles,Analyzers,Build"))
                return Stale($"Package {package.Key} has different aliases or asset-selection metadata than the last restore.");
        }
        foreach (var restored in restoredPackages)
            if (!packages.ContainsKey(restored.Key) && !(restored.Value.TryGetProperty("autoReferenced", out var automatic) && automatic.ValueKind == JsonValueKind.True))
                return Stale($"Package {restored.Key} was removed after restore or comes from an unevaluated input.");
        if (!restore.TryGetProperty("frameworks", out var restoreFrameworks) || !restoreFrameworks.TryGetProperty(framework, out var restoreFramework))
            return Unknown("Restore project-reference metadata is absent.");
        var actualReferences = restoreFramework.TryGetProperty("projectReferences", out var references)
            ? references.EnumerateObject().Select(item => Path.GetFullPath(item.Name)).ToHashSet(ProjectLoader.PathComparer)
            : new HashSet<string>(ProjectLoader.PathComparer);
        if (!actualReferences.SetEquals(projectReferences)) return Stale("The source project-reference graph changed after restore.");
        return new(assetsPath, "matched");
    }

    internal static bool DefaultPrivateAssets(string value) => AssetFlags(value, "ContentFiles,Analyzers,Build") ==
        AssetFlags("ContentFiles,Analyzers,Build", "ContentFiles,Analyzers,Build");

    internal static bool SameRange(string left, string right) => NormalizeRange(left) is { } normalized &&
        normalized.Equals(NormalizeRange(right), StringComparison.OrdinalIgnoreCase);

    internal static RestoredAssetsState ValidateExportedEdges(string assetsPath, JsonElement edge,
        IReadOnlyDictionary<string, string> expected, string project)
    {
        var actual = edge.TryGetProperty("dependencies", out var dependencies)
            ? dependencies.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in expected)
            if (!actual.TryGetValue(dependency.Key, out var range) || !SameRange(dependency.Value, range))
                return new(assetsPath, "stale", $"The parent assets contain a different exported dependency request for {project}: {dependency.Key}.");
        if (actual.Keys.Any(id => !expected.ContainsKey(id)))
            return new(assetsPath, "unverified", $"The parent assets contain unevaluated exported dependencies for {project}.");
        return new(assetsPath, "matched");
    }

    private static string String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : "";

    private static string Tokens(string value, string fallback) => string.Join(",", (string.IsNullOrWhiteSpace(value) ? fallback : value)
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal));

    private static string AssetFlags(string value, string fallback)
    {
        var tokens = Tokens(value, fallback).ToLowerInvariant().Split(',');
        var all = new[] { "analyzers", "build", "buildmultitargeting", "buildtransitive", "compile", "contentfiles", "native", "runtime" };
        return tokens.Contains("all") ? string.Join(",", all.Order(StringComparer.Ordinal)) :
            tokens.Length == 1 && tokens[0] == "none" ? "" : string.Join(",", tokens.Order(StringComparer.Ordinal));
    }

    // Equality normalization for conventional NuGet requests. This is not a
    // resolver: floating/complex requests stay unverified instead of guessed.
    internal static string? NormalizeRange(string value)
    {
        value = Regex.Replace(value, "\\s+", "");
        if (value.Length == 0 || value.Contains('*') || value.Contains("$(", StringComparison.Ordinal)) return null;
        if (value[0] is not ('[' or '(')) return Version(value) is { } version ? $"[{version},)" : null;
        if (value.Length < 3 || value[^1] is not (']' or ')')) return null;
        var bounds = value[1..^1].Split(',');
        if (bounds.Length == 1) return value[0] == '[' && value[^1] == ']' && Version(bounds[0]) is { } exact ? $"[{exact},{exact}]" : null;
        if (bounds.Length != 2) return null;
        var minimum = bounds[0].Length == 0 ? "" : Version(bounds[0]);
        var maximum = bounds[1].Length == 0 ? "" : Version(bounds[1]);
        return minimum == null || maximum == null ? null : $"{value[0]}{minimum},{maximum}{value[^1]}";
    }

    private static string? Version(string value)
    {
        var match = Regex.Match(value, @"^(\d+(?:\.\d+){0,3})(-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success) return null;
        var numbers = match.Groups[1].Value.Split('.');
        var parts = new List<int>();
        foreach (var number in numbers)
        {
            if (!int.TryParse(number, out var part)) return null;
            parts.Add(part);
        }
        while (parts.Count < 3) parts.Add(0);
        if (parts.Count == 4 && parts[3] == 0) parts.RemoveAt(3);
        return string.Join('.', parts) + match.Groups[2].Value;
    }
}
