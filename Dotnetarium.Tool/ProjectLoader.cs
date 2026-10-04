using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Dotnetarium.Tool;

internal sealed class ScanInputs(Workspace workspace, string? msbuildPath = null) : IDisposable
{
    internal Workspace Workspace { get; } = workspace;
    internal string? MSBuildPath { get; } = msbuildPath;
    internal Dictionary<ProjectId, string> TestProjectMetadata { get; } = [];
    internal Dictionary<ProjectId, Dictionary<string, string>> InputProperties { get; } = [];
    internal Dictionary<ProjectId, RestoredAssetsState> RestoredAssets { get; } = [];
    internal Dictionary<ProjectId, RestoredPackageInventory> PackageInventories { get; } = [];
    internal IEnumerable<Project> Projects => Workspace.CurrentSolution.Projects
        .Where(project => project.Language == LanguageNames.CSharp);
    public void Dispose() => Workspace.Dispose();
}

internal static class ProjectLoader
{
    internal static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    // Read the solution inventory before loading projects so one broken project
    // cannot prevent independent projects from being opened.
    internal static string[] FindProjects(string target)
    {
        var root = Path.GetDirectoryName(target)!;
        IEnumerable<string> paths = Path.GetExtension(target).ToLowerInvariant() switch
        {
            ".csproj" => [target],
            ".sln" => Regex.Matches(File.ReadAllText(target),
                "^Project\\([^\\r\\n]+?\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+\\.csproj)\"",
                RegexOptions.Multiline | RegexOptions.IgnoreCase).Select(match => match.Groups[1].Value),
            ".slnx" => XDocument.Load(target).Descendants().Where(element => element.Name.LocalName == "Project")
                .Select(element => (string?)element.Attribute("Path"))
                .OfType<string>().Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)),
            _ => throw new ArgumentException("Expected a .csproj, .sln, or .slnx path.")
        };
        return paths.Select(path => Path.GetFullPath(Path.Combine(root,
                path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))))
            .Distinct(PathComparer).ToArray();
    }

    internal static async Task<ScanInputs> LoadProjectAwareAsync(string target, ScanReport report)
    {
        var query = VisualStudioInstanceQueryOptions.Default;
        query.WorkingDirectory = Path.GetDirectoryName(target)!;
        var sdk = MSBuildLocator.QueryVisualStudioInstances(query).FirstOrDefault() ??
            throw new InvalidOperationException("No compatible .NET SDK was found.");
        MSBuildLocator.RegisterInstance(sdk);
        var workspace = MSBuildWorkspace.Create();
        var inputs = new ScanInputs(workspace, sdk.MSBuildPath);
        workspace.RegisterWorkspaceFailedHandler(diagnostic =>
            report.Warn("workspace", diagnostic.Diagnostic.Message));
        try
        {
            foreach (var path in FindProjects(target))
            {
                try
                {
                    if (!workspace.CurrentSolution.Projects.Any(project => PathComparer.Equals(project.FilePath, path)))
                        await workspace.OpenProjectAsync(path);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    report.Warn("project-load", $"{path}: {error.Message}");
                    report.SkippedProjects.Add(path);
                }
            }
            return inputs;
        }
        catch { inputs.Dispose(); throw; }
    }
}
