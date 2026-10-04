# Real SDK restores cover direct pruning and pruning through a lower-framework
# source dependency. The scanner itself must not restore or execute targets.
$pruneRoot = Join-Path $scratch 'pruning'
$pruneChild = Join-Path $pruneRoot 'child'
$pruneParent = Join-Path $pruneRoot 'parent'
New-Item -ItemType Directory -Path $pruneChild, $pruneParent -Force | Out-Null
$pruneChildProject = Join-Path $pruneChild 'Child.csproj'
$pruneParentProject = Join-Path $pruneParent 'Parent.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
  <PropertyGroup Condition="'`$(TargetFramework)' == 'net8.0'"><RestoreEnablePackagePruning>false</RestoreEnablePackagePruning></PropertyGroup>
  <ItemGroup><PackageReference Include="System.Text.Json" Version="9.0.1" /><PackageReference Include="Google.Protobuf" Version="$protobufVersion" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $pruneChildProject
@'
public static class Child
{
    public static string Read(Google.Protobuf.WellKnownTypes.StringValue input) => input.Value;
    public static string Json() => System.Text.Json.JsonSerializer.Serialize("fixed");
}
'@ | Set-Content -LiteralPath (Join-Path $pruneChild 'Child.cs')
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
  <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0'"><RestoreEnablePackagePruning>false</RestoreEnablePackagePruning></PropertyGroup>
  <ItemGroup><ProjectReference Include="../child/Child.csproj" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $pruneParentProject
@'
public static class Parent
{
    public static string Read(Google.Protobuf.WellKnownTypes.StringValue input) => Child.Read(input);
    public static void Unsafe() => System.Diagnostics.Process.Start(System.Console.ReadLine());
    public static void Safe() => System.Diagnostics.Process.Start("fixed");
}
'@ | Set-Content -LiteralPath (Join-Path $pruneParent 'Parent.cs')
& dotnet restore $pruneParentProject --nologo -v quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Pruning fixture restore failed.' }
$pruneAware = Scan $pruneParentProject $false
$pruneDirect = Scan $pruneParentProject $true
SameFindings $pruneAware $pruneDirect
if ($pruneDirect.inputInventory.projects.Count -ne 4 -or
    @($pruneDirect.inputInventory.projects | Where-Object { $_.restoredAssets.status -ne 'matched' }).Count -ne 0 -or
    (HasNotice $pruneDirect 'compiler-error') -or
    @($pruneDirect.inputInventory.projects.references | Where-Object identity -like 'Google.Protobuf,*').Count -ne 4) {
    throw 'Fresh framework pruning rejected valid parent package bindings.'
}
$pruneChildAssets = Join-Path $pruneChild 'obj/project.assets.json'
$pruneParentAssets = Join-Path $pruneParent 'obj/project.assets.json'
$savedPruneChild = Get-Content -LiteralPath $pruneChildAssets -Raw
$savedPruneParent = Get-Content -LiteralPath $pruneParentAssets -Raw
$pruneChildXml = Get-Content -LiteralPath $pruneChildProject -Raw
$pruneParentXml = Get-Content -LiteralPath $pruneParentProject -Raw
foreach ($framework in @('net8.0', 'net10.0')) {
    $childInput = @($pruneDirect.inputInventory.projects | Where-Object { $_.path -like '*Child.csproj' -and $_.targetFramework -eq $framework })[0]
    $jsonReference = @($childInput.references | Where-Object identity -like 'System.Text.Json,*')
    if ($jsonReference.Count -ne 1 -or
        ($framework -eq 'net8.0' -and $jsonReference[0].identity -notlike 'System.Text.Json, Version=9.*') -or
        ($framework -eq 'net10.0' -and $jsonReference[0].identity -notlike 'System.Text.Json, Version=10.*')) {
        throw 'Pruned framework and nonpruned package assemblies were mixed.'
    }
}

# A refreshed child restore with a changed prunable request cannot validate an
# old parent graph. Independent findings must still survive rejection.
$pruneChildXml.Replace('Version="9.0.1"', 'Version="[9.0.1]"') | Set-Content -LiteralPath $pruneChildProject
$stalePrune = Scan $pruneParentProject $true -Selection @('--framework', 'net10.0')
SameFindings $pruneDirect $stalePrune
if (@($stalePrune.inputInventory.projects | Where-Object { $_.restoredAssets.status -ne 'stale' }).Count -ne 0) {
    throw 'A changed prunable package request was accepted without restore.'
}
$pruneChildXml | Set-Content -LiteralPath $pruneChildProject

# The current policy is part of the request. Disabling pruning after restore
# must invalidate old pruned bindings instead of treating absent edges as safe.
$pruneChildXml.Replace('</Project>', '<PropertyGroup><RestoreEnablePackagePruning>false</RestoreEnablePackagePruning></PropertyGroup></Project>') |
    Set-Content -LiteralPath $pruneChildProject
$disabledPrune = Scan $pruneParentProject $true -Selection @('--framework', 'net10.0')
if (@($disabledPrune.inputInventory.projects | Where-Object { $_.restoredAssets.status -eq 'matched' }).Count -gt 0) {
    throw 'Changed pruning policy was ignored.'
}
$pruneChildXml | Set-Content -LiteralPath $pruneChildProject

# A package name in packagesToPrune alone is not evidence: it must have a valid
# upper-bound range and no active resolved assets in the pruning context.
$invalidPrune = $savedPruneChild | ConvertFrom-Json -AsHashtable
$invalidPrune.project.frameworks['net10.0'].packagesToPrune['Google.Protobuf'] = '(,99.0.0]'
$invalidPrune | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $pruneChildAssets
$unverifiedPrune = Scan $pruneParentProject $true -Selection @('--framework', 'net10.0')
if (@($unverifiedPrune.inputInventory.projects | Where-Object path -eq 'Parent.csproj')[0].restoredAssets.status -eq 'matched') {
    throw 'A pruning entry with live package assemblies was accepted.'
}
$savedPruneChild | Set-Content -LiteralPath $pruneChildAssets

$outsidePrune = $savedPruneChild | ConvertFrom-Json -AsHashtable
$outsidePrune.project.frameworks['net10.0'].packagesToPrune['System.Text.Json'] = '(,8.0.0]'
$outsidePrune | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $pruneChildAssets
$outsideParent = $savedPruneParent | ConvertFrom-Json -AsHashtable
$outsideParent.project.frameworks['net10.0'].packagesToPrune['System.Text.Json'] = '(,8.0.0]'
$outsideParent | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $pruneParentAssets
$outsideRange = Scan $pruneParentProject $true -Selection @('--framework', 'net10.0')
if (@($outsideRange.inputInventory.projects | Where-Object path -eq 'Parent.csproj')[0].restoredAssets.status -eq 'matched') {
    throw 'Pruning accepted a missing edge outside its version range.'
}
$savedPruneChild | Set-Content -LiteralPath $pruneChildAssets
$savedPruneParent | Set-Content -LiteralPath $pruneParentAssets

$pruneChildXml.Replace('</Project>', '<ItemGroup><PrunePackageReference Include="Google.Protobuf" Version="99.0.0" /></ItemGroup></Project>') |
    Set-Content -LiteralPath $pruneChildProject
$customPrune = Scan $pruneParentProject $true -Selection @('--framework', 'net10.0')
if (@($customPrune.inputInventory.projects | Where-Object { $_.restoredAssets.status -ne 'unverified' }).Count -ne 0) {
    throw 'Unevaluated custom pruning items were accepted.'
}
$pruneChildXml | Set-Content -LiteralPath $pruneChildProject

$pruneParentXml.Replace('</Project>', '<PropertyGroup><RestoreEnablePackagePruning>false</RestoreEnablePackagePruning></PropertyGroup></Project>') |
    Set-Content -LiteralPath $pruneParentProject
$changedParentPolicy = Scan $pruneParentProject $true -Selection @('--framework', 'net10.0')
if (@($changedParentPolicy.inputInventory.projects | Where-Object path -eq 'Parent.csproj')[0].restoredAssets.status -ne 'stale') {
    throw 'Changed root pruning policy was ignored.'
}
$pruneParentXml | Set-Content -LiteralPath $pruneParentProject

# Pruning cannot hide a missing unrelated dependency edge in the parent graph.
$missingEdge = $savedPruneParent | ConvertFrom-Json -AsHashtable
$childEdge = $missingEdge.targets['net10.0'].GetEnumerator() | Where-Object { $_.Value.type -eq 'project' }
$childEdge.Value.dependencies.Remove('Google.Protobuf') | Out-Null
$missingEdge | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $pruneParentAssets
$missingPrune = Scan $pruneParentProject $true -Selection @('--framework', 'net10.0')
if (@($missingPrune.inputInventory.projects | Where-Object path -eq 'Parent.csproj')[0].restoredAssets.status -ne 'stale') {
    throw 'Pruning masked an unrelated missing package edge.'
}
$savedPruneParent | Set-Content -LiteralPath $pruneParentAssets

# Keep the child on .NET 8; the .NET 10 root prunes a transitive package even
# though the child's own compilation still needs its package-provided assembly.
$pruneParentXml.Replace('net8.0;net10.0', 'net10.0') | Set-Content -LiteralPath $pruneParentProject
$pruneChildXml.Replace('<TargetFrameworks>net8.0;net10.0</TargetFrameworks>', '<TargetFramework>net8.0</TargetFramework>') |
    Set-Content -LiteralPath $pruneChildProject
& dotnet restore $pruneParentProject --nologo -v quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Lower-framework pruning fixture restore failed.' }
$lowerPruneAware = Scan $pruneParentProject $false
$lowerPruneDirect = Scan $pruneParentProject $true
SameFindings $lowerPruneAware $lowerPruneDirect
if ($lowerPruneDirect.inputInventory.projects.Count -ne 2 -or
    @($lowerPruneDirect.inputInventory.projects | Where-Object { $_.restoredAssets.status -ne 'matched' }).Count -ne 0 -or
    (HasNotice $lowerPruneDirect 'compiler-error')) { throw 'Parent-only transitive pruning rejected a compatible source dependency.' }

'Framework package pruning and stale-request checks passed.' | Write-Output
