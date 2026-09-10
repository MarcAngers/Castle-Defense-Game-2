using System;
using System.Collections.Generic;
using System.Linq;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Replays
{
    /// <summary>
    /// WHAT THE BOT WAS THINKING AT A PIVOTAL MOMENT -- re-derived from the reconstructed
    /// board, in the shape of HeuristicBot's own gates.
    ///
    /// ── READ THIS BEFORE BELIEVING ANY OF IT ─────────────────────────────────────────────
    ///
    /// **THE BOT'S REASONING IS NOT RECORDED ANYWHERE.** A .replay stores the action byte and
    /// nothing else; no branch id, no score, no gate state. So this class does what
    /// `--replay-why` does: it rebuilds the state at that tick and re-evaluates the
    /// CONDITIONS HeuristicBot's settings test, then reports them.
    ///
    /// Two halves, and they have very different standing:
    ///
    ///   * WHAT IT DID is read off the recorded action stream. That is ground truth.
    ///   * WHY is a re-derivation of the inputs to `Decide()`, not a recording of the branch
    ///     that fired. Where a gate is a pure function of state (InvestmentCount, Income,
    ///     money against a price, an enemy within EnemyIsCloseDistance) the value here is
    ///     exact. Where `Decide()` uses rolling internal state that a rebuild does not have
    ///     -- the HP window behind time-to-death, `_blockCredit`, the decision cadence -- it
    ///     is NOT reproduced and is deliberately absent rather than guessed.
    ///
    /// Anything reported here is therefore "these were the conditions", never "this is the
    /// branch it took". Deriving the latter honestly means instrumenting HeuristicBot to
    /// emit a real trace, which changes the bot and so needs a bot-checksum run behind it.
    ///
    /// Evaluation order of `Decide()` is documented in BOT_MECHANICS.md section 12; the gates
    /// below are named to match it so the two can be read side by side.
    /// </summary>
    public sealed class BotRationale
    {
        private readonly int _side;
        private readonly HeuristicBotSettings _cfg;

        public BotRationale(int side, HeuristicBotSettings cfg)
        {
            _side = side;
            _cfg = cfg ?? new HeuristicBotSettings();
        }

        /// <summary>
        /// The board facts a rationale needs, lifted at a sampled tick. Kept separate from
        /// ReplayAnalysis.Sample because a Sample is serialised to the browser once per
        /// second for the whole game and this is only needed at a handful of instants.
        /// </summary>
        public sealed class Snapshot
        {
            public long Tick;
            public int Second;
            public double Money, Income, InvestmentPrice, RepairPrice;
            public int InvestmentCount, RepairCount, AutoSpawnLevel;
            public double AutoSpawnPrice;
            public bool ArmageddonUsed;
            public int CastleHealth, CastleMaxHealth;
            public int EnemyUnitsNear, EnemyUnitsTotal, OwnUnitsTotal;
            public double EnemyValueNear;
            public int EnemyToughestHp;
            public string EnemyToughestId = "-";
            public double FrontmostEnemyDistance = -1;
            public int AffordableTiers, ChargedTiers, AffordableAndChargedTiers;
            public string CheapestAffordable = "-";
        }

        public sealed class Gate
        {
            public string Name;
            public bool Open;
            public string Detail;
            /// <summary>Where the rule lives, so the number can be checked against the code.</summary>
            public string Source;
        }

        public sealed class Rationale
        {
            /// <summary>One sentence, plain English. The overview.</summary>
            public string Summary;
            /// <summary>What the bot actually did in the window. Ground truth.</summary>
            public string Did;
            public List<Gate> Gates = new();
            /// <summary>Flagged when the bot was sitting on a win condition it cannot buy.</summary>
            public string Note;
        }

        public Snapshot Capture(GameState state, GameEngine engine)
        {
            var me = _side == 1 ? state.Player1 : state.Player2;
            int myCastle = _side == 1 ? 200 : GameEngine.MAP_WIDTH - 200;

            var s = new Snapshot
            {
                Tick = state.CurrentTick,
                Money = me.Money,
                Income = me.Income,
                InvestmentPrice = me.InvestmentPrice,
                RepairPrice = me.RepairPrice,
                InvestmentCount = me.InvestmentCount,
                RepairCount = me.RepairCount,
                AutoSpawnLevel = me.AutoSpawnLevel,
                AutoSpawnPrice = me.AutoSpawnPrice,
                ArmageddonUsed = me.ArmageddonUsed,
                CastleHealth = me.CastleHealth,
                CastleMaxHealth = me.CastleMaxHealth
            };

            foreach (var u in state.Units)
            {
                if (u.Side == _side) { s.OwnUnitsTotal++; continue; }
                if (u.Side is not (1 or 2)) continue;
                s.EnemyUnitsTotal++;
                double dist = Math.Abs(u.Position - myCastle);
                if (s.FrontmostEnemyDistance < 0 || dist < s.FrontmostEnemyDistance)
                    s.FrontmostEnemyDistance = dist;
                if (dist > _cfg.EnemyIsCloseDistance) continue;
                s.EnemyUnitsNear++;
                s.EnemyValueNear += Gadgets.GadgetTargeting.UnitCost(engine, u);
                if (u.CurrentHealth > s.EnemyToughestHp)
                {
                    s.EnemyToughestHp = u.CurrentHealth;
                    s.EnemyToughestId = u.DefinitionId;
                }
            }

            // WHAT COULD IT ACTUALLY BUY. Charges are the half a money-only reading misses:
            // since 2026-09-01 a tier with no charge left is unbuyable at any price, and
            // SpendOnUnits tests `def.Cost <= spendable` and nothing else, so a bot with
            // money and no charges looks idle for a reason that is invisible in the economy.
            var team = GameDataManager.Teams.Find(t => t.Color == me.Team);
            double cheapest = double.MaxValue;
            if (team != null)
            {
                foreach (var def in team.Roster.Where(d => d.Cost > 0))
                {
                    bool affordable = me.Money >= def.Cost;
                    bool charged = me.GetUnitCharges(def.Id) > 0;
                    if (affordable) s.AffordableTiers++;
                    if (charged) s.ChargedTiers++;
                    if (affordable && charged)
                    {
                        s.AffordableAndChargedTiers++;
                        if (def.Cost < cheapest) { cheapest = def.Cost; s.CheapestAffordable = def.Id; }
                    }
                }
            }
            return s;
        }

        public Rationale Explain(Snapshot s, ReplayAnalysis.Highlight h)
        {
            var r = new Rationale();
            var acts = _side == 1 ? h.P1Actions : h.P2Actions;
            r.Did = acts.Count == 0
                ? "nothing — no action was recorded in this window"
                : string.Join(", ", acts);

            double hpPct = s.CastleMaxHealth > 0 ? 100.0 * s.CastleHealth / s.CastleMaxHealth : 0;
            bool atArmageddonRung = s.InvestmentCount >= PlayerState.ArmageddonInvestmentCount;
            bool canAffordInvest = s.Money >= s.InvestmentPrice;

            r.Gates.Add(new Gate
            {
                Name = "enemyIsClose",
                Open = s.EnemyUnitsNear > 0,
                Detail = s.EnemyUnitsNear > 0
                    ? $"{s.EnemyUnitsNear} enemy unit(s) within {_cfg.EnemyIsCloseDistance:N0} of the castle, ${s.EnemyValueNear:N0} of value; toughest {s.EnemyToughestId} at {s.EnemyToughestHp:N0} HP"
                    : s.FrontmostEnemyDistance < 0
                        ? "no enemy units on the field"
                        : $"nearest enemy is {s.FrontmostEnemyDistance:N0} away, outside the {_cfg.EnemyIsCloseDistance:N0} threshold",
                Source = "HeuristicBotSettings.EnemyIsCloseDistance"
            });

            r.Gates.Add(new Gate
            {
                Name = "attack gate: investments",
                Open = s.InvestmentCount >= _cfg.AttackGateMinInvestment,
                Detail = $"{s.InvestmentCount} investments against a minimum of {_cfg.AttackGateMinInvestment}",
                Source = "HeuristicBotSettings.AttackGateMinInvestment"
            });

            r.Gates.Add(new Gate
            {
                Name = "attack gate: income",
                Open = s.Income >= 50,
                Detail = $"${s.Income:N0}/s against the $50/s floor",
                Source = "HeuristicBot.Decide step 17"
            });

            r.Gates.Add(new Gate
            {
                Name = "can buy the next invest rung",
                Open = canAffordInvest && !atArmageddonRung,
                Detail = atArmageddonRung
                    ? $"at the top rung ({PlayerState.ArmageddonInvestmentCount}); the invest button is now ARMAGEDDON at ${s.InvestmentPrice:N0}"
                    : $"${s.Money:N0} in hand against a ${s.InvestmentPrice:N0} rung",
                Source = "PlayerState.ArmageddonInvestmentCount"
            });

            r.Gates.Add(new Gate
            {
                Name = "has something to spend on",
                Open = s.AffordableAndChargedTiers > 0,
                Detail = s.AffordableAndChargedTiers > 0
                    ? $"{s.AffordableAndChargedTiers} of {s.AffordableTiers} affordable tiers still hold a charge (cheapest {s.CheapestAffordable})"
                    : s.AffordableTiers == 0
                        ? "cannot afford any unit"
                        : $"{s.AffordableTiers} tiers affordable but NONE hold a charge — every purchase would be refused",
                Source = "PlayerState.UnitCharges, SpendOnUnits"
            });

            r.Gates.Add(new Gate
            {
                Name = "castle",
                Open = hpPct > (double)_cfg.RepairHpFloorPct,
                Detail = $"{hpPct:F1}% health, {s.RepairCount} repairs bought, next costs ${s.RepairPrice:N0}",
                Source = "HeuristicBotSettings.RepairHpFloorPct"
            });

            // ── the summary ──────────────────────────────────────────────────────────────
            // Each clause is written to stand on its own, joined with semicolons -- an
            // earlier version prefixed the first with "It was", which read as "It was no
            // enemy near its castle".
            var why = new List<string>();
            if (s.EnemyUnitsNear > 0)
                why.Add($"It was under pressure — ${s.EnemyValueNear:N0} of enemy value inside its own {_cfg.EnemyIsCloseDistance:N0} threshold");
            else if (s.EnemyUnitsTotal == 0)
                why.Add("The field was empty");
            else
                why.Add($"Nothing was near its castle (nearest enemy {s.FrontmostEnemyDistance:N0} away), so the reactive path was closed");

            if (s.InvestmentCount < _cfg.AttackGateMinInvestment)
                why.Add($"it was below the attack gate at {s.InvestmentCount} of {_cfg.AttackGateMinInvestment} investments, so it was building economy rather than pushing");
            else if (atArmageddonRung)
                why.Add("the economy ladder was finished, with no rung left to buy");
            else if (canAffordInvest)
                why.Add($"it could afford the next rung at ${s.InvestmentPrice:N0}, and the invest claim is evaluated before any spending on units");
            else
                why.Add($"it was saving toward the next rung at ${s.InvestmentPrice:N0}");

            if (s.AffordableAndChargedTiers == 0 && s.AffordableTiers > 0)
                why.Add("and every affordable unit was out of charges, so a spawn would have silently failed");

            string leader = h.Leader == _side ? "It took the economy lead here" : "It lost the economy lead here";
            r.Summary = $"{leader}. {string.Join("; ", why)}.";

            // THE ONE THING WORTH SHOUTING ABOUT. HeuristicBot has no rung for ARMAGEDDON --
            // action 9 at the cap is a win condition and the bot treats the invest claim as
            // an economy upgrade -- so a bot sitting on the price is sitting on the game.
            if (atArmageddonRung && !s.ArmageddonUsed)
            {
                r.Note = s.Money >= s.InvestmentPrice
                    ? $"ARMAGEDDON was AFFORDABLE at this moment (${s.Money:N0} in hand against ${s.InvestmentPrice:N0}) and was not bought."
                    : $"ARMAGEDDON is the only purchase left on this ladder and costs ${s.InvestmentPrice:N0}; the bot held ${s.Money:N0}, {(s.InvestmentPrice - s.Money) / Math.Max(1, s.Income):F0}s of income short.";
            }
            return r;
        }
    }
}
