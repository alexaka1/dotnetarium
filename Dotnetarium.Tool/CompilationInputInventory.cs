using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Dotnetarium.Tool;

// An opt-in diagnostic artifact, separate from SARIF findings. Capture the
// compilation used by the engine; do not load additional projects or generators.
internal sealed class CompilationInputInventory(string target, bool direct, ScanSelection selection)
{
    private readonly string root = Path.GetDirectoryName(target)!;
    private readonly ConcurrentDictionary<ProjectId, object> projects = new();

    internal async Task CaptureAsync(Project project, Compilation compilation, AnalyzerOptions options, ScanInputs inputs)
    {
        var documents = project.Documents.Where(document => document.FilePath != null)
            .GroupBy(document => document.FilePath!, ProjectLoader.PathComparer)
            .ToDictionary(group => group.Key, group => group.First(), ProjectLoader.PathComparer);
        var sources = new List<object>();
        foreach (var tree in compilation.SyntaxTrees.OrderBy(tree => tree.FilePath, StringComparer.Ordinal))
        {
            var text = await tree.GetTextAsync();
            // Compilation trees absent from normal documents are source-generator
            // output. A .g.cs filename alone is only a generated-code candidate.
            var documentBacked = documents.ContainsKey(tree.FilePath);
            var synthetic = direct && documentBacked && ProjectLoader.PathComparer.Equals(tree.FilePath,
                Path.Combine(Path.GetDirectoryName(project.FilePath!)!, "obj", "Dotnetarium.ImplicitUsings.g.cs"));
            sources.Add(new
            {
                path = Relative(tree.FilePath),
                origin = synthetic ? "synthesized-usings" : documentBacked ? "document" : "source-generator",
                generatedCandidate = !documentBacked || synthetic || IsGeneratedCandidate(tree.FilePath),
                characters = text.Length,
                sha256 = Hash(text.ToString()),
                settings = ParseSettings(tree.Options)
            });
        }

        inputs.InputProperties.TryGetValue(project.Id, out var properties);
        string? Property(string name) => properties?.GetValueOrDefault(name) ??
            (options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property." + name, out var value) ? value : null);
        var frameworkSymbol = FrameworkSymbol(project.ParseOptions);
        var framework = Property("TargetFramework") ?? frameworkSymbol?.Replace("NET", "net").Replace('_', '.');
        var references = compilation.References.Select(reference =>
        {
            var symbol = compilation.GetAssemblyOrModuleSymbol(reference);
            var dependency = reference is CompilationReference compiled
                ? inputs.Projects.FirstOrDefault(candidate => candidate.AssemblyName == compiled.Compilation.AssemblyName &&
                    candidate.Documents.Any(document => compiled.Compilation.SyntaxTrees.Any(tree =>
                        ProjectLoader.PathComparer.Equals(tree.FilePath, document.FilePath))))?.FilePath : null;
            return new
            {
                path = reference is PortableExecutableReference pe ? Relative(pe.FilePath ?? reference.Display ?? "") : null,
                project = dependency == null ? null : Relative(dependency),
                kind = reference is CompilationReference ? "project" : "metadata",
                targetFramework = reference is CompilationReference frameworkReference
                    ? FrameworkSymbol(frameworkReference.Compilation.SyntaxTrees.FirstOrDefault()?.Options)?.Replace("NET", "net").Replace('_', '.') : null,
                identity = symbol is IAssemblySymbol assembly ? assembly.Identity.ToString() : symbol?.Name,
                aliases = reference.Properties.Aliases.Order(StringComparer.Ordinal).ToArray(),
                embedInteropTypes = reference.Properties.EmbedInteropTypes,
                imageKind = reference.Properties.Kind.ToString()
            };
        }).OrderBy(reference => reference.identity, StringComparer.Ordinal).ThenBy(reference => reference.path, StringComparer.Ordinal).ToArray();
        var settings = compilation.Options as CSharpCompilationOptions;
        projects[project.Id] = new
        {
            path = Relative(project.FilePath ?? ""),
            name = project.Name,
            assemblyName = compilation.AssemblyName,
            targetFramework = framework,
            targetFrameworkSource = Property("TargetFramework") != null ? "project-metadata" : frameworkSymbol != null ? "preprocessor-symbol" : "unavailable",
            configuration = Property("Configuration"),
            platformProperty = Property("Platform"),
            restoredAssets = inputs.RestoredAssets.TryGetValue(project.Id, out var assets) ? new
            {
                path = Relative(assets.Path), status = assets.Status, reason = assets.Reason
            } : null,
            parse = ParseSettings(project.ParseOptions),
            compilation = new
            {
                outputKind = compilation.Options.OutputKind.ToString(),
                optimizationLevel = compilation.Options.OptimizationLevel.ToString(),
                platform = compilation.Options.Platform.ToString(),
                nullable = settings?.NullableContextOptions.ToString(),
                allowUnsafe = settings?.AllowUnsafe,
                checkOverflow = compilation.Options.CheckOverflow,
                warningLevel = compilation.Options.WarningLevel,
                generalDiagnosticOption = compilation.Options.GeneralDiagnosticOption.ToString(),
                specificDiagnosticOptions = compilation.Options.SpecificDiagnosticOptions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.ToString())
            },
            isTestProject = options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.IsTestProject", out var test) ? test : null,
            sources,
            references,
            projectReferences = project.ProjectReferences.Select(reference => new
            {
                path = Relative(project.Solution.GetProject(reference.ProjectId)?.FilePath ?? ""),
                targetFramework = FrameworkSymbol(project.Solution.GetProject(reference.ProjectId)?.ParseOptions)?.Replace("NET", "net").Replace('_', '.'),
                aliases = reference.Aliases.Order(StringComparer.Ordinal).ToArray(),
                embedInteropTypes = reference.EmbedInteropTypes
            }).OrderBy(reference => reference.path, StringComparer.Ordinal).ToArray(),
            analyzerConfigs = await DocumentFilesAsync(project.AnalyzerConfigDocuments),
            additionalFiles = options.AdditionalFiles.OrderBy(file => file.Path, StringComparer.Ordinal).Select(file => new
            {
                path = Relative(file.Path),
                sha256 = file.GetText() is { } text ? Hash(text.ToString()) : null
            }).ToArray(),
            analyzerReferences = project.AnalyzerReferences.Select(reference => new
            {
                path = Relative(reference.FullPath ?? ""), display = reference.Display
            }).OrderBy(reference => reference.path, StringComparer.Ordinal).ToArray()
        };
    }

    internal async Task WriteAsync(string path, ScanReport report, ScanInputs inputs)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var stream = File.Create(fullPath);
        await JsonSerializer.SerializeAsync(stream, new
        {
            schemaVersion = 1,
            target = Relative(target),
            loadingMode = direct ? "direct" : "project",
            selection = new { configuration = selection.Configuration, framework = selection.Framework },
            projects = inputs.Projects.Where(project => projects.ContainsKey(project.Id)).Select(project => projects[project.Id]).ToArray(),
            // Dependency evidence survives even if a project has no usable
            // compilation. This snapshot does not perform an advisory lookup.
            restoreInputs = inputs.RestoredAssets.Select(pair => new
            {
                project = Relative(inputs.Workspace.CurrentSolution.GetProject(pair.Key)?.FilePath ?? ""),
                targetFramework = inputs.InputProperties.GetValueOrDefault(pair.Key)?.GetValueOrDefault("TargetFramework"),
                assets = new { path = Relative(pair.Value.Path), status = pair.Value.Status, reason = pair.Value.Reason },
                advisoryCheck = "not-performed",
                packageInventory = inputs.PackageInventories.GetValueOrDefault(pair.Key)
            }).OrderBy(input => input.project, StringComparer.Ordinal).ThenBy(input => input.targetFramework, StringComparer.Ordinal).ToArray(),
            analyzedProjects = report.AnalyzedProjects.Order(StringComparer.Ordinal).ToArray(),
            skippedProjects = report.SkippedProjects.Distinct().Order(StringComparer.Ordinal).ToArray(),
            notices = report.Notices.Distinct().OrderBy(notice => notice.Id, StringComparer.Ordinal)
                .ThenBy(notice => notice.Message, StringComparer.Ordinal)
                .Select(notice => new { id = notice.Id, message = notice.Message, isFailure = notice.IsFailure })
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private async Task<object[]> DocumentFilesAsync(IEnumerable<TextDocument> documents)
    {
        var files = new List<object>();
        foreach (var document in documents.OrderBy(document => document.FilePath, StringComparer.Ordinal))
            files.Add(new { path = Relative(document.FilePath ?? ""), sha256 = Hash((await document.GetTextAsync()).ToString()) });
        return files.ToArray();
    }

    private static object? ParseSettings(ParseOptions? options) => options is CSharpParseOptions csharp ? new
    {
        languageVersion = csharp.LanguageVersion.ToDisplayString(),
        kind = csharp.Kind.ToString(),
        documentationMode = csharp.DocumentationMode.ToString(),
        symbols = csharp.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray(),
        features = csharp.Features.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value)
    } : null;

    private static string? FrameworkSymbol(ParseOptions? options)
    {
        var symbols = (options as CSharpParseOptions)?.PreprocessorSymbolNames
            .Where(symbol => System.Text.RegularExpressions.Regex.IsMatch(symbol, "^NET[0-9]+_[0-9]+$")).Distinct().ToArray();
        return symbols?.Length == 1 ? symbols[0] : null;
    }

    private string Relative(string path) => string.IsNullOrEmpty(path) ? "" :
        (Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path).Replace('\\', '/');
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool IsGeneratedCandidate(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals("obj", StringComparison.OrdinalIgnoreCase) || part.Equals("bin", StringComparison.OrdinalIgnoreCase)) ||
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase);
}
