using GoalKeeper.Application;
using GoalKeeper.Application.Monitoring;

namespace GoalKeeper.Infrastructure;

public sealed class SessionArtifactStore : ISnapshotArtifactStore
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

    public async Task<RetainedSnapshotArtifact> RetainAsync(
        Guid sessionId,
        int sequence,
        CapturedJpegFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A Snapshot requires a Focus Session.", nameof(sessionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        var sessionPath = Claim(sessionId);
        ValidateOwned(sessionId, sessionPath);
        var snapshotsPath = Path.Combine(sessionPath, "snapshots");
        Directory.CreateDirectory(snapshotsPath);
        var finalPath = Path.Combine(
            snapshotsPath,
            $"{sequence:D8}-{frame.Id:N}.jpg");
        var temporaryPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(
                temporaryPath,
                frame.Jpeg.ToArray(),
                cancellationToken);
            File.Move(temporaryPath, finalPath);
            return new(finalPath, frame.Jpeg.Length);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task DeleteAsync(
        Guid sessionId,
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessionPath = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path)));
        ValidateOwned(sessionId, sessionPath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private bool IsWithinRoot(string path)
    {
        var relative = Path.GetRelativePath(_sessionsRoot, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }
}
