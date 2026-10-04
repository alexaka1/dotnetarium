using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
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
            using var inputs = options.ExperimentalDirect
                ? await new DirectProjectLoader(report).LoadAsync(target)
                : await ProjectLoader.LoadProjectAwareAsync(target, report);

            var analyzerTypes = typeof(DnaRuleCatalog).Assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(type) &&
                               type.GetCustomAttributes(typeof(DiagnosticAnalyzerAttribute), false).Length > 0)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
            var analyzers = analyzerTypes.Select(type => (DiagnosticAnalyzer)Activator.CreateInstance(type)!).ToImmutableArray();
            var diagnostics = new List<Diagnostic>();
            foreach (var project in inputs.Projects)
            {
                Compilation? compilation;
                try { compilation = await project.GetCompilationAsync(); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    report.Warn("compilation-load", $"{project.Name}: {error.Message}");
                    report.SkippedProjects.Add(project.Name);
                    continue;
                }
                if (compilation == null || !compilation.SyntaxTrees.Any())
                {
                    report.Warn("compilation-load", $"{project.Name}: no usable source compilation.");
                    report.SkippedProjects.Add(project.Name);
                    continue;
                }
                if (compilation.GetSpecialType(SpecialType.System_Object).TypeKind == TypeKind.Error)
                {
                    report.Warn("compilation-load", $"{project.Name}: core framework symbols are unavailable; no usable semantic analysis.");
                    report.SkippedProjects.Add(project.Name);
                    continue;
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
                if (inputs.TestProjectMetadata.TryGetValue(project.Id, out var isTestProject))
                    configOptions = new ProjectAnalysisOptions(configOptions, isTestProject);
                else if (!configOptions.GlobalOptions.TryGetValue("build_property.IsTestProject", out _) && inputs.MSBuildPath != null)
                {
                    try
                    {
                        configOptions = await ProjectAnalysisOptions.WithTestProjectMetadataAsync(configOptions, project.FilePath!, inputs.MSBuildPath);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        report.Warn("project-metadata", $"{project.Name}: {error.Message}");
                    }
                }
                var analyzerOptions = new AnalyzerOptions(additionalFiles, configOptions);
                try
                {
                    var result = await compilation.WithAnalyzers(analyzers, analyzerOptions).GetAllDiagnosticsAsync();
                    report.AnalyzedProjects.Add(project.Name);
                    diagnostics.AddRange(result.Where(diagnostic => diagnostic.Id.StartsWith("DNA", StringComparison.Ordinal)));
                    foreach (var error in result.Where(diagnostic => diagnostic.Id == "AD0001" ||
                        (diagnostic.Severity == DiagnosticSeverity.Error && !diagnostic.Id.StartsWith("DNA", StringComparison.Ordinal))))
                    {
                        if (error.Id == "AD0001") report.Fail("analyzer-failure", $"{project.Name}: {error}");
                        else report.Warn("compiler-error", $"{project.Name}: {error}");
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    report.Fail("analysis-failure", $"{project.Name}: {error.Message}");
                    report.SkippedProjects.Add(project.Name);
                }
            }
            if (report.AnalyzedProjects.Count == 0) report.Fail("no-analysis", "No usable C# projects were analyzed.");

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
                var line = diagnostic.Location.GetLineSpan();
                var path = line.Path;
                if (!string.IsNullOrEmpty(path))
                    path = Path.GetRelativePath(root, path);
                var cwe = DnaRuleCatalog.TryGetCwe(diagnostic.Id, out var id)
                    ? $" [CWE-{id}]" : string.Empty;
                Console.WriteLine($"{path}({line.StartLinePosition.Line + 1},{line.StartLinePosition.Character + 1}): {diagnostic.Id}{cwe}: {diagnostic.GetMessage()}");
            }

            foreach (var notice in report.Notices.Distinct())
                Console.Error.WriteLine($"{(notice.IsFailure ? "Error" : "Coverage")}: {notice.Message}");
            Console.WriteLine($"{findings.Length} security finding(s){(report.IsPartial ? " (partial scan)" : string.Empty)}; {report.AnalyzedProjects.Count} project compilation(s) analyzed.");
            if (options.SarifPath != null)
                await SarifWriter.WriteAsync(options.SarifPath, target, findings, report,
                    options.ExperimentalDirect ? "direct" : "project");
            if (report.HasFailures) return 2;
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
        "  --experimental-direct      Reconstruct Roslyn inputs without MSBuild targets\n" +
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
        bool Fail, bool ExperimentalDirect)
    {
        internal static Options Parse(string[] args)
        {
            string? target = null, sarif = null, config = null;
            bool fail = false, experimentalDirect = false;
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
                    case "--experimental-direct": experimentalDirect = true; break;
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
            return new Options(target, sarif, config, fail, experimentalDirect);
        }
    }
}
