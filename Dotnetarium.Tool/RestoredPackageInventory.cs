using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dotnetarium.Tool;

// Saved NuGet graph evidence, including packages that have no compile DLLs.
// Do not infer current advisories, authenticity or vulnerable-code reachability.
internal sealed record RestoredPackageInventory(PackageTarget[] Targets, SavedAuditPolicy AuditPolicy,
    SavedAuditDiagnostic[] HistoricalAuditDiagnostics)
{
    internal static RestoredPackageInventory Read(JsonElement assets, string framework, string projectRoot)
    {
        var requests = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (assets.TryGetProperty("project", out var project) && project.TryGetProperty("frameworks", out var frameworks) &&
            frameworks.TryGetProperty(framework, out var settings) && settings.TryGetProperty("dependencies", out var dependencies))
            requests = dependencies.EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var targets = new List<PackageTarget>();
        var projectReferences = new HashSet<string>(ProjectLoader.PathComparer);
        if (project.ValueKind == JsonValueKind.Object && project.TryGetProperty("restore", out var restored) &&
            restored.TryGetProperty("frameworks", out var restoredFrameworks) && restoredFrameworks.TryGetProperty(framework, out var restoredFramework) &&
            restoredFramework.TryGetProperty("projectReferences", out var restoredReferences))
            foreach (var reference in restoredReferences.EnumerateObject()) projectReferences.Add(Path.GetFullPath(reference.Name));
        if (assets.TryGetProperty("targets", out var graphs) && assets.TryGetProperty("libraries", out var libraries))
            foreach (var graph in graphs.EnumerateObject().Where(item => item.Name == framework || item.Name.StartsWith(framework + "/", StringComparison.Ordinal)))
            {
                var versions = graph.Value.EnumerateObject().ToDictionary(item => Id(item.Name), item => Version(item.Name), StringComparer.OrdinalIgnoreCase);
                var nodes = new List<PackageNode>();
                var roots = new List<string>();
                foreach (var entry in graph.Value.EnumerateObject())
                {
                    if (!libraries.TryGetProperty(entry.Name, out var metadata)) continue;
                    var type = Text(metadata, "type");
                    if (type is not ("package" or "project")) continue;
                    var id = Id(entry.Name);
                    requests.TryGetValue(id, out var request);
                    if (request.ValueKind == JsonValueKind.Object || (type == "project" && Text(metadata, "msbuildProject") is { } projectPath &&
                        projectReferences.Contains(Path.GetFullPath(Path.Combine(projectRoot, projectPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))))))
                        roots.Add(id);
                    var automatic = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("autoReferenced", out var auto) && auto.ValueKind == JsonValueKind.True;
                    var edges = entry.Value.TryGetProperty("dependencies", out var children)
                        ? children.EnumerateObject().Select(child => new PackageEdge(child.Name, child.Value.GetString(), versions.GetValueOrDefault(child.Name)))
                            .OrderBy(child => child.Id, StringComparer.Ordinal).ToArray() : [];
                    var kinds = new[] { "compile", "runtime", "runtimeTargets", "native", "build", "buildTransitive", "buildMultiTargeting", "contentFiles" }
                        .Where(kind => entry.Value.TryGetProperty(kind, out var files) && files.EnumerateObject().Any(file => !file.Name.EndsWith("/_._", StringComparison.Ordinal))).ToList();
                    if (metadata.TryGetProperty("files", out var packageFiles) && packageFiles.EnumerateArray().Any(file =>
                        file.GetString()?.StartsWith("analyzers/", StringComparison.Ordinal) == true)) kinds.Add("analyzers");
                    nodes.Add(new(id, Version(entry.Name), type,
                        type == "project" ? "source-project" : automatic ? "automatic" : request.ValueKind == JsonValueKind.Object ? "direct" : "transitive",
                        Text(request, "version"), Text(metadata, "sha512"), kinds.ToArray(), edges));
                }
                targets.Add(new(graph.Name, roots.Order(StringComparer.Ordinal).ToArray(), nodes.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray()));
            }
        JsonElement audit = default;
        if (project.ValueKind == JsonValueKind.Object && project.TryGetProperty("restore", out var restore))
            restore.TryGetProperty("restoreAuditProperties", out audit);
        var diagnostics = assets.TryGetProperty("logs", out var logs) ? logs.EnumerateArray()
            .Where(log => Text(log, "code") is "NU1900" or "NU1901" or "NU1902" or "NU1903" or "NU1904" or "NU1905")
            .Select(log => new SavedAuditDiagnostic(Text(log, "code")!, Text(log, "level"), Text(log, "libraryId"),
                Text(log, "code") switch { "NU1901" => "low", "NU1902" => "moderate", "NU1903" => "high", "NU1904" => "critical", _ => null },
                log.TryGetProperty("targetGraphs", out var targetGraphs) ? targetGraphs.EnumerateArray().Select(value => value.GetString()!).ToArray() : [],
                Text(log, "code") is "NU1901" or "NU1902" or "NU1903" or "NU1904" ? AdvisoryUrls(Text(log, "message")) : []))
            .OrderBy(log => log.Code, StringComparer.Ordinal).ThenBy(log => log.PackageId, StringComparer.Ordinal).ToArray() : [];
        return new(targets.ToArray(), new(Text(audit, "enableAudit"), Text(audit, "auditMode"), Text(audit, "auditLevel")), diagnostics);
    }

    // Retain advisory links, not raw restore messages or feed credentials.
    private static string[] AdvisoryUrls(string? message) => Regex.Matches(message ?? "", @"https://[^\s,'""<>]+")
        .Select(match => match.Value).Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0).Distinct(StringComparer.Ordinal).ToArray();
    private static string Id(string key) => key[..key.LastIndexOf('/')];
    private static string Version(string key) => key[(key.LastIndexOf('/') + 1)..];
    private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal sealed record PackageTarget(string Target, string[] RootDependencyIds, PackageNode[] Nodes);
internal sealed record PackageNode(string Id, string Version, string Type, string Relationship, string? RequestedVersion,
    string? RecordedSha512, string[] AssetKinds, PackageEdge[] Dependencies);
internal sealed record PackageEdge(string Id, string? RequestedVersion, string? ResolvedVersion);
internal sealed record SavedAuditPolicy(string? Enabled, string? Mode, string? MinimumSeverity);
internal sealed record SavedAuditDiagnostic(string Code, string? Level, string? PackageId, string? Severity,
    string[] TargetGraphs, string[] AdvisoryUrls);
