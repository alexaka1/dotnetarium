# Build-independent Roslyn experiment

Status: first prototype and compilation-input comparison implemented and locally
verified on Windows.
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
Windows and Linux CI checks are enabled for this experimental branch. The first
remote run passed Linux; Windows failed before scanning because the workflow had
not built the tool DLL. The Windows workflow now builds it before the experiment,
and both jobs retain inventories, SARIF and logs, including after failed checks.
Those artifacts also identified a cross-drive path error in the aliased-reference
fixture: Windows stores packages on `C:` and fixtures on `D:`. The fixture now
preserves rooted reference paths when resolving the inventory back to a DLL.
Remote CI results are separate from the local verification above.

## Compilation-input comparison

The optional `--experimental-inputs <path>` exports a versioned JSON inventory
from the exact compilations passed to the analyzers. It does not change analyzer
execution or authorize extra generation. The inventory records:

- Included syntax trees, UTF-8 text hashes and each tree's parse settings.
- Regular documents, synthesized implicit usings and source-generator output.
  Files under `obj`/`bin` or with conventional generated names are marked as
  generated candidates; that marker alone is not proof of their provenance.
- Bound assembly identities and versions, physical reference paths, source
  project references, target frameworks, aliases and interop settings.
- Effective language version, conditional symbols, compiler features, nullable,
  output kind, optimization, platform, overflow and diagnostic options.
- Analyzer configs, additional-file hashes, analyzer/generator assembly paths,
  test-project metadata, coverage notices and skipped projects.

Paths are relative to the target directory where possible. Source/configuration
contents are not copied into the inventory. Configuration/platform property names
are null when the workspace does not expose them; effective compiler settings
are recorded regardless. A framework inferred from conditional symbols is
explicitly labeled, and ambiguous symbols are not treated as authoritative TFM
metadata. Compare inventories from the same checkout and target directory.

The comparison script pairs projects by path and TFM, compares reference
identities and aliases rather than cache locations, and reports generated inputs
separately from user source. Differences are evidence to investigate, not a
scan failure or proof that a finding was missed.

Measured locally on 2026-10-04 after the fixes below:

| Corpus/project | User C# trees, both modes | Generated candidates, project/direct | Bound references, both modes | Findings, project/direct |
| --- | --- | --- | --- | --- |
| SharpSaster | 17 | 5 / 1 | 327 | 41 / 41 |
| Razor server | 2 | 7 / 1 | 310 | 3 / 3 across server/client |
| Blazor client | 2 | 5 / 1 | 193 | Included above |
| Existing gRPC corpus | 2 | 6 / 1 | 314 | 0 / 0; no positive security sink in this corpus |

User source text and complete finding/flow sets match in these comparisons.
The separate real-provider gRPC positive fixture still yields the same command
finding in both modes. Equal reference counts do not establish identity parity:
the Razor server's client project reference has version `2.3.0.0` in project mode
and `0.0.0.0` in direct mode because assembly-version attributes are not generated.

The inventory exposed and the expanded CLI suite verifies these fixes:

- Honor explicit optimization, overflow checking, platform, warning level,
  unsafe-code and documentation settings; preserve Windows application output
  kind separately from console output.
- Preserve aliases and interop metadata on explicit `Reference`/`HintPath` and
  source project references, with real protobuf assembly and `extern alias` tests.
- Exclude `ReferenceOutputAssembly=false` build-order dependencies from semantic
  project references instead of analyzing them as ordinary dependencies.
- Capture actual SDK regex-generator output in project mode. Direct mode does
  not invent it; unresolved generated partial methods remain coverage warnings,
  while unrelated findings and witnesses survive.

Remaining measured differences include generated assembly attributes, SDK
analyzer configuration, compiler diagnostic defaults, interceptor features,
package analyzer/generator assemblies, and SDK-selected additional files. Blank
test-project metadata versus explicit `false` is visible too; it is not evidence
of different TLS suppression behavior by itself.

The gRPC comparison includes generated protobuf/service C# in project mode and
not in direct mode. The Razor project-aware CLI compilation also lacks generated
page/component C# in this fixture: it includes markup as additional files, while
direct mode omits those SDK-selected files. Neither result is evidence of complete
Razor coverage. Full package-build Razor smoke remains the coverage reference.

Both modes retain the same known engine limitations. Do not promote this loader
based only on matching finding counts.

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

Export and compare compilation inputs:

```powershell
dotnet Dotnetarium.Tool/bin/Release/net10.0/Dotnetarium.Tool.dll path/to/App.csproj --experimental-inputs project-inputs.json --sarif project.sarif
dotnet Dotnetarium.Tool/bin/Release/net10.0/Dotnetarium.Tool.dll path/to/App.csproj --experimental-direct --experimental-inputs direct-inputs.json --sarif direct.sarif
./tests/BuildIndependentSmoke/Compare-Inputs.ps1 -ProjectInventory project-inputs.json -DirectInventory direct-inputs.json -OutputPath comparison.json
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
  richer package metadata, package-provided aliases, and build-time assets need further work.
  Explicit assembly/source project aliases and interop metadata are supported.
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

1. **Complete:** compilation-input inventory and comparison, including resolved
   references, included sources, generated inputs, effective compiler settings
   and available target-framework/configuration metadata. Measured settings and
   reference-metadata discrepancies are fixed and covered by CLI fixtures.
2. Validate assets freshness and more source/reference/configuration metadata.
   Fix measured discrepancies while keeping unknown inputs visible.
3. Reuse explicitly supplied generated C# and measure Razor/gRPC coverage. Then
   evaluate controlled SDK generation separately, without arbitrary custom
   target execution. Source-generator policy needs a separate decision.
4. Extend the real-project corpus with custom imports and build workflows.
   Review false positives as well as missed findings and unresolved bindings.
5. Require passing Windows/Linux CI and repeatable finding/flow comparisons
   before considering automatic fallback or a default-loading change.

The first experiment and input-inventory step are complete. Production promotion, automatic fallback,
full import evaluation and generation support are intentionally not complete.
No release or merge into `main` is part of this work.
