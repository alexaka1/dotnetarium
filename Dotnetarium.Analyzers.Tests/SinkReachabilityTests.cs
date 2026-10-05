using System.Collections.Immutable;
using System.Text;
using Analyzer.Utilities.Extensions;
using Analyzer.Utilities.FlowAnalysis.Analysis.TaintedDataAnalysis;
using Dotnetarium.Analyzers.Taint;
using Dotnetarium.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace Dotnetarium.Analyzers.Tests;

public sealed class SinkReachabilityTests
{
    [Fact]
    public void Proves_a_recursive_sink_free_closure_without_poisoning_a_sink_bearing_cycle()
    {
        var compilation = Compile("""
            using System.Diagnostics;
            public static class Demo
            {
                public static string Safe(string value, int count) => A(value, count);
                static string A(string value, int count) => count > 0 ? B(value, count - 1) : value.Trim();
                static string B(string value, int count) => A(value, count);
                public static void Unsafe(string value, int count) => X(value, count);
                static void X(string value, int count) { Y(value, count); }
                static void Y(string value, int count) { if (count > 0) X(value, count - 1); else Process.Start(value); }
            }
            """);
        var checker = Checker(compilation);
        Assert.False(checker.MayReachSink(Graph(compilation, "Demo", "Safe")));
        Assert.True(checker.MayReachSink(Graph(compilation, "Demo", "Unsafe")));
        Assert.True(checker.MayReachSink(Graph(compilation, "Demo", "Y")));
        Assert.False(checker.MayReachSink(Graph(compilation, "Demo", "B")));
    }

    [Theory]
    [InlineData("Helper(value)")]
    [InlineData("new Runner(value)")]
    [InlineData("Read = value")]
    [InlineData("_ = Read")]
    [InlineData("_ = (string)new Runner(value)")]
    [InlineData("_ = new Runner(value) + new Runner(value)")]
    public void Preserves_helper_constructor_accessor_and_operator_sinks(string expression)
    {
        var compilation = Compile($$"""
            using System.Diagnostics;
            public static class Demo
            {
                public static void Run(string value) { {{expression}}; }
                static void Helper(string value) => Process.Start(value);
                static string Read { get { Process.Start("fixed"); return "fixed"; } set { Process.Start(value); } }
            }
            public sealed class Runner
            {
                readonly string value;
                public Runner(string value) { this.value = value; Process.Start(value); }
                public static implicit operator string(Runner value) { Process.Start(value.value); return value.value; }
                public static Runner operator +(Runner left, Runner right) { Process.Start(left.value); return left; }
            }
            """);
        Assert.True(Checker(compilation).MayReachSink(Graph(compilation, "Demo", "Run")));
    }

    [Theory]
    [InlineData("void Execute() { Helper(value); }")]
    [InlineData("System.Action action = () => Helper(value);")]
    [InlineData("System.Func<System.Action> factory = () => () => Helper(value);")]
    public void Retains_nested_callbacks_and_local_functions(string nested)
    {
        var compilation = Compile($$"""
            using System.Diagnostics;
            public static class Demo
            {
                public static void Run(string value) { {{nested}} }
                static void Helper(string value) => Process.Start(value);
            }
            """);
        Assert.True(Checker(compilation).MayReachSink(Graph(compilation, "Demo", "Run")));
    }

    [Fact]
    public void Considers_all_interface_implementations_and_class_overrides()
    {
        var compilation = Compile("""
            using System.Diagnostics;
            public interface IRunner { void Run(string value); }
            public sealed class Safe : IRunner { public void Run(string value) {} }
            public abstract class Base { public virtual void Execute(string value) {} }
            public sealed class Unsafe : Base, IRunner
            {
                void IRunner.Run(string value) => Process.Start(value);
                public override void Execute(string value) => Process.Start(value);
            }
            public static class Demo
            {
                public static void Interface(IRunner runner, string value) => runner.Run(value);
                public static void Virtual(Base runner, string value) => runner.Execute(value);
            }
            """);
        var checker = Checker(compilation);
        Assert.True(checker.MayReachSink(Graph(compilation, "Demo", "Interface")));
        Assert.True(checker.MayReachSink(Graph(compilation, "Demo", "Virtual")));
    }

    [Fact]
    public void Includes_lowered_foreach_disposal_and_move_next_calls()
    {
        var compilation = Compile("""
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Diagnostics;
            public sealed class Entries : IEnumerable<string>
            {
                public IEnumerator<string> GetEnumerator() => new Enumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public sealed class Enumerator : IEnumerator<string>
            {
                public string Current => "fixed";
                object IEnumerator.Current => Current;
                public bool MoveNext() { Process.Start("fixed"); return false; }
                public void Reset() {}
                public void Dispose() { Process.Start("fixed"); }
            }
            public static class Demo
            {
                public static void Run() { foreach (var entry in new Entries()) {} }
                public static void DisposeOnly() { using (new Enumerator()) {} }
            }
            """);
        var checker = Checker(compilation);
        Assert.True(checker.MayReachSink(Graph(compilation, "Demo", "Run")));
        Assert.True(checker.MayReachSink(Graph(compilation, "Demo", "DisposeOnly")));
    }

    [Fact]
    public void Opaque_delegate_invocations_and_invalid_code_preserve_normal_analysis()
    {
        var compilation = Compile("""
            public static class Demo
            {
                public static void Delegate(System.Action<string> action, string value) => action(value);
                public static void Invalid(string value) => Missing(value);
            }
            """, allowErrors: true);
        var checker = Checker(compilation);
        Assert.True(checker.MayReachSink(Graph(compilation, "Demo", "Delegate")));
        Assert.True(checker.MayReachSink(Graph(compilation, "Demo", "Invalid")));
    }

    [Fact]
    public void Source_project_bodies_outside_the_compilation_are_not_assumed_sink_free()
    {
        var dependency = Compile("""
            public static class Helper { public static void Execute(string value) => System.Diagnostics.Process.Start(value); }
            """, name: "Dependency");
        var root = Compile("public static class Demo { public static void Run(string value) => Helper.Execute(value); }",
            dependencies: [dependency.ToMetadataReference()]);
        Assert.True(Checker(root).MayReachSink(Graph(root, "Demo", "Run")));
    }

    [Fact]
    public void Proof_budget_exhaustion_preserves_analysis()
    {
        var source = new StringBuilder("public static class Demo { public static string Run(string value) => M0(value);");
        for (var index = 0; index < 540; index++)
            source.Append($"static string M{index}(string value) => " + (index == 539 ? "value;" : $"M{index + 1}(value);"));
        source.Append('}');
        var compilation = Compile(source.ToString());
        Assert.True(Checker(compilation).MayReachSink(Graph(compilation, "Demo", "Run")));
    }

    [Fact]
    public void Custom_sink_models_participate_in_the_proof()
    {
        var compilation = Compile("""
            public static class Target { public static void Send(string value) {} }
            public static class Demo { public static void Run(string value) => Target.Send(value); }
            """);
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("""
            {"Version":"2.0","Sinks":[{"Type":"Target","TaintTypes":["CommandInjection"],"Methods":[{"Name":"Send","Arguments":["value"]}]}]}
            """)));
        var config = new ConfigurationReader().DeserializeAndValidate<ConfigData>(reader, true);
        var options = new Microsoft.CodeAnalysis.Diagnostics.AnalyzerOptions(ImmutableArray<AdditionalText>.Empty);
        var map = new TaintConfiguration(config, compilation, options).GetSinkSymbolMap((SinkKind)(int)TaintType.CommandInjection);
        Assert.True(new SinkReachability(compilation, map).MayReachSink(Graph(compilation, "Demo", "Run")));
    }

    [Fact]
    public void Non_sink_members_on_a_sink_type_do_not_require_flow_analysis()
    {
        var compilation = Compile("""
            using System.Diagnostics;
            public static class Demo {
                public static void Run(Process process) { _ = process.Id; _ = process.HasExited; process.Kill(); process.Dispose(); }
            }
            """);
        Assert.False(Checker(compilation).MayReachSink(Graph(compilation, "Demo", "Run")));
    }

    [Fact]
    public void Lowered_metadata_cleanup_does_not_reach_unrelated_source_disposers()
    {
        var compilation = Compile("""
            using System;
            using System.IO;
            using System.Diagnostics;
            public sealed class Unrelated : IDisposable { public void Dispose() => Process.Start("fixed"); }
            public static class Demo {
                public static void Run(string[] values) {
                    using var stream = new MemoryStream();
                    foreach (var value in values) stream.WriteByte((byte)value.Length);
                }
            }
            """);
        Assert.False(Checker(compilation).MayReachSink(Graph(compilation, "Demo", "Run")));
    }

    [Fact]
    public void Interface_bound_keeps_a_compatible_source_disposer_sink()
    {
        var compilation = Compile("""
            using System;
            using System.IO;
            using System.Diagnostics;
            public sealed class ChildStream : MemoryStream, IDisposable { void IDisposable.Dispose() => Process.Start("fixed"); }
            public static class Demo { public static void Run(MemoryStream stream) { using (stream) {} } }
            """);
        Assert.True(Checker(compilation).MayReachSink(Graph(compilation, "Demo", "Run")));
    }

    private static SinkReachability Checker(CSharpCompilation compilation)
    {
        var options = new Microsoft.CodeAnalysis.Diagnostics.AnalyzerOptions(ImmutableArray<AdditionalText>.Empty);
        var config = new TaintConfiguration(new ConfigurationReader().GetBuiltinConfiguration(), compilation, options);
        return config.GetSinkReachability((SinkKind)(int)TaintType.CommandInjection);
    }

    private static ControlFlowGraph Graph(CSharpCompilation compilation, string type, string method) =>
        ((IMethodSymbol)compilation.GetTypeByMetadataName(type)!.GetMembers(method).Single())
            .GetTopmostOperationBlock(compilation)!.GetEnclosingControlFlowGraph()!;

    private static CSharpCompilation Compile(string source, bool allowErrors = false,
        string name = "Example", params MetadataReference[] dependencies)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).Cast<MetadataReference>().Concat(dependencies);
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        if (!allowErrors) Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation;
    }
}
