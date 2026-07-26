using CastleDefense.BotArena;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

GameDataManager.Initialize();

int gamesPerMatchup = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 100;

var offenseOptions = new[] { "nuke", "firebomb", "snipe", "freeze" };
var defenseOptions = new[] { "heal", "reinforcements", "speed", "wall" };

var rng = new Random();

void AssignRandomLoadout(PlayerState player, int side, int timeSkip = 0)
{
    var team = GameDataManager.GetRandomTeam();
    string upg = timeSkip > 5 ? "_3" : timeSkip > 3 ? "_2" : "";
    player.Side = side;
    player.Team = team;
    player.SetLoadout(new[]
    {
        GameDataManager.GetRandomOGadgetId() + upg,
        GameDataManager.GetRandomDGadgetId() + upg,
        GameDataManager.GetSignatureGadgetIdForTeam(team) + upg,
    });
}

void AssignLoadout(PlayerState player, int side, TeamColour team, string offense, string defense)
{
    player.Side = side;
    player.Team = team;
    player.SetLoadout(new[] { offense, defense, GameDataManager.GetSignatureGadgetIdForTeam(team) });
}

// Mirrors GameHub's "league" mode head-start exactly (the same "time machine" real
// training games use): PlayerState(timeSkip) fast-forwards investments/repairs/castle
// HP, gadgets get pre-upgraded past a threshold, tick clock is advanced to match, and
// a single shared coin-flip decides whether BOTH players get a bit of starting cash.
(GameState state, GameEngine engine) CreateGame(bool allowHeadStart)
{
    int timeSkip = allowHeadStart ? Math.Max(rng.Next(-8, 9), 0) : 0;

    var state = new GameState();
    state.Player1 = new PlayerState(timeSkip);
    state.Player2 = new PlayerState(timeSkip);
    AssignRandomLoadout(state.Player1, 1, timeSkip);
    AssignRandomLoadout(state.Player2, 2, timeSkip);

    if (timeSkip > 0 && rng.Next(5) == 0)
    {
        state.Player1.Money = state.Player1.InvestmentPrice + state.Player1.Income;
        state.Player2.Money = state.Player2.InvestmentPrice + state.Player2.Income;
    }

    state.CurrentTick = 30 * 30 * timeSkip;

    var engine = new GameEngine(state);
    return (state, engine);
}

(int winner, long ticks, bool isTimeLimit) RunOneGame(Func<int, IArenaOpponent> makeP1, Func<int, IArenaOpponent> makeP2, bool allowHeadStart = false)
{
    var (state, engine) = CreateGame(allowHeadStart);

    var p1 = makeP1(1);
    var p2 = makeP2(2);

    // GameEngine.Tick() enforces MAX_TICKS itself (declares a winner by castle HP, or a
    // draw if tied), so no external tick cap is needed here.
    while (!state.IsGameOver)
    {
        engine.Tick();
        p1.Update(engine);
        p2.Update(engine);
    }

    (p1 as IDisposable)?.Dispose();
    (p2 as IDisposable)?.Dispose();

    return (state.WinnerSide, state.CurrentTick, state.IsTimeLimit);
}

// A win awarded because the 10-minute clock ran out and we merely had more castle HP
// at that instant is a much weaker signal than actually destroying the enemy castle --
// Marc's explicit ask is that these NOT get counted as full wins in the headline rate.
// Track them separately and report a decisive-only rate as the primary number, with the
// timeout-decided games broken out alongside it so they're visible, not hidden.
void RunMatchup(string label, Func<int, IArenaOpponent> baselineFactory, bool allowHeadStart = false)
{
    int botDecisiveWins = 0, baselineDecisiveWins = 0, draws = 0;
    int botTimeoutWins = 0, baselineTimeoutWins = 0, timeoutDraws = 0;
    long totalTicks = 0;

    for (int i = 0; i < gamesPerMatchup; i++)
    {
        // Alternate which side the heuristic bot plays to cancel out any side asymmetry.
        bool botIsP1 = i % 2 == 0;
        var (winner, ticks, isTimeLimit) = botIsP1
            ? RunOneGame(side => new HeuristicBotAdapter(side), baselineFactory, allowHeadStart)
            : RunOneGame(baselineFactory, side => new HeuristicBotAdapter(side), allowHeadStart);

        totalTicks += ticks;

        int botSide = botIsP1 ? 1 : 2;
        if (winner == 0)
        {
            if (isTimeLimit) timeoutDraws++; else draws++;
        }
        else if (winner == botSide)
        {
            if (isTimeLimit) botTimeoutWins++; else botDecisiveWins++;
        }
        else
        {
            if (isTimeLimit) baselineTimeoutWins++; else baselineDecisiveWins++;
        }
    }

    int totalTimeoutGames = botTimeoutWins + baselineTimeoutWins + timeoutDraws;
    double decisiveWinRate = 100.0 * botDecisiveWins / gamesPerMatchup;
    double avgSeconds = totalTicks / (double)gamesPerMatchup / 30.0;
    Console.WriteLine($"{label,-26} bot wins: {botDecisiveWins,4}/{gamesPerMatchup} ({decisiveWinRate,5:F1}%)  baseline wins: {baselineDecisiveWins,4}  draws: {draws,3}  avg game length: {avgSeconds,6:F1}s" +
        (totalTimeoutGames > 0 ? $"  [timeout: bot {botTimeoutWins}, baseline {baselineTimeoutWins}, draw {timeoutDraws}]" : ""));
}

// Sweeps every offense x defense combination the bot could be given (team stays
// random each game -- signature is tied to team anyway) against a fixed opponent,
// to confirm no specific loadout is a weak point the aggregate numbers could hide.
void RunLoadoutSweep(Func<int, IArenaOpponent> opponentFactory, int gamesPerCombo, bool allowHeadStart = false)
{
    foreach (var offense in offenseOptions)
    {
        foreach (var defense in defenseOptions)
        {
            int botWins = 0, oppWins = 0, draws = 0;
            int botTimeoutWins = 0, oppTimeoutWins = 0, timeoutDraws = 0;

            for (int i = 0; i < gamesPerCombo; i++)
            {
                var (state, engine) = CreateGame(allowHeadStart);
                var botTeam = GameDataManager.GetRandomTeam();
                AssignLoadout(state.Player1, 1, botTeam, offense, defense);
                AssignRandomLoadout(state.Player2, 2);

                var bot = new HeuristicBotAdapter(1);
                var opponent = opponentFactory(2);

                while (!state.IsGameOver)
                {
                    engine.Tick();
                    bot.Update(engine);
                    opponent.Update(engine);
                }
                (opponent as IDisposable)?.Dispose();

                if (state.WinnerSide == 0)
                {
                    if (state.IsTimeLimit) timeoutDraws++; else draws++;
                }
                else if (state.WinnerSide == 1)
                {
                    if (state.IsTimeLimit) botTimeoutWins++; else botWins++;
                }
                else
                {
                    if (state.IsTimeLimit) oppTimeoutWins++; else oppWins++;
                }
            }

            int totalTimeoutGames = botTimeoutWins + oppTimeoutWins + timeoutDraws;
            double winRate = 100.0 * botWins / gamesPerCombo;
            Console.WriteLine($"{offense,-15}{defense,-16} bot wins: {botWins,4}/{gamesPerCombo} ({winRate,5:F1}%)  opp wins: {oppWins,4}  draws: {draws,3}" +
                (totalTimeoutGames > 0 ? $"  [timeout: bot {botTimeoutWins}, opp {oppTimeoutWins}, draw {timeoutDraws}]" : ""));
        }
    }
}

// Finds every league_models folder with .onnx files sitting in it (these are
// gitignored local artifacts -- never committed -- so there's no single canonical
// source path; just check the places a build has actually left them).
string? FindLeagueModelsDir()
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "league_models"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CastleDefense.Simulation", "bin", "Release", "net10.0", "league_models"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CastleDefenseGame2", "bin", "Debug", "net10.0", "league_models"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CastleDefenseGame2", "bin", "Release", "net10.0", "league_models"),
    };
    foreach (var c in candidates)
    {
        var full = Path.GetFullPath(c);
        if (Directory.Exists(full) && Directory.GetFiles(full, "*.onnx").Length > 0)
            return full;
    }
    return null;
}

// Resolves a partial model name (e.g. "v4") to whichever league_models file's
// filename contains it, for trace/hunt-style single-game debugging against a
// specific ONNX model. Falls back to RusherBaseline if nothing matches.
Func<int, IArenaOpponent> MakeModelOpponentOrRusher(string nameFragment)
{
    var dir = FindLeagueModelsDir();
    if (dir != null)
    {
        var match = Directory.GetFiles(dir, "*.onnx").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(nameFragment, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            Console.WriteLine($"(resolved '{nameFragment}' -> {Path.GetFileName(match)})");
            return side => new AIModelOpponent(side, match);
        }
    }
    Console.WriteLine($"(no model matched '{nameFragment}', falling back to Rusher)");
    return side => new RusherBaseline(side);
}

if (args.Length > 0 && args[0] == "models")
{
    bool headStart = args.Length > 1 && args[1] == "headstart";
    int games = args.Length > 2 && int.TryParse(args[2], out var mg) ? mg : gamesPerMatchup;
    gamesPerMatchup = games; // RunMatchup loops on gamesPerMatchup, not this local

    var dir = FindLeagueModelsDir();
    if (dir == null)
    {
        Console.WriteLine("No league_models folder with .onnx files found. Build/run the main app at least once to populate it, or pass a path.");
        return;
    }

    // Optional trailing non-numeric, non-"headstart" arg filters to model filenames
    // containing it (e.g. "models headstart 300 v23") -- lets a single matchup be
    // re-tested quickly instead of paying for the full ~10-model sweep every time.
    string? filter = args.Skip(1).FirstOrDefault(a => a != "headstart" && !int.TryParse(a, out _));
    var modelFiles = Directory.GetFiles(dir, "*.onnx").OrderBy(f => f)
        .Where(f => filter == null || Path.GetFileNameWithoutExtension(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
        .ToList();
    Console.WriteLine($"Found {modelFiles.Count} model(s) in {dir}{(filter != null ? $" matching '{filter}'" : "")}");
    Console.WriteLine($"Running {games} games per model{(headStart ? " (with time-machine head starts)" : " (fresh start)")}...\n");

    foreach (var modelFile in modelFiles)
    {
        string name = Path.GetFileNameWithoutExtension(modelFile);
        RunMatchup($"vs {name}", side => new AIModelOpponent(side, modelFile), headStart);
    }
    return;
}

// Marc's ask (2026-07-25): average invests-per-game as a progress metric for the RL
// campaign, not just a win-rate number -- needs a real reference point for "what does
// good play look like" rather than a guessed target. RunMatchup/RunOneGame don't
// expose final InvestmentCount at all (only winner/ticks/timeout), so this is a
// dedicated mode: plays a model against HeuristicBot (sides alternated, same
// fairness convention as RunMatchup) and reports both sides' average final
// InvestmentCount (PlayerState.InvestmentCount only ever increases, so its value at
// game-over is exactly the total number of times that side invested that game).
// Usage: invest-stats <modelFragment> [headstart] [games]
if (args.Length > 0 && args[0] == "invest-stats")
{
    string modelArg = args.Length > 1 ? args[1] : "v4";
    bool headStart = args.Length > 2 && args[2] == "headstart";
    int games = args.Length > 3 && int.TryParse(args[3], out var ig) ? ig : 100;

    var dir = FindLeagueModelsDir();
    if (dir == null) { Console.WriteLine("No league_models folder found."); return; }
    var match = Directory.GetFiles(dir, "*.onnx").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(modelArg, StringComparison.OrdinalIgnoreCase));
    if (match == null) { Console.WriteLine($"No model matching '{modelArg}' found in {dir}."); return; }
    string modelName = Path.GetFileNameWithoutExtension(match);

    Console.WriteLine($"Running {games} games: {modelName} vs HeuristicBot{(headStart ? " (headstart)" : "")}...");

    var modelInvests = new List<int>();
    var botInvests = new List<int>();
    for (int i = 0; i < games; i++)
    {
        bool modelIsP1 = i % 2 == 0;
        var (state, engine) = CreateGame(headStart);
        var modelOpp = new AIModelOpponent(modelIsP1 ? 1 : 2, match);
        var bot = new HeuristicBotAdapter(modelIsP1 ? 2 : 1);
        while (!state.IsGameOver)
        {
            engine.Tick();
            if (modelIsP1) { modelOpp.Update(engine); bot.Update(engine); }
            else { bot.Update(engine); modelOpp.Update(engine); }
        }
        modelInvests.Add(modelIsP1 ? state.Player1.InvestmentCount : state.Player2.InvestmentCount);
        botInvests.Add(modelIsP1 ? state.Player2.InvestmentCount : state.Player1.InvestmentCount);
        (modelOpp as IDisposable)?.Dispose();
    }

    Console.WriteLine($"{modelName}: avg invests/game = {modelInvests.Average():F2}  (min={modelInvests.Min()}, max={modelInvests.Max()})");
    Console.WriteLine($"HeuristicBot:      avg invests/game = {botInvests.Average():F2}  (min={botInvests.Min()}, max={botInvests.Max()})");
    return;
}

if (args.Length > 0 && args[0] == "dashboard")
{
    // Sweeps the FULL team x offense x defense cross-tab (8 x 4 x 4 = 128 cells) for
    // every opponent (all 8 spam tiers + every ONNX model found), so the dashboard can
    // answer "is a weak matchup uniformly weak, or is it one bad team/gadget combo
    // dragging the average down" -- Marc's explicit ask after Tier4 spam sat at ~41%
    // in aggregate. Tier4 and the two hardest models (v4/v7) get extra games/cell since
    // they're the ones worth resolving at finer confidence; everything else gets a
    // lighter pass so the dashboard still covers the whole roster for comparison.
    string outputDir = args.Length > 1 ? args[1] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "dashboard"));
    Directory.CreateDirectory(outputDir);
    string csvPath = Path.Combine(outputDir, "results.csv");
    string jsonPath = Path.Combine(outputDir, "results.json");
    string htmlPath = Path.Combine(outputDir, "dashboard.html");
    string templatePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "dashboard_template.html");

    var allTeams = Enum.GetValues<TeamColour>();
    var priorityModelFragments = new[] { "v4", "v7" };

    var opponentSpecs = new List<(string label, string kind, Func<int, IArenaOpponent> factory, bool headStart, int gamesPerCell)>();
    for (int t = 1; t <= 8; t++)
    {
        int tt = t;
        int cellGames = tt == 4 ? 25 : 8;
        opponentSpecs.Add(($"Tier{tt}Spam", "spam", side => new TierSpamBaseline(side, tt), false, cellGames));
    }
    var modelsDir = FindLeagueModelsDir();
    if (modelsDir != null)
    {
        foreach (var f in Directory.GetFiles(modelsDir, "*.onnx").OrderBy(f => f))
        {
            var path = f;
            string name = Path.GetFileNameWithoutExtension(path);
            bool isPriority = priorityModelFragments.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
            int cellGames = isPriority ? 12 : 5;
            opponentSpecs.Add((name, "model", side => new AIModelOpponent(side, path), true, cellGames));
        }
    }
    else
    {
        Console.WriteLine("No league_models folder found -- dashboard will only cover spam tiers.");
    }

    int totalGames = opponentSpecs.Sum(o => 128 * o.gamesPerCell);
    Console.WriteLine($"Dashboard sweep: {opponentSpecs.Count} opponents, {totalGames} games total. Writing to {outputDir}\n");

    using var csv = new StreamWriter(csvPath, false);
    csv.WriteLine("opponent,kind,team,offense,defense,outcome,ticks,seconds");

    // opponent -> team -> "offense|defense" -> list of outcomes (raw, aggregated after the sweep)
    var raw = new Dictionary<string, Dictionary<TeamColour, Dictionary<string, List<string>>>>();
    var opponentKind = new Dictionary<string, string>();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    int completed = 0;

    foreach (var (label, kind, factory, headStart, cellGames) in opponentSpecs)
    {
        opponentKind[label] = kind;
        var byTeam = raw[label] = new Dictionary<TeamColour, Dictionary<string, List<string>>>();

        foreach (var team in allTeams)
        {
            var byCombo = byTeam[team] = new Dictionary<string, List<string>>();
            foreach (var offense in offenseOptions)
            {
                foreach (var defense in defenseOptions)
                {
                    string comboKey = $"{offense}|{defense}";
                    var outcomes = byCombo[comboKey] = new List<string>();

                    for (int i = 0; i < cellGames; i++)
                    {
                        // Alternate which physical side the bot occupies, matching
                        // RunMatchup's own convention ("cancel out any side asymmetry") --
                        // a smoke test at cellGames=1 (bot always on side 1) showed Tier1
                        // spam at 99.2% here vs the ~85% established via the alternating
                        // "spam 400" benchmark, confirming the engine really does have a
                        // side asymmetry and this sweep must alternate too or its numbers
                        // won't be comparable to the rest of this project's benchmarks.
                        bool botIsP1 = i % 2 == 0;
                        int botSide = botIsP1 ? 1 : 2;
                        int oppSide = botIsP1 ? 2 : 1;

                        var (state, engine) = CreateGame(headStart);
                        var botPlayer = botSide == 1 ? state.Player1 : state.Player2;
                        var oppPlayer = botSide == 1 ? state.Player2 : state.Player1;
                        AssignLoadout(botPlayer, botSide, team, offense, defense);
                        AssignRandomLoadout(oppPlayer, oppSide);

                        var bot = new HeuristicBotAdapter(botSide);
                        var opponent = factory(oppSide);
                        while (!state.IsGameOver)
                        {
                            engine.Tick();
                            bot.Update(engine);
                            opponent.Update(engine);
                        }
                        (opponent as IDisposable)?.Dispose();

                        string outcome = state.WinnerSide == 0
                            ? (state.IsTimeLimit ? "timeout_draw" : "draw")
                            : state.WinnerSide == botSide
                                ? (state.IsTimeLimit ? "timeout_win" : "decisive_win")
                                : (state.IsTimeLimit ? "timeout_loss" : "decisive_loss");

                        csv.WriteLine($"{label},{kind},{team},{offense},{defense},{outcome},{state.CurrentTick},{(state.CurrentTick / 30.0):F1}");
                        outcomes.Add(outcome);
                        completed++;
                    }
                }
            }
        }
        csv.Flush();
        Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] {label,-26} done  ({completed}/{totalGames}, {100.0 * completed / totalGames:F1}%)");
    }
    csv.Dispose();

    // --- Aggregate raw outcomes into win-rate stats and write results.json ---
    JsonObject StatsNode(IEnumerable<string> outcomes)
    {
        int decisiveWins = 0, decisiveLosses = 0, timeoutWins = 0, timeoutLosses = 0, draws = 0, total = 0;
        foreach (var outcome in outcomes)
        {
            total++;
            switch (outcome)
            {
                case "decisive_win": decisiveWins++; break;
                case "decisive_loss": decisiveLosses++; break;
                case "timeout_win": timeoutWins++; break;
                case "timeout_loss": timeoutLosses++; break;
                default: draws++; break;
            }
        }
        // Decisive-only win rate is the primary metric throughout this project's own
        // benchmarking history -- a timeout win (higher HP at the 10-minute mark,
        // castle never destroyed) is a much weaker signal than an actual castle kill.
        double winRate = total > 0 ? 100.0 * decisiveWins / total : 0;
        return new JsonObject
        {
            ["decisiveWins"] = decisiveWins,
            ["decisiveLosses"] = decisiveLosses,
            ["timeoutWins"] = timeoutWins,
            ["timeoutLosses"] = timeoutLosses,
            ["draws"] = draws,
            ["total"] = total,
            ["winRate"] = Math.Round(winRate, 1),
        };
    }

    var opponentsArray = new JsonArray();
    var byTeamRoot = new JsonObject();
    var byOffenseRoot = new JsonObject();
    var byDefenseRoot = new JsonObject();
    var byComboRoot = new JsonObject();

    foreach (var (label, kind, _, _, gamesPerCell) in opponentSpecs)
    {
        var byTeam = raw[label];
        var allOutcomesForOpponent = byTeam.Values.SelectMany(c => c.Values).SelectMany(o => o).ToList();
        var overall = StatsNode(allOutcomesForOpponent);
        overall["name"] = label;
        overall["kind"] = kind;
        overall["gamesPerCell"] = gamesPerCell;
        opponentsArray.Add(overall);

        var teamNode = new JsonObject();
        var offenseNode = new JsonObject();
        var defenseNode = new JsonObject();
        var comboNode = new JsonObject();

        foreach (var team in allTeams)
        {
            var combos = byTeam[team];
            teamNode[team.ToString()] = StatsNode(combos.Values.SelectMany(o => o));

            var teamComboNode = new JsonObject();
            foreach (var (comboKey, outcomes) in combos)
                teamComboNode[comboKey] = StatsNode(outcomes);
            comboNode[team.ToString()] = teamComboNode;
        }
        foreach (var offense in offenseOptions)
            offenseNode[offense] = StatsNode(byTeam.Values.SelectMany(c => c.Where(kv => kv.Key.StartsWith(offense + "|")).SelectMany(kv => kv.Value)));
        foreach (var defense in defenseOptions)
            defenseNode[defense] = StatsNode(byTeam.Values.SelectMany(c => c.Where(kv => kv.Key.EndsWith("|" + defense)).SelectMany(kv => kv.Value)));

        byTeamRoot[label] = teamNode;
        byOffenseRoot[label] = offenseNode;
        byDefenseRoot[label] = defenseNode;
        byComboRoot[label] = comboNode;
    }

    var root = new JsonObject
    {
        ["generatedAt"] = DateTime.UtcNow.ToString("o"),
        ["totalGames"] = totalGames,
        ["teams"] = new JsonArray(allTeams.Select(t => JsonValue.Create(t.ToString())).ToArray()),
        ["offenseOptions"] = new JsonArray(offenseOptions.Select(o => JsonValue.Create(o)).ToArray()),
        ["defenseOptions"] = new JsonArray(defenseOptions.Select(d => JsonValue.Create(d)).ToArray()),
        ["opponents"] = opponentsArray,
        ["byTeam"] = byTeamRoot,
        ["byOffense"] = byOffenseRoot,
        ["byDefense"] = byDefenseRoot,
        ["byCombo"] = byComboRoot,
    };

    var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
    string json = root.ToJsonString(jsonOptions);
    File.WriteAllText(jsonPath, json);
    Console.WriteLine($"\nWrote {csvPath}\nWrote {jsonPath}");

    if (File.Exists(templatePath))
    {
        string template = File.ReadAllText(templatePath);
        string html = template.Replace("/*__DASHBOARD_DATA__*/", "const DASHBOARD_DATA = " + json + ";");
        File.WriteAllText(htmlPath, html);
        Console.WriteLine($"Wrote {htmlPath}");
    }
    else
    {
        Console.WriteLine($"WARNING: template not found at {templatePath} -- dashboard.html not generated. Run again after creating it, or regenerate manually from results.json.");
    }

    return;
}

if (args.Length > 0 && args[0] == "paramsearch")
{
    // Automated random search over the TTD/danger-trigger knobs pulled out into
    // HeuristicBotSettings (SafetyMarginMultiplier, SafetyBufferSeconds,
    // EnemyIsCloseDistance, RepairHpThreshold). Every one of these was a hand-picked
    // "reasonable first guess" this session, validated only against 2-3 hand-chosen
    // alternatives at most (see HpHistoryWindow's own tuning history for the closest
    // precedent) -- a systematic search can try far more combinations, and cheaply,
    // than manually guessing-and-validating one at a time. Deliberately does NOT touch
    // the reactive-spend/unit-scoring constants in SpendOnUnits -- that domain has 4
    // confirmed dead-end manual tuning attempts already this session; this search
    // targets the domain that's actually been productive (the danger trigger itself).
    //
    // Two-stage discipline: this mode runs a CHEAP coarse pass (moderate sample sizes,
    // a representative model subset, not the full roster) to rank candidates. Any
    // promising candidate still needs the full 400-spam/300-model x2-replicate
    // validation this project always requires before being trusted or committed --
    // this mode is a triage step, not a replacement for that.
    int candidateCount = args.Length > 1 && int.TryParse(args[1], out var cc) ? cc : 40;
    var searchRng = new Random();

    var dir = FindLeagueModelsDir();
    // A small, deliberately mixed subset: the two hardest matchups all session (v4, v7),
    // one more mid-weak one (v23), and two already-strong ones (v14, v21) as regression
    // canaries so a candidate that only helps the weak side by wrecking the strong side
    // shows up immediately, the same way every manual validation this session checked both.
    var modelFragments = new[] { "v4", "v7", "v23", "v14", "v21" };
    var resolvedModels = new List<(string label, string path)>();
    if (dir != null)
    {
        foreach (var frag in modelFragments)
        {
            var match = Directory.GetFiles(dir, "*.onnx").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(frag, StringComparison.OrdinalIgnoreCase));
            if (match != null) resolvedModels.Add((Path.GetFileNameWithoutExtension(match), match));
        }
    }
    if (resolvedModels.Count == 0) Console.WriteLine("WARNING: no league_models found -- paramsearch will only use spam tiers.");

    const int spamGamesPerTier = 60;
    const int modelGamesPerOpponent = 40;

    // (label, decisive win rate) for one matchup, alternating sides like RunMatchup.
    double RunOneMatchup(Func<int, IArenaOpponent> botFactory, Func<int, IArenaOpponent> oppFactory, bool headStart, int games)
    {
        int decisiveWins = 0;
        for (int i = 0; i < games; i++)
        {
            bool botIsP1 = i % 2 == 0;
            int botSide = botIsP1 ? 1 : 2;
            var (state, engine) = CreateGame(headStart);
            var bot = botFactory(botSide);
            var opp = oppFactory(botIsP1 ? 2 : 1);
            while (!state.IsGameOver)
            {
                engine.Tick();
                if (botIsP1) { bot.Update(engine); opp.Update(engine); }
                else { opp.Update(engine); bot.Update(engine); }
            }
            (opp as IDisposable)?.Dispose();
            if (state.WinnerSide == botSide && !state.IsTimeLimit) decisiveWins++;
        }
        return 100.0 * decisiveWins / games;
    }

    (double avgScore, Dictionary<string, double> perMatchup) EvaluateCandidate(HeuristicBotSettings settings)
    {
        var results = new Dictionary<string, double>();
        for (int t = 1; t <= 8; t++)
        {
            int tt = t;
            results[$"Tier{tt}Spam"] = RunOneMatchup(
                side => new HeuristicBotAdapter(side, settings),
                side => new TierSpamBaseline(side, tt),
                false, spamGamesPerTier);
        }
        foreach (var (label, path) in resolvedModels)
        {
            results[label] = RunOneMatchup(
                side => new HeuristicBotAdapter(side, settings),
                side => new AIModelOpponent(side, path),
                true, modelGamesPerOpponent);
        }
        double avg = results.Values.Average();
        return (avg, results);
    }

    HeuristicBotSettings RandomSettings() => new HeuristicBotSettings
    {
        SafetyMarginMultiplier = (float)(1.05 + searchRng.NextDouble() * (2.2 - 1.05)),
        SafetyBufferSeconds = (float)(0.25 + searchRng.NextDouble() * (4.5 - 0.25)),
        EnemyIsCloseDistance = (float)(450 + searchRng.NextDouble() * (1050 - 450)),
        RepairHpThreshold = (float)(0.55 + searchRng.NextDouble() * (0.90 - 0.55)),
    };

    string outputDir = args.Length > 2 ? args[2] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "dashboard"));
    Directory.CreateDirectory(outputDir);
    string searchCsvPath = Path.Combine(outputDir, "paramsearch_results.csv");
    using var searchCsv = new StreamWriter(searchCsvPath, false);
    var matchupLabels = Enumerable.Range(1, 8).Select(t => $"Tier{t}Spam").Concat(resolvedModels.Select(m => m.label)).ToList();
    searchCsv.WriteLine("candidate,safetyMargin,safetyBuffer,enemyIsCloseDist,repairThreshold,avgScore," + string.Join(",", matchupLabels));

    Console.WriteLine($"Parameter search: {candidateCount} candidates (+1 baseline), {spamGamesPerTier} games/spam-tier, {modelGamesPerOpponent} games/model ({resolvedModels.Count} models: {string.Join(", ", resolvedModels.Select(m => m.label))})\n");

    var allCandidates = new List<(string name, HeuristicBotSettings settings, double avgScore, Dictionary<string, double> perMatchup)>();

    void EvaluateAndLog(string name, HeuristicBotSettings settings)
    {
        var swc = System.Diagnostics.Stopwatch.StartNew();
        var (avg, results) = EvaluateCandidate(settings);
        allCandidates.Add((name, settings, avg, results));
        searchCsv.WriteLine($"{name},{settings.SafetyMarginMultiplier:F3},{settings.SafetyBufferSeconds:F3},{settings.EnemyIsCloseDistance:F1},{settings.RepairHpThreshold:F3},{avg:F2}," +
            string.Join(",", matchupLabels.Select(m => results[m].ToString("F1"))));
        searchCsv.Flush();
        Console.WriteLine($"[{swc.Elapsed:mm\\:ss}] {name,-12} margin={settings.SafetyMarginMultiplier:F2} buffer={settings.SafetyBufferSeconds:F2} dist={settings.EnemyIsCloseDistance:F0} repairHp={settings.RepairHpThreshold:F2}  =>  avg {avg:F2}%");
    }

    EvaluateAndLog("baseline", HeuristicBotSettings.Default);
    for (int c = 1; c <= candidateCount; c++)
        EvaluateAndLog($"cand{c}", RandomSettings());

    var baseline = allCandidates.First(c => c.name == "baseline");
    var ranked = allCandidates.OrderByDescending(c => c.avgScore).ToList();

    Console.WriteLine("\n=== Top 8 candidates by average decisive win rate across all tested matchups ===");
    Console.WriteLine($"baseline: avg {baseline.avgScore:F2}%  (" + string.Join(", ", matchupLabels.Select(m => $"{m}={baseline.perMatchup[m]:F0}%")) + ")\n");
    foreach (var c in ranked.Take(8))
    {
        string marker = c.name == "baseline" ? " <== baseline" : "";
        Console.WriteLine($"{c.name,-12} avg {c.avgScore,6:F2}%  (delta {c.avgScore - baseline.avgScore:+0.0;-0.0}){marker}");
        Console.WriteLine($"    margin={c.settings.SafetyMarginMultiplier:F3} buffer={c.settings.SafetyBufferSeconds:F3} enemyDist={c.settings.EnemyIsCloseDistance:F1} repairHp={c.settings.RepairHpThreshold:F3}");
        Console.WriteLine("    " + string.Join(", ", matchupLabels.Select(m => $"{m}={c.perMatchup[m]:F0}%")));
    }
    Console.WriteLine($"\nWrote {searchCsvPath}");
    return;
}

if (args.Length > 0 && args[0] == "paramsearch-attack")
{
    // Same harness shape as "paramsearch" above, but targets the four attack-vs-
    // savings knobs on HeuristicBotSettings (EnemyHpEvaluationSeconds,
    // MinMeaningfulEnemyHpLossPctPerSecond, KillerInstinctHpThreshold,
    // AttackSpendFraction) instead of the danger-trigger knobs. Marc's own framing
    // when he gave the starting values: "those numbers I mentioned are pulled out
    // of thin air... we should run some experiments to tweak those numbers." Holds
    // the four already-tuned danger-trigger knobs fixed at their defaults --
    // searching all 8 at once would need far more candidates for the same coverage
    // and risks perturbing settings that were already validated separately.
    int candidateCount = args.Length > 1 && int.TryParse(args[1], out var cc) ? cc : 40;
    var searchRng = new Random();

    var dir = FindLeagueModelsDir();
    // Same deliberately mixed subset as "paramsearch": the two hardest matchups all
    // project (v4, v7), one more mid-weak one (v23), and two already-strong ones
    // (v14, v21) as regression canaries. Tier4 spam and v23 in particular already
    // showed real, repeatable regressions in this system's hand-tuned iterations --
    // exactly the canaries this search needs to watch closest.
    var modelFragments = new[] { "v4", "v7", "v23", "v14", "v21" };
    var resolvedModels = new List<(string label, string path)>();
    if (dir != null)
    {
        foreach (var frag in modelFragments)
        {
            var match = Directory.GetFiles(dir, "*.onnx").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(frag, StringComparison.OrdinalIgnoreCase));
            if (match != null) resolvedModels.Add((Path.GetFileNameWithoutExtension(match), match));
        }
    }
    if (resolvedModels.Count == 0) Console.WriteLine("WARNING: no league_models found -- paramsearch-attack will only use spam tiers.");

    const int spamGamesPerTier = 60;
    const int modelGamesPerOpponent = 40;

    double RunOneMatchup(Func<int, IArenaOpponent> botFactory, Func<int, IArenaOpponent> oppFactory, bool headStart, int games)
    {
        int decisiveWins = 0;
        for (int i = 0; i < games; i++)
        {
            bool botIsP1 = i % 2 == 0;
            int botSide = botIsP1 ? 1 : 2;
            var (state, engine) = CreateGame(headStart);
            var bot = botFactory(botSide);
            var opp = oppFactory(botIsP1 ? 2 : 1);
            while (!state.IsGameOver)
            {
                engine.Tick();
                if (botIsP1) { bot.Update(engine); opp.Update(engine); }
                else { opp.Update(engine); bot.Update(engine); }
            }
            (opp as IDisposable)?.Dispose();
            if (state.WinnerSide == botSide && !state.IsTimeLimit) decisiveWins++;
        }
        return 100.0 * decisiveWins / games;
    }

    (double avgScore, Dictionary<string, double> perMatchup) EvaluateCandidate(HeuristicBotSettings settings)
    {
        var results = new Dictionary<string, double>();
        for (int t = 1; t <= 8; t++)
        {
            int tt = t;
            results[$"Tier{tt}Spam"] = RunOneMatchup(
                side => new HeuristicBotAdapter(side, settings),
                side => new TierSpamBaseline(side, tt),
                false, spamGamesPerTier);
        }
        foreach (var (label, path) in resolvedModels)
        {
            results[label] = RunOneMatchup(
                side => new HeuristicBotAdapter(side, settings),
                side => new AIModelOpponent(side, path),
                true, modelGamesPerOpponent);
        }
        double avg = results.Values.Average();
        return (avg, results);
    }

    HeuristicBotSettings RandomAttackSettings() => new HeuristicBotSettings
    {
        // Danger-trigger knobs held fixed at their already-validated defaults.
        EnemyHpEvaluationSeconds = (float)(8 + searchRng.NextDouble() * (30 - 8)),
        MinMeaningfulEnemyHpLossPctPerSecond = (float)(0.05 + searchRng.NextDouble() * (2.0 - 0.05)),
        KillerInstinctHpThreshold = (int)(500 + searchRng.NextDouble() * (10000 - 500)),
        AttackSpendFraction = (float)(0.55 + searchRng.NextDouble() * (0.97 - 0.55)),
    };

    string outputDir = args.Length > 2 ? args[2] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "dashboard"));
    Directory.CreateDirectory(outputDir);
    string searchCsvPath = Path.Combine(outputDir, "paramsearch_attack_results.csv");
    using var searchCsv = new StreamWriter(searchCsvPath, false);
    var matchupLabels = Enumerable.Range(1, 8).Select(t => $"Tier{t}Spam").Concat(resolvedModels.Select(m => m.label)).ToList();
    searchCsv.WriteLine("candidate,hpEvalSeconds,minHpLossPctPerSec,killerInstinctHp,attackSpendFraction,avgScore," + string.Join(",", matchupLabels));

    Console.WriteLine($"Attack-savings parameter search: {candidateCount} candidates (+1 baseline), {spamGamesPerTier} games/spam-tier, {modelGamesPerOpponent} games/model ({resolvedModels.Count} models: {string.Join(", ", resolvedModels.Select(m => m.label))})\n");

    var allCandidates = new List<(string name, HeuristicBotSettings settings, double avgScore, Dictionary<string, double> perMatchup)>();

    void EvaluateAndLog(string name, HeuristicBotSettings settings)
    {
        var swc = System.Diagnostics.Stopwatch.StartNew();
        var (avg, results) = EvaluateCandidate(settings);
        allCandidates.Add((name, settings, avg, results));
        searchCsv.WriteLine($"{name},{settings.EnemyHpEvaluationSeconds:F2},{settings.MinMeaningfulEnemyHpLossPctPerSecond:F3},{settings.KillerInstinctHpThreshold},{settings.AttackSpendFraction:F3},{avg:F2}," +
            string.Join(",", matchupLabels.Select(m => results[m].ToString("F1"))));
        searchCsv.Flush();
        Console.WriteLine($"[{swc.Elapsed:mm\\:ss}] {name,-12} hpEval={settings.EnemyHpEvaluationSeconds:F1}s minLossRate={settings.MinMeaningfulEnemyHpLossPctPerSecond:F3}%/s killerHp={settings.KillerInstinctHpThreshold} spendFrac={settings.AttackSpendFraction:F2}  =>  avg {avg:F2}%");
    }

    EvaluateAndLog("baseline", HeuristicBotSettings.Default);
    for (int c = 1; c <= candidateCount; c++)
        EvaluateAndLog($"cand{c}", RandomAttackSettings());

    var baseline = allCandidates.First(c => c.name == "baseline");
    var ranked = allCandidates.OrderByDescending(c => c.avgScore).ToList();

    Console.WriteLine("\n=== Top 8 candidates by average decisive win rate across all tested matchups ===");
    Console.WriteLine($"baseline: avg {baseline.avgScore:F2}%  (" + string.Join(", ", matchupLabels.Select(m => $"{m}={baseline.perMatchup[m]:F0}%")) + ")\n");
    foreach (var c in ranked.Take(8))
    {
        string marker = c.name == "baseline" ? " <== baseline" : "";
        Console.WriteLine($"{c.name,-12} avg {c.avgScore,6:F2}%  (delta {c.avgScore - baseline.avgScore:+0.0;-0.0}){marker}");
        Console.WriteLine($"    hpEvalSeconds={c.settings.EnemyHpEvaluationSeconds:F2} minLossPctPerSec={c.settings.MinMeaningfulEnemyHpLossPctPerSecond:F3} killerInstinctHp={c.settings.KillerInstinctHpThreshold} attackSpendFraction={c.settings.AttackSpendFraction:F3}");
        Console.WriteLine("    " + string.Join(", ", matchupLabels.Select(m => $"{m}={c.perMatchup[m]:F0}%")));
    }
    Console.WriteLine($"\nWrote {searchCsvPath}");
    return;
}

if (args.Length > 0 && args[0] == "loadouts")
{
    string opponent = args.Length > 1 ? args[1] : "balanced";
    int games = args.Length > 2 && int.TryParse(args[2], out var lg) ? lg : 25;
    Func<int, IArenaOpponent> makeOpponent = opponent switch
    {
        "balanced" => side => new BalancedHumanBaseline(side),
        "spam4" => side => new TierSpamBaseline(side, 4),
        "spam5" => side => new TierSpamBaseline(side, 5),
        "spam6" => side => new TierSpamBaseline(side, 6),
        "spam7" => side => new TierSpamBaseline(side, 7),
        _ => side => new BalancedHumanBaseline(side),
    };

    Console.WriteLine($"Sweeping all 16 offense/defense combos vs {opponent}, {games} games each (random team per game)...\n");
    RunLoadoutSweep(makeOpponent, games);
    return;
}

if (args.Length > 0 && args[0] == "hunt")
{
    // Finds and prints a trace of the first LOSS (or draw) against the given opponent
    // (a tier number for TierSpamBaseline, or anything else resolved as a model name
    // fragment) using randomized teams, instead of the fixed matchup "trace" uses --
    // useful for seeing what an actual bad matchup looks like.
    string huntOpponentArg = args.Length > 1 ? args[1] : "4";
    bool headStart = args.Length > 2 && args[2] == "headstart";
    Func<int, IArenaOpponent> makeHuntFoe = int.TryParse(huntOpponentArg, out var huntTier)
        ? side => new TierSpamBaseline(side, huntTier)
        : MakeModelOpponentOrRusher(huntOpponentArg);

    // Optional trailing "<offense> <defense>" pair forces P1's loadout instead of
    // randomizing it -- lets a specific weak combo (e.g. snipe|wall) be traced
    // directly instead of waiting for it to come up by chance across 200 rerolls.
    // e.g. `hunt 4 headstart snipe wall`
    string? forceOffense = args.Length > 3 ? args[3] : null;
    string? forceDefense = args.Length > 4 ? args[4] : null;

    for (int attempt = 0; attempt < 200; attempt++)
    {
        var (huntState, huntEngine) = CreateGame(headStart);
        if (forceOffense != null && forceDefense != null)
            AssignLoadout(huntState.Player1, 1, huntState.Player1.Team, forceOffense, forceDefense);
        var huntBot = new HeuristicBotAdapter(1);
        var huntFoe = makeHuntFoe(2);

        var log = new List<string>();
        while (!huntState.IsGameOver)
        {
            huntEngine.Tick();
            huntBot.Update(huntEngine);
            huntFoe.Update(huntEngine);

            if (huntState.CurrentTick % 15 == 0)
            {
                var p1u = huntState.Units.Count(u => u.Side == 1);
                var p2u = huntState.Units.Count(u => u.Side == 2);
                var p1hp = 100.0 * huntState.Player1.CastleHealth / huntState.Player1.CastleMaxHealth;
                var p2hp = 100.0 * huntState.Player2.CastleHealth / huntState.Player2.CastleMaxHealth;
                var p1comp = string.Join(",", huntState.Units.Where(u => u.Side == 1).GroupBy(u => u.DefinitionId).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}"));
                string ttd = huntBot.LastTimeToDeathSeconds >= 999999f ? "inf" : huntBot.LastTimeToDeathSeconds.ToString("F1");
                string tti = huntBot.LastTimeToInvestSeconds >= 999999f ? "inf" : huntBot.LastTimeToInvestSeconds.ToString("F1");
                log.Add($"{huntState.CurrentTick}\t{huntState.CurrentTick / 30}\t{huntState.Player1.Money:F2}\t{huntState.Player1.Income:F1}\t{huntState.Player1.InvestmentCount}\t{huntState.Player1.RepairCount}\t{p1u}\t{p1hp:F0}\t{huntState.Player2.Money:F0}\t{huntState.Player2.Income:F1}\t{p2u}\t{p2hp:F0}\t{huntBot.LastDecisionWasDanger}\t{ttd}\t{tti}\t{huntBot.LastThreatScore:F1}\t{huntBot.LastDefenseScore:F1}\t{huntBot.LastUnitsPurchased}\t{huntBot.LastSpendDebug}\t{p1comp}");
            }
        }

        if (huntState.WinnerSide != 1)
        {
            Console.WriteLine($"P1 team={huntState.Player1.Team} offense={huntState.Player1.OffensiveGadget?.Id} defense={huntState.Player1.DefensiveGadget?.Id} sig={huntState.Player1.SignatureGadget?.Id}, P2 team={huntState.Player2.Team}");
            Console.WriteLine("tick\tsec\tP1$\tP1inc\tP1inv\tP1rep\tP1units\tP1hp%\tP2$\tP2inc\tP2units\tP2hp%\tP1danger\tTTD\tTTI\tthreat\tdefense\tP1bought\tP1composition");
            foreach (var line in log) Console.WriteLine(line);
            string huntResult = huntState.WinnerSide == 0 ? "draw (timeout, tied HP)"
                : huntState.IsTimeLimit ? $"P{huntState.WinnerSide} wins BY TIMEOUT (higher HP, castle never destroyed)"
                : $"P{huntState.WinnerSide} wins (castle destroyed)";
            Console.WriteLine($"\nResult: {huntResult} at tick {huntState.CurrentTick} (attempt {attempt + 1})");
            (huntFoe as IDisposable)?.Dispose();
            return;
        }
        (huntFoe as IDisposable)?.Dispose();
    }
    Console.WriteLine("No loss/draw found in 200 attempts.");
    return;
}

if (args.Length > 0 && args[0] == "trace")
{
    string opponent = args.Length > 1 ? args[1] : "rusher";
    bool headStart = args.Length > 2 && args[2] == "headstart";
    Func<int, IArenaOpponent> makeOpponent = opponent switch
    {
        "investor" => side => new InvestorBaseline(side),
        "balanced" => side => new BalancedHumanBaseline(side),
        "spam1" => side => new TierSpamBaseline(side, 1),
        "spam2" => side => new TierSpamBaseline(side, 2),
        "spam3" => side => new TierSpamBaseline(side, 3),
        "spam4" => side => new TierSpamBaseline(side, 4),
        "spam5" => side => new TierSpamBaseline(side, 5),
        "spam6" => side => new TierSpamBaseline(side, 6),
        "spam7" => side => new TierSpamBaseline(side, 7),
        "spam8" => side => new TierSpamBaseline(side, 8),
        "rusher" => side => new RusherBaseline(side),
        _ => MakeModelOpponentOrRusher(opponent),
    };

    var (state, engine) = CreateGame(headStart);
    state.Player1.Team = TeamColour.White; state.Player1.SetLoadout(new[] { "freeze", "heal", "cash" });
    state.Player2.Team = TeamColour.White; state.Player2.SetLoadout(new[] { "freeze", "heal", "cash" });
    var bot = new HeuristicBotAdapter(1);
    var foe = makeOpponent(2);

    Console.WriteLine($"tick\tsec\tP1$\tP1inc\tP1inv\tP1units\tP1hp%\tP2$\tP2inc\tP2inv\tP2units\tP2hp%\tP1danger\tTTD\tTTI\tP1comp\tP2comp");
    while (!state.IsGameOver)
    {
        engine.Tick();
        bot.Update(engine);
        foe.Update(engine);

        if (state.CurrentTick % 15 == 0)
        {
            var p1u = state.Units.Count(u => u.Side == 1);
            var p2u = state.Units.Count(u => u.Side == 2);
            var p1hp = 100.0 * state.Player1.CastleHealth / state.Player1.CastleMaxHealth;
            var p2hp = 100.0 * state.Player2.CastleHealth / state.Player2.CastleMaxHealth;
            var p1comp = string.Join(",", state.Units.Where(u => u.Side == 1).GroupBy(u => u.DefinitionId).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}"));
            var p2comp = string.Join(",", state.Units.Where(u => u.Side == 2).GroupBy(u => u.DefinitionId).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}"));
            string ttd = bot.LastTimeToDeathSeconds >= 999999f ? "inf" : bot.LastTimeToDeathSeconds.ToString("F1");
            string tti = bot.LastTimeToInvestSeconds >= 999999f ? "inf" : bot.LastTimeToInvestSeconds.ToString("F1");
            Console.WriteLine($"{state.CurrentTick}\t{state.CurrentTick / 30}\t{state.Player1.Money:F1}\t{state.Player1.Income:F1}\t{state.Player1.InvestmentCount}\t{p1u}\t{p1hp:F0}\t{state.Player2.Money:F0}\t{state.Player2.Income:F1}\t{state.Player2.InvestmentCount}\t{p2u}\t{p2hp:F0}\t{bot.LastDecisionWasDanger}\t{ttd}\t{tti}\t{p1comp}\t{p2comp}");
        }
    }
    string traceResult = state.WinnerSide == 0 ? "draw (timeout, tied HP)"
        : state.IsTimeLimit ? $"P{state.WinnerSide} BY TIMEOUT (higher HP, castle never destroyed)"
        : $"P{state.WinnerSide} (castle destroyed)";
    Console.WriteLine($"\nWinner: {traceResult} at tick {state.CurrentTick}");
    return;
}

if (args.Length > 0 && args[0] == "mirror")
{
    // Sanity check, not a benchmark: HeuristicBot vs itself. Not meant to catch win-rate
    // regressions (there's no "better" side to compare against -- both are the exact
    // same policy) but to catch anything a gadget-targeting/economic change could break
    // structurally: games should still end in a reasonable time (not stall out to the
    // 10-minute timeout constantly), and the P1/P2 win split should stay roughly even
    // (random team/loadout assignment is the only asymmetry, so a big skew would flag a
    // real side-dependent bug in the change under test).
    bool headStart = args.Contains("headstart");
    int games = args.Skip(1).Select(a => int.TryParse(a, out var g) ? g : (int?)null).FirstOrDefault(g => g.HasValue) ?? gamesPerMatchup;
    Console.WriteLine($"Running {games} heuristic-vs-heuristic games (sanity check, not a benchmark){(headStart ? " (with time-machine head starts)" : " (fresh start)")}...\n");

    int p1Wins = 0, p2Wins = 0, draws = 0, timeouts = 0;
    long totalTicks = 0;
    for (int i = 0; i < games; i++)
    {
        var (winner, ticks, isTimeLimit) = RunOneGame(side => new HeuristicBotAdapter(side), side => new HeuristicBotAdapter(side), headStart);
        totalTicks += ticks;
        if (isTimeLimit) timeouts++;
        if (winner == 1) p1Wins++;
        else if (winner == 2) p2Wins++;
        else draws++;
    }
    double avgSeconds = totalTicks / (double)games / 30.0;
    Console.WriteLine($"P1 wins: {p1Wins}/{games} ({100.0 * p1Wins / games:F1}%)  P2 wins: {p2Wins}/{games} ({100.0 * p2Wins / games:F1}%)  draws: {draws}  timeouts: {timeouts}  avg length: {avgSeconds:F1}s");
    return;
}

// Controlled seat-bias diagnostic (2026-07-26): the existing "mirror" mode randomizes
// team/loadout INDEPENDENTLY per side, so a skew there could be team-balance noise, not
// a real engine-level P1/P2 asymmetry. This forces the EXACT same team AND loadout on
// both sides -- the only remaining variable is which physical side (1 vs 2) a given
// unit/player occupies. A truly fair engine should land at ~50/50 within noise; any real
// skew here is a genuine, isolated engine bug, not a game-balance artifact.
// Usage: mirror-fixed <team> <offense> <defense> [games] [headstart]
if (args.Length > 0 && args[0] == "mirror-fixed")
{
    string teamArg = args.Length > 1 ? args[1] : "White";
    string offense = args.Length > 2 ? args[2] : "nuke";
    string defense = args.Length > 3 ? args[3] : "wall";
    int games = args.Length > 4 && int.TryParse(args[4], out var mfg) ? mfg : gamesPerMatchup;
    bool headStart = args.Contains("headstart");

    var team = Enum.Parse<TeamColour>(teamArg, ignoreCase: true);
    Console.WriteLine($"Running {games} games: {team} ({offense}/{defense}) vs itself, IDENTICAL loadout both sides{(headStart ? " (headstart)" : " (fresh start)")}...\n");

    int p1Wins = 0, p2Wins = 0, draws = 0, timeouts = 0;
    long totalTicks = 0;
    for (int i = 0; i < games; i++)
    {
        var (state, engine) = CreateGame(headStart);
        AssignLoadout(state.Player1, 1, team, offense, defense);
        AssignLoadout(state.Player2, 2, team, offense, defense);
        var p1 = new HeuristicBotAdapter(1);
        var p2 = new HeuristicBotAdapter(2);
        bool trace = args.Contains("trace") && i == 0;
        while (!state.IsGameOver)
        {
            engine.Tick();
            p1.Update(engine);
            p2.Update(engine);
            if (trace && state.CurrentTick % 30 == 0)
            {
                var p1u = state.Units.Count(u => u.Side == 1);
                var p2u = state.Units.Count(u => u.Side == 2);
                Console.WriteLine($"t={state.CurrentTick,6} sec={state.CurrentTick/30,4} " +
                    $"P1[$={state.Player1.Money,7:F1} inc={state.Player1.Income,6:F1} inv={state.Player1.InvestmentCount} hp%={100.0*state.Player1.CastleHealth/state.Player1.CastleMaxHealth,5:F1} units={p1u,3}] " +
                    $"P2[$={state.Player2.Money,7:F1} inc={state.Player2.Income,6:F1} inv={state.Player2.InvestmentCount} hp%={100.0*state.Player2.CastleHealth/state.Player2.CastleMaxHealth,5:F1} units={p2u,3}]");
            }
        }
        totalTicks += state.CurrentTick;
        if (state.IsTimeLimit) timeouts++;
        if (state.WinnerSide == 1) p1Wins++;
        else if (state.WinnerSide == 2) p2Wins++;
        else draws++;
    }
    double avgSeconds = totalTicks / (double)games / 30.0;
    Console.WriteLine($"P1 wins: {p1Wins}/{games} ({100.0 * p1Wins / games:F1}%)  P2 wins: {p2Wins}/{games} ({100.0 * p2Wins / games:F1}%)  draws: {draws}  timeouts: {timeouts}  avg length: {avgSeconds:F1}s");
    return;
}

if (args.Length > 0 && args[0] == "spam")
{
    // Matches Marc's own model-evaluation setup: fixed-tier spam bots (spawn that tier
    // and nothing else, no economy/gadget play at all), optionally with games that
    // start already in progress via the real "time machine" (PlayerState(timeSkip)).
    // A decent human player reportedly beats these >95% of the time.
    bool headStart = args.Contains("headstart");
    int spamGames = args.Skip(1).Select(a => int.TryParse(a, out var g) ? g : (int?)null).FirstOrDefault(g => g.HasValue) ?? gamesPerMatchup;
    gamesPerMatchup = spamGames;
    Console.WriteLine($"Running {gamesPerMatchup} games per matchup vs tier-spam bots{(headStart ? " (with time-machine head starts)" : " (fresh start)")}...\n");

    for (int tier = 1; tier <= 8; tier++)
    {
        int t = tier;
        RunMatchup($"vs Tier{t} Spam", side => new TierSpamBaseline(side, t), headStart);
    }
    return;
}

if (args.Length > 0 && args[0] == "actions")
{
    // Tallies the bot's own action distribution the same way
    // CastleDefense.Simulation's --analyze-actions does for real recorded human
    // games, for a direct behavioral comparison instead of chasing win rate
    // against any one specific opponent (see [[project_ai_opponent_heuristic]]).
    bool headStart = args.Contains("headstart");
    int games = args.Skip(1).Select(a => int.TryParse(a, out var g) ? g : (int?)null).FirstOrDefault(g => g.HasValue) ?? 300;

    var actionLabels = new[]
    {
        "wait", "spawnT1", "spawnT2", "spawnT3", "spawnT4", "spawnT5", "spawnT6", "spawnT7", "spawnT8",
        "invest", "repair", "offenseGadget", "defenseGadget", "sigGadget"
    };
    long[] counts = new long[14];
    long totalNonWait = 0;

    // Diverse opponent pool: spam bots (every tier), scripted baselines, and any
    // ONNX models found -- roughly mirrors the range of opponents already tested
    // against, so the bot's action mix isn't tuned against just one matchup type.
    var pool = new List<Func<int, IArenaOpponent>>();
    for (int t = 1; t <= 8; t++) { int tt = t; pool.Add(side => new TierSpamBaseline(side, tt)); }
    pool.Add(side => new RusherBaseline(side));
    pool.Add(side => new InvestorBaseline(side));
    pool.Add(side => new BalancedHumanBaseline(side));
    var modelsDir = FindLeagueModelsDir();
    if (modelsDir != null)
    {
        foreach (var f in Directory.GetFiles(modelsDir, "*.onnx"))
        {
            var path = f;
            pool.Add(side => new AIModelOpponent(side, path));
        }
    }

    Console.WriteLine($"[Actions] Running {games} games (bot as P1, mixed opponent pool of {pool.Count} types{(headStart ? ", with head starts" : "")})...\n");

    for (int i = 0; i < games; i++)
    {
        var opponentFactory = pool[rng.Next(pool.Count)];
        var (state, engine) = CreateGame(headStart);
        var bot = new HeuristicBotAdapter(1);
        var opponent = opponentFactory(2);

        while (!state.IsGameOver)
        {
            engine.Tick();
            bot.Update(engine);
            opponent.Update(engine);
        }
        // Accumulated directly at each successful engine.SpawnUnit/Invest/Repair/
        // UseGadget call inside HeuristicBot -- NOT sampled from LastActionP1 once
        // per tick, because a single Decide() can call SpawnUnit dozens of times in
        // a row (see the doc comment on HeuristicBot.ActionCounts) and sampling would
        // only ever catch the last of those.
        var gameCounts = bot.ActionCounts;
        for (int a = 0; a < counts.Length; a++)
        {
            counts[a] += gameCounts[a];
            if (a != 0) totalNonWait += gameCounts[a];
        }
        (opponent as IDisposable)?.Dispose();
    }

    Console.WriteLine("─── BOT ACTION DISTRIBUTION (% of non-wait actions) ────────────────");
    for (int i = 1; i < actionLabels.Length; i++)
    {
        double pct = totalNonWait > 0 ? counts[i] * 100.0 / totalNonWait : 0;
        Console.WriteLine($"  {actionLabels[i],-16} {counts[i],8}  ({pct,5:F2}%)");
    }
    Console.WriteLine($"  (total non-wait actions: {totalNonWait} -- ActionCounts doesn't track waits, only successful actions taken)");
    return;
}

Console.WriteLine($"Running {gamesPerMatchup} games per matchup...\n");

RunMatchup("vs DoNothing", side => new DoNothingBaseline());
RunMatchup("vs Rusher", side => new RusherBaseline(side));
RunMatchup("vs Investor", side => new InvestorBaseline(side));
RunMatchup("vs BalancedHuman", side => new BalancedHumanBaseline(side));

class HeuristicBotAdapter : IArenaOpponent
{
    private readonly HeuristicBot _bot;
    public HeuristicBotAdapter(int side, HeuristicBotSettings? settings = null) => _bot = new HeuristicBot(side, settings);
    public void Update(GameEngine engine) => _bot.Update(engine);
    public bool LastDecisionWasDanger => _bot.LastDecisionWasDanger;
    public int LastUnitsPurchased => _bot.LastUnitsPurchased;
    public string LastSpendDebug => _bot.LastSpendDebug;
    public float LastThreatScore => _bot.LastThreatScore;
    public float LastDefenseScore => _bot.LastDefenseScore;
    public float LastTimeToDeathSeconds => _bot.LastTimeToDeathSeconds;
    public float LastTimeToInvestSeconds => _bot.LastTimeToInvestSeconds;
    public long[] ActionCounts => _bot.ActionCounts;
}
