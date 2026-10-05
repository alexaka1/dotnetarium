using Microsoft.CodeAnalysis;

namespace Dotnetarium.Analyzers
{
    /// <summary>Coverage notifications, separate from security findings.</summary>
    public static class AnalysisDiagnostics
    {
        public const string WorkLimitId = "DNA9000";
        public static readonly DiagnosticDescriptor WorkLimit = new(
            WorkLimitId, "Taint analysis work limit reached",
            "{0}: analysis of '{1}' stopped after {2} work units (limit {3}; {4} flow analyses, {5} block visits, {6} operation visits). Coverage is incomplete for this method; other methods continue. Increase MaxTaintAnalysisWork in dotnetarium.json to retry.",
            "Analysis coverage", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            description: "A bounded per-method dataflow budget prevents recursive or branching call trees from blocking the scan. This notice is not a vulnerability finding.",
            customTags: new[] { WellKnownDiagnosticTags.NotConfigurable });
    }
}
