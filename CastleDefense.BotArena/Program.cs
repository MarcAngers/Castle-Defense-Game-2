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

(int winner, long ticks) RunOneGame(Func<int, IArenaOpponent> makeP1, Func<int, IArenaOpponent> makeP2, bool allowHeadStart = false)
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

    return (state.WinnerSide, state.CurrentTick);
}

void RunMatchup(string label, Func<int, IArenaOpponent> baselineFactory, bool allowHeadStart = false)
{
    int botWins = 0, baselineWins = 0, draws = 0;
    long totalTicks = 0;

    for (int i = 0; i < gamesPerMatchup; i++)
    {
        // Alternate which side the heuristic bot plays to cancel out any side asymmetry.
        bool botIsP1 = i % 2 == 0;
        var (winner, ticks) = botIsP1
            ? RunOneGame(side => new HeuristicBotAdapter(side), baselineFactory, allowHeadStart)
            : RunOneGame(baselineFactory, side => new HeuristicBotAdapter(side), allowHeadStart);

        totalTicks += ticks;

        int botSide = botIsP1 ? 1 : 2;
        if (winner == 0) draws++;
        else if (winner == botSide) botWins++;
        else baselineWins++;
    }

    double winRate = 100.0 * botWins / gamesPerMatchup;
    double avgSeconds = totalTicks / (double)gamesPerMatchup / 30.0;
    Console.WriteLine($"{label,-26} bot wins: {botWins,4}/{gamesPerMatchup} ({winRate,5:F1}%)  baseline wins: {baselineWins,4}  draws: {draws,3}  avg game length: {avgSeconds,6:F1}s");
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

                if (state.WinnerSide == 0) draws++;
                else if (state.WinnerSide == 1) botWins++;
                else oppWins++;
            }

            double winRate = 100.0 * botWins / gamesPerCombo;
            Console.WriteLine($"{offense,-15}{defense,-16} bot wins: {botWins,4}/{gamesPerCombo} ({winRate,5:F1}%)  opp wins: {oppWins,4}  draws: {draws,3}");
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

if (args.Length > 0 && args[0] == "models")
{
    bool headStart = args.Length > 1 && args[1] == "headstart";
    int games = args.Length > 2 && int.TryParse(args[2], out var mg) ? mg : gamesPerMatchup;

    var dir = FindLeagueModelsDir();
    if (dir == null)
    {
        Console.WriteLine("No league_models folder with .onnx files found. Build/run the main app at least once to populate it, or pass a path.");
        return;
    }

    var modelFiles = Directory.GetFiles(dir, "*.onnx").OrderBy(f => f).ToList();
    Console.WriteLine($"Found {modelFiles.Count} model(s) in {dir}");
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
    // Finds and prints a trace of the first LOSS (or draw) against the given tier-spam
    // bot using randomized teams, instead of the fixed matchup "trace" uses -- useful
    // for seeing what an actual bad matchup looks like.
    int tier = args.Length > 1 && int.TryParse(args[1], out var tt) ? tt : 4;
    bool headStart = args.Length > 2 && args[2] == "headstart";

    for (int attempt = 0; attempt < 200; attempt++)
    {
        var (huntState, huntEngine) = CreateGame(headStart);
        var huntBot = new HeuristicBotAdapter(1);
        var huntFoe = new TierSpamBaseline(2, tier);

        var log = new List<string>();
        while (!huntState.IsGameOver)
        {
            huntEngine.Tick();
            huntBot.Update(huntEngine);
            huntFoe.Update(huntEngine);

            if (huntState.CurrentTick % 150 == 0)
            {
                var p1u = huntState.Units.Count(u => u.Side == 1);
                var p2u = huntState.Units.Count(u => u.Side == 2);
                var p1hp = 100.0 * huntState.Player1.CastleHealth / huntState.Player1.CastleMaxHealth;
                var p2hp = 100.0 * huntState.Player2.CastleHealth / huntState.Player2.CastleMaxHealth;
                var p1comp = string.Join(",", huntState.Units.Where(u => u.Side == 1).GroupBy(u => u.DefinitionId).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}"));
                log.Add($"{huntState.CurrentTick}\t{huntState.CurrentTick / 30}\t{huntState.Player1.Money:F0}\t{huntState.Player1.Income:F1}\t{huntState.Player1.InvestmentCount}\t{p1u}\t{p1hp:F0}\t{huntState.Player2.Money:F0}\t{huntState.Player2.Income:F1}\t{p2u}\t{p2hp:F0}\t{p1comp}");
            }
        }

        if (huntState.WinnerSide != 1)
        {
            Console.WriteLine($"P1 team={huntState.Player1.Team} offense={huntState.Player1.OffensiveGadget?.Id} defense={huntState.Player1.DefensiveGadget?.Id} sig={huntState.Player1.SignatureGadget?.Id}, P2 team={huntState.Player2.Team}");
            Console.WriteLine("tick\tsec\tP1$\tP1inc\tP1inv\tP1units\tP1hp%\tP2$\tP2inc\tP2units\tP2hp%\tP1composition");
            foreach (var line in log) Console.WriteLine(line);
            Console.WriteLine($"\nResult: {(huntState.WinnerSide == 0 ? "draw/timeout" : "P" + huntState.WinnerSide + " wins")} at tick {huntState.CurrentTick} (attempt {attempt + 1})");
            return;
        }
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
        _ => side => new RusherBaseline(side),
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
    Console.WriteLine($"\nWinner: {(state.WinnerSide == 0 ? "draw/timeout" : "P" + state.WinnerSide)} at tick {state.CurrentTick}");
    return;
}

if (args.Length > 0 && args[0] == "spam")
{
    // Matches Marc's own model-evaluation setup: fixed-tier spam bots (spawn that tier
    // and nothing else, no economy/gadget play at all), optionally with games that
    // start already in progress via the real "time machine" (PlayerState(timeSkip)).
    // A decent human player reportedly beats these >95% of the time.
    bool headStart = args.Length > 1 && args[1] == "headstart";
    Console.WriteLine($"Running {gamesPerMatchup} games per matchup vs tier-spam bots{(headStart ? " (with time-machine head starts)" : " (fresh start)")}...\n");

    for (int tier = 1; tier <= 8; tier++)
    {
        int t = tier;
        RunMatchup($"vs Tier{t} Spam", side => new TierSpamBaseline(side, t), headStart);
    }
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
}
