# Bounded taint analysis and scanner performance

The build-independent experiment exposed repeated and sometimes exponential
interprocedural work in the shared analyzer engine. The following improvements
are also applied to the MSBuild-based global tool and NuGet analyzer.

## Engine changes

| Change | Purpose and boundary |
| --- | --- |
| Exclude EF migrations and model snapshots | Skip all taint roots and body traversal, including nested helpers. Keep direct literal-secret checks, including generated snapshots. |
| Source/sink eligibility | Avoid dataflow only when the reachable code is proven irrelevant. Keep uncertain calls eligible. An inherited `System.Object` source-model container alone is not an origin. |
| Framework discovery filters | Check invocation names before semantic binding for Minimal APIs, endpoint filters, messaging and component rendering. Validate framework identity after candidate selection. |
| Callback eligibility | Use the same source-reachability proof for nested callbacks. Retain real request inputs, helper origins and captured-state analysis. |
| Interface dispatch cache | Reuse source interface/virtual target discovery within a compilation. Keep configured sources and sinks and DI dispatch behavior. |
| Points-to reuse | Reuse completed call-site results only when input state, aliases, captures and analysis context match. Never reuse an active recursive call as a completed summary. |
| Compilation-lifetime lookup caches | Reuse types, disposal helpers, operation blocks, CFGs and DI registrations while their compilation is alive. Completed compilation/cache cycles remain collectible. |
| Root work budget | Share 10,000 units across eligibility and nested dataflow for each method/rule. Stop the affected analysis and continue independent methods. Never cache an aborted result as complete. |

Disposal tracking, configured interprocedural depth and framework-specific source
models remain enabled. Ordinary code is not excluded because its directory or
class happens to be named `Migration`.

## How the main scanner differs

The main tool retains `MSBuildWorkspace`, SDK selection and its existing CLI
options. It evaluates the target's project/build inputs and loads every target
framework selected by the workspace. The experiment can reconstruct restored
inputs without custom MSBuild targets and explicitly select a framework. Those
experimental loading options are not included in this change.

The main tool analyzes a bounded number of project compilations concurrently,
using half the available processors with a cap of **four**, and starting larger
projects first. Each Roslyn driver also runs analyzer callbacks
concurrently. Findings and coverage notices are collected safely and ordered for
output. Compiler diagnostics are limited to the first 20 per project on the
console, with a total-count notice; failures remain explicit.

Requested SARIF is now preserved when compilation errors or work cutoffs make a
scan incomplete. Coverage notices are SARIF invocation notifications, not
security results or rules. An incomplete analysis returns **2**, including with
`--fail`. A complete scan with findings returns **1** only with `--fail`.

## LANCommander measurement

Measurements use the Windows development machine, fresh scanner processes and
the same local LANCommander solution. The unmodified main scanner and migration
exclusion alone both exceed the 60-second cutoff without producing SARIF.

The first engine port completes in **87.6 seconds**. Scheduling larger projects
first and tightening binder discovery reduces this to **77.4 seconds**, with
**1.50 GiB** peak working set, **32 compilations** and **22 findings**. All completed main-tool
runs retain identical finding/flow JSON.

Raising the project cap to eight takes **92.0 seconds** and **1.39 GiB**, so the
four-project cap is retained. The scheduled four-project run reports **2,212 work
cutoffs**, zero migration budget notices and zero analyzer failures. An additional
configuration-sharing cache did not improve turnaround and caused excessive
memory use in the full test suite, so it is not retained.

This is not a like-for-like comparison with the experiment's 50.5-second run:
that run selected .NET 10 inputs and analyzed 27 compilations. Main includes the
SDK's .NET 8/9/10 variants and spends roughly 13 seconds loading the workspace.
The solution also has missing Aspire inputs, failed npm targets and compiler
errors. Budget cutoffs and these input failures remain visible as partial
coverage; the measurements do not prove complete large-corpus taint coverage.

See [analysis scope and configuration](RuleConfiguration.md#ef-migration-scope)
for the migration policy and increasing the work budget.

## Verification

All **704 unit tests pass**. These include recursive budget exhaustion and
continuation, aborted-result cache handling, disposal and metadata cache lifetime,
interface/DI dispatch, source/sink eligibility and real EF Core 10 migration
exclusion with direct secrets preserved.

A main-tool SharpSaster comparison retains all **41 findings** and their exact
result/flow objects, with complete coverage and no budget cutoffs. Ordering is
normalized for this comparison because main now orders equal-location findings
by their messages.

The installed global tool and packed analyzer pass the .NET 8/10 CLI suite,
including constant secrets in real EF migrations/generated snapshots, ordinary
caller findings, independent findings after a recursive cutoff, exit code 2 with
and without `--fail`, and partial SARIF on compilation errors. Real provider sink
smoke checks and Razor/Blazor render-mode and safe-output checks also pass.
CI runs the unit and installed-package checks on Windows
and Linux.
