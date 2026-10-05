using System.Collections.Immutable;
using System.Collections.Concurrent;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Dotnetarium.Analyzers;
using Dotnetarium.Config;

namespace Dotnetarium.Tool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Any(arg => arg is "--help" or "-h" or "-?"))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        Options options;
        try { options = Options.Parse(args); }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            PrintUsage();
            return 2;
        }

        try
        {
            if (options.ConfigPath != null)
                new ConfigurationReader().GetProjectConfiguration(
                    ImmutableArray.Create<AdditionalText>(new FileAdditionalText(options.ConfigPath)));

            var target = Path.GetFullPath(options.Target);
            if (!File.Exists(target))
                throw new FileNotFoundException("Project or solution was not found.", target);
            var root = Path.GetDirectoryName(target)!;
            var defaultConfig = Path.Combine(root, "dotnetarium.json");
            var report = new ScanReport();
            var sdkQuery = VisualStudioInstanceQueryOptions.Default;
            sdkQuery.WorkingDirectory = root;
            var sdk = MSBuildLocator.QueryVisualStudioInstances(sdkQuery).FirstOrDefault() ??
                throw new InvalidOperationException("No compatible .NET SDK was found.");
            MSBuildLocator.RegisterInstance(sdk);

            using var workspace = MSBuildWorkspace.Create();
            var workspaceErrors = new ConcurrentQueue<WorkspaceDiagnostic>();
            workspace.RegisterWorkspaceFailedHandler(diagnostic =>
            {
                workspaceErrors.Enqueue(diagnostic.Diagnostic);
            });

            var projects = Path.GetExtension(target).ToLowerInvariant() switch
            {
                ".csproj" => new[] { await workspace.OpenProjectAsync(target) },
                ".sln" or ".slnx" => (await workspace.OpenSolutionAsync(target)).Projects.ToArray(),
                _ => throw new ArgumentException("Expected a .csproj, .sln, or .slnx path.")
            };
            if (!projects.Any(project => project.Language == LanguageNames.CSharp))
                throw new ArgumentException("The target contains no C# projects.");

            var analyzerTypes = typeof(DnaRuleCatalog).Assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(type) &&
                               type.GetCustomAttributes(typeof(DiagnosticAnalyzerAttribute), false).Length > 0)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
            var analyzers = analyzerTypes.Select(type => (DiagnosticAnalyzer)Activator.CreateInstance(type)!).ToImmutableArray();
            var diagnostics = new ConcurrentBag<Diagnostic>();
            // Roslyn already runs operation-block actions concurrently. Limit
            // active projects so nested analysis does not multiply without bound.
            var projectConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            // Start large projects first so one expensive compilation is not
            // left running after the smaller projects have drained the queue.
            await Parallel.ForEachAsync(projects.Where(project => project.Language == LanguageNames.CSharp)
                    .OrderByDescending(project => project.DocumentIds.Count)
                    .ThenBy(project => project.Name, StringComparer.Ordinal),
                new ParallelOptions { MaxDegreeOfParallelism = projectConcurrency }, async (project, cancellationToken) =>
            {
                GeneratorCoverage.Observe(project, report);
                var compilation = await project.GetCompilationAsync();
                if (compilation == null)
                {
                    report.Fail("compilation-load", $"Unable to compile {project.Name}.");
                    report.SkippedProjects.Add(project.Name);
                    return;
                }

                var additionalFiles = project.AnalyzerOptions.AdditionalFiles;
                if (options.ConfigPath != null)
                    additionalFiles = additionalFiles
                        .Where(file => !IsConfigurationFile(file.Path))
                        .Append(new FileAdditionalText(options.ConfigPath))
                        .ToImmutableArray();
                else if (File.Exists(defaultConfig) && !additionalFiles.Any(file => IsConfigurationFile(file.Path)))
                    additionalFiles = additionalFiles.Add(new FileAdditionalText(defaultConfig));
                var configOptions = project.AnalyzerOptions.AnalyzerConfigOptionsProvider;
                if (!configOptions.GlobalOptions.TryGetValue("build_property.IsTestProject", out _))
                    configOptions = await ProjectAnalysisOptions.WithTestProjectMetadataAsync(configOptions, project.FilePath!, sdk.MSBuildPath);
                var analyzerOptions = new AnalyzerOptions(additionalFiles, configOptions);
                var result = await compilation.WithAnalyzers(analyzers, analyzerOptions).GetAllDiagnosticsAsync();
                report.AnalyzedProjects.Add(project.Name);
                var projectErrors = result.Where(diagnostic =>
                    diagnostic.Id == "AD0001" ||
                    (diagnostic.Severity == DiagnosticSeverity.Error &&
                     !diagnostic.Id.StartsWith("DNA", StringComparison.Ordinal))).ToArray();
                foreach (var diagnostic in result.Where(diagnostic => diagnostic.Id.StartsWith("DNA", StringComparison.Ordinal) &&
                    diagnostic.Id != AnalysisDiagnostics.WorkLimitId)) diagnostics.Add(diagnostic);
                foreach (var notice in result.Where(diagnostic => diagnostic.Id == AnalysisDiagnostics.WorkLimitId))
                    report.Warn("analysis-budget", $"{project.Name}: {notice}");
                foreach (var error in projectErrors.Take(20))
                    report.Fail(error.Id == "AD0001" ? "analyzer-failure" : "compiler-error", $"{project.Name}: {error}");
                if (projectErrors.Length > 20)
                    report.Fail("compiler-error-summary", $"{project.Name}: {projectErrors.Length} compiler/analyzer errors; the first 20 are shown.");
            });

            foreach (var error in workspaceErrors.DistinctBy(error => (error.Kind, error.Message)))
                if (error.Kind == WorkspaceDiagnosticKind.Failure)
                    report.Fail("workspace-error", "Workspace: " + error.Message);
                else
                    report.Warn("workspace-warning", "Workspace: " + error.Message);

            var findings = diagnostics
                .GroupBy(diagnostic => new
                {
                    diagnostic.Id,
                    Path = diagnostic.Location.SourceTree?.FilePath,
                    diagnostic.Location.SourceSpan.Start,
                    Message = diagnostic.GetMessage()
                })
                .Select(group => group.First())
                .OrderBy(diagnostic => diagnostic.Location.SourceTree?.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(diagnostic => diagnostic.Location.SourceSpan.Start)
                .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.GetMessage(), StringComparer.Ordinal)
                .ToArray();

            foreach (var diagnostic in findings)
            {
                var line = SourceLocationSpan.GetDisplaySpan(diagnostic.Location);
                var path = line.Path;
                if (!string.IsNullOrEmpty(path) && Path.IsPathRooted(path))
                    path = Path.GetRelativePath(root, path);
                var cwe = DnaRuleCatalog.TryGetCwe(diagnostic.Id, out var id)
                    ? $" [CWE-{id}]" : string.Empty;
                Console.WriteLine($"{path}({line.StartLinePosition.Line + 1},{line.StartLinePosition.Character + 1}): {diagnostic.Id}{cwe}: {diagnostic.GetMessage()}");
            }

            foreach (var notice in report.Notices.Distinct().OrderBy(notice => notice.Id, StringComparer.Ordinal).ThenBy(notice => notice.Message, StringComparer.Ordinal))
                Console.Error.WriteLine($"{(notice.IsFailure ? "Error" : "Coverage")}: {notice.Message}");
            Console.WriteLine($"{findings.Length} security finding(s){(report.IsPartial ? " (partial scan)" : string.Empty)}; {report.AnalyzedProjects.Count} project compilation(s) analyzed.");
            if (options.SarifPath != null)
                await SarifWriter.WriteAsync(options.SarifPath, target, findings, report);
            if (report.HasIncompleteAnalysis)
            {
                Console.Error.WriteLine("Scan incomplete: project/workspace errors or taint work limits occurred; see coverage notices.");
                return 2;
            }
            return options.Fail && findings.Length > 0 ? 1 : 0;
        }
        catch (System.Text.Json.JsonException error)
        {
            Console.Error.WriteLine("Invalid dotnetarium.json: " + error.Message);
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 2;
        }
    }

    private static void PrintUsage() => Console.WriteLine(
        "Usage: dotnetarium <project.csproj|solution.sln|solution.slnx> [options]\n" +
        "  --sarif <path>             Write SARIF 2.1.0\n" +
        "  --config <path>            Override dotnetarium.json (version 2.0)\n" +
        "  --fail                     Return 1 when findings are present\n" +
        "  -h, --help                 Show this help");

    private sealed class FileAdditionalText(string path) : AdditionalText
    {
        private readonly string sourcePath = System.IO.Path.GetFullPath(path);
        public override string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, "dotnetarium.json");
        public override SourceText GetText(CancellationToken cancellationToken = default) =>
            SourceText.From(File.ReadAllText(sourcePath));
    }

    private static bool IsConfigurationFile(string path) =>
        string.Equals(Path.GetFileName(path), "dotnetarium.json", StringComparison.OrdinalIgnoreCase);

    private sealed record Options(string Target, string? SarifPath, string? ConfigPath,
        bool Fail)
    {
        internal static Options Parse(string[] args)
        {
            string? target = null, sarif = null, config = null;
            bool fail = false;
            for (int index = 0; index < args.Length; index++)
            {
                var arg = args[index];
                string NextValue() => ++index < args.Length
                    ? args[index] : throw new ArgumentException($"Missing value after {arg}.");
                switch (arg)
                {
                    case "--sarif": sarif = NextValue(); break;
                    case "--config": config = NextValue(); break;
                    case "--fail": fail = true; break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException($"Unknown option {arg}.");
                        if (target != null)
                            throw new ArgumentException("Specify one project or solution.");
                        target = arg;
                        break;
                }
            }
            if (target == null) throw new ArgumentException("A project or solution path is required.");
            if (config != null && !File.Exists(config)) throw new ArgumentException($"Configuration not found: {config}");
            return new Options(target, sarif, config, fail);
        }
    }
}
