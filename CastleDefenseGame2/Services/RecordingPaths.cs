namespace CastleDefense.Api.Services
{
    /// <summary>
    /// WHERE RECORDED GAMES ARE WRITTEN -- the single source of truth for the recordings
    /// root, used by both the replay files and the SQLite database that indexes them.
    ///
    /// WHY THIS IS CONFIGURABLE. The replay corpus under recordings/ is Marc's human play
    /// record: --divergence, --export-policy-table, --analyze-actions and the human win-rate
    /// numbers all read it, and they read it BY PATH. Anything else that plays a game
    /// through this server -- an agent driving a browser to test a feature, for instance --
    /// writes games that look exactly like his and silently corrupt every one of those
    /// numbers. That is not hypothetical: a diagnostic spectator mode seeded this corpus
    /// once already, and 12 bot-vs-bot files in recordings/singleplayer/ still have to be
    /// filtered out by hand (see ReplayFile.SelectHumanGames and the note in
    /// GameHostingService).
    ///
    /// Setting RecordingsDir moves the replay files AND the database together, so a
    /// redirected run is fully self-contained rather than half-separated: its games never
    /// reach game_records.db, which is where every win-rate query looks.
    ///
    /// Pass it as a normal ASP.NET configuration value, most simply on the command line:
    ///
    ///     dotnet run --project CastleDefenseGame2 -- --RecordingsDir=recordings-agent
    ///
    /// .claude/launch.json does exactly that, so any server an agent starts through the
    /// preview tooling is redirected automatically and Marc's own runs -- which do not pass
    /// the flag -- keep the default. Unset means "recordings", i.e. nothing changes.
    /// </summary>
    public static class RecordingPaths
    {
        /// <summary>The corpus Marc's own play goes to. Do not write agent games here.</summary>
        public const string DefaultDir = "recordings";

        /// <summary>The configuration key, spelled once.</summary>
        public const string ConfigKey = "RecordingsDir";

        /// <summary>
        /// Absolute path to the recordings root. A relative setting is resolved against
        /// <paramref name="contentRootPath"/> (the project source directory, NOT bin/ --
        /// recordings living in build output is what destroyed ~144 games once; see the
        /// comment on dbPath in Program.cs).
        /// </summary>
        public static string Root(IConfiguration config, string contentRootPath)
        {
            string dir = config?[ConfigKey];
            if (string.IsNullOrWhiteSpace(dir)) dir = DefaultDir;
            return Path.IsPathRooted(dir) ? dir : Path.Combine(contentRootPath, dir);
        }

        /// <summary>True when games are being routed away from the human corpus.</summary>
        public static bool IsRedirected(IConfiguration config)
        {
            string dir = config?[ConfigKey];
            return !string.IsNullOrWhiteSpace(dir) && dir != DefaultDir;
        }
    }
}
