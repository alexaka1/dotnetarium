# Runs in Test.ps1's scope using its scan, finding and notice helpers.
$assetsRoot = Join-Path $scratch 'assets'
New-Item -ItemType Directory -Path $assetsRoot | Out-Null
$assetsProject = Join-Path $assetsRoot 'Assets.csproj'
$grpcAssets = Get-Content -LiteralPath (Join-Path $grpcRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json
$protobufVersion = @($grpcAssets.libraries.PSObject.Properties | Where-Object Name -like 'Google.Protobuf/*')[0].Name.Split('/')[1]
$packageItem = "<ItemGroup><PackageReference Include=`"Google.Protobuf`" Version=`"$protobufVersion`" /></ItemGroup>"
$assetsXml = $original.Replace('</Project>', "$packageItem</Project>")
$assetsXml | Set-Content -LiteralPath $assetsProject
Copy-Item -LiteralPath (Join-Path $projectRoot 'Inputs.cs') -Destination $assetsRoot
'public static class PackageApi { public static string Read(Google.Protobuf.WellKnownTypes.StringValue value) => value.Value; }' |
    Set-Content -LiteralPath (Join-Path $assetsRoot 'PackageApi.cs')
& dotnet restore $assetsProject --nologo -v quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Assets freshness fixture restore failed.' }
$assetsPath = Join-Path $assetsRoot 'obj/project.assets.json'
$savedAssets = Get-Content -LiteralPath $assetsPath -Raw
$freshAssets = Scan $assetsProject $true
SameFindings $baseline $freshAssets
if ($freshAssets.inputInventory.projects[0].restoredAssets.status -ne 'matched' -or
    (HasNotice $freshAssets 'compiler-error') -or
    @($freshAssets.inputInventory.projects[0].references | Where-Object identity -like 'Google.Protobuf,*').Count -ne 1) { throw 'Fresh restore metadata did not preserve real package bindings.' }

# Old timestamps do not imply changed dependency requests.
(Get-Item -LiteralPath $assetsPath).LastWriteTimeUtc = [datetime]'2000-01-01'
$assetsXml.Replace('Version="' + $protobufVersion + '"', 'Version="[' + $protobufVersion + '.0,)"') |
    Set-Content -LiteralPath $assetsProject
$normalized = Scan $assetsProject $true
if ($normalized.inputInventory.projects[0].restoredAssets.status -ne 'matched' -or
    (HasNotice $normalized 'compiler-error')) { throw 'Equivalent version requests or old timestamps were treated as stale.' }

function AssertRejectedAssets($Report, [string]$Status = 'stale') {
    SameFindings $baseline $Report
    $projectInput = @($Report.inputInventory.projects | Where-Object path -eq 'Assets.csproj')[0]
    if ($projectInput.restoredAssets.status -ne $Status -or
        -not (HasNotice $Report $(if ($Status -eq 'stale') { 'package-assets-stale' } else { 'package-assets-unverified' })) -or
        @($projectInput.references | Where-Object identity -like 'Google.Protobuf,*').Count -ne 0) { throw "Unsafe cached package bindings were used ($Status)." }
}
$mutations = @(
    $assetsXml.Replace('Version="' + $protobufVersion + '"', 'Version="[' + $protobufVersion + ']"'),
    $assetsXml.Replace('Version="' + $protobufVersion + '"', 'Version="99.0.0"'),
    $assetsXml.Replace($packageItem, ''),
    $assetsXml.Replace('</Project>', '<ItemGroup><PackageReference Include="Missing.New.Package" Version="1.0.0" /></ItemGroup></Project>'),
    $assetsXml.Replace('Version="' + $protobufVersion + '"', 'Version="' + $protobufVersion + '" Aliases="proto"'),
    $assetsXml.Replace('Version="' + $protobufVersion + '"', 'Version="' + $protobufVersion + '" ExcludeAssets="compile"'),
    $assetsXml.Replace('Version="' + $protobufVersion + '"', 'Version="' + $protobufVersion + '" PrivateAssets="all"'),
    $assetsXml.Replace('</Project>', '<ItemGroup><ProjectReference Include="../contracts/Contracts.csproj" /></ItemGroup></Project>')
)
foreach ($mutation in $mutations) {
    $mutation | Set-Content -LiteralPath $assetsProject
    AssertRejectedAssets (Scan $assetsProject $true)
}
$assetsXml.Replace('Version="' + $protobufVersion + '"', 'Version="3.*"') | Set-Content -LiteralPath $assetsProject
AssertRejectedAssets (Scan $assetsProject $true) 'unverified'
$assetsXml.Replace(' Version="' + $protobufVersion + '"', '') | Set-Content -LiteralPath $assetsProject
AssertRejectedAssets (Scan $assetsProject $true) 'unverified'

$assetsXml | Set-Content -LiteralPath $assetsProject
$wrongProject = $savedAssets | ConvertFrom-Json
$wrongProject.project.restore.projectPath = Join-Path $assetsRoot 'Other.csproj'
$wrongProject | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $assetsPath
AssertRejectedAssets (Scan $assetsProject $true)
$savedAssets | Set-Content -LiteralPath $assetsPath

# After a real restore, package aliases must reach Roslyn as reference metadata.
$assetsXml.Replace('Version="' + $protobufVersion + '"', 'Version="' + $protobufVersion + '" Aliases="proto"') |
    Set-Content -LiteralPath $assetsProject
'extern alias proto; public static class PackageApi { public static string Read(proto::Google.Protobuf.WellKnownTypes.StringValue value) => value.Value; }' |
    Set-Content -LiteralPath (Join-Path $assetsRoot 'PackageApi.cs')
& dotnet restore $assetsProject --nologo -v quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Aliased package fixture restore failed.' }
$packageAlias = Scan $assetsProject $true
SameFindings $baseline $packageAlias
if ($packageAlias.inputInventory.projects[0].restoredAssets.status -ne 'matched' -or
    (HasNotice $packageAlias 'compiler-error') -or
    ($packageAlias.inputInventory.projects[0].references | Where-Object identity -like 'Google.Protobuf,*').aliases -notcontains 'proto') { throw 'Restored package aliases did not bind.' }

# A parent may contain old transitive package assets even when its own direct
# requests match. Until that graph is proven, keep it explicitly unverified.
$parentRoot = Join-Path $scratch 'package-parent'
New-Item -ItemType Directory -Path $parentRoot | Out-Null
$parentProject = Join-Path $parentRoot 'Parent.csproj'
$original.Replace('</Project>', '<ItemGroup><ProjectReference Include="../assets/Assets.csproj" /></ItemGroup></Project>') |
    Set-Content -LiteralPath $parentProject
Copy-Item -LiteralPath (Join-Path $projectRoot 'Inputs.cs') -Destination $parentRoot
& dotnet restore $parentProject --nologo -v quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Package-bearing source dependency restore failed.' }
$parentReport = Scan $parentProject $true
$parentInput = @($parentReport.inputInventory.projects | Where-Object path -eq 'Parent.csproj')[0]
if ($parentInput.restoredAssets.status -ne 'unverified' -or
    -not (HasNotice $parentReport 'package-assets-unverified') -or
    @($parentInput.references | Where-Object identity -like 'Google.Protobuf,*').Count -ne 0 -or
    @($parentReport.runs[0].results).Count -ne 6) { throw 'Transitive restore uncertainty was hidden or independent findings were lost.' }

# A custom output directory is excluded from default globs. Explicit compile
# selection can reuse its code, with an honest freshness/provenance notice.
$reuseRoot = Join-Path $scratch 'reuse'
New-Item -ItemType Directory -Path $reuseRoot | Out-Null
$reuseProject = Join-Path $reuseRoot 'Reuse.csproj'
$reuseXml = $original.Replace('</PropertyGroup>', '<BaseIntermediateOutputPath>cache/</BaseIntermediateOutputPath></PropertyGroup>')
$reuseXml | Set-Content -LiteralPath $reuseProject
Copy-Item -LiteralPath (Join-Path $projectRoot 'Inputs.cs') -Destination $reuseRoot
$output = Join-Path $reuseRoot 'cache/Debug/net10.0'
New-Item -ItemType Directory -Path $output -Force | Out-Null
'public static class ExplicitGenerated { public static void Run() => System.Diagnostics.Process.Start(System.Console.ReadLine()); }' |
    Set-Content -LiteralPath (Join-Path $output 'Generated.cs')
$unselected = Scan $reuseProject $true
SameFindings $baseline $unselected
if (@($unselected.inputInventory.projects[0].sources | Where-Object path -like 'cache/*').Count -ne 0) { throw 'Unselected output code was consumed by default globs.' }
$reuseXml.Replace('</Project>', '<ItemGroup><Compile Include="$(IntermediateOutputPath)Generated.cs" /></ItemGroup></Project>') |
    Set-Content -LiteralPath $reuseProject
$selected = Scan $reuseProject $true
if (@($selected.runs[0].results).Count -ne 4 -or
    -not (HasNotice $selected 'generated-reuse') -or
    @($selected.inputInventory.projects[0].sources | Where-Object path -eq 'cache/Debug/net10.0/Generated.cs').Count -ne 1) { throw 'Explicit generated C# selection was not honored.' }

'Restored-assets freshness and explicit output-selection checks passed.' | Write-Output
