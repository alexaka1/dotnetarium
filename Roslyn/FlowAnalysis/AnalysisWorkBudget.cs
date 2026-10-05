// Copyright (c) Dotnetarium contributors. Licensed under Apache-2.0.

using System;
using System.Threading;

namespace Analyzer.Utilities
{
    /// <summary>Shared by a synchronous root analysis and every nested dataflow call.</summary>
    internal sealed class AnalysisWorkBudget : IDisposable
    {
        // Roslyn invokes each operation-block action synchronously. Nested engine
        // calls stay on its thread; concurrent roots receive independent budgets.
        [ThreadStatic] private static AnalysisWorkBudget? current;
        private readonly AnalysisWorkBudget? previous;
        private readonly CancellationToken cancellationToken;
        private bool disposed;

        internal AnalysisWorkBudget(uint limit, CancellationToken cancellationToken)
        {
            if (limit == 0) throw new ArgumentOutOfRangeException(nameof(limit));
            cancellationToken.ThrowIfCancellationRequested();
            Limit = limit;
            this.cancellationToken = cancellationToken;
            previous = current;
            current = this;
        }

        internal uint Limit { get; }
        internal long Work { get; private set; }
        internal long Graphs { get; private set; }
        internal long Blocks { get; private set; }
        internal long Operations { get; private set; }

        internal static void EnterGraph()
        {
            if (current is { } budget) { budget.Graphs++; budget.Consume(); }
        }

        internal static void VisitBlock()
        {
            if (current is { } budget) { budget.Blocks++; budget.Consume(); }
        }

        internal static void VisitOperation()
        {
            if (current is { } budget) { budget.Operations++; budget.Consume(); }
        }

        private void Consume()
        {
            if (++Work > Limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new AnalysisWorkLimitException(this);
            }
            if ((Work & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            if (disposed) return;
            current = previous;
            disposed = true;
        }
    }

    internal sealed class AnalysisWorkLimitException : Exception
    {
        internal AnalysisWorkLimitException(AnalysisWorkBudget budget)
            : base("The root dataflow analysis exhausted its work budget.") => Budget = budget;

        internal AnalysisWorkBudget Budget { get; }
    }
}
