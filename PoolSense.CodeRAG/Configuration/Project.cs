namespace CodeRAG.Core.Configuration;

public sealed record ProjectRoot(string Label, string Path);
public sealed record Project(string Key, string DisplayName, IReadOnlyList<ProjectRoot> Roots);