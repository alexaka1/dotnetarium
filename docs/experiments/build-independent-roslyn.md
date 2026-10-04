# Build-independent Roslyn experiment

Status: direct-loader prototype, input comparison, bounded restore validation,
explicit generated-C# reuse, configuration/framework selection and conventional
framework package pruning implemented.
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
- [x] Select configuration and root framework explicitly; retain compatible source
      dependencies and configuration-specific generated output.
- [x] Measure custom imports/build workflows and an SDK-pinned real project.
- [x] Validate conventional source-dependency pruning and resolve global
      package/framework assembly conflicts without package-name exceptions.
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

## Restore validation and generated-C# reuse

The next slice validates the cached restore request against the current supported
project inputs. It checks project ownership/path, target framework, direct package
requests, package aliases and include/exclude/private asset metadata, and source
project-reference paths. Ordinary versions and interval requests are compared
after equality normalization; a NuGet minimum request such as `3.35.1` is not an
exact-version pin. [NuGet version semantics](https://learn.microsoft.com/en-us/nuget/concepts/package-versioning).

Validation uses dependency metadata rather than modification times. Equivalent
version spellings and an old assets timestamp remain usable. Changed versions,
added/removed packages, changed aliases/asset selection, different project paths
and changed source references produce `package-assets-stale` notifications.

The next slice adds conventional central versions from the nearest
`Directory.Packages.props`, simple per-TFM conditions and `VersionOverride`.
An override disabled by `CentralPackageVersionOverrideEnabled=false` is not
accepted. Nested central files shadow their parent; custom imports are still
reported. [Central package management](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management).

Package-bearing source dependencies are validated recursively. Each child's
saved request must match its current supported inputs. Its exported package and
source-project dependency requests, project identity/version and chosen framework
must also match the edges inside the parent's actual `project.assets.json`.
Refreshing a child restore or the parent's dgspec alone cannot establish this.
The fixture verifies changed child constraints with refreshed child metadata and
dgspec but old parent assets, plus changed leaf identity in a three-project graph.
Ordinary asset propagation and `PrivateAssets=all` are supported. A source project
can still be analyzed independently when its parent's cached bindings are rejected.

Floating/unresolved versions, unsupported item operations/imports/conditions,
central transitive pinning/global references, unsupported child asset propagation,
custom/unsupported pruning arrangements and missing/error restore
metadata remain `unverified`. These limits are explicit rather than a replacement
NuGet resolver. Matching validates supported restore requests, not freshness of
feeds or every SDK-generated restore property.

### Conventional framework package pruning

The next slice supports the omission of platform-provided package edges from
source dependencies. NuGet can privatize a prunable direct reference while
retaining its restore request and resolved package node; its compile/runtime
assets become placeholders. A parent can also prune a transitive request from a
lower-framework child that still needs the package in its own compilation.
[NuGet pruning behavior](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#prunepackagereference).

Validation retains the existing ownership, request/metadata, project identity,
framework and recursive graph checks. It accepts an omitted edge only with a
recorded pruning range covering the request and no active resolved assets in
that pruning context. Both child and parent contexts are considered. Explicit
pruning-policy changes invalidate incompatible saved metadata. Unrelated missing
edges remain rejected. Package names are not special-cased.

The bounded proof supports stable numeric requests and the SDK's inclusive
maximum pruning ranges. Prerelease/floating/custom ranges or ambiguous metadata
remain unverified. Explicit `PrunePackageReference` items are unsupported inputs,
so editing custom pruning rules cannot silently reuse old bindings. This checks
conventional saved restore evidence, not every SDK pruning policy or data revision.

Real-restored .NET 8/10 CLI fixtures verify request/policy changes, unrelated
missing edges, out-of-range omissions, custom pruning items, and contradictory
pruning records with live package assemblies. They compare complete findings and
flows against project-aware loading. The .NET 8 child uses package System.Text.Json
9; the .NET 10 compilation uses its framework System.Text.Json 10.

This exposed a separate input bug: framework and newer package assemblies were
both passed to Roslyn, producing ambiguous types. Global package/framework
conflicts with matching assembly name, culture and public-key token now compare
assembly version, then file version, preferring the platform on a tie. Explicit
HintPath and aliased references retain their metadata. This is bounded conflict
resolution; custom package ranks/overrides are not a full SDK recreation.
[SDK conflict resolver](https://github.com/dotnet/sdk/blob/main/src/Tasks/Common/ConflictResolution/ConflictResolver.cs).

Stale/unverified cached package bindings are omitted. Framework packs, explicit
assembly references and available source project compilations still feed the
engine; independent findings are retained in a partial scan. The scanner does
not restore, download replacements or run target build tasks. Use explicit
references for unsupported dependency arrangements; restoring alone cannot make
an unsupported evaluator arrangement verifiable in this prototype.

The input inventory adds `restoredAssets` with relative path, status and reason.
States are `matched`, `stale`, `unverified`, `invalid` or `absent`. `matched` means
the supported dependency request matched the saved metadata, not proof of complete
generation/import parity, package-content integrity or the newest feed version.
Restored package aliases now reach Roslyn correctly.

Default source/config globs exclude custom output/intermediate directories as
well as `obj`/`bin`. Literal and supported in-project wildcard `Compile` items
can explicitly select generated C#. Conventional `IntermediateOutputPath`
is reconstructed per configuration/TFM, for example:

```xml
<ItemGroup>
  <Compile Include="$(IntermediateOutputPath)Protos/*.cs" />
</ItemGroup>
```

For Razor, select the materialized SDK-generated `*_razor.g.cs`/`*_cshtml.g.cs`
files and explicitly provide the corresponding markup through `AdditionalFiles`
when needed for render-mode evidence. The controlled fixture setup uses
`EmitCompilerGeneratedFiles` to materialize them; the scanner does not invoke that
generation. Include only the intended framework/configuration output, and refresh
it separately when templates, schemas, dependencies or generator settings change.

Reused output C# produces a `generated-reuse` coverage notification. Its freshness
and generation provenance are unverified; an explicit selection is not a complete
scan guarantee. Scanning never automatically consumes the existing output tree.

Razor `#line` mappings now place CLI/SARIF finding and flow locations in the
original markup when available, with relative paths. Hidden/unmapped generated
sections retain their physical C# locations.

Verified with the unchanged engine and analyzer dependencies:

| Case | Result |
| --- | --- |
| Fresh protobuf package and restored package alias | Real assembly binds; baseline findings/flows retained |
| Equivalent minimum requests and old assets timestamp | Metadata remains matched; bindings preserved |
| Changed version constraint, version, package set, alias, asset metadata or reference paths | Cached package bindings omitted; independent findings retained; partial SARIF |
| Floating/missing version or unsupported graph arrangement | Unverified restore graph is visible; independent findings retained |
| Conventional central versions, TFM conditions and overrides | Real protobuf bindings preserved; changed requests rejected per TFM |
| Package-bearing source dependencies and three-project graph | Fresh edges accepted; changed exported requests/leaf identity rejected even after child restore |
| Private source dependency package | Child package retained locally; no package binding invented in parent |
| Custom intermediate directory | Unselected C# excluded; explicit selection adds its real command finding and reuse notice |
| Real protoc-generated service, before/after explicit C# selection | 3 / 4 findings; the missing request-to-command finding returns with the same witness as project-aware loading |
| Protobuf reuse with a failing custom build target | Same 4 findings; target marker was not written |
| Real SDK-generated Razor page/component | 2 raw-output findings; encoded controls excluded; locations `Pages/Probe.cshtml:2` and `Probe.razor:3` |
| SharpSaster | Same 41 complete finding/flow results as the recorded project-aware baseline |

Generation in these positive fixtures is a separate controlled test-setup build.
The direct scanner's build-independent behavior is checked after that setup.
Automatic generators, restore execution and automatic fallback remain separate
decisions. This work changes no analyzer rule, engine algorithm or package version.

## SCA information available before compilation

SCA can use restored dependency metadata independently of C# compilation. The
experimental inventory now adds top-level `restoreInputs`, including when there
is no usable source compilation. Each entry carries the project/TFM, assets path,
validation status/reason, and a package snapshot. This addition currently applies
to direct loading; project-aware inventories do not yet export a package graph.

| Evidence at this step | Useful SCA check | Limits |
| --- | --- | --- |
| Resolved package ID/version per saved TFM/RID target | Match exact versions against known vulnerable ranges | `stale`/`unverified` means a saved graph, not a proven current dependency set |
| Root dependency IDs and package/source-project edges with requested/resolved versions | Explain direct/transitive inclusion paths and which direct dependency to update | Constraints are not resolved versions; absent edge targets remain null rather than guessed |
| Runtime/native/build/content/analyzer assets, including packages without compile DLLs | Include build-time and runtime-only exposure in SCA | Asset categories describe potential use, not deployment or vulnerable API reachability |
| Saved NU1901–NU1904 code, package ID, severity, target graphs and advisory links | Retain evidence of a previous NuGet audit finding | Historical observations only; changed or suppressed advisories may be absent |
| Saved audit enabled/mode/minimum severity; NU1900/NU1905 diagnostics | Explain limited or failed saved audit coverage | No diagnostics does not prove that an audit ran or that the database was current; suppression details are not exported yet |
| Recorded package SHA-512 | Identify the restored artifact for an eventual SBOM/integrity comparison | Recorded metadata is not an independent hash check, signature verification or publisher attestation |
| Declared package requests without assets | Inventory declared dependencies as unresolved | No exact version or complete transitive graph; never label a range as the installed version |
| `HintPath` DLLs and framework reference assemblies | Report unclassified dependency evidence | Assembly versions do not establish NuGet package versions; reference-pack versions do not prove a deployed runtime patch level |

Only the resolved snapshot and saved audit evidence above are exported now.
Declaration-only SCA, SBOM export, DLL provenance and runtime inventory are future
work. Graph parsing runs only when the input inventory is requested. The scanner
performs no new audit lookup: every entry says `advisoryCheck: not-performed`.
Stale graphs remain inspectable with their status, while their package DLLs are
omitted from analysis. Raw audit messages, NuGet configuration contents and feed
URLs/credentials are not copied into the package snapshot.

Measured on 2026-10-04: SharpSaster exposes 24 restored packages and retains the
same 41 complete finding/flow results. A separate, unbuilt restore-only probe
using Newtonsoft.Json 12.0.1 produced NU1903 for GHSA-5crp-9r3c-p9vr. Its saved
assets provided the package/version, high severity, advisory URL and `net10.0`
target; the experimental inventory retained those fields despite an empty C#
project. This fixture package is not added to Dotnetarium's dependencies.

### Deferred SCA spinoff

SCA is a separate follow-up after the main loading experiment is concluded.
The existing dependency snapshot is diagnostic evidence, not a vulnerability
scanner. Advisory lookup and reachability implementation are outside the current
experiment. The following is the proposed scope for that future work:

1. Consume NuGet's `VulnerabilityInfo` API against resolved package versions,
   using official NuGet version/range semantics. Its bulk pages allow local
   matching and include update metadata. Record source, fetched time and database
   revision; support an explicit offline snapshot and visible fetch failures.
   [NuGet advisory API](https://learn.microsoft.com/en-us/nuget/api/vulnerability-info).
2. Keep SCA results separate from DNA taint findings. Report advisory, severity,
   package/version, TFM/RID and inclusion path. Deduplicate identical advisories
   across paths, preserving affected targets. A vulnerable dependency does not
   by itself prove that an exploitable API is reachable.
3. Require a validated graph for a current-project conclusion. Historical/stale
   matches can be shown with explicit uncertainty; missing assets/database data
   cannot produce a successful clean SCA result. Use a separate SCA coverage state
   so compilation errors do not prevent a valid dependency audit.
4. Add advisory suppression with rationale and optional expiry, then SBOM export.
   Package deprecation/license checks require additional package metadata and a
   policy; old versions, missing lock files or package names alone are not
   vulnerability findings. Signatures/package hashes need actual verification.

For a separate manual current advisory check with an existing restore:

```sh
dotnet package list --project path/to/App.csproj --no-restore --include-transitive --vulnerable --format json --output-version 1
```

Verified against the restore-only probe. This does not compile; the SDK command
still reads project inputs and accesses advisory sources, so it is not used by
the direct scanner. `.NET 10` otherwise allows automatic restore for package-list
commands. [Package-list command](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list).
NuGet auditing defaults to all dependencies for projects targeting .NET 10;
lower targets may default to direct dependencies. Audit source failures and
suppression policy must remain visible when reporting coverage.
[NuGet audit configuration](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages).

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
  Reference packs can also be read from the local NuGet cache. Supported direct
  restore requests, conventional central versions and bounded source dependency
  graphs are validated; unsupported arrangements omit cached bindings and report
  partial coverage. Full NuGet evaluation, complex asset propagation, central
  pinning/global references and build-time assets need further work.
  Restored package aliases, explicit assembly/source project aliases and interop
  metadata are supported.
  Package build-time inputs are reported when present. Properties such as
  `IsTestProject` are only reconstructed from the supported project/props files;
  values contributed by package imports may differ from project-aware loading.
- Default configuration remains Debug. Explicit configuration and root-framework
  selection are covered below. Platform selection and more SDK-specific compiler
  properties still need coverage.
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

## Configuration and framework selection

Both loaders accept an explicit configuration and framework:

```sh
dotnetarium path/to/App.csproj --configuration Release --framework net10.0 --experimental-direct --sarif findings.sarif --experimental-inputs inputs.json
```

- `--configuration` is a global property. Supported simple property/item
  conditions, symbols, optimization and test-project metadata use that value.
  Custom names are accepted. SDK configuration symbols are reconstructed with
  the SDK's uppercase and punctuation substitutions; disabling implicit
  configuration defines is respected. The inventory distinguishes the requested
  selection from effective project metadata, which is not always exported by
  project-aware loading.
- `--framework` selects root compilations, then retains their source dependency
  closure. A `net10.0` root may therefore retain a `net8.0` dependency. It does not
  override every project's `TargetFramework`. Current choices are `net8.0` and
  `net10.0`; omitting the option retains the existing all-framework behavior.
- Missing requested root frameworks produce a coverage notice. Other eligible
  solution roots still scan; no eligible compilation returns exit 2. The direct
  loader does not read source/package inputs or recurse through dependencies
  belonging only to intentionally unselected frameworks. Project-aware loading
  opens the workspace before selection and can still report loading failures
  from other frameworks.
- Explicit generated output uses the selected configuration/TFM's
  `IntermediateOutputPath`. Nothing is generated or refreshed by direct loading.

CLI fixtures compare complete findings and engine flows for Debug, Release and
a custom test configuration. They cover conditional compile items/constants,
test metadata, lower-framework dependencies, unavailable roots, mixed solutions,
configuration-specific output reuse and custom target nonexecution. A controlled
custom import supplies an extra sink: project-aware Release loading reports both
sinks, direct loading preserves the independent sink and reports partial coverage
for the omitted import. This is an expected input gap, not finding parity.

## Additional real-project evidence

Measured on 2026-10-04 with explicit Release selection:

| Project | Selection | Result | Interpretation |
| --- | --- | --- | --- |
| SharpSaster, locally upgraded corpus | `net10.0` | 41 identical complete SARIF results, including engine flows, in project-aware/direct modes | Configuration selection preserves existing findings; direct mode still reports omitted generation |
| HelveticOps Application + Domain, commit `49dd07b` | `net8.0` | 2 compilations, 0 findings, no coverage notices, no assets/restore/build | Shared `Directory.Build.props`, framework-only source reference and an SDK-8-pinned repository scan using available reference packs |
| LANCommander UI + SDK + Steam, commit `4a2eef7`, locally upgraded UI corpus | `net10.0` | 3 compilations, partial coverage | Real custom npm/completion targets are not run; omitted package bindings and generated inputs prevent a clean conclusion |
| LANCommander after pruning fix, compilation-input inspection only | `net10.0` | All 3 restore graphs matched; 4 compiler errors instead of 1,195 | UI/SDK/Steam reference counts are 298/271/176; no restore, custom targets or security analyzers ran during this inspection |

The initial LANCommander scan exposed a dependency gap: source-dependency
framework pruning made parent restore graphs unverified. The loader omitted
cached parent package bindings and reported 1,195 compiler errors. The subsequent
pruning slice supports conventional pruned graphs as described above. Zero
findings in the initial scan do not establish absence of vulnerabilities, and
no equivalence with a full project-aware scan is claimed.
The follow-up input-only inspection independently confirms restored bindings:
UI and Steam compile without errors; SDK has four errors from missing generated
SignalR partial methods and PowerShell cmdlet extensions. This is a loader and
compiler measurement, not a completed security audit. Generated-input policy is
therefore the next concrete coverage decision.
The custom-import fixture separately demonstrates an actual missed finding when
an imported file is unavailable. These cases argue against silently promoting
the prototype to the default or treating an automatic fallback as equivalent.

## Next experiment and promotion gates

1. **Complete:** compilation-input inventory and comparison, including resolved
   references, included sources, generated inputs, effective compiler settings
   and available target-framework/configuration metadata. Measured settings and
   reference-metadata discrepancies are fixed and covered by CLI fixtures.
2. **Bounded prototype complete:** validate conventional direct/central restore
   requests, overrides, aliases/asset metadata and recursive source dependency
   edges. Keep unsupported restore arrangements unverified. Dependency inventory
   and saved audit evidence are available for a separate SCA experiment; current
   advisory matching is not implemented.
3. **Reuse verified:** explicitly supplied generated C#, custom output selection,
   real Razor/gRPC positive and safe cases, mapped SARIF and target nonexecution.
   Evaluate controlled SDK generation separately, without arbitrary custom
   target execution. Source-generator policy remains a separate decision.
4. **Selection/corpus/pruning slices complete:** configuration/framework
   selection, real custom-build/SDK-pinned projects, conventional direct/transitive
   pruning and global package/framework assembly conflicts. Before production
   promotion, decide whether to support bounded imports or keep omitted imports
   as an explicit partial-coverage boundary. Complex asset propagation, custom
   pruning and full SDK conflict policy remain unsupported.
5. Require passing Windows/Linux CI and repeatable finding/flow comparisons
   before considering automatic fallback or a default-loading change.

The direct prototype, inventory, bounded validation, explicit reuse, selection and
conventional pruning slices are complete. Production promotion, automatic fallback,
full import evaluation and generation support are intentionally not complete.
No release or merge into `main` is part of this work.
