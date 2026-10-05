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

    [Fact]
    public void Receiver_bound_excludes_unrelated_disposal_but_preserves_compatible_subclasses()
    {
        var compilation = Compile("Bounds", """
            using System;
            using System.IO;
            public sealed class Unrelated : IDisposable { public void Dispose() {} }
            public sealed class ChildStream : MemoryStream, IDisposable { void IDisposable.Dispose() {} }
            """);
        var member = (IMethodSymbol)compilation.GetTypeByMetadataName("System.IDisposable")!.GetMembers("Dispose").Single();
        var map = SourceInterfaceImplementationMap.GetOrCreate(compilation);
        Assert.Equal(2, map.GetTargets(member).Length);
        var bounded = Assert.Single(map.GetTargets(member, compilation.GetTypeByMetadataName("System.IO.MemoryStream")));
        Assert.Equal("ChildStream", bounded.ContainingType.Name);
        Assert.Empty(map.GetTargets(member, compilation.GetTypeByMetadataName("System.String")));
        Assert.Equal(2, map.GetTargets(member, null).Length);
    }

    [Fact]
    public void Filters_candidates_before_mapping_an_inherited_implementation()
    {
        var compilation = Compile("Inherited", """
            public interface IService { void Go(); }
            public class Base : IService { public void Go() {} }
            public class Derived : Base {}
            public class Other : IService { public void Go() {} }
            """);
        var member = (IMethodSymbol)compilation.GetTypeByMetadataName("IService")!.GetMembers("Go").Single();
        var target = Assert.Single(SourceInterfaceImplementationMap.GetOrCreate(compilation)
            .GetTargets(member, compilation.GetTypeByMetadataName("Derived")));
        Assert.Equal("Base", target.ContainingType.Name);
    }

    [Fact]
    public void Preserves_covariant_and_generic_unknown_receiver_candidates()
    {
        var compilation = Compile("Variance", """
            public interface IService { void Go(); }
            public interface IProducer<out T> { T Get(); }
            public class Compatible : IService, IProducer<string> { public void Go() {} public string Get() => ""; }
            public class Other : IService { public void Go() {} }
            public class Generic<T> where T : IService {}
            """);
        var member = (IMethodSymbol)compilation.GetTypeByMetadataName("IService")!.GetMembers("Go").Single();
        var map = SourceInterfaceImplementationMap.GetOrCreate(compilation);
        var bound = compilation.GetTypeByMetadataName("IProducer`1")!.Construct(compilation.GetSpecialType(SpecialType.System_Object));
        Assert.Equal("Compatible", Assert.Single(map.GetTargets(member, bound)).ContainingType.Name);
        Assert.Equal(2, map.GetTargets(member, compilation.GetTypeByMetadataName("Generic`1")!.TypeParameters[0]).Length);
    }

    [Fact]
    public void Closed_receiver_does_not_exclude_an_open_source_definition()
    {
        var compilation = Compile("Generics", """
            public interface IService { void Go(); }
            public class Generic<T> : IService { public void Go() {} }
            public class Other : IService { public void Go() {} }
            """);
        var member = (IMethodSymbol)compilation.GetTypeByMetadataName("IService")!.GetMembers("Go").Single();
        var receiver = compilation.GetTypeByMetadataName("Generic`1")!.Construct(compilation.GetSpecialType(SpecialType.System_Int32));
        Assert.Equal("Generic", Assert.Single(SourceInterfaceImplementationMap.GetOrCreate(compilation)
            .GetTargets(member, receiver)).ContainingType.Name);
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
