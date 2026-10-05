// Copyright (c) Dotnetarium contributors. Licensed under Apache-2.0.

using System;
using System.Collections.Concurrent;

namespace Analyzer.Utilities
{
    /// <summary>Shares active results without retaining completed analysis trees.</summary>
    internal sealed class SharedWeakAnalysisCache<TKey, TValue> where TValue : class
    {
        private sealed class Entry
        {
            internal WeakReference<TValue>? Result;
        }
        private readonly ConcurrentDictionary<TKey, Entry> entries = new();

        internal TValue? GetOrCompute(TKey key, Func<TKey, TValue?> compute)
        {
            var entry = entries.GetOrAdd(key, _ => new Entry());
            lock (entry)
            {
                if (entry.Result?.TryGetTarget(out var cached) == true) return cached;
                var result = compute(key);
                if (result != null) entry.Result = new WeakReference<TValue>(result);
                return result;
            }
        }
    }
}
