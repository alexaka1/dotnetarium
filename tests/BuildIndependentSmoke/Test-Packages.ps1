# Runs in Test.ps1's scope. All package assemblies come from real restores.
$centralRoot = Join-Path $scratch 'central'
New-Item -ItemType Directory -Path $centralRoot | Out-Null
$centralProject = Join-Path $centralRoot 'Central.csproj'
$centralProps = Join-Path $centralRoot 'Directory.Packages.props'
$centralXml = @"
<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><ProtoVersion>$protobufVersion</ProtoVersion></PropertyGroup>
<ItemGroup>
<PackageVersion Include="Google.Protobuf" Version="`$(ProtoVersion)" Condition="'`$(TargetFramework)' == 'net8.0'" />
<PackageVersion Include="Google.Protobuf" Version="[`$(ProtoVersion)]" Condition="'`$(TargetFramework)' == 'net10.0'" />
</ItemGroup></Project>
"@
$centralXml | Set-Content -LiteralPath $centralProps
$centralProjectXml = $original.Replace('<TargetFramework>net10.0</TargetFramework>', '<TargetFrameworks>net8.0;net10.0</TargetFrameworks>').Replace('</Project>',
    '<ItemGroup><PackageReference Include="Google.Protobuf" /></ItemGroup></Project>')
$centralProjectXml | Set-Content -LiteralPath $centralProject
Copy-Item -LiteralPath (Join-Path $projectRoot 'Inputs.cs') -Destination $centralRoot
'public static class PackageApi { public static string Read(Google.Protobuf.WellKnownTypes.StringValue value) => value.Value; }' |
    Set-Content -LiteralPath (Join-Path $centralRoot 'PackageApi.cs')
& dotnet restore $centralProject --nologo -v quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Central-package fixture restore failed.' }
$centralReport = Scan $centralProject $true
SameFindings $baseline $centralReport
if (@($centralReport.inputInventory.projects).Count -ne 2 -or (HasNotice $centralReport 'compiler-error') -or
    @($centralReport.inputInventory.projects | Where-Object { $_.restoredAssets.status -ne 'matched' }).Count -ne 0 -or
    @($centralReport.inputInventory.projects.references | Where-Object identity -like 'Google.Protobuf,*').Count -ne 2) { throw 'Conditional central package requests did not preserve real bindings.' }

$centralXml.Replace('Version="[$(ProtoVersion)]"', 'Version="$(ProtoVersion)"') | Set-Content -LiteralPath $centralProps
$centralChanged = Scan $centralProject $true
if (@($centralChanged.inputInventory.projects | Where-Object targetFramework -eq 'net10.0')[0].restoredAssets.status -ne 'stale' -or
    @($centralChanged.inputInventory.projects | Where-Object targetFramework -eq 'net8.0')[0].restoredAssets.status -ne 'matched') {
    throw 'Changing one central TFM request failed to invalidate only that binding.'
}
$centralXml | Set-Content -LiteralPath $centralProps

# VersionOverride wins over the central version, but can be disabled.
$overrideRoot = Join-Path $centralRoot 'override'
New-Item -ItemType Directory -Path $overrideRoot | Out-Null
$overrideProject = Join-Path $overrideRoot 'Override.csproj'
$overrideXml = $original.Replace('</Project>', "<ItemGroup><PackageReference Include=`"Google.Protobuf`" VersionOverride=`"[$protobufVersion]`" /></ItemGroup></Project>")
$overrideXml | Set-Content -LiteralPath $overrideProject
Copy-Item -LiteralPath (Join-Path $projectRoot 'Inputs.cs') -Destination $overrideRoot
& dotnet restore $overrideProject --nologo -v quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Central override fixture restore failed.' }
$overrideReport = Scan $overrideProject $true
if ($overrideReport.inputInventory.projects[0].restoredAssets.status -ne 'matched') { throw 'Central VersionOverride was not honored.' }
$overrideXml.Replace('VersionOverride="[' + $protobufVersion + ']"', 'VersionOverride="' + $protobufVersion + '"') | Set-Content -LiteralPath $overrideProject
if ((Scan $overrideProject $true).inputInventory.projects[0].restoredAssets.status -ne 'stale') { throw 'Changed central override was not detected.' }
$overrideXml.Replace('</PropertyGroup>', '<CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled></PropertyGroup>') |
    Set-Content -LiteralPath $overrideProject
if ((Scan $overrideProject $true).inputInventory.projects[0].restoredAssets.status -ne 'unverified') { throw 'Disabled central override was accepted.' }
$overrideXml | Set-Content -LiteralPath $overrideProject

# Closest central file shadows its parent; no implicit merging of both files.
$nestedProps = Join-Path $overrideRoot 'Directory.Packages.props'
$centralXml.Replace('[$(ProtoVersion)]', '$(ProtoVersion)') | Set-Content -LiteralPath $nestedProps
$noOverride = $overrideXml.Replace(' VersionOverride="[' + $protobufVersion + ']"', '')
$noOverride | Set-Content -LiteralPath $overrideProject
if ((Scan $overrideProject $true).inputInventory.projects[0].restoredAssets.status -ne 'stale') { throw 'Nearest central file did not shadow its parent.' }
Remove-Item -LiteralPath $nestedProps
$centralXml.Replace('</PropertyGroup>', '<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled></PropertyGroup>') |
    Set-Content -LiteralPath $centralProps
if ((Scan $overrideProject $true).inputInventory.projects[0].restoredAssets.status -ne 'unverified') { throw 'Unevaluated transitive pinning was accepted.' }
$centralXml | Set-Content -LiteralPath $centralProps

# SCA inventories include tool/build dependencies with no compile DLLs.
$packageReport = Scan $grpcProject $true
$packageInputs = $packageReport.inputInventory.restoreInputs[0]
$nodes = $packageInputs.packageInventory.targets[0].nodes
if ($packageInputs.assets.status -ne 'matched' -or $packageInputs.advisoryCheck -ne 'not-performed' -or
    @($nodes | Where-Object { $_.id -eq 'Grpc.Tools' -and $_.assetKinds -contains 'build' -and $_.assetKinds -notcontains 'compile' }).Count -ne 1 -or
    @($nodes | Where-Object { $_.id -eq 'Google.Protobuf' -and $_.relationship -eq 'transitive' }).Count -ne 1 -or
    @($nodes | Where-Object id -eq 'Grpc.AspNetCore')[0].dependencies.Count -eq 0) { throw 'SCA evidence omitted packages without compiler references or dependency edges.' }

# Deterministic saved-log test, shaped like a real NuGet audit restore result.
# No live advisory assertions in CI and no advisory requests by the scanner.
$packageAssetsPath = Join-Path $grpcRoot 'obj/project.assets.json'
$savedPackageAssets = Get-Content -LiteralPath $packageAssetsPath -Raw
$packageAssets = $savedPackageAssets | ConvertFrom-Json
$packageAssets | Add-Member -Force -NotePropertyName logs -NotePropertyValue @(
    @{code='NU1903'; level='Warning'; libraryId='Google.Protobuf'; targetGraphs=@('net10.0'); message="Package 'Google.Protobuf' $protobufVersion has a known high severity vulnerability, https://github.com/advisories/GHSA-fixture-only"},
    @{code='NU1900'; level='Warning'; message='Unable to load https://user:private-token@example.invalid/v3/index.json?secret=private-token'}
)
$packageAssets | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $packageAssetsPath
$historical = Scan $grpcProject $true
$audit = $historical.inputInventory.restoreInputs[0]
if ($audit.advisoryCheck -ne 'not-performed' -or $audit.packageInventory.historicalAuditDiagnostics.Count -ne 2 -or
    @($audit.packageInventory.historicalAuditDiagnostics | Where-Object code -eq 'NU1903')[0].advisoryUrls -notcontains 'https://github.com/advisories/GHSA-fixture-only' -or
    @($audit.packageInventory.historicalAuditDiagnostics | Where-Object code -eq 'NU1900')[0].advisoryUrls.Count -ne 0 -or
    ($historical.inputInventory | ConvertTo-Json -Depth 100) -match 'private-token') { throw 'Saved audit evidence was lost, leaked credentials or implied a current audit.' }
$savedPackageAssets | Set-Content -LiteralPath $packageAssetsPath

# Dependency evidence is captured before requiring a usable C# compilation.
$emptyRoot = Join-Path $centralRoot 'empty'
New-Item -ItemType Directory -Path $emptyRoot | Out-Null
$emptyProject = Join-Path $emptyRoot 'Empty.csproj'
$original.Replace('<ImplicitUsings>enable</ImplicitUsings>', '<ImplicitUsings>disable</ImplicitUsings>').Replace('</Project>',
    '<ItemGroup><PackageReference Include="Google.Protobuf" /></ItemGroup></Project>') | Set-Content -LiteralPath $emptyProject
& dotnet restore $emptyProject --nologo -v quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Empty package fixture restore failed.' }
$emptyReport = Scan $emptyProject $true 2
if ($emptyReport.inputInventory.restoreInputs.Count -ne 1 -or
    $emptyReport.inputInventory.restoreInputs[0].assets.status -ne 'matched' -or
    @($emptyReport.inputInventory.restoreInputs[0].packageInventory.targets[0].nodes | Where-Object id -eq 'Google.Protobuf').Count -ne 1) {
    throw 'Dependency evidence was gated on usable source compilation.'
}

# A stale graph remains inspectable for SCA, explicitly tagged as stale.
$centralXml.Replace('[$(ProtoVersion)]', '99.0.0') | Set-Content -LiteralPath $centralProps
$staleInventory = Scan $centralProject $true
$stalePackageInput = @($staleInventory.inputInventory.restoreInputs | Where-Object targetFramework -eq 'net10.0')[0]
if ($stalePackageInput.assets.status -ne 'stale' -or $stalePackageInput.packageInventory.targets.Count -eq 0 -or
    $stalePackageInput.advisoryCheck -ne 'not-performed') { throw 'Stale package evidence lost its uncertainty label.' }
'Central-package, source graph and SCA evidence checks passed.' | Write-Output
