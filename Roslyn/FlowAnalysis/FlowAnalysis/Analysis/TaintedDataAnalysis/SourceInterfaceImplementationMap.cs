using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Analyzer.Utilities.Extensions;

namespace Analyzer.Utilities.FlowAnalysis.Analysis.TaintedDataAnalysis
{
    /// <summary>Compilation-wide dispatch candidates; never caches receiver-specific decisions.</summary>
    internal sealed class SourceInterfaceImplementationMap
    {
        private static readonly ConditionalWeakTable<Compilation, Lazy<SourceInterfaceImplementationMap>> Cache =
            new ConditionalWeakTable<Compilation, Lazy<SourceInterfaceImplementationMap>>();
        private static readonly ConditionalWeakTable<ControlFlowGraph, Dictionary<CaptureId, IOperation[]>> Captures = new();

        private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _implementations =
            new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<IMethodSymbol, ImmutableArray<IMethodSymbol>> _targets =
            new ConcurrentDictionary<IMethodSymbol, ImmutableArray<IMethodSymbol>>(SymbolEqualityComparer.Default);
        private readonly Dictionary<IMethodSymbol, List<IMethodSymbol>> _overrides =
            new Dictionary<IMethodSymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);
        private readonly Compilation _compilation;
        private readonly ConcurrentDictionary<ITypeSymbol, ConcurrentDictionary<IMethodSymbol, ImmutableArray<IMethodSymbol>>> _constrainedTargets =
            new ConcurrentDictionary<ITypeSymbol, ConcurrentDictionary<IMethodSymbol, ImmutableArray<IMethodSymbol>>>(SymbolEqualityComparer.Default);

        private SourceInterfaceImplementationMap(Compilation compilation)
        {
            _compilation = compilation;
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
                foreach (var member in type.GetMembers())
                {
                    if (member is IMethodSymbol method) AddOverride(method);
                    if (member is IPropertySymbol property)
                    {
                        if (property.GetMethod != null) AddOverride(property.GetMethod);
                        if (property.SetMethod != null) AddOverride(property.SetMethod);
                    }
                    if (member is IEventSymbol @event)
                    {
                        if (@event.AddMethod != null) AddOverride(@event.AddMethod);
                        if (@event.RemoveMethod != null) AddOverride(@event.RemoveMethod);
                    }
                }
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

            void AddOverride(IMethodSymbol method)
            {
                for (var parent = method.OverriddenMethod; parent != null; parent = parent.OverriddenMethod)
                {
                    var key = parent.OriginalDefinition;
                    if (!_overrides.TryGetValue(key, out var candidates))
                        _overrides.Add(key, candidates = new List<IMethodSymbol>());
                    candidates.Add(method);
                }
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

        internal ImmutableArray<IMethodSymbol> GetTargets(IMethodSymbol method, ITypeSymbol? receiverType)
        {
            // Static types constrain candidates; they never identify a unique
            // runtime implementation. Unknown receivers stay conservative.
            if (receiverType == null || receiverType.TypeKind is not
                    (TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Array or TypeKind.TypeParameter))
                return GetTargets(method);
            return _constrainedTargets.GetOrAdd(receiverType, _ =>
                new ConcurrentDictionary<IMethodSymbol, ImmutableArray<IMethodSymbol>>(SymbolEqualityComparer.Default))
                .GetOrAdd(method, member =>
                {
                    if (!_implementations.TryGetValue(member.ContainingType, out var candidates))
                        return ImmutableArray<IMethodSymbol>.Empty;
                    // Filter concrete candidates before resolving inherited methods.
                    return candidates.Where(type => IsCompatible(type, receiverType))
                        .Select(type => type.FindImplementationForInterfaceMember(member))
                        .OfType<IMethodSymbol>()
                        .Where(target => target.Locations.Any(location => location.IsInSource))
                        .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToImmutableArray();
                });
        }

        private bool IsCompatible(INamedTypeSymbol type, ITypeSymbol? receiverType)
        {
            if (receiverType is ITypeParameterSymbol parameter)
            {
                return (!parameter.HasValueTypeConstraint || type.IsValueType) &&
                    parameter.ConstraintTypes.All(constraint => IsCompatible(type, constraint));
            }

            if (receiverType is not INamedTypeSymbol receiver)
            {
                return receiverType == null || _compilation.ClassifyCommonConversion(type, receiverType).IsImplicit;
            }

            if (_compilation.ClassifyCommonConversion(type, receiver).IsImplicit)
            {
                return true;
            }

            return receiver.TypeKind == TypeKind.Interface
                ? type.AllInterfaces.Any(implemented => CanMatchType(implemented, receiver, VarianceKind.Out))
                : CanMatchType(type, receiver, VarianceKind.Out);
        }

        private bool CanMatchType(ITypeSymbol from, ITypeSymbol to, VarianceKind variance)
        {
            if (variance == VarianceKind.In)
            {
                return CanMatchType(to, from, VarianceKind.Out);
            }

            var conversion = _compilation.ClassifyCommonConversion(from, to);
            if (conversion.IsIdentity ||
                (variance == VarianceKind.Out && conversion.IsImplicit && conversion.IsReference))
            {
                return true;
            }

            if (!ContainsTypeParameter(from) && !ContainsTypeParameter(to))
            {
                return false;
            }

            if (from is ITypeParameterSymbol fromParameter)
            {
                return (!fromParameter.HasValueTypeConstraint || !to.IsReferenceType) &&
                    (!fromParameter.HasReferenceTypeConstraint || !to.IsValueType);
            }

            if (to is ITypeParameterSymbol toParameter)
            {
                return (!toParameter.HasValueTypeConstraint || !from.IsReferenceType) &&
                    (!toParameter.HasReferenceTypeConstraint || !from.IsValueType);
            }

            if (from is IArrayTypeSymbol fromArray && to is IArrayTypeSymbol toArray)
            {
                return fromArray.Rank == toArray.Rank &&
                    CanMatchType(fromArray.ElementType, toArray.ElementType, variance);
            }

            if (from is INamedTypeSymbol fromNamed && to is INamedTypeSymbol toNamed &&
                SymbolEqualityComparer.Default.Equals(fromNamed.OriginalDefinition, toNamed.OriginalDefinition))
            {
                if (fromNamed.ContainingType != null && toNamed.ContainingType != null &&
                    !CanMatchType(fromNamed.ContainingType, toNamed.ContainingType, VarianceKind.None))
                {
                    return false;
                }

                for (var index = 0; index < fromNamed.TypeArguments.Length; index++)
                {
                    var argumentVariance = variance == VarianceKind.None
                        ? VarianceKind.None
                        : fromNamed.OriginalDefinition.TypeParameters[index].Variance;
                    if (!CanMatchType(fromNamed.TypeArguments[index], toNamed.TypeArguments[index], argumentVariance))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (variance == VarianceKind.Out && !from.IsValueType && !to.IsValueType)
            {
                return (from is INamedTypeSymbol { BaseType: { } baseType } &&
                        CanMatchType(baseType, to, variance)) ||
                    from.AllInterfaces.Any(implemented => CanMatchType(implemented, to, variance));
            }

            return false;
        }

        private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
        {
            ITypeParameterSymbol => true,
            IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
            INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter) ||
                (named.ContainingType != null && ContainsTypeParameter(named.ContainingType)),
            _ => false
        };

        internal static ITypeSymbol? GetReceiverType(IOperation? instance, ControlFlowGraph? graph = null) =>
            ReceiverType(instance, graph, 0);

        private static ITypeSymbol? ReceiverType(IOperation? instance, ControlFlowGraph? graph, int depth)
        {
            // Lowered foreach/using calls erase receivers to IDisposable/IEnumerable.
            // Built-in implicit conversions preserve the operand's type bound.
            while (instance is IConversionOperation conversion &&
                   (conversion.IsImplicit || conversion.Conversion.IsReference || conversion.Conversion.IsIdentity) &&
                   conversion.OperatorMethod == null)
                instance = conversion.Operand;
            if (graph != null && depth < 8 && instance is IFlowCaptureReferenceOperation reference &&
                Captures.GetValue(graph, cfg => cfg.DescendantOperations<IFlowCaptureOperation>(OperationKind.FlowCapture)
                    .GroupBy(capture => capture.Id).ToDictionary(group => group.Key, group => group.Select(capture => capture.Value).ToArray()))
                .TryGetValue(reference.Id, out var values))
            {
                // Reuse a narrower bound only when every reaching assignment agrees.
                // Mixed or cyclic captures preserve the declared capture type.
                var bounds = values.Select(value => ReceiverType(value, graph, depth + 1)).ToArray();
                if (bounds.Length > 0 && bounds[0] != null && bounds.All(bound =>
                    SymbolEqualityComparer.Default.Equals(bounds[0], bound))) return bounds[0];
            }
            return instance?.Type;
        }

        // Used only to overapproximate reachability, not to choose a runtime receiver.
        internal IEnumerable<IMethodSymbol> GetVirtualTargets(IMethodSymbol method) =>
            _overrides.TryGetValue(method.OriginalDefinition, out var targets)
                ? targets : Enumerable.Empty<IMethodSymbol>();
    }
}
