# Generation happens only during controlled fixture setup. The scanner must
# reuse explicitly selected C# without invoking the target's build pipeline.
$generatedGrpcRoot = Join-Path $scratch 'generated-grpc'
$protoRoot = Join-Path $generatedGrpcRoot 'Protos'
New-Item -ItemType Directory -Path $protoRoot -Force | Out-Null
$generatedGrpcProject = Join-Path $generatedGrpcRoot 'GeneratedGrpc.csproj'
$generatedGrpcXml = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /><PackageReference Include="Grpc.AspNetCore" Version="2.84.0" /><Protobuf Include="Protos/entry.proto" GrpcServices="Server" /></ItemGroup>
</Project>
'@
$generatedGrpcXml | Set-Content -LiteralPath $generatedGrpcProject
@'
syntax = "proto3";
option csharp_namespace = "GeneratedGrpc";
package generated;
service Entry { rpc Run (Input) returns (Reply); }
message Input { string value = 1; }
message Reply { string value = 1; }
'@ | Set-Content -LiteralPath (Join-Path $protoRoot 'entry.proto')
@'
using System.Diagnostics;
using Grpc.Core;
namespace GeneratedGrpc;
public sealed class Service : Entry.EntryBase
{
    public override Task<Reply> Run(Input request, ServerCallContext context)
    {
        Process.Start(request.Value);
        var safe = request.Value;
        safe = "fixed";
        Process.Start(safe);
        Process.Start(context.Method);
        return Task.FromResult(new Reply());
    }
}
'@ | Set-Content -LiteralPath (Join-Path $generatedGrpcRoot 'Service.cs')
Copy-Item -LiteralPath (Join-Path $projectRoot 'Inputs.cs') -Destination $generatedGrpcRoot
& dotnet build $generatedGrpcProject --nologo -v quiet -p:RunAnalyzers=false > (Join-Path $scratch 'grpc-generation.log') 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Controlled protobuf fixture generation failed.' }
$generatedGrpcAware = Scan $generatedGrpcProject $false
$generatedGrpcMissing = Scan $generatedGrpcProject $true
if (@($generatedGrpcAware.runs[0].results).Count -ne 4 -or
    @($generatedGrpcMissing.runs[0].results).Count -ne 3 -or
    -not (HasNotice $generatedGrpcMissing 'compiler-error')) { throw 'Real generated-service coverage gap was not reproduced.' }

$generatedMarker = Join-Path $scratch 'generated-reuse-target-ran.txt'
$explicitGrpcXml = $generatedGrpcXml.Replace('</Project>', @"
<ItemGroup><Compile Include="`$(IntermediateOutputPath)Protos/*.cs" /></ItemGroup>
<Target Name="ForbiddenDuringScan" BeforeTargets="ResolveReferences;CoreCompile"><WriteLinesToFile File="$generatedMarker" Lines="executed" /><Error Text="SCANNER MUST NOT BUILD" /></Target>
</Project>
"@)
$explicitGrpcXml | Set-Content -LiteralPath $generatedGrpcProject
$generatedGrpcDirect = Scan $generatedGrpcProject $true
SameFindings $generatedGrpcAware $generatedGrpcDirect
if ((Test-Path -LiteralPath $generatedMarker) -or (HasNotice $generatedGrpcDirect 'compiler-error') -or
    -not (HasNotice $generatedGrpcDirect 'generated-reuse') -or
    @($generatedGrpcDirect.inputInventory.projects[0].sources | Where-Object path -like 'obj/Debug/net10.0/Protos/*.cs').Count -ne 2) { throw 'Explicit protobuf reuse did not restore bindings without a build.' }

'Real protobuf-generated C# reuse checks passed.' | Write-Output

# Real SDK-generated Razor pages/components, with encoded output as a safe
# control. Explicit additional markup files preserve the render-mode evidence.
$generatedRazorRoot = Join-Path $scratch 'generated-razor'
New-Item -ItemType Directory -Path (Join-Path $generatedRazorRoot 'Pages') -Force | Out-Null
$generatedRazorProject = Join-Path $generatedRazorRoot 'GeneratedRazor.csproj'
$generatedRazorXml = @'
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles><CompilerGeneratedFilesOutputPath>obj/generated/net10.0</CompilerGeneratedFilesOutputPath></PropertyGroup>
  <ItemGroup><AdditionalFiles Include="*.razor;Pages/*.cshtml" /></ItemGroup>
</Project>
'@
$generatedRazorXml | Set-Content -LiteralPath $generatedRazorProject
'var builder = WebApplication.CreateBuilder(args); var app = builder.Build();' |
    Set-Content -LiteralPath (Join-Path $generatedRazorRoot 'Program.cs')
@'
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Web
@using static Microsoft.AspNetCore.Components.Web.RenderMode
'@ | Set-Content -LiteralPath (Join-Path $generatedRazorRoot '_Imports.razor')
@'
@page "/probe"
@rendermode InteractiveServer
@((MarkupString)(Query ?? ""))
@(Query)
@code {
    [SupplyParameterFromQuery] public string? Query { get; set; }
}
'@ | Set-Content -LiteralPath (Join-Path $generatedRazorRoot 'Probe.razor')
@'
@page
@Html.Raw(Request.Query["value"])
@(Request.Query["value"])
'@ | Set-Content -LiteralPath (Join-Path $generatedRazorRoot 'Pages/Probe.cshtml')
& dotnet build $generatedRazorProject --nologo -v quiet -p:RunAnalyzers=false > (Join-Path $scratch 'razor-generation.log') 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Controlled SDK Razor fixture generation failed.' }
$generatedRazorFiles = @(Get-ChildItem -LiteralPath (Join-Path $generatedRazorRoot 'obj/generated/net10.0') -Recurse -File |
    Where-Object { $_.Name -like '*_razor.g.cs' -or $_.Name -like '*_cshtml.g.cs' })
if ($generatedRazorFiles.Count -lt 2) { throw 'SDK Razor output files were not materialized.' }
$missingRazor = Scan $generatedRazorProject $true
if (@($missingRazor.runs[0].results).Count -ne 0) { throw 'Markup-only fixture should require generated C# for these taint flows.' }
$compileItems = ($generatedRazorFiles | ForEach-Object {
    '<Compile Include="' + [Security.SecurityElement]::Escape([IO.Path]::GetRelativePath($generatedRazorRoot, $_.FullName)) + '" />'
}) -join ''
$generatedRazorXml.Replace('</Project>', "<ItemGroup>$compileItems</ItemGroup></Project>") |
    Set-Content -LiteralPath $generatedRazorProject
$reusedRazor = Scan $generatedRazorProject $true
if (@($reusedRazor.runs[0].results | Where-Object ruleId -eq 'DNA0003').Count -ne 2 -or
    (HasNotice $reusedRazor 'compiler-error') -or -not (HasNotice $reusedRazor 'generated-reuse')) { throw 'Real Razor reuse did not preserve raw-output findings and encoded-output controls.' }
$razorLocations = @($reusedRazor.runs[0].results | ForEach-Object {
    $physical = $_.locations[0].physicalLocation
    "$($physical.artifactLocation.uri):$($physical.region.startLine)"
} | Sort-Object)
if (Compare-Object @('Pages/Probe.cshtml:2', 'Probe.razor:3') $razorLocations) { throw 'Reused Razor findings did not map back to relative markup locations.' }
'Real SDK-generated Razor C# reuse checks passed.' | Write-Output
