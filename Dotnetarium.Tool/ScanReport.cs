using System.Collections.Concurrent;

namespace Dotnetarium.Tool;

internal sealed record ScanNotice(string Id, string Message, bool IsFailure = false);

internal sealed class ScanReport
{
    internal ConcurrentQueue<ScanNotice> Notices { get; } = new();
    internal List<string> AnalyzedProjects { get; } = [];
    internal List<string> SkippedProjects { get; } = [];
    internal bool HasFailures => Notices.Any(notice => notice.IsFailure);
    internal bool IsPartial => Notices.Count > 0 || SkippedProjects.Count > 0;
    internal void Warn(string id, string message) => Notices.Enqueue(new(id, message));
    internal void Fail(string id, string message) => Notices.Enqueue(new(id, message, true));
}
