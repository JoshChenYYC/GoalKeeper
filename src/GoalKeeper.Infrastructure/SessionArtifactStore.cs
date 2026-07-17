namespace GoalKeeper.Infrastructure;

public sealed class SessionArtifactStore
{
    private const string MarkerName = ".goalkeeper-session";
    private readonly string _sessionsRoot;

    public SessionArtifactStore(string dataRoot)
    {
        _sessionsRoot = Path.GetFullPath(Path.Combine(dataRoot, "sessions"));
        Directory.CreateDirectory(_sessionsRoot);
    }

    public string Claim(Guid sessionId)
    {
        var path = Path.Combine(_sessionsRoot, sessionId.ToString("N"));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, MarkerName), sessionId.ToString("D"));
        return path;
    }

    public void ValidateOwned(Guid sessionId, string? path)
    {
        if (path is null) return;
        var fullPath = Path.GetFullPath(path);
        if (!IsWithinRoot(fullPath) || !Directory.Exists(fullPath))
        {
            throw new InvalidOperationException("The session artifact directory is missing or outside the application data root.");
        }

        var marker = Path.Combine(fullPath, MarkerName);
        if (!File.Exists(marker) ||
            !Guid.TryParse(File.ReadAllText(marker), out var owner) || owner != sessionId)
        {
            throw new InvalidOperationException("The session artifact directory is not owned by this Focus Session.");
        }
    }

    public void DeleteOwned(Guid sessionId, string? path)
    {
        if (path is null) return;
        ValidateOwned(sessionId, path);
        Directory.Delete(Path.GetFullPath(path), recursive: true);
    }

    private bool IsWithinRoot(string path)
    {
        var relative = Path.GetRelativePath(_sessionsRoot, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }
}
