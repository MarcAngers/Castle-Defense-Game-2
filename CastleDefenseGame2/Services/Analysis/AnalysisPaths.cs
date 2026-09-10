namespace CastleDefense.Api.Services.Analysis;

/// <summary>
/// Where the /analysis pages find things on disk.
///
/// The recordings root comes from <see cref="RecordingPaths"/>, so an agent-redirected
/// server browses ITS OWN games and never Marc's -- the same reasoning that made recordings
/// configurable in the first place. Everything else lives at the REPO root, which is one
/// level above the web project's content root but is found by walking up and looking for a
/// landmark rather than by assuming, so a published layout fails loudly instead of silently
/// reporting an empty dashboard.
/// </summary>
public sealed class AnalysisPaths
{
    public string RecordingsRoot { get; }
    public string RepoRoot { get; }

    /// <summary>Null when this is not a source checkout (a published server, say).</summary>
    public string DashboardHtml { get; }
    public string UpdateScript { get; }

    public AnalysisPaths(IConfiguration config, IWebHostEnvironment env)
    {
        RecordingsRoot = RecordingPaths.Root(config, env.ContentRootPath);
        RepoRoot = FindRepoRoot(env.ContentRootPath);
        if (RepoRoot != null)
        {
            var dash = Path.Combine(RepoRoot, "CastleDefense.BotArena", "dashboard", "dashboard.html");
            if (File.Exists(dash)) DashboardHtml = dash;
            var bat = Path.Combine(RepoRoot, "update-dashboard.bat");
            if (File.Exists(bat)) UpdateScript = bat;
        }
    }

    /// <summary>
    /// Walk up looking for update-dashboard.bat. Capped rather than unbounded so a server
    /// started from an odd working directory cannot wander into someone's home folder.
    /// </summary>
    private static string FindRepoRoot(string from)
    {
        var dir = new DirectoryInfo(from);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "update-dashboard.bat")))
                return dir.FullName;
        return null;
    }

    /// <summary>Every folder under the recordings root that may hold .replay files.</summary>
    public IEnumerable<string> ReplayFolders()
    {
        if (!Directory.Exists(RecordingsRoot)) yield break;
        foreach (var d in Directory.EnumerateDirectories(RecordingsRoot))
        {
            // quarantine_* folders are games Marc deliberately set aside; they are not part
            // of the record and listing them would put withdrawn games back in front of him.
            var name = Path.GetFileName(d);
            if (name.StartsWith("quarantine", StringComparison.OrdinalIgnoreCase)) continue;
            yield return d;
        }
    }

    public string ReplayPath(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId) || gameId.Any(c => !char.IsLetterOrDigit(c))) return null;
        foreach (var folder in ReplayFolders())
        {
            var p = Path.Combine(folder, gameId + ".replay");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
