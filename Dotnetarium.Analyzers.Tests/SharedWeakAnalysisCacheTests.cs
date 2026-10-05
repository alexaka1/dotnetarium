using System.Runtime.CompilerServices;
using Analyzer.Utilities;

namespace Dotnetarium.Analyzers.Tests;

public sealed class SharedWeakAnalysisCacheTests
{
    [Fact]
    public async Task Concurrent_requests_share_a_live_result()
    {
        var cache = new SharedWeakAnalysisCache<int, object>();
        var count = 0;
        var values = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            cache.GetOrCompute(1, _ => { Interlocked.Increment(ref count); return new object(); }))));
        Assert.Equal(1, count);
        Assert.All(values, value => Assert.Same(values[0], value));
    }

    [Fact]
    public void Completed_results_can_be_collected_and_recomputed()
    {
        var cache = new SharedWeakAnalysisCache<int, object>();
        var previous = Create(cache);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(previous.TryGetTarget(out _));
        Assert.NotNull(cache.GetOrCompute(1, _ => new object()));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> Create(SharedWeakAnalysisCache<int, object> cache) =>
        new(cache.GetOrCompute(1, _ => new object())!);
}
