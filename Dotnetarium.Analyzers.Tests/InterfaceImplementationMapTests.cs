using Analyzer.Utilities.FlowAnalysis.Analysis.TaintedDataAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Dotnetarium.Analyzers.Tests;

public sealed class InterfaceImplementationMapTests
{
    [Fact]
    public async Task Concurrent_lookup_preserves_nested_explicit_and_inherited_source_targets()
    {
        var compilation = Compile("Local", """
            public interface IService<T> { void Go(T value); }
            public abstract class Container
            {
                public sealed class Nested : IService<string> { public void Go(string value) {} }
                public struct Explicit : IService<string> { void IService<string>.Go(string value) {} }
            }
            public abstract class Base : IService<string> { public void Go(string value) {} }
            public sealed class Derived : Base {}
            public sealed class OtherDerived : Base {}
            public sealed class Different : IService<int> { public void Go(int value) {} }
            """);
        var contract = compilation.GetTypeByMetadataName("IService`1")!
            .Construct(compilation.GetSpecialType(SpecialType.System_String));
        var method = (IMethodSymbol)contract.GetMembers("Go").Single();
        var maps = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
            SourceInterfaceImplementationMap.GetOrCreate(compilation))));
        Assert.All(maps, map => Assert.Same(maps[0], map));
        var results = await Task.WhenAll(maps.Select(map => Task.Run(() => map.GetTargets(method))));
        foreach (var targets in results)
            Assert.Equal(new[] { "Base", "Container.Explicit", "Container.Nested" },
                targets.Select(target => target.ContainingType.ToDisplayString()).Order().ToArray());
    }

    [Fact]
    public void Includes_source_project_implementations_but_not_emitted_metadata_bodies()
    {
        var dependency = Compile("Dependency", """
            public interface IService { void Go(string value); }
            public sealed class Service : IService { public void Go(string value) {} }
            """);
        var sourceRoot = Compile("Root", "public sealed class Endpoint { public IService Service = null!; }",
            dependency.ToMetadataReference());
        var contract = sourceRoot.GetTypeByMetadataName("IService")!;
        var method = (IMethodSymbol)contract.GetMembers("Go").Single();
        var target = Assert.Single(SourceInterfaceImplementationMap.GetOrCreate(sourceRoot).GetTargets(method));
        Assert.Equal("Service", target.ContainingType.Name);
        using var output = new MemoryStream();
        Assert.True(dependency.Emit(output).Success);
        var metadataRoot = Compile("MetadataRoot", "public sealed class Endpoint { public IService Service = null!; }",
            MetadataReference.CreateFromImage(output.ToArray()));
        var metadataMethod = (IMethodSymbol)metadataRoot.GetTypeByMetadataName("IService")!.GetMembers("Go").Single();
        Assert.Empty(SourceInterfaceImplementationMap.GetOrCreate(metadataRoot).GetTargets(metadataMethod));
        Assert.NotSame(SourceInterfaceImplementationMap.GetOrCreate(sourceRoot),
            SourceInterfaceImplementationMap.GetOrCreate(metadataRoot));
    }

    private static CSharpCompilation Compile(string name, string source, params MetadataReference[] dependencies)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).Cast<MetadataReference>().Concat(dependencies);
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation;
    }
}
