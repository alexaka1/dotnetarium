using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Analyzer.Utilities.FlowAnalysis.Analysis.TaintedDataAnalysis
{
    /// <summary>Compilation-wide dispatch candidates; never caches receiver-specific decisions.</summary>
    internal sealed class SourceInterfaceImplementationMap
    {
        private static readonly ConditionalWeakTable<Compilation, Lazy<SourceInterfaceImplementationMap>> Cache =
            new ConditionalWeakTable<Compilation, Lazy<SourceInterfaceImplementationMap>>();

        private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _implementations =
            new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<IMethodSymbol, ImmutableArray<IMethodSymbol>> _targets =
            new ConcurrentDictionary<IMethodSymbol, ImmutableArray<IMethodSymbol>>(SymbolEqualityComparer.Default);

        private SourceInterfaceImplementationMap(Compilation compilation)
        {
            // The merged namespace also includes source project references. Metadata
            // types cannot contribute a source body, so do not expand their members
            // or compute their interface closure on every analyzed invocation.
            AddNamespace(compilation.GlobalNamespace);

            void AddNamespace(INamespaceSymbol scope)
            {
                foreach (var type in scope.GetTypeMembers()) AddType(type);
                foreach (var child in scope.GetNamespaceMembers()) AddNamespace(child);
            }

            void AddType(INamedTypeSymbol type)
            {
                if (!type.Locations.Any(location => location.IsInSource)) return;
                if (!type.IsAbstract)
                    foreach (var contract in type.AllInterfaces)
                    {
                        if (!_implementations.TryGetValue(contract, out var candidates))
                            _implementations.Add(contract, candidates = new List<INamedTypeSymbol>());
                        candidates.Add(type);
                    }
                // Concrete nested types can exist inside abstract source containers.
                foreach (var nested in type.GetTypeMembers()) AddType(nested);
            }
        }

        internal static SourceInterfaceImplementationMap GetOrCreate(Compilation compilation) =>
            Cache.GetValue(compilation, key => new Lazy<SourceInterfaceImplementationMap>(
                () => new SourceInterfaceImplementationMap(key))).Value;

        internal ImmutableArray<IMethodSymbol> GetTargets(IMethodSymbol method) =>
            _targets.GetOrAdd(method, member =>
            {
                if (!_implementations.TryGetValue(member.ContainingType, out var candidates))
                    return ImmutableArray<IMethodSymbol>.Empty;
                var targets = ImmutableArray.CreateBuilder<IMethodSymbol>();
                var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
                foreach (var type in candidates)
                    if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol target &&
                        target.Locations.Any(location => location.IsInSource) && seen.Add(target))
                        targets.Add(target);
                return targets.ToImmutable();
            });
    }
}
