// Copyright (c) Dotnetarium contributors. Licensed under Apache-2.0.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Microsoft.CodeAnalysis.FlowAnalysis.DataFlow
{
    public abstract partial class DataFlowOperationVisitor<TAnalysisData, TAnalysisContext, TAnalysisResult, TAbstractAnalysisValue>
        where TAnalysisData : AbstractAnalysisData
        where TAnalysisContext : AbstractDataFlowAnalysisContext<TAnalysisData, TAnalysisContext, TAnalysisResult, TAbstractAnalysisValue>
        where TAnalysisResult : class, IDataFlowAnalysisResult<TAbstractAnalysisValue>
    {
        // One completed recursive-call summary per call site, scoped to this visitor and bounded.
        // Recursive active calls are never memoized. This is not a method-global
        // cache: caller state, aliases, captures and call depth remain significant.
        private const int CompletedInvocationLimit = 32;
        private Dictionary<IOperation, CompletedInvocation>? _completedInvocations;
        protected virtual bool ReuseCompletedInvocations => false;
        protected virtual bool InvocationInputsEqual(TAnalysisData left, TAnalysisData right) => false;

        private TAnalysisResult? GetOrComputeCompletedInvocation(IOperation operation, TAnalysisContext context)
        {
            var input = context.InterproceduralAnalysisData;
            if (!ReuseCompletedInvocations || input?.InitialAnalysisData == null || input.CachedCallerValues == null)
                return TryGetOrComputeAnalysisResult(context);

            if (_completedInvocations != null && _completedInvocations.TryGetValue(operation, out var previous) &&
                previous.Matches(this, context))
                return previous.Result;
            if (_completedInvocations?.Count >= CompletedInvocationLimit && !_completedInvocations.ContainsKey(operation))
                return TryGetOrComputeAnalysisResult(context);

            // Clone before execution: nested analysis can query the caller's
            // operation cache, and the input will be disposed by the caller.
            var snapshot = GetClonedAnalysisData(input.InitialAnalysisData);
            try
            {
                var result = TryGetOrComputeAnalysisResult(context);
                if (result == null) return null;
                _completedInvocations ??= new Dictionary<IOperation, CompletedInvocation>();
                if (_completedInvocations.TryGetValue(operation, out previous))
                    previous.InputSnapshot.Dispose();
                else if (_completedInvocations.Count >= CompletedInvocationLimit)
                    return result;
                _completedInvocations[operation] = new CompletedInvocation(context, snapshot, input.CachedCallerValues!.ToImmutableDictionary(),
                    ExecutingExceptionPathsAnalysisPostPass, result);
                snapshot = null!;
                return result;
            }
            finally { snapshot?.Dispose(); }
        }

        internal void ClearCompletedInvocations()
        {
            if (_completedInvocations == null) return;
            foreach (var summary in _completedInvocations.Values) summary.InputSnapshot.Dispose();
            _completedInvocations.Clear();
        }

        private sealed class CompletedInvocation
        {
            private readonly TAnalysisContext context;
            private readonly ImmutableDictionary<IOperation, TAbstractAnalysisValue> callerValues;
            private readonly ImmutableDictionary<IOperation, AnalysisEntity?> callerFlowCaptures;
            private readonly bool exceptionPass;
            internal TAnalysisData InputSnapshot { get; }
            internal TAnalysisResult Result { get; }

            internal CompletedInvocation(TAnalysisContext context, TAnalysisData snapshot,
                ImmutableDictionary<IOperation, TAbstractAnalysisValue> callerValues, bool exceptionPass, TAnalysisResult result)
            {
                this.context = context;
                InputSnapshot = snapshot;
                this.callerValues = callerValues;
                callerFlowCaptures = context.InterproceduralAnalysisData!.CachedCallerFlowCaptures!.ToImmutableDictionary();
                this.exceptionPass = exceptionPass;
                Result = result;
            }

            internal bool Matches(DataFlowOperationVisitor<TAnalysisData, TAnalysisContext, TAnalysisResult, TAbstractAnalysisValue> visitor,
                TAnalysisContext next)
            {
                var previous = context.InterproceduralAnalysisData!;
                var input = next.InterproceduralAnalysisData!;
                return SymbolEqualityComparer.Default.Equals(context.OwningSymbol, next.OwningSymbol) &&
                    ReferenceEquals(context.ControlFlowGraph, next.ControlFlowGraph) &&
                    ReferenceEquals(context.PointsToAnalysisResult, next.PointsToAnalysisResult) &&
                    ReferenceEquals(context.CopyAnalysisResult, next.CopyAnalysisResult) &&
                    ReferenceEquals(context.ValueContentAnalysisResult, next.ValueContentAnalysisResult) &&
                    exceptionPass == visitor.ExecutingExceptionPathsAnalysisPostPass &&
                    Equals(previous.InvocationInstance, input.InvocationInstance) &&
                    Equals(previous.ThisOrMeInstanceForCaller, input.ThisOrMeInstanceForCaller) &&
                    ArgumentsEqual(previous.ArgumentValuesMap, input.ArgumentValuesMap) &&
                    MapsEqual(previous.CapturedVariablesMap, input.CapturedVariablesMap) &&
                    MapsEqual(previous.AddressSharedEntities, input.AddressSharedEntities) &&
                    callerValues.All(pair => EqualityComparer<TAbstractAnalysisValue>.Default.Equals(
                        pair.Value, visitor.GetCachedAbstractValue(pair.Key))) &&
                    callerFlowCaptures.All(pair => Equals(pair.Value, input.GetAnalysisEntityForFlowCapture(pair.Key))) &&
                    visitor.InvocationInputsEqual(InputSnapshot, input.InitialAnalysisData!);
                // Both requests come from the same visitor at the same call site:
                // parent context, call stack, depth, options and callbacks are fixed.
            }

            private static bool ArgumentsEqual(ImmutableDictionary<IParameterSymbol, ArgumentInfo<TAbstractAnalysisValue>> left,
                ImmutableDictionary<IParameterSymbol, ArgumentInfo<TAbstractAnalysisValue>> right) =>
                left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) &&
                    ReferenceEquals(pair.Value.Operation, value.Operation) &&
                    Equals(pair.Value.AnalysisEntity, value.AnalysisEntity) &&
                    Equals(pair.Value.InstanceLocation, value.InstanceLocation) &&
                    EqualityComparer<TAbstractAnalysisValue>.Default.Equals(pair.Value.Value, value.Value));

            private static bool MapsEqual<TKey, TValue>(ImmutableDictionary<TKey, TValue> left, ImmutableDictionary<TKey, TValue> right)
                where TKey : notnull => left.Count == right.Count && left.All(pair =>
                    right.TryGetValue(pair.Key, out var value) && EqualityComparer<TValue>.Default.Equals(pair.Value, value));
        }
    }
}
