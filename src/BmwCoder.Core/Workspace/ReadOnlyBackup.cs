namespace BmwCoder.Core.Workspace;

public sealed record ReadOnlyBackup(string? Directory, int Saved, IReadOnlyList<string> Failures);
