# Configuration is a global property. It controls source selection, symbols,
# generated output paths and test metadata in both loading modes.
$selectionRoot = Join-Path $scratch 'selection'
New-Item -ItemType Directory -Path $selectionRoot | Out-Null
$selectionProject = Join-Path $selectionRoot 'Selection.csproj'
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Configuration>Debug</Configuration></PropertyGroup>
  <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
  <PropertyGroup Condition="'$(Configuration)' == 'Release'"><DefineConstants>$(DefineConstants);RELEASE_VARIANT</DefineConstants></PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)' == 'Security-Test'"><IsTestProject>true</IsTestProject></PropertyGroup>
  <ItemGroup Condition="'$(Configuration)' != 'Release'"><Compile Remove="ReleaseOnly.cs" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $selectionProject
@'
using System.Diagnostics;
using System.Net.Http;
public static class Selection
{
    public static void Run()
    {
        Process.Start(System.Console.ReadLine());
#if RELEASE_VARIANT
        Process.Start(System.Console.ReadLine());
#endif
#if !DEBUG
        _ = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
#endif
    }
}
'@ | Set-Content -LiteralPath (Join-Path $selectionRoot 'Selection.cs')
'public static class ReleaseOnly { public static void Run() => System.Diagnostics.Process.Start(System.Console.ReadLine()); }' |
    Set-Content -LiteralPath (Join-Path $selectionRoot 'ReleaseOnly.cs')
& dotnet restore $selectionProject --nologo -v quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Selection fixture restore failed.' }
foreach ($configuration in @('Debug', 'Release', 'Security-Test')) {
    $arguments = @('--configuration', $configuration)
    $awareSelection = Scan $selectionProject $false -Selection $arguments
    $directSelection = Scan $selectionProject $true -Selection $arguments
    SameFindings $awareSelection $directSelection
    $expectedCommands = if ($configuration -eq 'Release') { 3 } else { 1 }
    $expectedTls = if ($configuration -eq 'Release') { 1 } else { 0 }
    if (@($directSelection.runs[0].results | Where-Object ruleId -eq 'DNA0002').Count -ne $expectedCommands -or
        @($directSelection.runs[0].results | Where-Object ruleId -eq 'DNA0020').Count -ne $expectedTls -or
        (HasNotice $directSelection 'compiler-error')) { throw "Configuration changed expected findings: $configuration" }
    $awareSettings = $awareSelection.inputInventory.projects[0]
    $directSettings = $directSelection.inputInventory.projects[0]
    if ($directSettings.configuration -ne $configuration -or
        $directSelection.inputInventory.selection.configuration -ne $configuration -or
        $awareSettings.compilation.optimizationLevel -ne $directSettings.compilation.optimizationLevel -or
        (($awareSettings.isTestProject -eq 'true') -ne ($directSettings.isTestProject -eq 'true')) -or
        (($directSettings.isTestProject -eq 'true') -ne ($configuration -eq 'Security-Test')) -or
        (Compare-Object $awareSettings.parse.symbols $directSettings.parse.symbols)) {
        throw "Effective configuration metadata differs: $configuration"
    }
}
$selectionXml = Get-Content -LiteralPath $selectionProject -Raw
$selectionXml.Replace('</Project>', '<PropertyGroup><DisableImplicitConfigurationDefines>true</DisableImplicitConfigurationDefines></PropertyGroup></Project>') |
    Set-Content -LiteralPath $selectionProject
foreach ($configuration in @('Debug', 'Release')) {
    $disabledAware = Scan $selectionProject $false -Selection @('--configuration', $configuration)
    $disabledDirect = Scan $selectionProject $true -Selection @('--configuration', $configuration)
    SameFindings $disabledAware $disabledDirect
    if ($disabledDirect.inputInventory.projects[0].parse.symbols -contains $configuration.ToUpperInvariant() -or
        @($disabledDirect.runs[0].results | Where-Object ruleId -eq 'DNA0020').Count -ne 1 -or
        (Compare-Object $disabledAware.inputInventory.projects[0].parse.symbols $disabledDirect.inputInventory.projects[0].parse.symbols)) {
        throw 'DisableImplicitConfigurationDefines was ignored.'
    }
}
$selectionXml | Set-Content -LiteralPath $selectionProject

# Framework selection keeps only the chosen root compilation, while retaining
# a lower-framework source dependency. Do not force TargetFramework globally.
$lowerRoot = Join-Path $scratch 'selection-contracts'
New-Item -ItemType Directory -Path $lowerRoot | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>
'@ | Set-Content -LiteralPath (Join-Path $lowerRoot 'Lower.csproj')
'public class Lower { public string Value { get; set; } = "fixed"; }' | Set-Content -LiteralPath (Join-Path $lowerRoot 'Lower.cs')
$multiRoot = Join-Path $scratch 'selection-multi'
New-Item -ItemType Directory -Path $multiRoot | Out-Null
$multiProject = Join-Path $multiRoot 'Multi.csproj'
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
  <ItemGroup><ProjectReference Include="../selection-contracts/Lower.csproj" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $multiProject
@'
public static class Multi
{
    public static string Read(Lower value) => value.Value;
    public static void Run()
    {
#if NET8_0
        System.Diagnostics.Process.Start(System.Console.ReadLine());
#elif NET10_0
        System.Diagnostics.Process.Start(System.Console.ReadLine());
#endif
    }
}
'@ | Set-Content -LiteralPath (Join-Path $multiRoot 'Multi.cs')
& dotnet restore $multiProject --nologo -v quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Selected framework fixture restore failed.' }
foreach ($framework in @('net8.0', 'net10.0')) {
    $arguments = @('--configuration', 'Release', '--framework', $framework)
    $awareSelection = Scan $multiProject $false -Selection $arguments
    $directSelection = Scan $multiProject $true -Selection $arguments
    SameFindings $awareSelection $directSelection
    if ($directSelection.inputInventory.projects.Count -ne 2 -or
        @($directSelection.runs[0].results).Count -ne 1 -or
        (HasNotice $directSelection 'compiler-error') -or
        $directSelection.runs[0].invocations[0].properties.'dotnetarium.coverage' -ne 'complete' -or
        ($directSelection.inputInventory.projects | Where-Object path -eq 'Multi.csproj').targetFramework -ne $framework -or
        ($directSelection.inputInventory.projects | Where-Object { $_.path -like '*Lower.csproj' }).targetFramework -ne 'net8.0') {
        throw "Root framework/dependency closure selection failed: $framework"
    }
}
# Intentionally unselected frameworks must not load their missing dependencies
# or count an unsupported framework as skipped in the selected direct scan.
$multiXml = Get-Content -LiteralPath $multiProject -Raw
$multiXml.Replace('net8.0;net10.0', 'net8.0;net9.0;net10.0').Replace('</Project>', @'
  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><ProjectReference Include="../missing/Missing.csproj" /></ItemGroup>
</Project>
'@) | Set-Content -LiteralPath $multiProject
$unselected = Scan $multiProject $true -Selection @('--framework', 'net10.0')
if ($unselected.runs[0].invocations[0].properties.'dotnetarium.coverage' -ne 'complete' -or
    $unselected.inputInventory.projects.Count -ne 2 -or @($unselected.runs[0].results).Count -ne 1) {
    throw 'Unselected framework inputs affected the selected direct scan.'
}
$multiXml | Set-Content -LiteralPath $multiProject
foreach ($directMode in @($false, $true)) {
    $unavailable = Scan $selectionProject $directMode 2 -Selection @('--framework', 'net8.0')
    if (-not (HasNotice $unavailable 'framework-selection') -or $unavailable.inputInventory.projects.Count -ne 0) {
        throw 'Unavailable requested framework should not fall back to another framework.'
    }
    $mixedSelection = Join-Path $scratch 'selection.slnx'
    '<Solution><Project Path="selection/Selection.csproj" /><Project Path="selection-contracts/Lower.csproj" /></Solution>' |
        Set-Content -LiteralPath $mixedSelection
    $mixed = Scan $mixedSelection $directMode -Selection @('--framework', 'net10.0', '--configuration', 'Release')
    if (-not (HasNotice $mixed 'framework-selection') -or
        @($mixed.runs[0].results).Count -ne 4 -or $mixed.inputInventory.projects.Count -ne 1) {
        throw 'Unavailable solution root should preserve the selected healthy root and report partial coverage.'
    }
}

# Reusing generated output is configuration-specific and cannot execute a target.
$generatedRoot = Join-Path $scratch 'selection-output'
New-Item -ItemType Directory -Path $generatedRoot | Out-Null
$generatedProject = Join-Path $generatedRoot 'Output.csproj'
$targetMarker = Join-Path $generatedRoot 'executed.txt'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="`$(IntermediateOutputPath)Generated.cs" /></ItemGroup>
  <Target Name="ForbiddenBuild" BeforeTargets="Compile"><WriteLinesToFile File="$targetMarker" Lines="executed" /><Error Text="Must not run" /></Target>
</Project>
"@ | Set-Content -LiteralPath $generatedProject
foreach ($configuration in @('Debug', 'Release')) {
    $outputRoot = Join-Path $generatedRoot "obj/$configuration/net10.0"
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $code = if ($configuration -eq 'Release') {
        'public static class Generated { public static void Run() => System.Diagnostics.Process.Start(System.Console.ReadLine()); }'
    } else { 'public static class Generated { public static void Run() => System.Diagnostics.Process.Start("fixed"); }' }
    $code | Set-Content -LiteralPath (Join-Path $outputRoot 'Generated.cs')
    $generatedSelection = Scan $generatedProject $true -Selection @('--configuration', $configuration)
    $expected = if ($configuration -eq 'Release') { 1 } else { 0 }
    if (@($generatedSelection.runs[0].results).Count -ne $expected -or
        (Test-Path -LiteralPath $targetMarker) -or -not (HasNotice $generatedSelection 'custom-targets')) {
        throw 'Generated output selection ignored configuration or executed custom targets.'
    }
}

# A custom import supplies a second sink. Direct loading must expose the input
# gap and retain the independent sink, rather than claim equivalent coverage.
$importRoot = Join-Path $scratch 'selection-import'
New-Item -ItemType Directory -Path $importRoot | Out-Null
$importProject = Join-Path $importRoot 'Imported.csproj'
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <ItemGroup><Compile Include="Main.cs" /></ItemGroup>
  <Import Project="Extra.props" />
</Project>
'@ | Set-Content -LiteralPath $importProject
'<Project><ItemGroup Condition="''$(Configuration)'' == ''Release''"><Compile Include="Imported.cs" /></ItemGroup></Project>' |
    Set-Content -LiteralPath (Join-Path $importRoot 'Extra.props')
'public static class Main { public static void Run() => System.Diagnostics.Process.Start(System.Console.ReadLine()); }' |
    Set-Content -LiteralPath (Join-Path $importRoot 'Main.cs')
'public static class Imported { public static void Run() => System.Diagnostics.Process.Start(System.Console.ReadLine()); }' |
    Set-Content -LiteralPath (Join-Path $importRoot 'Imported.cs')
& dotnet restore $importProject --nologo -v quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Custom import fixture restore failed.' }
$importAware = Scan $importProject $false -Selection @('--configuration', 'Release')
$importDirect = Scan $importProject $true -Selection @('--configuration', 'Release')
if (@($importAware.runs[0].results).Count -ne 2 -or @($importDirect.runs[0].results).Count -ne 1 -or
    -not (HasNotice $importDirect 'import') -or
    $importDirect.runs[0].invocations[0].properties.'dotnetarium.coverage' -ne 'partial') {
    throw 'Custom imported sources must expose reduced direct coverage.'
}

foreach ($invalid in @(@('--framework', 'net9.0'), @('--configuration'), @('--configuration', '--fail'))) {
    & dotnet $ToolDll $selectionProject @invalid > (Join-Path $scratch 'invalid-selection.log') 2>&1
    if ($LASTEXITCODE -ne 2) { throw 'Invalid selection arguments were accepted.' }
}
'Configuration, root framework and custom-import coverage checks passed.' | Write-Output
