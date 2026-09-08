namespace Oom.Contracts;

public sealed class Compile
{
    public CompileResult Run(string dailyName, string dailyText, string modelOutput) => throw new NotImplementedException("lane C: Compile.Run");
    public CompileDecision MaybeCompile(DateTimeOffset now, DateTimeOffset? lastSuccess, bool hasPending) => throw new NotImplementedException("lane C: Compile.MaybeCompile");
    public IReadOnlyList<string> ValidateOutputPaths(string modelOutput) => throw new NotImplementedException("lane C: Compile.ValidateOutputPaths");
    public string BuildRegistry(IReadOnlyList<Note> notes, IReadOnlyList<string> assignedHubs) => throw new NotImplementedException("lane C: Compile.BuildRegistry");
    public IReadOnlyList<string> SelectCandidates(Note incoming, IReadOnlyList<Note> corpus) => throw new NotImplementedException("lane C: Compile.SelectCandidates");
    public PublicationResult Publish(string dailyName, IReadOnlyDictionary<string, string> files, bool failDuringRebuild) => throw new NotImplementedException("lane C: Compile.Publish");
    public Note ApplyCorrection(Note stale, Note replacement, string source) => throw new NotImplementedException("lane C: Compile.ApplyCorrection");
}

public sealed class RootMap
{
    public string Regenerate() => throw new NotImplementedException("lane C: RootMap.Regenerate");
    public IReadOnlyList<string> Assign(string dailyText) => throw new NotImplementedException("lane C: RootMap.Assign");
}

public sealed class Bridge
{
    public string Refresh() => throw new NotImplementedException("lane C: Bridge.Refresh");
}
