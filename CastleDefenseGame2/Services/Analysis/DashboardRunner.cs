using System.Diagnostics;

namespace CastleDefense.Api.Services.Analysis;

/// <summary>
/// Runs update-dashboard.bat on request and lets the page watch it.
///
/// ── WHY THIS IS LOCALHOST-ONLY ───────────────────────────────────────────────────────────
/// This endpoint executes a batch script. Everything else the server exposes is a game; this
/// is arbitrary local command execution reachable over HTTP, and the fact that the script is
/// currently a harmless sweep is not the point -- the endpoint is the capability, not the
/// script. It therefore refuses any request that did not come from the loopback interface.
/// The analysis PAGES stay viewable from anywhere on the network; only the button is gated,
/// which is the split Marc asked for.
///
/// ── ONE RUN AT A TIME ────────────────────────────────────────────────────────────────────
/// The sweep writes dashboard/balance_matrix.csv and rotates balance_matrix_prev.csv, and
/// two concurrent runs would interleave those writes and corrupt the delta column that makes
/// the dashboard trustworthy. A second request while one is in flight is refused, not queued.
///
/// The bat is invoked with --no-open so it does NOT launch a browser tab: the page that
/// started the run is already showing the dashboard and reloads it when the run finishes.
/// </summary>
public sealed class DashboardRunner
{
    private readonly AnalysisPaths _paths;
    private readonly object _lock = new();
    private readonly List<string> _log = new();
    private Process _proc;
    private DateTimeOffset _startedAt, _finishedAt;
    private int? _exitCode;

    private const int MaxLogLines = 400;

    public DashboardRunner(AnalysisPaths paths) { _paths = paths; }

    public sealed record Status(bool Running, bool Available, string ScriptPath,
        DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int? ExitCode,
        double? ElapsedSeconds, IReadOnlyList<string> Log);

    public Status Current()
    {
        lock (_lock)
        {
            bool running = _proc != null && !_proc.HasExited;
            double? elapsed = _startedAt == default ? null
                : ((running ? DateTimeOffset.UtcNow : _finishedAt) - _startedAt).TotalSeconds;
            return new Status(running, _paths.UpdateScript != null, _paths.UpdateScript,
                _startedAt == default ? null : _startedAt,
                _finishedAt == default ? null : _finishedAt,
                running ? null : _exitCode, elapsed, _log.ToList());
        }
    }

    /// <summary>Returns null on success, or the reason it was refused.</summary>
    public string Start(string extraArgs)
    {
        if (_paths.UpdateScript == null)
            return "update-dashboard.bat was not found — this does not look like a source checkout.";

        lock (_lock)
        {
            if (_proc != null && !_proc.HasExited) return "A dashboard update is already running.";

            _log.Clear();
            _exitCode = null;
            _finishedAt = default;
            _startedAt = DateTimeOffset.UtcNow;

            // Arguments are passed through to balance-matrix (--games, --threads, --out),
            // so they are restricted to the shape those take. This is a shell script being
            // invoked with user input; a permissive filter here would hand it a command line.
            string args = Sanitise(extraArgs);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // /c with the bat quoted: the repo path can contain spaces.
                Arguments = $"/c \"\"{_paths.UpdateScript}\" --no-open {args}\"",
                WorkingDirectory = _paths.RepoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => Append(e.Data);
            _proc.ErrorDataReceived += (_, e) => Append(e.Data);
            _proc.Exited += (_, _) =>
            {
                lock (_lock)
                {
                    _finishedAt = DateTimeOffset.UtcNow;
                    try { _exitCode = _proc.ExitCode; } catch { _exitCode = -1; }
                }
            };

            try
            {
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _proc = null;
                return "Could not start the script: " + ex.Message;
            }
            return null;
        }
    }

    private void Append(string line)
    {
        if (line == null) return;
        lock (_lock)
        {
            _log.Add(line);
            // The sweep prints a progress line per thread batch; keep the tail bounded so a
            // long run cannot grow this without limit.
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
    }

    /// <summary>
    /// Allow only `--flag` and bare numeric/word values. Anything with a quote, ampersand,
    /// pipe, redirect or backtick is dropped rather than escaped -- escaping shell input
    /// correctly is a losing game and there is no need for it here.
    /// </summary>
    private static string Sanitise(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var ok = new List<string>();
        foreach (var tok in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.Length > 32) continue;
            bool clean = tok.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.');
            if (clean) ok.Add(tok);
        }
        return string.Join(" ", ok);
    }
}
