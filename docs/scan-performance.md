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
| Root work budget | Share the profile's budget across eligibility and nested dataflow for each method/rule. Stop the affected analysis and continue independent methods. Never cache an aborted result as complete. |

Disposal tracking, configured interprocedural depth and framework-specific source
models remain enabled. Ordinary code is not excluded because its directory or
class happens to be named `Migration`.

## How the main scanner differs

The main tool retains `MSBuildWorkspace`, SDK selection and its existing CLI
options by default. It evaluates the target's project/build inputs and loads
the target frameworks selected by the workspace. Opt-in `-nb` / `--no-build`
can reconstruct inputs without custom MSBuild targets; configuration/framework
selection is available in both modes. See [scan modes](scan-modes.md).

The main tool analyzes a bounded number of project compilations concurrently,
using half the available processors with a cap of **four**, and starting larger
projects first. Each Roslyn driver also runs analyzer callbacks
concurrently. Findings and coverage notices are collected safely and ordered for
output. Compiler diagnostics are limited to the first 20 per project on the
console, with a total-count notice; failures remain explicit.

Requested SARIF is now preserved when compilation errors or work cutoffs make a
scan incomplete. Coverage notices are SARIF invocation notifications, not
security results or rules. Default mode returns **2** for compiler errors or
cutoffs, including with `--fail`. No-build mode accepts these as partial coverage
and returns **0**, or **1** when findings are present with `--fail`. Genuine
analyzer/tool failures still return **2** in both modes.

## Analysis profiles

The package defaults to `fast`: source-method and lambda/local-function depth
**3**, with **1,000 work units** per root/rule. The tool defaults to `full`:
depth **5**, with **5,000 units**. The opt-in `max` profile uses depth **10** and
**10,000 units**. Explicit JSON profiles and numeric limits
override these defaults. Binder and component-state summaries use the same
depth settings. Fast analysis aborts at either limit and summarizes affected
roots once per rule; full/max analysis retains detailed work-limit notices and the
existing Roslyn depth behavior. See [configuration](RuleConfiguration.md#analysis-profiles-and-call-depth).

Fresh-process measurements on the Windows development machine separate analyzer
execution from project loading and compilation. Services uses the SDK-loaded
.NET 10 compilation (133 syntax trees); server uses reconstructed .NET 10 inputs (137 trees),
so its finding comparison does not certify generated Razor coverage or a full
IDE scan. All enabled rules run concurrently within each compilation.

| Method/lambda depth / work | Services analyzer seconds | Services cutoffs | Server analyzer seconds | Server findings | Server cutoffs |
| --- | --- | --- | --- | --- | --- |
| fast: 3 / 1,000 | 4.23 | 2,200 | 3.21 | 11 | 60 |
| full: 5 / 5,000 | 7.73 | 340 | 4.15 | 11 | 23 |
| max: 10 / 10,000 | 12.73 | 301 | 6.31 | 11 | 16 |

The server finding sets match by rule, source file, line and message, not just
their counts. Services reports no security findings in these inputs. No analyzer
failures occurred. Peak working set for Services is 0.43/0.54/0.59 GiB for
fast/full/max; Server stays at 0.93 GiB. Fast profiles emit eight summary notices on each
measured project rather than thousands of individual warnings. Cutoff counts
describe incomplete roots, not vulnerabilities or a proportion of safe code.

An earlier 3 / 500 trial took 3.42 seconds for Services and 3.01 for Server,
with 2,403 and 157 cutoffs respectively. The default 3 / 1,000 leaves more room
for common controller/service/repository flows at a modest cost. A 500-unit override remains available for
latency-sensitive projects. Compilation, generators, dependencies, cold caches
and other IDE analyzers add their own cost; these figures are not an end-to-end
build, whole-solution or per-keystroke latency guarantee.

Profile checks cover configuration precedence, numeric overrides, deep helper
calls, HTTP binder summaries and Blazor state summaries. All 918 unit tests pass.
Freshly packed .NET 8/10 analyzer and CLI checks verify the default profiles and
explicit fast/full/max selection in both loading modes. Generated Razor checks
retain direct findings with fast and expose its cross-component work cutoff;
full restores that flow. No-build exit policy and boundary-precision fixtures pass.

## Earlier LANCommander full-solution measurement

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

## Earlier performance-port verification

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
