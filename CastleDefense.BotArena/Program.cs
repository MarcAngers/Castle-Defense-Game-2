using CastleDefense.BotArena;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;
using System;
using System.Collections.Generic;
using System.Linq;

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

    for (int attempt = 0; attempt < 200; attempt++)
    {
        var (huntState, huntEngine) = CreateGame(headStart);
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
                log.Add($"{huntState.CurrentTick}\t{huntState.CurrentTick / 30}\t{huntState.Player1.Money:F2}\t{huntState.Player1.Income:F1}\t{huntState.Player1.InvestmentCount}\t{huntState.Player1.RepairCount}\t{p1u}\t{p1hp:F0}\t{huntState.Player2.Money:F0}\t{huntState.Player2.Income:F1}\t{p2u}\t{p2hp:F0}\t{huntBot.LastDecisionWasDanger}\t{huntBot.LastThreatScore:F1}\t{huntBot.LastDefenseScore:F1}\t{huntBot.LastUnitsPurchased}\t{huntBot.LastSpendDebug}\t{p1comp}");
            }
        }

        if (huntState.WinnerSide != 1)
        {
            Console.WriteLine($"P1 team={huntState.Player1.Team} offense={huntState.Player1.OffensiveGadget?.Id} defense={huntState.Player1.DefensiveGadget?.Id} sig={huntState.Player1.SignatureGadget?.Id}, P2 team={huntState.Player2.Team}");
            Console.WriteLine("tick\tsec\tP1$\tP1inc\tP1inv\tP1rep\tP1units\tP1hp%\tP2$\tP2inc\tP2units\tP2hp%\tP1danger\tthreat\tdefense\tP1bought\tP1composition");
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

    Console.WriteLine($"tick\tsec\tP1$\tP1inc\tP1inv\tP1units\tP1hp%\tP2$\tP2inc\tP2inv\tP2units\tP2hp%\tP1comp\tP2comp");
    while (!state.IsGameOver)
    {
        engine.Tick();
        bot.Update(engine);
        foe.Update(engine);

        if (state.CurrentTick % 150 == 0)
        {
            var p1u = state.Units.Count(u => u.Side == 1);
            var p2u = state.Units.Count(u => u.Side == 2);
            var p1hp = 100.0 * state.Player1.CastleHealth / state.Player1.CastleMaxHealth;
            var p2hp = 100.0 * state.Player2.CastleHealth / state.Player2.CastleMaxHealth;
            var p1comp = string.Join(",", state.Units.Where(u => u.Side == 1).GroupBy(u => u.DefinitionId).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}"));
            var p2comp = string.Join(",", state.Units.Where(u => u.Side == 2).GroupBy(u => u.DefinitionId).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}"));
            Console.WriteLine($"{state.CurrentTick}\t{state.CurrentTick / 30}\t{state.Player1.Money:F0}\t{state.Player1.Income:F1}\t{state.Player1.InvestmentCount}\t{p1u}\t{p1hp:F0}\t{state.Player2.Money:F0}\t{state.Player2.Income:F1}\t{state.Player2.InvestmentCount}\t{p2u}\t{p2hp:F0}\t{p1comp}\t{p2comp}");
        }
    }
    string traceResult = state.WinnerSide == 0 ? "draw (timeout, tied HP)"
        : state.IsTimeLimit ? $"P{state.WinnerSide} BY TIMEOUT (higher HP, castle never destroyed)"
        : $"P{state.WinnerSide} (castle destroyed)";
    Console.WriteLine($"\nWinner: {traceResult} at tick {state.CurrentTick}");
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
    public HeuristicBotAdapter(int side) => _bot = new HeuristicBot(side);
    public void Update(GameEngine engine) => _bot.Update(engine);
    public bool LastDecisionWasDanger => _bot.LastDecisionWasDanger;
    public int LastUnitsPurchased => _bot.LastUnitsPurchased;
    public string LastSpendDebug => _bot.LastSpendDebug;
    public float LastThreatScore => _bot.LastThreatScore;
    public float LastDefenseScore => _bot.LastDefenseScore;
    public long[] ActionCounts => _bot.ActionCounts;
}
