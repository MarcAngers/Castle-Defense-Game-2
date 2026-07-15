using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CastleDefense.Engine.Bot
{
    // Tunable knobs for the TTD/danger trigger, pulled out into an injectable settings
    // object so an automated parameter search (CastleDefense.BotArena's "paramsearch"
    // mode) can try candidate values without editing/rebuilding this file per
    // candidate. Defaults match the values every fix this session was validated
    // against -- passing null/omitting the constructor argument reproduces the exact
    // committed behavior. Only the cheap, pure-comparison TTD/danger-trigger knobs are
    // exposed here (never the reactive-spend/unit-scoring constants in SpendOnUnits --
    // that domain has 4 confirmed dead-end tuning attempts this session already and
    // isn't a good target for further automated search without a new angle there).
    public class HeuristicBotSettings
    {
        public float SafetyMarginMultiplier { get; init; } = 1.4f;
        public float SafetyBufferSeconds { get; init; } = 2f;
        public float EnemyIsCloseDistance { get; init; } = 700f;
        public float RepairHpThreshold { get; init; } = 0.75f;

        public static readonly HeuristicBotSettings Default = new HeuristicBotSettings();
    }

    // Rule-based opponent. Drives a side entirely through GameEngine's public API
    // (SpawnUnit / Invest / Repair / UseGadget) -- the same surface a human player
    // uses via the SignalR hub -- so it plays by the exact same rules a human does.
    public class HeuristicBot
    {
        private readonly int _side;
        private readonly HeuristicBotSettings _settings;

        // Debug/test visibility into the last decision -- not used by the bot itself.
        public bool LastDecisionWasDanger { get; private set; }
        public int LastUnitsPurchased { get; private set; }
        public string LastSpendDebug { get; private set; } = "";
        public float LastThreatScore { get; private set; }
        public float LastDefenseScore { get; private set; }
        public float LastTimeToDeathSeconds { get; private set; }
        public float LastTimeToInvestSeconds { get; private set; }

        // Running per-game tally of every successful action actually taken, indexed by
        // the same 14-action ID space as GetActionMask/ApplyAction (0=wait unused here,
        // 1-8=spawn tier, 9=invest, 10=repair, 11-13=gadget slots) -- lets a harness
        // compare the bot's real action mix against recorded human play. Counted directly
        // at each call site (not sampled from GameEngine.LastActionP1/P2) because a single
        // Decide() can call SpawnUnit many times in a row -- sampling LastAction once per
        // tick would only ever see the last of those and badly undercount.
        public readonly long[] ActionCounts = new long[14];

        // ~6 decisions/sec at 30 TPS. Fast enough to never leave money idle,
        // slow enough that it doesn't look like it's cheating with instant reactions.
        private const int DecisionIntervalTicks = 5;
        private long _nextDecisionTick;

        // Rolling window of CastleHealth readings, feeding EstimateTimeToDeathSeconds
        // below (drain rate AND its rate of change). Tuned empirically at full
        // 400-spam/300-model x2-replicate validation across several stages:
        // - 6/3/9 decisions (~1/0.5/1.5s) were compared back when this window only fed a
        //   simple "has HP dropped at all in this window" recency check (a first-derivative
        //   proxy) -- 9 won clearly then. See [[project_ai_opponent_heuristic]] for that
        //   comparison.
        // - Once the window started feeding an actual SECOND derivative (acceleration --
        //   see EstimateTimeToDeathSeconds), 9 decisions proved too short: differencing two
        //   already-noisy rate estimates (each covering only ~0.67s) amplified that noise,
        //   measurably hurting steadier opponents (Tier3 spam -8.75 avg, Tier5 spam -8.5
        //   avg) even while it helped the two hardest matchups (v4 +5.15, v7 +2.8 avg).
        //   Widening to 18 decisions (~3s, ~1.5s per half) stabilized the acceleration
        //   estimate: kept v4's gain (+2.8 avg) while shrinking Tier3/Tier5's regressions to
        //   -7.1/-2.35 avg and recovering Tier4/v3/v22/v23/v25/v21 to roughly flat. A damped
        //   (0.5x) acceleration term was also tried at this window size and made things
        //   worse, not better (new small regressions on v3/v22/v23/v25 with no compensating
        //   gain) -- reverted. Tier3's regression was not fully resolved within this
        //   session's time budget; flagged as still-open in memory.
        private const int HpHistoryWindow = 18;
        private readonly List<int> _recentCastleHealth = new List<int>();

        private const float EffectivelyInfiniteSeconds = 999999f;

        // Distinguishes a static single-tier spam bot (never changes what it spawns,
        // by definition) from an adaptive opponent (model or human, which diversifies
        // as its own economy/loadout evolves) -- feeds the early-army-pivot gate below.
        // Cumulative since game start, not a rolling window: a spam bot's defining trait
        // is NEVER changing, so any diversity observed even once permanently disqualifies
        // "confident spammer" for the rest of the game, the same way a human would reason
        // ("I've now seen them field a second unit type, so they're not a fixed spammer,
        // even if they don't do it again"). Tracks distinct unit INSTANCES ever seen
        // (not current on-field count, which fluctuates as units die) so the confidence
        // count only grows, and distinct TIERS among those instances.
        private readonly HashSet<Guid> _observedEnemyUnitIds = new HashSet<Guid>();
        private readonly HashSet<int> _observedEnemyTiers = new HashSet<int>();
        private const int MinEnemyUnitsForSpammerRead = 8;

        public HeuristicBot(int side, HeuristicBotSettings settings = null)
        {
            _side = side;
            _settings = settings ?? HeuristicBotSettings.Default;
        }

        // Projects seconds-until-castle-death from the rolling HP window, modeling BOTH
        // the current drain rate (first derivative) and how that rate is itself changing
        // (second derivative / acceleration) -- against a spam-style opponent, HP drain
        // doesn't stay constant: more units pile into melee range of the castle each
        // second (no per-tick damage cap), so a naive constant-rate projection
        // underestimates how bad things are about to get. Symmetrically, once a wave
        // gets broken (by reactive spend or a gadget), the rate eases back off and a
        // constant-rate projection would overstate the remaining danger for a beat.
        //
        // Splits the window into an early half and a recent half, estimates the average
        // drain rate within each, and treats the difference between them as a constant
        // acceleration applied going forward from the recent-half rate -- then solves the
        // standard kinematic "distance covered under constant acceleration" equation
        // (hpRemaining = v*t + 0.5*a*t^2) for the smallest positive t, instead of the
        // simpler hpRemaining / v.
        private float EstimateTimeToDeathSeconds(int currentHp)
        {
            int n = _recentCastleHealth.Count;
            if (n < HpHistoryWindow) return EffectivelyInfiniteSeconds; // window not full yet

            float decisionSeconds = DecisionIntervalTicks / 30f;
            int mid = n / 2;
            if (mid <= 0 || mid >= n - 1) return EffectivelyInfiniteSeconds; // window too small to split

            int hpAtStart = _recentCastleHealth[0];
            int hpAtMid = _recentCastleHealth[mid];
            // currentHp (not _recentCastleHealth[n-1]) is used for the end of the recent
            // half -- they're the same value, but currentHp is what the caller is
            // actually asking "how long until THIS reaches zero", so use it directly.

            float earlySeconds = mid * decisionSeconds;
            float recentSeconds = (n - 1 - mid) * decisionSeconds;

            float rateEarly = (hpAtStart - hpAtMid) / earlySeconds;   // HP/sec, positive = draining
            float rateRecent = (hpAtMid - currentHp) / recentSeconds; // HP/sec, positive = draining

            // A trickle of chip damage with no real acceleration isn't a meaningful
            // threat -- don't let noise around zero register as "draining".
            if (rateRecent <= 0.5f && rateEarly <= 0.5f) return EffectivelyInfiniteSeconds;

            float timeBetweenRateSamples = (earlySeconds + recentSeconds) / 2f;
            float acceleration = (rateRecent - rateEarly) / timeBetweenRateSamples; // HP/sec^2

            // Constant-rate fallback when acceleration is negligible -- avoids dividing by
            // a near-zero acceleration in the quadratic solve below.
            if (Math.Abs(acceleration) < 0.1f)
                return rateRecent > 0.5f ? currentHp / rateRecent : EffectivelyInfiniteSeconds;

            // Solve currentHp = v*t + 0.5*a*t^2 for the smallest positive t.
            float v = rateRecent;
            float a = acceleration;
            float discriminant = v * v + 2f * a * currentHp;
            if (discriminant < 0f)
            {
                // Math says a decelerating trend would stop us reaching 0 HP at all --
                // but traced a real Tier3 spam loss (hunt 3) where this branch reported
                // "inf" (totally safe) repeatedly while castleHpPct was VISIBLY, MONOTONICALLY
                // dropping every single logged row (100 -> 99 -> 98 -> ... -> 1, no
                // plateaus). A genuine "wave broken, drain stopping" scenario shows up as
                // the RECENT rate (v) itself dropping toward zero, not just a large
                // computed deceleration while v stays elevated -- Tier3's bursty/uneven hit
                // timing within a short half-window produces noisy acceleration estimates
                // that swung deceleration hard enough to hit this branch spuriously, over
                // and over, right when the castle needed a repair/reactive response the
                // most. Don't let a merely-computed deceleration override a live, still-
                // significant current rate -- fall back to the honest (always-conservative)
                // constant-rate estimate whenever v itself hasn't actually eased off yet.
                return v > 0.5f ? currentHp / v : EffectivelyInfiniteSeconds;
            }

            float sqrtDisc = MathF.Sqrt(discriminant);
            float t1 = (-v + sqrtDisc) / a;
            float t2 = (-v - sqrtDisc) / a;
            float result = EffectivelyInfiniteSeconds;
            if (t1 > 0f && t1 < result) result = t1;
            if (t2 > 0f && t2 < result) result = t2;
            return result;
        }

        public void Update(GameEngine engine)
        {
            var state = engine._state;
            if (state.IsGameOver) return;
            if (state.CurrentTick < _nextDecisionTick) return;
            _nextDecisionTick = state.CurrentTick + DecisionIntervalTicks;

            Decide(engine);
        }

        private void Decide(GameEngine engine)
        {
            var state = engine._state;
            var me = _side == 1 ? state.Player1 : state.Player2;

            // Loadout not assigned yet (shouldn't happen once the game has started).
            if (me.OffensiveGadget == null || me.DefensiveGadget == null || me.SignatureGadget == null) return;

            var teamDef = GameDataManager.Teams.FirstOrDefault(t => t.Color == me.Team);
            if (teamDef == null || teamDef.Roster.Count == 0) return;

            int myCastlePos = _side == 1 ? 200 : GameEngine.MAP_WIDTH - 200;

            var myUnits = state.Units.Where(u => u.Side == _side).ToList();
            var enemyUnits = state.Units.Where(u => u.Side != _side).ToList();

            foreach (var u in enemyUnits)
            {
                if (_observedEnemyUnitIds.Add(u.InstanceId))
                    _observedEnemyTiers.Add(u.Tier);
            }
            bool confidentStaticSpammer = _observedEnemyUnitIds.Count >= MinEnemyUnitsForSpammerRead && _observedEnemyTiers.Count == 1;
            int observedEnemySpamTier = confidentStaticSpammer ? _observedEnemyTiers.First() : 0;

            // --- THREAT ASSESSMENT ---
            // Weight enemy strength by how close it is to our castle so a distant
            // skirmish doesn't trigger the same panic response as a unit at the gate.
            float threatScore = 0f;
            foreach (var u in enemyUnits)
            {
                float distToMyCastle = Math.Abs(u.Position - myCastlePos);
                float proximityWeight = Math.Max(0.15f, 1200f / (distToMyCastle + 250f));
                threatScore += Power(u) * proximityWeight;
            }
            float defenseScore = myUnits.Sum(Power);

            bool enemyIsClose = enemyUnits.Count > 0 && enemyUnits.Min(u => Math.Abs(u.Position - myCastlePos)) < _settings.EnemyIsCloseDistance;
            float castleHpPct = me.CastleMaxHealth > 0 ? (float)me.CastleHealth / me.CastleMaxHealth : 1f;

            // Under the boom strategy our standing army is intentionally near-zero most of
            // the game, so a naive "is any enemy near our castle" trigger fires almost
            // constantly (an approaching unit hasn't necessarily landed a hit yet) --
            // draining a small amount of money into reactive defense EVERY decision is
            // exactly what was capping our own income: it never let money accumulate past
            // ~2 investments while a model opponent's income kept climbing unimpeded past
            // it. React once the castle has actually confirmed taking damage (a real
            // threat, not just a unit walking by), or if the incoming mass is overwhelming
            // enough to be worth preempting before it lands.
            //
            // The "overwhelming mass" clause had the exact same degenerate failure mode as
            // the naive trigger above, just one level deeper: defenseScore is 0 for most of
            // the early game (we have no standing army yet by design), and threatScore from
            // even a single distant scout is still > 0 (proximityWeight has a 0.15 floor),
            // so "threatScore > defenseScore * 1.5" collapses to "threatScore > 0" -- true
            // for almost any nearby enemy, not just a genuine incoming mass. Traced against
            // Tier3 spam and found this alone can keep the bot permanently reacting to lone
            // scouts from tick 0, spending every dollar on one-off fodder and never once
            // reaching the very first InvestmentPrice (18) the whole game (see
            // [[project_ai_opponent_heuristic]]). "Mass" implies more than one attacker --
            // require a real cluster (3+) before treating it as worth preempting; a lone
            // unit approaching an empty board should just be left to chip in the (cheap,
            // insured-against-by-the-HP-threshold-above) worst case, not panic-bought.
            //
            // The HP clause had a THIRD instance of the same degenerate pattern, and this
            // one turned out to be the biggest: "enemyUnits.Count > 0" has no proximity
            // requirement at all, unlike enemyIsClose. Traced a full lost game against
            // castle_defense_p1_v4 (headstart) and found castleHpPct got chipped to 89% by
            // tick ~900 (30s) and then sat EXACTLY at 89% for the entire rest of the ~370s
            // game -- never enough to need repair (75% threshold), never allowed to be
            // "safe" either, because the model always had at least one unit *somewhere* on
            // its own half of the map. inDanger was true on effectively every decision for
            // over 5 minutes straight, so SpendOnUnits(preferDefense: true) -- which has no
            // investment reserve at all, by design, since reactive defense shouldn't hold
            // back when actually threatened -- kept consuming money on cheap fodder before
            // the invest check downstream ever got a real shot at it. Money never
            // re-accumulated to InvestmentPrice (169.4 at that point) for the rest of the
            // game while the model's kept compounding unimpeded (investment 3 -> 9). Require
            // enemyIsClose here too: a stale HP deficit with nothing actually near our
            // castle isn't an active threat, just a scoreboard number repair will fix on its
            // own once genuinely worth it (or that's cheap to just tank -- see the
            // insurance comment above).
            // The mass clause (enemyUnits.Count >= 3 && threatScore > defenseScore * 1.5f)
            // that used to live here had a subtler version of the exact same "true almost
            // always" problem as the two clauses above: under the boom strategy defenseScore
            // is deliberately kept near-zero, so the 1.5x ratio bar was trivially satisfied
            // by completely ordinary, already-being-handled enemy production, not just a
            // genuine incoming alpha strike -- traced `hunt 1` (Tier1 spam) and found this
            // true for essentially an entire 600-second game while castleHpPct never once
            // moved off 100%. A recency requirement (HP now lower than ~1.5s ago) fixed the
            // worst of it, but the underlying question a fixed ratio-against-a-near-zero
            // baseline can never really answer is "how much runway do we actually have" --
            // which matters because the real decision isn't just "danger yes/no", it's
            // "can we safely reach the next investment before we'd die, or do we need to buy
            // more time first" (Marc's framing: HP is a resource you spend to buy time for
            // the economy to compound -- a repair is a huge, cheap, one-time time purchase,
            // e.g. the first one takes CastleMaxHealth 2000 -> 12000, a ~6x swing).
            //
            // Estimate an actual time-to-death from the observed HP drain rate over the same
            // rolling window used for the recency check above, and compare it against how
            // long it will take to save up the next InvestmentPrice at the current income.
            // If we have comfortably more runway than that, saving straight for the
            // investment is safe and reactive spending/repair are unnecessary this decision
            // (mirrors "I monitor my HP and decide if I can get away with an investment
            // before I need to upgrade my HP"). If not, this decision needs to buy time
            // instead -- via repair (a big, permanent HP/time purchase, see below) and/or
            // reactive spending (kill the incoming wave, which lowers the drain rate itself).
            _recentCastleHealth.Add(me.CastleHealth);
            if (_recentCastleHealth.Count > HpHistoryWindow) _recentCastleHealth.RemoveAt(0);

            float timeToDeathSeconds = EstimateTimeToDeathSeconds(me.CastleHealth);

            double moneyStillNeeded = Math.Max(0, me.InvestmentPrice - me.Money);
            float timeToInvestSeconds = me.Income > 0.01 ? (float)(moneyStillNeeded / me.Income) : EffectivelyInfiniteSeconds;

            // Require real headroom, not just "barely more time than needed" -- decisions
            // only run ~6/sec and an enemy's incoming mass can keep growing, so the drain
            // rate measured this instant is a floor on how bad it gets, not a guarantee.
            bool investmentRunwayIsSafe = timeToDeathSeconds >= timeToInvestSeconds * _settings.SafetyMarginMultiplier + _settings.SafetyBufferSeconds;

            // EXPERIMENT: castleHpPct < 0.9f dropped from this OR -- traced a v4 matchup
            // (trace v4, fine-grained log) where HP sat flat at exactly 90% (a stale,
            // non-recovering deficit from an earlier, now-resolved skirmish) while a
            // SINGLE non-threatening enemy unit lingered nearby. This clause has no
            // recency requirement (unlike the TTD-based runway check), so it latched
            // inDanger permanently true off that stale reading alone, triggering a
            // disproportionate reactive-buy spree (built up to 17 "doggo" units against
            // that 1 enemy) that cost enough accumulated savings to lose the investment-5
            // race to v4 by a wide margin. investmentRunwayIsSafe should already catch
            // any GENUINE ongoing danger (it's a strictly more accurate, recency-aware
            // signal) -- testing whether the cruder accumulated-damage clause is now
            // pure liability rather than added safety. See [[project_ai_opponent_heuristic]].
            bool inDanger = enemyIsClose && !investmentRunwayIsSafe;
            LastDecisionWasDanger = inDanger;
            LastThreatScore = threatScore;
            LastDefenseScore = defenseScore;
            LastTimeToDeathSeconds = timeToDeathSeconds;
            LastTimeToInvestSeconds = timeToInvestSeconds;

            // Claim a safely-affordable investment before ANYTHING else this decision gets
            // a chance to spend the money, rather than checking it last (as below) and
            // hoping nothing else got to it first. Found via trace (hunt v4 headstart) that
            // this race is real and not just theoretical: money visibly reached $32.32
            // against a $31.20 InvestmentPrice while inDanger was false, yet InvestmentCount
            // never moved -- because unlike the first investment ($18, landed on exactly by
            // clean $2 increments from zero), later thresholds aren't round numbers the
            // income accrual lands on exactly, so money can jump straight PAST the price in
            // a single decision. DeferForInvestment's <= boundary guard (see its own
            // comment) only protects gadgets up to the exact crossover value -- once money
            // overshoots it in one step, that guard is already inactive the very same
            // decision a gadget or reactive spend could also fire and steal the dollar
            // investing needed. Checking investment first eliminates that whole class of
            // race at the root instead of patching each specific competing spend. Gated on
            // investmentRunwayIsSafe (not just affordability) so a genuine emergency still
            // gets first claim on the money via the normal repair/reactive-spend path below
            // -- this is Marc's framing directly: "if you can get away with an investment
            // before you need to upgrade your HP, take it."
            if (investmentRunwayIsSafe && me.Money >= me.InvestmentPrice)
            {
                if (engine.Invest(_side)) ActionCounts[9]++;
                return;
            }

            // --- GADGETS: cheap relative to overall spend, high impact, own cooldowns ---
            TryUseOffenseGadget(engine, me, myUnits, enemyUnits, myCastlePos);
            TryUseDefenseGadget(engine, me, myUnits, myCastlePos);
            TryUseSignatureGadget(engine, me, myUnits, enemyUnits, myCastlePos, inDanger, castleHpPct);

            // --- MILITARY / ECONOMY ---
            // Boom strategy: a spam bot (or a human who isn't optimizing) never invests,
            // so out-scaling its flat income is a far more reliable win condition than
            // trying to win a cheap-unit production race we might structurally lose on
            // team cost alone. Only spend on units REACTIVELY to clear whatever's actually
            // attacking the castle; every other dollar goes to investing (which has no
            // trough in this economy -- see below) and to repair, which keeps the castle
            // alive to tank chip damage while poor. Once the economy has clearly outscaled
            // a non-investing opponent, surplus money starts converting into an offensive
            // army too, since by then unit purchases no longer meaningfully compete with
            // investing for the same dollars.
            //
            // Repair when hurt -- keeps us alive through the early game while income is
            // still small. Repair() also permanently raises CastleMaxHealth (1000 -> 12000
            // -> ...) even when called at full health, and multiple enemies can hit the
            // castle in the same tick with no per-tick damage cap, so extra HP is real
            // insurance. Threshold is fairly generous (75%) so damage gets addressed before
            // it compounds into an emergency, rather than always waiting until critical.
            // Deliberately unconditional on inDanger (unlike everything below): this used to
            // live below an "if (inDanger) { ...; return; }" block, which created a real
            // death spiral once the castle first dipped under 90% HP (see inDanger's own
            // comment above) -- danger stayed permanently true from then on for as long as
            // ANY enemy unit existed anywhere on the map (no proximity requirement in that
            // clause), which is true almost continuously against any active opponent past
            // the early game. Repair only fires under 75%, so HP would just sit parked in
            // the 75-90% band forever: never hurt enough to repair, never healthy enough to
            // stop being "in danger" and reach the repair/invest checks below at all. Now
            // repair gets a chance every decision regardless, which is what actually breaks
            // the loop -- matches Marc's own read that repairing ("the HP upgrade") and
            // investing are naturally linked, since climbing back over 90% HP is what lets
            // the rest of this method run again.
            //
            // Also repair proactively whenever the time-to-death model says we don't have
            // enough runway to safely reach the next investment (`inDanger`), even if HP%
            // hasn't dropped all the way to 75% yet -- a fast burst can make time-to-death
            // short well before the cumulative damage does. This is the "trade HP for time"
            // move: the first repair alone takes CastleMaxHealth 2000 -> 12000, a ~6x swing
            // that can turn a losing race against the clock into a comfortable one for the
            // price of a single, cheap, permanent purchase.
            bool repairWouldHelp = castleHpPct < _settings.RepairHpThreshold || inDanger;
            if (repairWouldHelp && me.Money >= me.RepairPrice * 1.25)
            {
                if (engine.Repair(_side)) ActionCounts[10]++;
            }

            if (inDanger)
            {
                SpendOnUnits(engine, me, teamDef.Roster, preferDefense: true, enemyUnits);
            }

            // Fallback investment check: the primary one now happens at the very top of
            // this method (see its comment) whenever investmentRunwayIsSafe, before
            // anything else can touch the money. This one exists for the danger case that
            // skipped that early check -- repair/reactive spend above may not have needed
            // all the money even while genuinely in danger, so still grab an investment
            // with whatever's left rather than let it sit idle till next decision.
            //
            // Investing has essentially no downside in THIS economy: the hardcoded
            // starting Income (2) is already below the investment formula's very first
            // step (~2.65), so every investment -- starting with the very first one -- is
            // a strict, permanent income increase. (That's not true of every economy this
            // bot might run under: if the starting income is ever tuned back up above
            // where the formula naturally would be, the first investment can crater income
            // for a while before recovering -- worth re-checking this assumption if the
            // starting Income constant in PlayerState() ever changes again.)
            //
            // Tried giving the first couple of repairs priority alongside (or ahead of)
            // investing -- RepairPrice starts about the same as the first InvestmentPrice,
            // and permanently multiplies CastleMaxHealth 6x for that price -- reasoning
            // that a bigger HP buffer should help survive early pressure. Measured against
            // the real trained models it was a net loss every way it was ordered (before
            // investing, after investing, gated to only the first 1-2 repairs): even
            // spending idle money that investing wasn't using yet cost more win rate than
            // the extra HP bought back. Left as purely reactive (above) rather than
            // proactive -- see [[project_ai_opponent_heuristic]] for the full investigation.
            if (me.Money >= me.InvestmentPrice)
            {
                if (engine.Invest(_side)) ActionCounts[9]++;
                return;
            }

            // Only start converting surplus into an offensive army once income has clearly
            // pulled away from what a flat, non-investing opponent could ever match --
            // before that point, every dollar spent on units is a dollar that isn't
            // compounding, and a spam bot only ever needs a small reactive defense to
            // ignore entirely. Explicitly !inDanger (used to be implicit via the early
            // return above) -- while actively defending, reactive spending already ran
            // above and nothing further should be layered on the same decision.
            //
            // TESTED AND REJECTED (variant 1): lowering this to InvestmentCount >= 3 while
            // keeping the existing generic SpendOnUnits(preferDefense:false) scorer,
            // motivated by tracing 4 of Marc's own recorded Green-vs-Tier4-spam wins
            // (--trace-human tooling, CastleDefense.Simulation), which showed an identical
            // human pattern in all 4 -- invest exactly 3 times (Income 2 -> ~8.5), then
            // pivot entirely to buying Tier5 units and win within ~70s, tolerating HP as
            // low as 31-56% rather than grinding 2 more investments (~$474 then ~$1677)
            // first the way Income >= 50 forces (that threshold only crosses at
            // InvestmentCount 5). Validated at full two-replicate discipline (spam n=400
            // x2, headstart): a broad, consistent REGRESSION, not the hoped-for win --
            // Tier1 -4.3, Tier2 -7.4, Tier3 -7.8, Tier4 -7.9 (the very matchup it targeted
            // got WORSE, 65.4%->57.5%), Tier5 -8.4, Tier6 -3.2, timeout counts roughly
            // doubled or more on nearly every tier. Root cause identified afterward: the
            // human wasn't just buying "whatever scores well" earlier -- checking the
            // bot's own ScoreUnit formula against Green's roster showed Tier3 durdle
            // actually outscores Tier5 gecko on defensive cost-efficiency (durdle is
            // cheap and tanky but has almost no DPS, 4.8 vs gecko's 96) -- the generic
            // scorer would never have converged on gecko at all. The human was optimizing
            // for OFFENSIVE throughput to end the game fast, not cost-efficient trading,
            // and committing to ONE unit type repeatedly rather than diluting across
            // whatever the scorer ranks highest tick to tick.
            //
            // TESTED AND REJECTED (variant 2): same InvestmentCount >= 3 trigger, but
            // replaced the generic scorer with a direct mimic of the observed behavior --
            // save up for and repeatedly buy ONLY the team's Tier5 unit (the literal
            // "wave-breaker" tier the human targeted in all 4 traces), never switching to
            // anything else -- see BuyWaveBreaker below (dead code, not called). This one
            // is NOT a clean regression like variant 1 -- it's a genuine, consistent
            // trade-off. Validated at full two-replicate discipline: spam n=400 x2
            // headstart gave a real, repeatable WIN on low/mid tiers, including the
            // targeted matchup -- Tier1 +4.9, Tier2 +6.2, Tier3 +4.1, Tier4 65.4%->71.7%
            // (+6.3) -- but a severe LOSS on high tiers, worse than variant 1 ever was:
            // Tier5 -7.3, Tier6 -20.7(!), Tier7 -13.9(!), Tier8 -7.5. Mechanistically
            // obvious in hindsight: locking onto Tier5 forever means the army never
            // upgrades once the opponent's own units clearly outclass it, which a
            // Tier6-8 spam bot's fixed high-tier output punishes hard. Worse, models
            // n=300 headstart was CATASTROPHIC, not just a trade-off -- every single one
            // of the 10 models dropped, most by 10-47 points (v14 -40.4, v25 -46.7, v22
            // -12.2, v3 -9.7), including v4 (the actual highest-priority hard matchup)
            // getting WORSE too (50.3%->40.7%, -9.6). Adaptive opponents punish a
            // committed single-tier army far harder than a static spam bot ever could.
            // Reverted. Two independently-designed attempts at "start the non-reactive
            // army-build phase earlier" (lower the threshold; lower the threshold AND
            // commit to one tier) have now both been tried and both net-lose once models
            // are weighed in, despite variant 2 posting a real win on the narrow spam
            // slice Marc's recordings came from. Don't attempt a third variant of "just
            // move the threshold" without a mechanism that can tell a static spam bot
            // apart from an adaptive one before committing to an early, narrow army --
            // e.g. gating the early pivot on detecting the opponent hasn't invested/
            // diversified after some observation window, not on the bot's OWN economy
            // state alone. See [[project_ai_opponent_heuristic]] for the full writeup.
            // TESTED AND REJECTED (variant 3): implemented exactly the "gate on detected
            // opponent behavior" mechanism the variant-2 writeup called for --
            // confidentStaticSpammer (enemy has shown MinEnemyUnitsForSpammerRead=8 units,
            // never once varied tier) gated an `else if` alongside (not replacing) the
            // Income >= 50 branch, so the wave-breaker pivot only applied during the
            // narrow InvestmentCount 3->5 window and behavior reverted to the untouched
            // generic SpendOnUnits the instant Income reached 50 either way. Also fixed
            // variant 2's Tier6-8 mechanism gap: BuyWaveBreaker outclassed the SPECIFIC
            // observed spam tier by one (capped at 8) instead of hardcoding Tier5.
            //
            // Validated at full two-replicate discipline (spam n=400 x2, models n=300 x2,
            // headstart). Partial success, net rejected: it DID fix what variant 2 broke
            // -- no more catastrophic model collapse (worst single model was v25 at -11.0,
            // not v25's -46.7 or v14's -40.4 from variant 2) -- confirming the opponent-
            // read concept itself works as a circuit breaker against adaptive opponents.
            // But it FAILED to deliver the thing this whole investigation was for: Tier4
            // spam came back essentially flat (65.4%->65.6%, +0.2, noise-level), not
            // variant 2's real +6.3. Tier1/Tier2 picked up new, consistent-both-replicates
            // regressions (-4.75/-3.2) despite never being the target. Models were a mixed
            // bag rather than a clean save -- v14/v21/v22 up 1.6-3.2, but v25 -11.0, v3
            // -4.3, and v4 (the actual top-priority matchup) still down -4.5, not fixed.
            //
            // Root cause for the vanished Tier4 gain: variant 2's whole-game-permanent
            // pivot (it replaced Income>=50 entirely, never handing back to generic
            // spending at any income) is what let the human's "commit hard and win fast"
            // strategy actually play out over a full game. Confining the SAME pivot to
            // only the few investments' worth of game-time between count 3 and Income=50
            // (~15-30s typically) undoes most of that benefit -- the win condition needed
            // sustained aggression, not a brief early window before reverting to slower
            // play. Confirms this lever is now dead-ended for good in EVERY combination
            // tried (bare threshold; threshold + fixed tier; threshold + fixed tier +
            // opponent-gating) -- a real 4th attempt would need the pivot to persist for
            // the rest of the game once triggered (like variant 2) while ALSO carrying
            // variant 3's tier-escalation fix and opponent-read gate together, which
            // hasn't been tried. Reverted -- see [[project_ai_opponent_heuristic]] for the
            // full three-variant writeup. BuyWaveBreaker/confidentStaticSpammer/
            // observedEnemySpamTier tracking left in place as dead code for that attempt.
            if (!inDanger && me.Income >= 50)
            {
                SpendOnUnits(engine, me, teamDef.Roster, preferDefense: false, enemyUnits);
            }
        }

        // Commits to repeatedly buying ONLY units of targetTier once affordable, mimicking
        // the human's observed concentrated single-tier buying instead of the generic
        // multi-candidate scorer in SpendOnUnits. Deliberately has no reserve/richMode
        // logic (unlike SpendOnUnits) -- only called from the confidentStaticSpammer
        // branch above, which is itself gated tightly enough (income still low, opponent
        // confirmed static) that competing for gadget money isn't the concern it is once
        // SpendOnUnits takes over post-Income-50.
        private void BuyWaveBreaker(GameEngine engine, PlayerState me, List<UnitDefinition> roster, int targetTier)
        {
            int ownUnitCount = engine._state.Units.Count(u => u.Side == _side);
            if (ownUnitCount >= MaxOwnUnitsOnField) return;

            var waveBreaker = roster.FirstOrDefault(u => u.Tier == targetTier);
            if (waveBreaker == null || me.Money < waveBreaker.Cost) return;

            if (engine.SpawnUnit(_side, waveBreaker.Id))
            {
                LastUnitsPurchased++;
                if (targetTier >= 1 && targetTier <= 8) ActionCounts[targetTier]++;
            }
        }

        private static float Power(Unit u)
        {
            float aps = u.AttackSpeed > 0 ? u.AttackSpeed : 0.3f;
            return u.Damage * aps + u.CurrentHealth * 0.04f + u.CurrentShield * 0.04f;
        }

        // Buys at most ONE unit per decision -- matching the pacing every other agent
        // (a human clicking buy, or a trained ONNX model getting one action per inference
        // step) is bound by. This used to loop up to 40 purchases in a single decision,
        // which measurably diverges from real play: comparing recorded human games against
        // the bot's own logged actions (see [[project_ai_opponent_heuristic]]) showed the
        // bot spawning units for ~96% of its actions vs ~80% for humans, with invest/repair/
        // gadget usage diluted to a fraction of the human rate -- almost entirely explained
        // by this single loop letting one "decision" batch-buy dozens of units at once,
        // something no human or model opponent can ever do. With DecisionIntervalTicks=5
        // (~6 decisions/sec), a single purchase per call still lets money get spent quickly
        // when needed; it just can't happen all in the same instant anymore.
        // Cap how large our own army is allowed to get: combat is O(units^2) per tick,
        // and a battlefield with hundreds of units per side isn't more effective anyway
        // (they just queue up waiting for a turn to attack).
        private const int MaxOwnUnitsOnField = 120;

        private void SpendOnUnits(GameEngine engine, PlayerState me, List<UnitDefinition> roster, bool preferDefense, List<Unit> enemyUnits)
        {
            LastUnitsPurchased = 0;
            int ownUnitCount = engine._state.Units.Count(u => u.Side == _side);

            // Once the cost-efficient swarm is already at full size and the economy is
            // still piling up money beyond what it needs, cost-per-value stops mattering
            // -- more cheap units just stalemates against an equally cheap trickle from
            // the other side. Switch to buying pure raw power instead, and make room for it.
            double topCost = roster.Where(u => u.Cost > 0).Select(u => (double)u.Cost).DefaultIfEmpty(1).Max();
            bool richMode = ownUnitCount >= MaxOwnUnitsOnField && me.Money >= topCost * 3;
            int cap = richMode ? MaxOwnUnitsOnField * 2 : MaxOwnUnitsOnField;

            if (ownUnitCount >= cap) return;

            // A cheap unit's HP means nothing if the enemy's biggest hitter one-shots it --
            // and since every unit in this game is melee (no roster defines a Range, so
            // ArmorType/AttackType always fall back to melee) an attack cleaves ALL
            // defenders in contact simultaneously, so a whole cheap cluster can die to a
            // single swing. Size against the worst hit currently on the field, not the
            // average, since the average is what gets a fragile swarm wiped out.
            float enemyHitDamage = enemyUnits.Count > 0 ? enemyUnits.Max(u => (float)u.Damage) : 0f;

            // Cost-efficiency ratios alone chronically default to the cheapest unit that
            // still scores well, which is exactly the fodder that gets cleaved by a
            // sustained mid/high-tier push. So identify what tier the enemy is actually
            // fielding (weighted by damage contributed, not raw count). Matching that tier
            // exactly still tends to converge on a near-mirror trade though, since the
            // matching tier is usually also the best-value option within "tier >=
            // dominant" -- a fair fight, not a won one. Prefer OUTCLASSING it by one tier
            // when affordable, only settling for an even trade or pure cost-efficiency
            // fallback if we can't afford the tech edge yet.
            int dominantEnemyTier = enemyUnits.Count > 0
                ? enemyUnits.GroupBy(u => u.Tier).OrderByDescending(g => g.Sum(u => (double)u.Damage)).First().Key
                : 0;

            List<(UnitDefinition def, double score)> RankPool(int minTier)
            {
                IEnumerable<UnitDefinition> pool = roster;
                if (minTier > 0)
                    pool = pool.Where(u => u.Tier >= minTier);

                return pool
                    .Select(def => (def, score: richMode ? RawPower(def, enemyHitDamage) : ScoreUnit(def, preferDefense, enemyHitDamage)))
                    .OrderByDescending(x => x.score)
                    .ToList();
            }

            var outclassing = RankPool(dominantEnemyTier + 1);
            var tierMatched = RankPool(dominantEnemyTier);
            var anyAffordable = RankPool(0);

            // Investing is now handled as a higher standing priority in Decide() itself
            // (checked, and returned on, before this is ever reached for non-reactive
            // spending), so it doesn't need its own reserve here anymore. Gadgets are
            // still worth protecting a little: units are cheap enough to always win the
            // "who's affordable first" race against them, which can starve a gadget of the
            // money needed to ever fire even though it's checked earlier the same decision.
            // Only bother once a minimal army (10 units) is out, and only during
            // non-reactive spending -- while actively clearing a wave off the castle
            // (preferDefense), survival comes first and nothing should be held back.
            double reserve = 0;
            if (!preferDefense && ownUnitCount >= 10)
            {
                double gadgetGap = double.MaxValue;
                foreach (var g in new[] { me.OffensiveGadget, me.DefensiveGadget, me.SignatureGadget })
                {
                    if (g == null) continue;
                    bool onCooldown = me.GadgetCooldowns.TryGetValue(g.Id, out var cd) && cd > 0;
                    if (onCooldown) continue;
                    if (g.Cost > me.Money) gadgetGap = Math.Min(gadgetGap, g.Cost - me.Money);
                }
                // Cap at 70% of current money -- replacements still need to keep flowing.
                if (gadgetGap != double.MaxValue) reserve = Math.Min(gadgetGap, me.Money * 0.7);
            }

            LastSpendDebug = $"money={me.Money:F1} reserve={reserve:F1} dominantTier={dominantEnemyTier} anyAffordableCount={anyAffordable.Count(x => x.def.Cost > 0 && x.def.Cost <= me.Money - reserve)} cheapestAny={(anyAffordable.Count > 0 ? anyAffordable.Min(x => x.def.Cost) : -1)}";

            double spendable = me.Money - reserve;
            var matchedPick = tierMatched.FirstOrDefault(x => x.def.Cost > 0 && x.def.Cost <= spendable);
            var outclassPick = outclassing.FirstOrDefault(x => x.def.Cost > 0 && x.def.Cost <= spendable);

            // Only take the tech edge if it's ALSO a competitive value, not just a
            // higher tier -- a naive "always outclass" rule ends up paying a real
            // premium (e.g. 33% more per unit) for a WORSE cost-efficiency pick every
            // single purchase, which starves total army size for no real benefit once
            // the cost-matched option already fields comfortably (see
            // SurvivabilityMultiplier). That compounds into a much smaller army over a
            // whole game, which is exactly what happened testing this against a cheap
            // tier-1 spam bot: it kept passing up 3-cost units for a 4-cost one that
            // scored *worse* per dollar, and lost the production race outright.
            var pick = matchedPick.def != null && outclassPick.def != null && outclassPick.score < matchedPick.score * 0.9
                ? matchedPick
                : (outclassPick.def != null ? outclassPick : matchedPick);
            if (pick.def == null) pick = anyAffordable.FirstOrDefault(x => x.def.Cost > 0 && x.def.Cost <= spendable);
            if (pick.def == null) return;

            if (engine.SpawnUnit(_side, pick.def.Id))
            {
                LastUnitsPurchased++;
                if (pick.def.Tier >= 1 && pick.def.Tier <= 8) ActionCounts[pick.def.Tier]++;
            }
        }

        // Below ~1.5x the enemy's average hit, a unit is one-or-two-shot fodder that
        // never gets to swing back enough to matter -- crush its score rather than
        // excluding it outright (so it's still a fallback if literally nothing survives).
        private static double SurvivabilityMultiplier(UnitDefinition def, float enemyHitDamage)
        {
            if (enemyHitDamage <= 0) return 1.0;
            double effectiveHp = def.MaxHealth + def.MaxShield;
            double hitsSurvived = effectiveHp / enemyHitDamage;

            // Dies to a single hit -- pure cleave fodder, worth almost nothing.
            if (hitsSurvived < 1.0) return 0.05;
            // Survives one hit but dies to essentially any second one (including a
            // stray hit from a DIFFERENT enemy in the same cleave). "Just barely enough
            // HP to pass" is not the same as "actually holds up" -- needs real headroom.
            if (hitsSurvived < 2.5) return 0.2;
            return 1.0;
        }

        private static double ScoreUnit(UnitDefinition def, bool preferDefense, float enemyHitDamage)
        {
            double cost = Math.Max(1, def.Cost);
            double dps = def.Damage * (def.AttackSpeed > 0 ? def.AttackSpeed : 0.3f) * RangeMultiplier(def);
            // Defense leans harder into durability (blocking/trading); offense still wants
            // real HP too -- a pure glass cannon dies before it ever reaches the castle,
            // and a fragile army stops replacing its losses the moment money gets tight.
            double baseScore = preferDefense
                ? (dps * 1.5 + def.MaxHealth + def.MaxShield) / cost
                : (dps * 1.8 + def.MaxHealth * 0.8 + def.MaxShield * 0.8) / cost;
            return baseScore * SurvivabilityMultiplier(def, enemyHitDamage);
        }

        private static double RawPower(UnitDefinition def, float enemyHitDamage)
        {
            double dps = def.Damage * (def.AttackSpeed > 0 ? def.AttackSpeed : 0.3f) * RangeMultiplier(def);
            return (dps + def.MaxHealth + def.MaxShield) * SurvivabilityMultiplier(def, enemyHitDamage);
        }

        // Melee combat oscillates (hit, knock the target back, chase, re-engage), which
        // wastes a lot of a melee unit's uptime. Ranged/Siege/Magic units suffer less from
        // this (they also take half knockback impact themselves) and Ranged can hit flyers,
        // so weight them a bit higher rather than always defaulting to the cheapest melee grunt.
        private static double RangeMultiplier(UnitDefinition def) => def.AttackType switch
        {
            AttackType.Ranged => 1.3,
            AttackType.Magic => 1.25,
            AttackType.Siege => 1.15,
            _ => 1.0,
        };

        private bool IsReady(PlayerState me, GadgetDefinition def)
        {
            if (def == null) return false;
            if (me.GadgetCooldowns.TryGetValue(def.Id, out var cd) && cd > 0) return false;
            return me.Money >= def.Cost;
        }

        // A handful of gadgets (reinforcements, wave, goo) fire unconditionally "on
        // cooldown" with no real urgency behind the cast -- no danger check, no HP
        // check, nothing they're reacting to. That's fine on its own, but these are
        // checked and fired before the repair/invest logic runs each decision, and at
        // least one (reinforcements: $12 cost, 6s cooldown) accumulates almost exactly
        // its own cost per cooldown cycle at the starting $2/s income -- a near-perfect
        // trap that can keep money capped indefinitely below the very first
        // InvestmentPrice ($18), found via a `hunt v4 headstart` trace where a
        // reinforcements-loadout bot never invested once in 40+ seconds, income pinned
        // flat at 2.0 while money oscillated $0-14. Investing compounds and has no
        // downside in this economy (see the comment on the invest check in Decide()),
        // so defer these specific low-urgency gadgets while that first foothold is
        // still being built. Bounded to InvestmentCount < 3 (not unconditional) so this
        // can't stall these gadgets forever once income has scaled -- by investment 3
        // the trap can't reproduce (their cost no longer approximates income*cooldown).
        // Was strict "<", which let deferral lift the exact instant money first reached
        // InvestmentPrice -- precisely when a same-cost gadget competes hardest, not when
        // it's safe to stop deferring. Traced two separate losses (`hunt 3`/`hunt v3`,
        // both `offense=firebomb`, base cost $18 == the first InvestmentPrice exactly)
        // where money hit $18.00 on the nose and firebomb (checked before the invest
        // logic in Decide()) fired and consumed the ENTIRE $18 that same decision, before
        // Invest() ever got a chance to run at a nonzero balance -- investment stayed at
        // 0 for the whole rest of both games. "<=" keeps deferral active through the
        // exact crossover tick, giving the invest check first claim there instead of
        // losing every time to whichever gadget happens to be checked first.
        private bool DeferForInvestment(PlayerState me) => me.InvestmentCount < 3 && me.Money <= me.InvestmentPrice;

        // The offense slot can be any of "nuke" / "firebomb" / "snipe" / "freeze" --
        // the loadout isn't fixed, so all four need real usage logic, not just whichever
        // one happened to be equipped when this was written.
        private void TryUseOffenseGadget(GameEngine engine, PlayerState me, List<Unit> myUnits, List<Unit> enemyUnits, int myCastlePos)
        {
            var def = me.OffensiveGadget;
            if (!IsReady(me, def)) return;
            if (enemyUnits.Count == 0) return; // nothing to hit -- don't burn the cooldown/cost for free

            string family = def.Id.Split('_')[0].ToLowerInvariant();
            int radius = Math.Max(150, def.Radius);
            bool used = false;

            switch (family)
            {
                case "snipe":
                    // No splash, no friendly fire, no self-castle-damage. SnipeEffect
                    // targets whichever enemy is nearest the given position, so aiming at
                    // our own castle makes it snipe whichever enemy is closest to reaching
                    // it -- directly preventing the exact chip damage that ends games,
                    // rather than chasing whoever hits hardest somewhere else on the field.
                    used = engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "freeze":
                    // Hits and freezes EVERY enemy unit on the field regardless of
                    // position -- no friendly fire. Frozen units skip their whole
                    // attack/move step, so they take free hits while stunned. This is what
                    // actually breaks a chokepoint stalemate: a small steady trickle of
                    // defenders can otherwise permanently pin a much bigger army just by
                    // always having *something* in contact range.
                    used = engine.UseGadget(_side, def.Id, 0);
                    break;

                case "nuke":
                {
                    // Always damages BOTH castles by BaseValue/2 and hits ALL units (any
                    // side) in the blast radius -- a real cost, not a free chip. Only
                    // worth it against an actual cluster, and only where none of our own
                    // units would eat the same blast.
                    int? target = FindBestAoeTarget(enemyUnits, radius);
                    if (target.HasValue && enemyUnits.Count >= 2 && !myUnits.Any(u => Math.Abs(u.Position - target.Value) <= radius))
                        used = engine.UseGadget(_side, def.Id, target.Value);
                    break;
                }

                case "firebomb":
                {
                    // Leaves a damage-over-time zone that burns ANYONE standing in it,
                    // ally or enemy (FireHazard doesn't filter by side). Prefer the densest
                    // enemy cluster, but if that overlaps our own units, retarget to
                    // whichever enemy is farthest from our army instead of skipping the
                    // cast outright -- still a valid burn, and this gadget needs real usage
                    // to ever earn enough XP to upgrade past its weak base tier.
                    // Base-tier cost ($18) matches the first InvestmentPrice exactly, and
                    // this fires off a single enemy unit anywhere -- the most permissive
                    // trigger of any offense gadget. Same trap shape as reinforcements/wave/
                    // goo (see DeferForInvestment); defer it while that first foothold is
                    // still being built.
                    if (DeferForInvestment(me)) break;
                    int? target = FindBestAoeTarget(enemyUnits, radius);
                    if (target.HasValue && myUnits.Any(u => Math.Abs(u.Position - target.Value) <= radius))
                    {
                        float myAvgPos = myUnits.Count > 0 ? myUnits.Average(u => u.Position) : myCastlePos;
                        target = (int)enemyUnits.OrderByDescending(u => Math.Abs(u.Position - myAvgPos)).First().Position;
                    }
                    if (target.HasValue)
                        used = engine.UseGadget(_side, def.Id, target.Value);
                    break;
                }
            }
            if (used) ActionCounts[11]++;
        }

        // The defense slot can be any of "heal" / "reinforcements" / "speed" / "wall".
        private void TryUseDefenseGadget(GameEngine engine, PlayerState me, List<Unit> myUnits, int myCastlePos)
        {
            var def = me.DefensiveGadget;
            if (!IsReady(me, def)) return;

            string family = def.Id.Split('_')[0].ToLowerInvariant();
            bool used = false;

            switch (family)
            {
                case "heal":
                {
                    if (myUnits.Count == 0) return; // nothing to heal
                    float avgHpPct = myUnits.Average(u => u.MaxHealth > 0 ? (float)u.CurrentHealth / u.MaxHealth : 1f);
                    if (avgHpPct < 0.85f)
                        used = engine.UseGadget(_side, def.Id, myCastlePos);
                    break;
                }

                case "reinforcements":
                    // Spawns free units (bypasses cost entirely) regardless of position --
                    // pure value with no downside to OUR ARMY, but the cast itself has a
                    // real cost ($12 base) that competes with saving for the first
                    // InvestmentPrice ($18) -- see DeferForInvestment.
                    if (DeferForInvestment(me)) break;
                    used = engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "speed":
                    if (myUnits.Count > 0)
                        used = engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "wall":
                {
                    // Only ONE wall (any level) is ever allowed on the field at a time --
                    // casting again while one is already up just refunds the cost and
                    // grants NO xp, which stalls its upgrade path if we keep trying anyway.
                    // Wait for the existing one to die before recasting.
                    bool alreadyHaveWall = engine._state.Units.Any(u => u.Side == _side && u.DefinitionId.StartsWith("wall"));
                    if (alreadyHaveWall) break;

                    // WallEffect ignores the position for level 1 (fixed spawn point) but
                    // uses it directly for level 2/3, so place it in our own front line
                    // where it can actually tank alongside the rest of the army.
                    int target = myUnits.Count > 0 ? (int)myUnits.Average(u => u.Position) : myCastlePos;
                    used = engine.UseGadget(_side, def.Id, target);
                    break;
                }
            }
            if (used) ActionCounts[12]++;
        }

        private void TryUseSignatureGadget(GameEngine engine, PlayerState me, List<Unit> myUnits, List<Unit> enemyUnits,
            int myCastlePos, bool inDanger, float castleHpPct)
        {
            var def = me.SignatureGadget;
            if (!IsReady(me, def)) return;

            string family = def.Id.Split('_')[0].ToLowerInvariant();
            bool used = false;

            switch (family)
            {
                case "cash":
                    // Pure economy, no downside to the team's own board state -- always take it.
                    used = engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "rage":
                    if (myUnits.Count > 0 && (inDanger || myUnits.Any(u => enemyUnits.Any(e => Math.Abs(e.Position - u.Position) < 250))))
                        used = engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "divine":
                    if (castleHpPct < 0.3f || (inDanger && enemyUnits.Count >= 3))
                        used = engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "wave":
                    // Always fires from our own edge regardless of target position -- but
                    // like reinforcements, the cast itself has a real cost that can compete
                    // with saving for the first InvestmentPrice; see DeferForInvestment.
                    if (DeferForInvestment(me)) break;
                    used = engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "goo":
                    {
                        if (DeferForInvestment(me)) break;
                        int target = myUnits.Count > 0 ? (int)myUnits.Average(u => u.Position) : myCastlePos;
                        used = engine.UseGadget(_side, def.Id, target);
                        break;
                    }

                case "meteor":
                case "poison":
                    {
                        // Both of these only ever affect enemy units -- no friendly fire risk.
                        int? target = FindBestAoeTarget(enemyUnits, Math.Max(150, def.Radius));
                        if (target.HasValue)
                            used = engine.UseGadget(_side, def.Id, target.Value);
                        break;
                    }

                case "blackhole":
                    {
                        // Unlike meteor/poison, a black hole pulls in and damages BOTH sides
                        // in its radius (and can instantly kill non-tier-8 units at its core),
                        // so only fire it where none of our own units would get caught in it.
                        int radius = Math.Max(150, def.Radius);
                        int? target = FindBestAoeTarget(enemyUnits, radius);
                        if (target.HasValue && !myUnits.Any(u => Math.Abs(u.Position - target.Value) <= radius))
                            used = engine.UseGadget(_side, def.Id, target.Value);
                        break;
                    }
            }
            if (used) ActionCounts[13]++;
        }

        // Finds the enemy unit position with the most enemy "power" clustered within radius.
        private int? FindBestAoeTarget(List<Unit> targets, int radius)
        {
            if (targets.Count == 0) return null;

            int bestPos = 0;
            float bestScore = -1f;
            foreach (var candidate in targets)
            {
                float score = 0f;
                foreach (var other in targets)
                {
                    if (Math.Abs(other.Position - candidate.Position) <= radius)
                        score += Power(other);
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPos = (int)candidate.Position;
                }
            }
            return bestPos;
        }
    }
}
