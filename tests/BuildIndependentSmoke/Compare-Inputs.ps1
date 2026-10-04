param(
    [Parameter(Mandatory)][string]$ProjectInventory,
    [Parameter(Mandatory)][string]$DirectInventory,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$project = Get-Content -LiteralPath $ProjectInventory -Raw | ConvertFrom-Json
$direct = Get-Content -LiteralPath $DirectInventory -Raw | ConvertFrom-Json
if ($project.schemaVersion -ne 1 -or $direct.schemaVersion -ne 1) { throw 'Expected input inventory schema 1.' }
if ($project.loadingMode -ne 'project' -or $direct.loadingMode -ne 'direct') { throw 'Provide project-aware and direct inventories in that order.' }
if ($project.target -cne $direct.target) { throw 'Inventories must describe the same target.' }

function ProjectKey($Value) { "$($Value.path)|$($Value.targetFramework)" }
function Json($Value) { ConvertTo-Json -InputObject $Value -Depth 40 -Compress }
function SetDifference($Left, $Right) {
    $leftValues = @($Left | Sort-Object -Unique -CaseSensitive)
    $rightValues = @($Right | Sort-Object -Unique -CaseSensitive)
    $changes = @(Compare-Object $leftValues $rightValues -CaseSensitive)
    [ordered]@{
        onlyProject = @($changes | Where-Object SideIndicator -eq '<=' | ForEach-Object InputObject)
        onlyDirect = @($changes | Where-Object SideIndicator -eq '=>' | ForEach-Object InputObject)
    }
}

$pathComparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
$projectMap = [Collections.Generic.Dictionary[string,object]]::new($pathComparer)
$directMap = [Collections.Generic.Dictionary[string,object]]::new($pathComparer)
foreach ($item in $project.projects) { $projectMap[(ProjectKey $item)] = $item }
foreach ($item in $direct.projects) { $directMap[(ProjectKey $item)] = $item }
$pairs = @(foreach ($key in @(@($projectMap.Keys) + @($directMap.Keys) | Sort-Object -Unique -CaseSensitive)) {
    $left = $projectMap[$key]
    $right = $directMap[$key]
    if (-not $left -or -not $right) {
        [ordered]@{ project = $key; status = $(if ($left) { 'only-project' } else { 'only-direct' }) }
        continue
    }
    $differences = [ordered]@{}
    foreach ($field in @('assemblyName', 'parse', 'compilation', 'isTestProject')) {
        if ((Json $left.$field) -cne (Json $right.$field)) {
            $differences[$field] = [ordered]@{ project = $left.$field; direct = $right.$field }
        }
    }
    # Physical cache/SDK paths can differ while assembly identity and reference
    # semantics match. Keep paths in the inventories; compare semantic bindings.
    $categories = [ordered]@{
        userSources = { param($p) @($p.sources | Where-Object { -not $_.generatedCandidate } | ForEach-Object { Json ([ordered]@{ path = $_.path; sha256 = $_.sha256 }) }) }
        sourceParseOverrides = { param($p) @($p.sources | Where-Object { (Json $_.settings) -cne (Json $p.parse) } | ForEach-Object { Json ([ordered]@{ path = $_.path; settings = $_.settings }) }) }
        generatedSources = { param($p) @($p.sources | Where-Object generatedCandidate | ForEach-Object { Json $_ }) }
        references = { param($p) @($p.references | ForEach-Object { Json ([ordered]@{ identity = $_.identity; kind = $_.kind; project = $_.project; targetFramework = $_.targetFramework; aliases = $_.aliases; embedInteropTypes = $_.embedInteropTypes; imageKind = $_.imageKind }) }) }
        projectReferences = { param($p) @($p.projectReferences | ForEach-Object { Json $_ }) }
        analyzerConfigs = { param($p) @($p.analyzerConfigs | ForEach-Object { Json $_ }) }
        additionalFiles = { param($p) @($p.additionalFiles | ForEach-Object { Json $_ }) }
        analyzerReferences = { param($p) @($p.analyzerReferences | ForEach-Object { Json $_ }) }
    }
    foreach ($category in $categories.GetEnumerator()) {
        $delta = SetDifference (& $category.Value $left) (& $category.Value $right)
        if ($delta.onlyProject.Count -gt 0 -or $delta.onlyDirect.Count -gt 0) { $differences[$category.Key] = $delta }
    }
    [ordered]@{
        project = $key
        status = $(if ($differences.Count) { 'different' } else { 'same' })
        # Configuration names may not be exposed by MSBuildWorkspace. Unknown
        # metadata is not a settings mismatch; the effective settings are above.
        configuration = [ordered]@{ project = $left.configuration; direct = $right.configuration }
        differences = $differences
    }
})
$result = [ordered]@{ schemaVersion = 1; target = $project.target; projects = $pairs }
if ($OutputPath) {
    $fullPath = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath)) | Out-Null
    $result | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $fullPath -Encoding utf8
}
# Differences are evidence for review, not a failing scan or proof of a missed
# finding. Emit a structured object for the smoke suite and interactive use.
[pscustomobject]$result
