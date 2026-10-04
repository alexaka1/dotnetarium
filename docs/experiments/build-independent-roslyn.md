# Build-independent Roslyn experiment

Status: first prototype implemented and locally verified on Windows.
Branch: `experiment/build-independent-roslyn`.
This work must stay off `main` until its coverage and limitations are reviewed.

## Objective

Scan available C# code without a full application build or a hard stop on
compilation errors. Preserve Roslyn semantics, the existing analyzers, taint
engine, configuration, and real SARIF flow witnesses. Do not replace missing
symbols with name-based security guesses.

## Implementation plan

- [x] Record the current small-project findings and representative engine baseline.
- [x] Separate loading inputs and coverage diagnostics from analyzer execution.
- [x] Continue after recoverable compilation/project-loading errors; always export
      available findings and partial coverage to SARIF.
- [x] Distinguish compiler/loading limitations from analyzer failures. Exit 0 for
      usable scans, 1 for findings with `--fail`, and 2 for invocation/configuration
      errors, no usable analysis, or internal tool/analyzer failures.
- [x] Prototype a direct loader for conventional SDK-style .NET 8/10 projects.
      Use framework reference packs, cached restored package metadata, project
      references, common properties, implicit usings, and source selection.
- [x] Preserve per-project/per-target-framework compilations. Report unsupported
      imports/conditions, missing dependencies, and missing generated code.
- [x] Direct loading must not run custom targets, restore, or arbitrary generators.
      Keep a way to select it before any design-time loading occurs.
- [x] Verify CLI output, exit codes, SARIF, and positive/negative findings against
      compilation errors, broken targets, missing references, generated code,
      mixed solutions, environment/debug guards, and interface dispatch.
- [x] Compare project-aware and direct modes on SharpSaster and Razor/gRPC cases.
- [x] Record measured results, gaps, and the next experiment in this document.

## Experiment boundaries

Project-aware loading remains available. The direct loader is a deliberately
limited reconstruction of project inputs, not another complete MSBuild evaluator.
No framework, analyzer, test dependency, rule ID, package version, or release
workflow upgrade is part of this experiment.

The first direct-loader experiment uses existing reference packs and NuGet assets
without a network restore. Cached generated C# can be supplied through explicit
compile items; missing generation is a coverage limitation. Broader generator,
import, and dependency-restoration support requires a separate decision.

## Verification record

Verified locally on Windows on 2026-10-04, using Roslyn 5.0.0 and .NET 10.
The existing analyzer suite passed: 629 tests, no failures or skips. The new
CLI experiment suite and existing installed-package CLI smoke suite passed.
No analyzer rules, engine algorithms, or dependency versions were changed.

| Case | Project-aware result | Direct result | Assessment |
| --- | --- | --- | --- |
| Small clean fixture | 3 findings | 3 findings | Same locations, messages, and engine flows; safe reassignment, development and debug cases excluded |
| Unrelated missing type | Original findings retained | Original findings retained | Exit 0 and partial SARIF; `--fail` returns 1 |
| Unrelated syntax error | Not measured in this fixture | Original findings retained | Compiler error recorded without blocking analysis |
| Broken custom target | Not exercised | Original findings retained | Direct mode did not execute the target's marker-writing task |
| Malformed project plus healthy project | Healthy project analyzed | Healthy project analyzed | Skipped project recorded; unusable-only target returns 2 |
| Missing dependency | Not exercised | Independent findings retained | Missing assets and missing symbols recorded |
| .NET 8/10 with shared project reference | 4 compilations, 2 findings | 4 compilations, 2 findings | Same findings and flows; conditions, reference packs, compile exclusions preserved |
| Registered interface implementation | 1 redirect finding | 1 redirect finding | Same witness; registering the safe implementation gives no findings |
| gRPC using real provider/protobuf assemblies | 1 command finding | 1 command finding | Same witness; method metadata is not treated as attacker input |
| SharpSaster, locally upgraded to .NET 10 | 41 findings | 41 findings | Same complete result/flow set; existing dummy-repository limitations remain |
| Existing Razor/Blazor CLI fixture | 3 C# component findings | Same 3 C# component findings | This comparison does not establish generated Razor coverage; direct mode reports missing generation |

The initial SharpSaster result ordering differed when several sources shared a
sink location. Comparing complete result sets showed parity. Console/SARIF
ordering now also sorts by the finding message for reproducibility.

The fixture suite verifies framework-only scanning without target restore or
build, test-project TLS suppression and opt-in, `.editorconfig`, source exclusions,
and preservation of relative SARIF locations and real engine witnesses. Its
setup restores dependencies for comparisons; the direct loader itself performs
no restore. The installed-package smoke also covers custom `dotnetarium.json`,
configuration errors, and .NET 8/10.

Internal analyzer exceptions remain failures rather than successful partial
scans. This handling was reviewed; no artificial analyzer crash was injected.
Windows and Linux CI checks are enabled for this experimental branch. Remote CI
results are separate from the local verification above.

## Running the experiment

Build the tool itself once:

```sh
dotnet build Dotnetarium.Tool/Dotnetarium.Tool.csproj -c Release -p:RunAnalyzers=false
```

Then scan a target without invoking its MSBuild targets:

```sh
dotnet Dotnetarium.Tool/bin/Release/net10.0/Dotnetarium.Tool.dll path/to/App.csproj --experimental-direct --sarif results.sarif
```

Omit `--experimental-direct` to compare project-aware loading. Existing CLI
options remain available. Partial scans report limitations to stderr and in
SARIF invocation notifications; `dotnetarium.coverage` is `partial` when a
limitation is encountered. `complete` means no loading/compilation limitation
was recorded, not proof that every application behavior or vulnerability was
analyzed. Analyzer failures set `executionSuccessful` to false and return 2.

Run the fixture suite after building the tool:

```powershell
./tests/BuildIndependentSmoke/Test.ps1
```

## Known prototype limitations

- Only conventional SDK projects with exact `net8.0` and `net10.0` TFMs are
  reconstructed. OS-specific TFMs, custom SDKs and unsupported frameworks are
  reported and skipped.
- Only simple property expansion, boolean conditions and quoted equality or
  inequality conditions are supported. This is not full MSBuild evaluation.
- The nearest `Directory.Build.props` is read. Custom imports and
  `Directory.Build.targets` are reported rather than evaluated.
- Package compile references come from existing `project.assets.json` and its
  package folders. No dependency version is guessed or silently downloaded.
  Reference packs can also be read from the local NuGet cache. Assets freshness,
  richer package metadata, aliases, and build-time assets need further work.
  Package build-time inputs are reported when present. Properties such as
  `IsTestProject` are only reconstructed from the supported project/props files;
  values contributed by package imports may differ from project-aware loading.
- Compilation defaults follow Debug configuration. More configuration/platform
  selections and SDK-specific compiler properties need explicit coverage.
- Generated code is not recreated. Default `obj`/`bin` exclusions avoid consuming
  stale code; existing generated C# must be selected explicitly with compile
  items. Dependency-provided analyzer/generator assemblies are not executed.
- Linked literal compile files are supported; external wildcard selections are
  reported as unsupported. Advanced analyzer-config and linked-file arrangements
  require more coverage.
- Analyzing available compilations does not establish that every invalid method
  body was analyzed. Unsupported operations and unresolved calls retain the
  engine's existing behavior; absence of findings in a partial scan is not a
  clean bill of health.

## Next experiment and promotion gates

1. Add a compilation-input inventory with resolved references, included sources,
   generated inputs, target framework and configuration. Compare it against the
   project-aware loader before expanding reconstruction rules.
2. Validate assets freshness and more source/reference/configuration metadata.
   Fix measured discrepancies while keeping unknown inputs visible.
3. Reuse explicitly supplied generated C# and measure Razor/gRPC coverage. Then
   evaluate controlled SDK generation separately, without arbitrary custom
   target execution. Source-generator policy needs a separate decision.
4. Extend the real-project corpus with custom imports and build workflows.
   Review false positives as well as missed findings and unresolved bindings.
5. Require passing Windows/Linux CI and repeatable finding/flow comparisons
   before considering automatic fallback or a default-loading change.

The first experiment is complete. Production promotion, automatic fallback,
full import evaluation and generation support are intentionally not complete.
No release or merge into `main` is part of this work.
