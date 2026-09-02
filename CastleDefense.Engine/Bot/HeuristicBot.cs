using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Models.Hazards;
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
        // Required castle-HP headroom over a nuke's OWN-castle damage before the bot will
        // cast it. Not flag-gated and on by default: casting a gadget that kills you is
        // never a strategy trade-off, it is a bug, and it was confirmed happening in live
        // replays. See the suicide guard in TryUseOffenseGadget's nuke case.
        //
        // MEASURED FREE (seed 12345, 250 setups x 2 sides x 2 modes). NukeGuardOff sets this
        // to 0, which makes the check trivially true and reproduces the pre-fix bot exactly:
        //   Tier4Spam ns   74.6 (guard) vs 74.6 (off)   -- identical
        //   Tier1Spam / Investor / BalancedHuman        -- identical on every cell
        //   mirror ns      48.8 (guard) vs 46.4 (off)   -- guard is BETTER
        //   mirror hs      46.0 (guard) vs 44.6 (off)   -- guard is BETTER
        // So not casting the suicide nuke costs nothing offensively and wins the mirror
        // outright. A 1.2 margin scored the same as 2.0, so the exact value does not matter
        // much; 2.0 is kept because the blast lands def.Delay (48 ticks) later, during which
        // the enemy keeps hitting us -- surviving it at CAST time is not the question.
        // (21) CASTLE-CLAMPED LEAD -- Marc's live-play catch, 2026-07-31. Gadget targeting
        // leads each unit by its speed over the gadget's deployment Delay, but a unit CANNOT
        // walk past the castle it is attacking -- it stops on contact and starts hitting it.
        // Against a fast wave already near our castle the lead therefore put the aim point
        // BEHIND our own castle, so the units crashed into it and dealt damage while the
        // gadget landed on empty ground. His words: "there's never any enemy units back
        // there, so it's never a good idea to do that."
        //
        // The error is large, not marginal: meteor's Delay is 70 ticks, so a unit at speed 10
        // is projected 700px past where it can actually be -- a side-2 unit at position 250
        // aims at -450 when the castle is at 200.
        //
        // Clamped inside ProjectedPosition rather than on the final target so it also fixes
        // FindBestAoeTarget's CLUSTER SCORING, which compares projected positions to each
        // other. On by default: aiming at ground no unit can occupy is a bug, not a
        // trade-off. The flag exists only so the fix is measurable against its own absence.
        //
        // ALSO CLAMPS TO WALLS (Marc's follow-up: "we should make the same change for an
        // allied wall, that will stop units as well"). Any unit in contact has CurrentSpeed
        // zeroed by MoveAndFight, and a wall is a stationary persistent blocker in front of
        // the castle, so leading a unit through one is the same error further out.
        //
        // MEASURED FLAT and kept anyway. With `--offense nuke` pinned for full exposure,
        // 250 setups x 2 sides x 2 modes: Tier1Spam 100.0/100.0, Tier4Spam 82.4/82.4,
        // Investor 99.2/99.2, BalancedHuman 100.0/100.0, mirror 48.8 vs 48.0; headstart
        // likewise within 0.2 everywhere. The reason it cannot show up is structural:
        // ProjectedPosition returns early when CurrentSpeed <= 0, and GameEngine zeroes
        // speed for any unit in combat -- so units already grinding at the castle were never
        // mis-projected. The bug only bites units still MOVING FAST at the castle, which is
        // the case Marc reported from live play and which no ladder opponent reliably
        // produces. Same standing as the nuke suicide guard: no regression, obviously
        // correct, kept on live-play evidence rather than ladder evidence.
        public bool ClampProjectionToCastle { get; init; } = true;

        public float NukeSelfDamageMargin { get; init; } = 2.0f;

        // INCOMING-NUKE REPAIR (Marc's report, 2026-08-20). The margin above stops the bot
        // killing itself with its OWN nuke. Nothing looked at the ENEMY'S -- and a nuke
        // damages BOTH castles equally, so the exact blast that the suicide guard refuses
        // to self-inflict is one the opponent can hand us for free by casting it themselves.
        //
        // The bot's whole danger model is a DAMAGE-RATE model: time-to-death is HP divided
        // by an observed or projected drain from units in contact. A nuke is not a rate. It
        // is a single instantaneous 100 / 1500 / 12000 hit that appears in no drain estimate
        // at all, so a castle sitting at 8,000 HP with no enemy unit near it reads as
        // perfectly safe right up until a nuke_3 deletes it. Marc's framing: the bot should
        // survive it "even if it isn't in any danger otherwise".
        //
        // What makes it answerable is the 48-tick (~1.6s) delay between cast and detonation,
        // which is ~10 decisions at this bot's cadence. Repair is the only thing that heals
        // a castle (heal/goo are units-only), and one step is enormous -- CastleMaxHealth is
        // 1000 + 11000*RepairCount and the refill is +20% of the NEW max -- so a single
        // affordable repair clears most blasts outright.
        //
        // Reads the queued blast rather than assuming a level: GameEngine.IncomingCastleDamage
        // sums the actual pending detonations, so it gets the level right by construction,
        // covers several nukes in the air at once (ARMAGEDDON fires nuke_3 on a repeating
        // schedule), and covers our own in-flight nuke as well as theirs.
        //
        // CONFIRMED MECHANICALLY FIRST, before any ladder run: `--nuke-defence-check` puts
        // the bot on an EMPTY board (no units, so every rate-based danger signal reads safe)
        // below the blast already in flight. Without this flag it dies to nuke_2 and nuke_3
        // outright, having spent the window investing; with it, it buys 1 and 2 repairs and
        // survives on 9,299 and 23,060 HP. Level 1 is a genuine no-op -- a 100 blast is only
        // lethal under 100 HP, which RepairHpThreshold already covers.
        //
        // MEASURED ON THE LADDER, seed 12345, 250 setups x 2 sides, vs IncomingNukeRepairOff
        // paired inside one run. THE ONLY RUNG THAT MOVES IS THE MIRROR, which is the only
        // rung whose opponent casts nukes at all -- DoNothing, Tier1Spam, Tier4Spam,
        // Investor, BalancedHuman and HumanClone come back BYTE-IDENTICAL (same wins, same
        // avg_ticks to the tick) in both modes, pinned and unpinned:
        //
        //                        mirror ns      mirror hs
        //   --offense nuke     49.0 vs 35.2   48.8 vs 39.8      (full exposure)
        //   unpinned           51.8 vs 44.8   51.2 vs 47.6      (nuke = 1 of 4 draws)
        //
        // +13.8 / +9.0 points head-to-head at full exposure, i.e. ~+104 / +68 Elo, diluting
        // to +7.0 / +3.6 when the gadget is only drawn a quarter of the time. Sharp and
        // one-sided in exactly the place the mechanism predicts, which is the strongest form
        // this evidence can take -- and note that the DEPLOYED singleplayer configuration
        // pins the bot to White/nuke/*, so it sits at the pinned end of that range.
        public bool IncomingNukeRepair { get; init; } = true;

        // Headroom over the incoming blast, same reasoning as NukeSelfDamageMargin: the
        // detonation lands ~1.6s later and ordinary unit damage keeps landing in between,
        // so "survives it exactly, right now" is not the question being asked.
        public float IncomingNukeSurvivalMargin { get; init; } = 1.25f;

        // (15) BOUNDED ATTACK WAVE -- TESTED AND REJECTED, kept at 0 (disabled). The
        // DIAGNOSIS below is correct and was confirmed by trace; it is this particular
        // remedy that fails. Marc's other suggestion, the delayed gate (16), is what works.
        //
        // Seed 12345, 250 setups x 2 sides x 2 modes, vs the committed reference:
        //            ref    Wave50  Wave100
        //   mir ns   50.6    47.4    45.6
        //   mir hs   45.0    41.2    40.6
        //   T1  hs   98.4    95.4    96.2
        //   T4  hs   75.6    71.0    71.8
        //   Bal hs   93.4    89.8    91.4
        //   Inv ns  100.0    97.8    99.2
        // Negative essentially everywhere it has any effect at all.
        //
        // TWO THINGS WORTH KEEPING FROM THE NULL. First, nostart Tier4Spam and Tier1Spam
        // came back BYTE-IDENTICAL across ref/Wave50/Wave100 (79.2 and 98.2) -- against
        // those opponents the bot is nearly always inDanger, so the non-reactive branch
        // barely runs and a cap on it cannot bind. The cap only touches games the bot is
        // winning comfortably. Second, where it DOES bind it loses: the capped bot stops
        // attacking while its opponent keeps producing, so it hands over the field. Earned
        // invests did rise (mirror 5.26 -> 5.60), i.e. the mechanism worked exactly as
        // designed -- it just turns out that trading tempo for savings at this point in the
        // game is the wrong trade, which is the same lesson IncomeAdvantageAttack taught.
        //
        // Original diagnosis, confirmed and still accurate:
        // Marc's live-play report, 2026-07-31. He identified the
        // trigger from the outside before seeing the code, and he was exactly right: "there's
        // a flag that flips to True around investment 4 or 5 that causes this stream."
        //
        // It is `me.Income >= 50`, the gate on the non-reactive attack branch in Decide().
        // Income is 19.7 at investment 4 and 59.877 at investment 5, so the gate opens at
        // exactly 5 -- and once open it NEVER CLOSES. Spending is rate-limited but has no
        // TOTAL budget, so the bot streams units forever. Traced (mirror-fixed White): units
        // 0 at sec 74, 16 by sec 77, 49 by sec 83, then 30-45 sustained indefinitely.
        //
        // Marc's framing of why that is wrong is the important part: he LIKES the aggression
        // and it puts him on the back foot -- it just costs slightly more than the bot can
        // afford, so he out-economies it from investment 5 onward. The fix is a ceiling on
        // the wave, not removing the wave.
        //
        // Cap cumulative non-reactive spend BETWEEN INVESTMENTS at this fraction of the next
        // InvestmentPrice, then stop attacking and save until the investment lands, which
        // resets the budget. Expressed against InvestmentPrice rather than as an absolute so
        // it scales with the economy automatically, and because it makes the guarantee legible:
        // at fraction f the bot spends f x price on the wave and needs (1+f) x price of income
        // to complete the cycle, so the next investment is always reachable by construction.
        // 0 disables the cap entirely (committed behaviour).
        public double AttackBudgetPerInvestment { get; init; } = 0;

        // (16) DELAYED ATTACK GATE -- KEPT, default 6 as of 2026-07-31. Marc's third
        // suggestion, and the strongest single change of that session: "delay the attack for
        // 1 more investment. If the bot was 1 investment level higher it would have the
        // funds to afford this type of stream."
        //
        // He identified the trigger from live play WITHOUT seeing the code -- "there's a flag
        // that flips to True around investment 4 or 5 that causes this stream" -- and it is
        // exactly `me.Income >= 50` in Decide(), since income is 19.7 at investment 4 and
        // 59.877 at 5. Traced: units 0 at sec 74, 16 by sec 77, 49 by sec 83, then 30-45
        // sustained forever, because the gate has a RATE limit but no total budget and never
        // closes once open.
        //
        // MEASURED, two seeds x 250 setups x 2 sides x 2 modes, vs the committed reference:
        //   mirror ns   50.6 -> 58.6 (+8.0)  |  48.4 -> 62.4 (+14.0)
        //   mirror hs   45.0 -> 54.4 (+9.4)  |  52.2 -> 57.2 (+5.0)
        //   Investor ns 100.0 -> 88.0        |  100.0 -> 88.4        <- the cost
        //   Investor hs  85.2 -> 79.2        |   84.0 -> 81.0
        //   Tier4Spam ns  79.2 -> 79.2       |   82.6 -> 82.6        <- unchanged
        //   Tier1/Bal hs  -1.4 to -2.8       |  -1.4 to -2.6
        //   EARNED INVESTS UP ON EVERY RUNG, BOTH SEEDS (+0.5 to +0.7)
        //
        // KEEP ReactiveFlowCap ON ALONGSIDE THIS -- they are complements, not substitutes,
        // and a 2x2 was run specifically to check. Averaged over both seeds:
        //                 mirror ns  mirror hs  Tier4Spam ns
        //   gate + cap        +11.0       +7.2          0.0
        //   gate, no cap      +13.1      +10.6         -8.0
        // Dropping the cap buys ~2 mirror points and costs ~8 on Tier4Spam. The mechanism is
        // symmetric: the GATE delays offence, which builds economy but concedes ground to a
        // relentless attacker; the CAP bounds defence, which is what recovers that ground.
        // Each covers the other's weakness. (An earlier hypothesis that the Investor loss was
        // an INTERACTION between the two was tested by that same 2x2 and refuted -- gate-only
        // still loses Investor 88.8, cap-only keeps it at 100.0. The Investor cost belongs to
        // the gate alone: not attacking until investment 6 gives an opponent that scales its
        // own economy a free hand early.)
        //
        // 0 disables, restoring the committed pre-2026-07-31 gate (Income >= 50 alone).
        public int AttackGateMinInvestment { get; init; } = 6;

        // MEASUREMENT ONLY -- never set this on a shipping bot. Blocks TryUseDefenseGadget
        // entirely, so the defence slot is carried but never cast.
        //
        // WHY IT EXISTS (2026-08-11). The capability map found speed defence is the bot's
        // single largest deficit (23.7% against ~56.4% for the other three defences), and
        // the suspected cause is that TryUseDefenseGadget's "speed" branch fires on
        // cooldown -- it checks only that a non-wall unit exists and the money is there,
        // the weakest condition of the four. That diagnosis predicts something testable:
        // if casting speed badly is WORSE than not casting it at all, this switch should
        // RAISE the win rate. If it changes nothing, the cast is not what is costing and
        // a speed macro has less headroom than it looks.
        public bool DisableDefenseGadget { get; init; } = false;

        // MEASUREMENT ONLY, and the SPECIFICITY CONTROL for the setting above. If
        // suppressing the OFFENCE gadget buys about the same as suppressing the defence
        // gadget, then the gain is "one less thing to spend money on" rather than anything
        // about defensive play.
        public bool DisableOffenseGadget { get; init; } = false;

        // -- (21) GADGET DOCTRINE, 2026-08-12 (Marc's play notes) --------------------
        // Audit found 6 of 16 gadget branches fire on cooldown -- gated only by
        // BigSpendJustified, which is `cost < 0.8*money || income >= 50`, i.e. pure
        // AFFORDABILITY with no usefulness test, and permanently true from investment 5.
        // These flags replace those gates with the doctrine Marc actually plays.
        // All default OFF so the committed bot stays byte-identical until each is measured.

        // "My units are sieging the enemy castle." Deliberately requires TWO units, not
        // one -- Marc's explicit ask, so the flag cannot flip on a single straggler and
        // every gadget keyed to it is guaranteed a real target.
        public int SiegeMinUnits { get; init; } = 2;

        // (22) AOE FRIENDLY-FIRE TRADE. nuke/firebomb/blackhole damage ALLIES in the blast
        // as well as enemies. The committed rule is "never cast if any ally is in radius",
        // which is safe but refuses every good trade. Marc's rule is a comparison: cast
        // when the enemy army caught is worth more than ours. The canonical good case is a
        // defensive fight at OUR castle against a stream -- drop it at THEIR end, where the
        // stream is dense and our units cannot reach in time.
        public bool AoeTradeRule { get; init; } = false;
        public double AoeTradeMargin { get; init; } = 1.0;   // enemyValue >= margin * allyValue

        // (23) DIVINE SHIELDS UNITS, NOT THE CASTLE. The committed trigger is castle HP and
        // inDanger, which is simply the wrong quantity -- DivineEffect shields UNITS. The
        // correct trigger is "we have an army worth protecting and can afford it".
        public bool DivineShieldsUnits { get; init; } = false;

        // (24) RAGE ON SIEGE. A damage buff is at its best when units are already hitting
        // the enemy castle, which the committed proximity trigger does not cover.
        public bool RageOnSiege { get; init; } = false;

        // (25) BLACKHOLE BUY-TIME. It already has freeze's earlyStall; it is missing
        // freeze's buyTime (inDanger, within the reactive budget). Same gadget role.
        public bool BlackholeBuyTime { get; init; } = false;

        // (26) SIEGE PRE-CAST for poison/meteor/goo. These have a deployment Delay, so
        // casting at the enemy castle while sieging pays even with NO enemy units on the
        // field: the defender's answer spawns into the effect. Marc: "worst case you waste
        // the gadget, but your units are getting free castle damage while this happens."
        public bool SiegePreCast { get; init; } = false;

        // (27) UPGRADE SPAM. Gadget tiers are bought with USES (100 XP per cast,
        // UpgradeCost per tier), so deliberately spamming a cheap gadget once income makes
        // its cost irrelevant is a real strategy -- Marc plays it. The income-drain cap in
        // TryCast already produces this as a SIDE EFFECT: a gadget becomes castable every
        // cooldown once income >= cost/(cooldown*k), and at the shipped k=0.30 that is
        // exactly $20/s for speed, the number Marc gave independently. But as a rate
        // limiter it has two defects -- it does not know what an upgrade IS, so it keeps
        // spamming a MAXED gadget whose XP is discarded, and it conflates tactical casting
        // with XP farming. This makes the intent explicit.
        //
        // TWO k values only, per Marc: one for L1->L2 and one for L2->L3. It is acceptable
        // for an expensive L2 (speed_2 needs income 300 at k=0.30) to wait until
        // investment 7.
        public bool GadgetUpgradeSpam { get; init; } = false;
        public double UpgradeSpamK1 { get; init; } = 0.30;   // level 1 -> 2
        public double UpgradeSpamK2 { get; init; } = 0.30;   // level 2 -> 3
        // Never cast two gadgets within this many seconds of each other while farming XP,
        // so the three slots are never all on cooldown together -- Marc: "you always have
        // 1 gadget up to defend yourself if the enemy decides to launch a big attack."
        public double UpgradeSpamStaggerSeconds { get; init; } = 3.0;
        // Defer XP farming while committed to the next investment. The save-invest macro is
        // the search bot's single largest source of strength and must not be competed with;
        // inside HeuristicBot the analogue is being this far toward the next price.
        public double UpgradeSpamInvestCommitFraction { get; init; } = 0.60;

        // (14) REACTIVE FLOW CAP -- full argument at the implementation site in
        // SpendOnUnits. Bounds DEFENSIVE spending by the income rate, which nothing
        // currently does; the existing flow cap only governs the !inDanger attack branch,
        // so it stops binding entirely against an opponent who applies constant pressure.
        // KEPT -- default as of 2026-07-31, at fraction 0.60. Two seeds, 250 setups x 2
        // sides x 2 modes:
        //   Tier4Spam ns  74.6 -> 79.2 (+4.6)  |  71.2 -> 82.6 (+11.4)
        //   Tier4Spam hs  72.4 -> 75.6 (+3.2)  |  76.6 -> 82.0 (+5.4)
        //   Tier1Spam     -0.6 / 0.0           |  0.0 / +0.2
        //   Investor/Bal  flat to +1.0         |  flat to +1.0
        //   mirror        -0.2 / -2.4          |  -2.6 / -3.4        <- the cost
        //   EARNED INVESTS vs Tier4Spam  3.96 -> 4.27                <- the mechanism
        // At 0.40 the Tier4Spam gain is larger still (+10.6 / +18.4) but it starts costing
        // Tier1Spam (-1.6 / -3.2) and more mirror; 0.60 takes most of the gain for none of
        // the Tier1Spam cost.
        //
        // WHY TIER4SPAM IS THE RUNG TO TRUST HERE, despite the mirror being this project's
        // usual primary instrument: Tier4Spam is the ONLY baseline that applies sustained
        // pressure, so it is the only one that reproduces the state this fixes -- the bot
        // pinned in inDanger, where the attack-side flow cap does not apply and defensive
        // spending is unbounded. The mirror is a symmetric-stall artifact: both sides boom,
        // the pressure state rarely arises, and the capped side just spends less on the
        // defence it does need. Marc's live games are the tiebreaker and they show the
        // pressure state, not the boom one -- 8 of 10 losses ended at exactly investment 5.
        //
        // NoReactiveFlowCap is the regression guard.
        public bool ReactiveFlowCap { get; init; } = true;
        // Share of income defence may consume. The complement is guaranteed savings growth,
        // which is the whole point -- at 0.6 the bot banks 40% of income even while under
        // sustained attack.
        // LOWERED 0.6 -> 0.3, 2026-08-21, Marc's call after the 3225A7 breakdown: the bot
        // allocated 74% of its money to military against his 30%, and the complement of this
        // fraction is exactly the guaranteed savings growth. At 0.3 it banks 70% of income
        // even under sustained attack, which is the split his winning line actually uses.
        // Repair75/DrainProximity-style regression profiles for the old value are below.
        public double ReactiveSpendFractionOfIncome { get; init; } = 0.3;
        // How many seconds of that rate may bank while quiet, so a real wave can still be
        // answered with a burst rather than a trickle.
        public double ReactiveAllowanceCapSeconds { get; init; } = 12.0;

        public float SafetyMarginMultiplier { get; init; } = 1.4f;
        public float SafetyBufferSeconds { get; init; } = 2f;
        public float EnemyIsCloseDistance { get; init; } = 700f;
        // ── CHANGED 0.75 -> 0.60, 2026-08-20, Marc's call. ──────────────────────────
        //
        // MEASURED NEUTRAL, ADOPTED FOR A REASON THE LADDER CANNOT SEE. ladder 200
        // (400 games/rung, seed 4242, paired in one run) put 0.60 at 88.39% nostart
        // (p=0.740 vs the 0.75 control's 88.11%) and 84.11% headstart (p=0.942 vs 84.18%),
        // with Tier4Spam byte-identical at 83.0% and earned invests unmoved. It is as close
        // to a pure null as this harness produces.
        //
        // The reason to take it anyway is the ROLLOUT, not the ladder. HeuristicBot is
        // RolloutSearchBot's opponent model, and --defender-trace on 7A385A tick 781 showed
        // the simulated defender answering a single incoming unit by repairing the moment
        // its castle dipped under 0.75 while nothing threatened it -- then spending ~$400 on
        // gadgets across the window. Marc plays through that chip damage and keeps saving.
        // A defender that reaches for the repair less readily is a closer model of the human
        // search actually has to beat, and this change buys that at no measured cost.
        //
        // DO NOT LOWER IT FURTHER WITHOUT RE-MEASURING. 0.50 was tested in the same run and
        // is clearly NEGATIVE: Tier4Spam collapses 83.0% -> 69.0% with earned invests
        // 4.57 -> 4.13. Sustained chip damage is exactly where the threshold is load-bearing,
        // and 0.50 leaves the bot sitting in the 50-75% band without repairing. The window
        // between 0.60 and 0.75 is free; below 0.60 it is not.
        //
        // BLAST RADIUS, stated because this is a DEFAULT and not a profile: it moves
        // singleplayer's bot (search's prior), every ladder rung's HeuristicBot opponent,
        // search's rollout policy for BOTH sides, and the reference the counter table was
        // fitted against. Any HeuristicBot number recorded before 2026-08-20 is now measured
        // against a slightly different agent. The 0.75 behaviour is preserved as the
        // Repair75 profile so the old configuration stays reproducible.
        public float RepairHpThreshold { get; init; } = 0.60f;

        /// <summary>
        /// Repair when time-to-death drops below this many seconds, and never otherwise.
        /// 0 restores the pre-2026-08-21 rule (RepairHpThreshold || inDanger). See the block
        /// in Decide() for why the old gate leaked.
        /// </summary>
        // ── MEASURED 2026-08-21: THE TIMING CHANGE IS NOT THE WIN. ─────────────────
        // ladder 200 (400 games/rung), seed 4242, both modes, paired:
        //
        //   threshold      nostart   headstart   Tier4Spam ns/hs   p vs legacy (ns/hs)
        //   legacy          91.82%     89.57%      89.2% / 94.0%          -
        //   ttd 8s          92.32%     89.25%      92.0% / 94.0%     0.49 / 0.70
        //   ttd 5.5s        90.68%     88.50%      83.8% / 88.5%     0.13 / 0.20
        //   ttd 3s          87.43%     87.29%      72.2% / 85.8%     7e-08 / 0.0075
        //
        // Monotone in the threshold, and the damage is concentrated almost entirely in
        // Tier4Spam -- sustained chip damage, the one matchup where the HP buffer is
        // load-bearing. This replicates the RepairHpThreshold 0.50 result exactly: tighten
        // the repair trigger and Tier4Spam collapses while everything else barely moves.
        // The bot has to repair BEFORE it is seconds from death, because the buffer is what
        // lets it survive the next minute, not the next second.
        //
        // THE DEEPER POINT, and the reason none of these thresholds is the answer: a TTD gate
        // changes WHEN the bot repairs, not WHAT PRICE it will pay. The 7th repair on 3225A7
        // cost $8,837 and was taken under real pressure -- a TTD gate would have permitted it
        // too. The problem measured there is PRICE-BLINDNESS: repair is still the only
        // inDanger-authorised purchase in this file with no cost test at all, while every
        // gadget path checks def.Cost against reactiveSpendBudget or BigSpendJustified.
        //
        // ── RE-MEASURED 2026-08-21 AFTER UnifiedTimeToDeath SHIPPED. 5.5s CONFIRMED. ──
        //
        // THE FIRST SWEEP WAS MEASURING THE ESTIMATOR, NOT THE THRESHOLD. It ran against the
        // contact-only TTD, which --repair-audit showed reading INFINITY at six of seven
        // repairs in 3225A7 -- so 3s/5.5s/8s all refused essentially every repair and the
        // "tighter is worse" trend was really "the input is blind". With the unified
        // estimator the same repairs read 8.9s to 422s.
        //
        // Re-swept, ladder 200, seed 4242, paired:
        //
        //   arm        nostart  headstart  Tier4Spam ns/hs  mirror ns  p vs legacy (ns/hs)
        //   ttd 5.5     90.96%    89.04%    86.2% / 88.8%     53.5%      0.24 / 0.38
        //   legacy      90.04%    88.29%    90.2% / 94.2%     40.5%          -
        //   ttd 15      90.21%    88.82%    92.5% / 94.8%     40.2%      0.82 / 0.53
        //   ttd 25      89.14%    87.68%    92.5% / 94.8%     32.5%      0.27 / 0.49
        //   ttd 45      88.50%    87.00%    92.8% / 94.5%     29.2%      0.06 / 0.14
        //
        // 5.5s is the best aggregate arm and now BEATS legacy, where against the old
        // estimator it lost to it. Same threshold, opposite verdict, because the input
        // changed. The mirror rung is a direct head-to-head (its opponent is the default
        // bot), and repairing less wins it monotonically: 53.5 / 40.5 / 40.2 / 32.5 / 29.2.
        //
        // A PREDICTION OF ~25s, EXTRAPOLATED FROM 3225A7's PER-REPAIR geoTtd, WAS WRONG --
        // 25s is among the worst arms. One game's repair timings do not determine which
        // threshold wins across seven rungs and two headstart modes.
        //
        // OPEN WEAKNESS, deliberately accepted: against Tier4Spam the bot now UNDER-repairs
        // by 4-6 points (86.2% at 5.5s against 92.8% at 45s, same shape in headstart). A
        // single scalar threshold cannot serve both sustained chip damage and the mirror;
        // separating them would need the gate to consider what KIND of pressure it is under,
        // not just how many seconds remain. Not attempted.
        //
        // The VALUE-gate idea this comment used to propose is dropped. --repair-audit showed
        // repair #n costs almost exactly investment rung #n (ratio 0.85-1.09 for n=1..7), so
        // a price-relative-to-rung test is ~constant across the whole game and cannot
        // discriminate early from late repairs at all.
        public double RepairTtdSeconds { get; init; } = 5.5;

        // ── REPAIR FIXES, 2026-08-23. ALL THREE DEFAULT TO FLAGSHIP BEHAVIOUR. ──
        //
        // The flagship's repair rule is `timeToDeath < 5.5 && money >= price`. It has no
        // notion of price and no notion of absolute health, and each omission produced one of
        // the two failures Marc reported and the replays confirmed. Every knob below is off
        // by default so `bot-checksum --games 24` keeps printing
        // 643A6CA19C1851CF04A2A0C9F873195C -- see FLAGSHIP_2026-08-23.md.

        /// <summary>
        /// Apply the price check (`RepairBuysItsPrice`) to the SHIPPED bot, not only the
        /// defence-only one. Today `worthRepairing = !DefenceOnly || ...` short-circuits to
        /// true for the shipped bot, so the check is dead code there.
        ///
        /// FIXES THE PANIC. In game 1269AD repairs #5/#6/#7 fired at 323.7s, 323.9s and
        /// 324.0s for $73,828 -- the price ladder is $8/$26/$66/$169/$493/$1,796/$8,837/
        /// $63,195 and nothing told it #7 costs seven times #6.
        /// </summary>
        public bool RepairPriceCheck { get; init; } = false;

        /// <summary>
        /// Repair whenever castle health is below this fraction of maximum, regardless of what
        /// the time-to-death estimate says -- still subject to affordability and, if enabled,
        /// the price check.
        ///
        /// FIXES THE OPPOSITE FAILURE. A rate-based time-to-death cannot see an absolute
        /// floor. In 061479 the bot sat at 822/2,000 HP for eleven seconds with 138 units on
        /// the field against Marc's 4 and $67,745 in the bank, and never repaired: its army
        /// was winning so overwhelmingly that incoming damage arrived as rare single hits, the
        /// observed drain rate was tiny, and TTD read long -- correctly on average, fatally at
        /// 297 HP. Repair #0 costs $8. 0 disables.
        /// </summary>
        public float RepairHpFloorPct { get; init; } = 0f;

        /// <summary>
        /// Minimum seconds between repairs. The decision loop runs 6x/second, so without this
        /// the bot can re-fire before the previous repair has changed anything about the
        /// threat -- five repairs inside 0.7s in game A8C7AC. 0 disables.
        /// </summary>
        public double RepairMinIntervalSeconds { get; init; } = 0.0;

        /// <summary>
        /// Stop buying ATTACKING units while a hazard is live that the purchase cannot survive,
        /// and put the money on the investment ladder instead. Defaults false so the flagship
        /// fingerprint is unchanged. See AttackBlockingHazardActive for why an enemy wave
        /// counts but an allied one does not, while a blackhole counts from EITHER side.
        ///
        /// MARC, 2026-08-23: "the Blue team's Wave gadget makes it effectively immune to unit
        /// damage while it's active. If there is an opposing WaveHazard on the field, you are
        /// not getting the kill with units... the bot keeps up its attack in this scenario,
        /// effectively wasting money trying to apply pressure when there is physically no way
        /// to do so."
        ///
        /// The engine agrees. `WaveHazard.ProcessEffect` knocks every non-wall enemy unit
        /// inside its span backwards by up to 3,000px (level 3) as it sweeps the map, so an
        /// attacker bought into an active wave is pushed off before it can land a swing.
        /// `BlackholeHazard` drags units toward its centre for its whole duration. In both
        /// cases the purchase cannot convert into damage, and the money is simply gone.
        ///
        /// DELIBERATELY ATTACK-ONLY. Reactive defence keeps buying: a blocker that gets pushed
        /// around is still a body the enemy has to chew through afterwards, and gating defence
        /// on an enemy gadget would hand the opponent a way to switch the bot's defence off.
        /// </summary>
        public bool HazardAttackBlackout { get; init; } = false;

        /// <summary>
        /// Suppress `killerInstinct` while the next investment rung is within this many seconds
        /// of income. 0 disables. Defaults off so the flagship is unchanged.
        ///
        /// WHY. killerInstinct is a deliberate bypass around the attack flow allowance, the
        /// gadget reserve and the disengage system all at once -- everything that keeps the bot
        /// investing while it attacks. That is defensible when it really is about to win. It is
        /// indefensible three seconds from a rung.
        ///
        /// Game 0A7658, 146.0s: the bot held $7,362 against a rung price of $8,080 -- $718
        /// short, 2.8 seconds of income -- and spent $8,264 on four tier-7 units across two
        /// killerInstinct bursts. It did not buy that rung for another 80 seconds and never
        /// bought the one after it. Marc: "We definitely do not want to be spending the whole
        /// bank on attacking, especially when we are close to our next investment rung."
        /// </summary>
        public double KillerInstinctInvestLockoutSeconds { get; init; } = 0.0;

        /// <summary>
        /// Once a killerInstinct push COLLAPSES, refuse to fire it again until the bot holds an
        /// income advantage. 0/false disables. Defaults off so the flagship is unchanged.
        ///
        /// WHY. The trigger asks "how many seconds to kill their castle at the DPS my units are
        /// applying right now", and has no term for the defender clearing the push. In 0A7658
        /// the bot's army went 64 -> 11 units in seven seconds; it then fired AGAIN on the
        /// remnant and spent down to $489. Marc: "If one of our attacks fails, that's typically
        /// a huge economic swing in favour of our opponent, so we need to re-establish our
        /// economic advantage before pressing again."
        /// </summary>
        public bool KillerInstinctPushLatch { get; init; } = false;

        // Attack-vs-savings balance knobs (see HeuristicBot's own field comments for the
        // full design). Marc's own explicit framing when he gave these: "those numbers I
        // mentioned are pulled out of thin air" -- starting guesses only, meant to be
        // swept (CastleDefense.BotArena's "paramsearch-attack" mode) rather than trusted
        // as committed values. Defaults here are exactly those guesses.
        // Tuned via CastleDefense.BotArena's "paramsearch-attack" mode (60-candidate
        // random search, triage sample size) -- winning candidate ("cand57") scored
        // 89.17% avg vs the original hand-picked guesses' 86.41% (+2.8) on the same
        // triage matchup set. Still needs full two-replicate validation before being
        // trusted -- see HeuristicBot's own comment for that result.
        public float EnemyHpEvaluationSeconds { get; init; } = 27.04f; // how long to watch an ongoing push before judging its HP trend
        public float MinMeaningfulEnemyHpLossPctPerSecond { get; init; } = 0.314f; // rate, not a total -- multiplied by EnemyHpEvaluationSeconds to get the actual stall-vs-working cutoff
        // WARNING (found 2026-07-30): this absolute threshold is ABOVE the starting castle
        // HP. PlayerState() sets CastleHealth = CastleMaxHealth = 2000, and only Repair()
        // ever raises MaxHealth (2000 -> 12000 on the first one). So against any opponent
        // that never repairs -- which is 5 of the 6 ladder rungs, and every spam baseline --
        // `enemy.CastleHealth <= 2676` is true from tick 0 to the end of the game, and
        // killer instinct is therefore PERMANENTLY ON. That silently disables three
        // separate savings mechanisms at once (the flow-based attack allowance, the gadget
        // reserve, and the entire attack-disengage system, all of which killerInstinct
        // bypasses by design), which is the most likely mechanical cause of the bot
        // ceasing to invest past ~5. Under `headstart` the time machine applies repairs, so
        // MaxHealth is 12000+ and the bug does NOT reproduce -- meaning this misfires in
        // `nostart` runs only, which is exactly the kind of mode-dependent difference that
        // survives a benchmark suite. The value itself came out of the 60-candidate
        // paramsearch, so the sweep may simply have discovered "turn savings discipline off
        // unless the enemy has repaired" and scored it well.
        //
        // NOW DEAD BY DEFAULT: PaceAttackSpendForInvestment defaults to true as of
        // 2026-07-30, and it replaces this predicate with KillerInstinctSeconds. This
        // field is only read on the flag-off path, which exists so PreInvestFlowCap can
        // still reproduce the historical bot for regression checks.
        //
        // CONSEQUENCE FOR THE SWEEP: CastleDefense.BotArena's "paramsearch-attack" mode
        // (RandomAttackSettings, Program.cs) still randomises this field over 500-10000.
        // It is now a no-op knob in that sweep -- every candidate will score identically
        // on it, which will read as "this parameter does not matter" rather than as
        // "this parameter is not connected". Swap it for KillerInstinctSeconds before
        // running that mode again, or the sweep burns its budget on a dead dimension.
        public int KillerInstinctHpThreshold { get; init; } = 2676; // absolute enemy castle HP -- below this, ignore savings discipline and go for the kill
        public float AttackSpendFraction { get; init; } = 0.91f; // non-reactive spending capped at this fraction of the income RATE (a flow limit, not a fraction of the money pile)

        // Per-spend EV check for REACTIVE defense (unit purchases, freeze, wall, wave,
        // goo's slow) -- Marc's own explicit framing: "the whole point [of buying time]
        // is so we can save up in that extra time we're buying ourselves. If we spend
        // all our money on defensive units and slowing down the enemy attack, we
        // haven't actually accomplished anything except delaying the inevitable."
        // Converts the runway DEFICIT (how many seconds short of safely reaching the
        // next investment we actually are, ignoring the conservative safety margin --
        // see reactiveSpendBudget in Decide()) into a dollar budget at our own income
        // rate, so defensive spending is judged against what it's actually worth in
        // savings-progress terms, not spent unconditionally just because SOME danger
        // exists. >1.0 allows some slack above pure breakeven, since real defense isn't
        // a perfectly efficient time-for-money exchange. A genuinely new lever vs. the
        // 4 previously-rejected reactive-spend-cap attempts (which all capped WHAT got
        // bought via combat-power ratios/exclusions) -- this caps based on an actual
        // economic trade, and only engages when the (now DPS-math-aware) danger signal
        // says there's a real deficit to bridge at all.
        public float ReactiveSpendEVMultiplier { get; init; } = 1.5f;

        // ─────────────────────────────────────────────────────────────────────────
        // BEHAVIOURAL CHANGE FLAGS (2026-07-30 session)
        //
        // Each flag guards one candidate behaviour change, and each guarded branch is
        // written so that flipping the flag off reduces to the exact arithmetic and
        // control flow of whatever came before it. That is load-bearing, not tidiness:
        // it lets the ladder run a variant and its own reference in the SAME BINARY (see
        // the contender note in Ladder.cs), so an A/B needs no rebuild-and-diff and no
        // unrelated edit can leak into the comparison. If you change an off-path branch,
        // the reference silently stops being the old bot and the numbers mean nothing.
        //
        // ALL FOUR WERE MEASURED at two seeds x 600 games x 6 rungs x both modes
        // (ladder, 2026-07-30). Exactly one was kept. Results recorded per flag below.
        //
        // Read this before adding a fifth: the five BASELINE rungs are saturated --
        // the bot already scores 100/96/80/81/93 against them -- so they cannot resolve
        // a few points either way, and the OVERALL column is dominated by them. The
        // self-play rung is the instrument that actually has power (it is pinned at 50%
        // by construction; the reference measured 49.3/49.5/50.2/51.9 across the four
        // seed x mode cells, which is how we know the harness is calibrated). Judge
        // candidate changes on the mirror and on earned_invests, not on OVERALL.
        // ─────────────────────────────────────────────────────────────────────────

        // (1) RESOLVED 2026-07-31: MEASURED PROPERLY AND REJECTED. This comment used to say
        // the fix was "almost certainly CORRECT; it is the measurement that could not see
        // it", and asked for a pinned-loadout run. Ladder.cs now has --offense, so that run
        // was done: 250 setups x 2 sides x 2 modes with firebomb pinned on BOTH sides, i.e.
        // 100% exposure instead of ~25%.
        //   mirror ns  50.4 -> 51.4      mirror hs  45.6 -> 43.2
        //   T4Spam hs  74.0 -> 71.2      T4Spam ns  77.2 -> 77.2
        //   Investor   99.8 -> 99.2 / 87.4 -> 88.4      Tier1Spam identical
        // Flat-to-negative at full exposure. The offsetting cost this comment itself
        // predicted is the likely explanation: the stricter swept test more often finds NO
        // safe position and skips the cast outright, forfeiting firebomb's damage and its
        // upgrade XP. The check is more correct and the bot is worse for it. Keep off.
        //
        // Original reasoning, retained for the mechanism description:
        // Firebomb leaves a persistent FireHazard that burns ANY unit standing in it,
        // including ours, and the existing friendly-fire check asks an instantaneous
        // question about a 6-second effect -- see the firebomb case in
        // TryUseOffenseGadget for why it is wrong three separate ways. The fix is
        // almost certainly CORRECT; it is the measurement that could not see it. Two
        // reasons: firebomb is 1 of 4 offense options so it appears in only ~25% of
        // games, and under the boom strategy our standing army is near-zero for most of
        // a game, so allies are rarely near the blast in bot-vs-bot play at all. Marc
        // observes this misfiring in HIS games, where he applies enough pressure that
        // the bot is actually fielding an army. Result was flat-to-marginally-negative
        // everywhere (mirror 49.0/48.4 vs the 49.3/49.5 reference), and there is a
        // plausible offsetting cost: the stricter test makes the retarget find no safe
        // position and skip the cast entirely, losing firebomb damage and its upgrade
        // XP. DO NOT re-judge this on a normal ladder run -- it needs the loadout pinned
        // to firebomb (an --offense override that overwrites the drawn loadout AFTER
        // spec generation, so the rng stream and cross-run comparability are untouched).
        public bool FirebombSweptFriendlyFireCheck { get; init; } = false;

        // (2) KEPT -- this is committed default behaviour as of 2026-07-30.
        // Stops non-reactive attack spending from starving the investment engine once it
        // switches on at Income >= 50. Two independent mechanisms, both in this one flag
        // (they were measured together and are documented separately at their sites: the
        // killer-instinct redefinition in Decide(), the flow-pacing in SpendOnUnits).
        //
        // MEASURED: positive in all four seed x mode cells, both seeds, on the self-play
        // rung -- nostart 54.1%/58.9% and headstart 50.4%/55.7%, against a reference
        // measuring 49.3/49.5/50.2/51.9. Earned investments rose in the same games
        // (nostart 5.07 -> 5.34, headstart 3.31 -> 3.56) and average mirror game length
        // FELL from 436s to 385s: it invests more and closes faster, rather than
        // stalling into longer games. Baseline rungs were unchanged, which is expected --
        // those games end in 85-155 seconds, and at investment 5 the next price is ~1677
        // against income ~60, so no behaviour whatsoever reaches investment 6 inside 85
        // seconds. Only the long games can show this.
        //
        // The gap between the two modes is the diagnostic worth remembering. nostart
        // gained ~+7.1 points, headstart only ~+2.0. Under headstart the time machine
        // applies repairs, so CastleMaxHealth is 12000+ and the stuck-killer-instinct
        // bug (see KillerInstinctHpThreshold's warning) never fired there -- only the
        // flow-pacing half was active. The ~5-point difference between the modes IS that
        // bug, measured.
        public bool PaceAttackSpendForInvestment { get; init; } = true;
        // Target pace for the next investment while a push is running: the bot reserves
        // whatever income rate is needed to afford InvestmentPrice within this many
        // seconds, and only the surplus above that funds the attack. Chosen so an
        // investment still lands roughly every 45s mid-game rather than never.
        public float InvestPaceTargetSeconds { get; init; } = 45f;

        // (17) DYNAMIC INVEST PACE -- KEPT, default on as of 2026-07-31. Marc's design.
        // Measured TOGETHER with flag (18); they are additive because they plug different
        // leaks (units vs gadgets). Two seeds, 250 setups x 2 sides x 2 modes:
        //                        seed 12345          seed 777
        //   mirror ns      46.2 -> 70.8 (+24.6)  49.4 -> 72.6 (+23.2)
        //   mirror hs      50.2 -> 65.6 (+15.4)  47.8 -> 69.8 (+22.0)
        //   mirror invests  5.77 -> 6.83          5.88 -> 6.98
        //   Tier4Spam ns   79.2 -> 79.2          82.6 -> 82.6   (EXACTLY flat)
        //   Tier1Spam      flat                  flat
        //   Balanced       +0.4 / +0.6           0.0 / +1.6
        //   Investor hs    -2.2                  -2.0            <- the only cost
        // Individually at seed 12345: pace alone +9.4 mirror ns, drain alone +13.6, both
        // +24.6 -- near-perfectly additive, which is the direct evidence they are not
        // overlapping. The drain cap is the larger contributor, matching the D23E91 replay
        // (~$72,000 of meteors vs ~$21,949 of units).
        //
        // Replaces the single static InvestPaceTargetSeconds with a target derived from the
        // investment level itself.
        //
        // WHY A CONSTANT CANNOT WORK: the game is balanced so each investment takes longer
        // than the last. Measured vs DoNothing (invest-timing, 40 games), seconds per level:
        //   inv 1..6 deltas  9.0 / 12.0 / 16.9 / 19.0 / 22.8 / 27.8
        // which matches the exact algebra -- price(n) = income(n) * (4n+8), so the zero-spend
        // time is price/income = (4n+8)s, independent of income. The two price OVERRIDES
        // break the pattern: investment 8 is 40,000 at income 750 = 53.3s, ARMAGEDDON is
        // 121,221 at 2,500 = 48.5s. A single 45s is wildly generous at low levels and far too
        // tight at 7, which is what drives the pacing into the MinAttackFlowFraction floor.
        //
        // THE OLD FORM WAS ALSO THE WRONG SHAPE, not just the wrong constant. It reserved
        // `stillNeeded / target`, so dm/dt = (price - m)/target -- exponential decay, whose
        // solution m = price*(1 - e^(-t/target)) reaches only 63% of the price at t = target
        // and NEVER ARRIVES. It only worked at high levels by accident: the reservation
        // exceeded income, the floor caught, and savings became linear at 85% of income.
        // The floor was load-bearing. Removing it under the old shape would have stopped the
        // bot investing at all past level 6.
        //
        // The correct reservation is CONSTANT at price/target rather than stillNeeded/target.
        // With target = baseTime * (1+extra) and baseTime = price/Income, the price cancels
        // to Income/(1+extra), leaving the attack a constant Income*extra/(1+extra). That is
        // linear, arrives exactly on schedule, scales through the overrides for free, and is
        // strictly positive so no floor is needed.
        public bool DynamicInvestPace { get; init; } = true;
        // Extra time, as a fraction of the zero-spend base time, that the attack may consume.
        // 0.20 => investments take 20% longer than the theoretical minimum and the attack
        // gets 0.2/1.2 = 16.7% of income, at every level, forever.
        public double InvestPaceExtraTimeFraction { get; init; } = 0.20;

        // (18) GADGET INCOME-DRAIN CAP -- Marc's design, 2026-07-31, from the D23E91 replay
        // where the bot spent ~$72,000 on ~8 meteor_3 casts in 152s against ~$21,949 on its
        // entire unit stream, and consequently never reached the 40,000 investment.
        //
        // THE HOLE: investPaceRate governs UNIT spending only. Gadgets are checked earlier in
        // Decide() and never consult it -- TargetValueJustified / BigSpendJustified wave any
        // cast through on `me.Income >= 50`, a threshold set for investment 5. At investment 7
        // the bot fires a $9,000 gadget every 15s and never compares it to anything.
        //
        // MARC'S ARITHMETIC, which is the rule implemented here: a $9,000 meteor on a 15s
        // cooldown at 750/s income means 11,250 is earned per cooldown cycle, so firing on
        // cooldown burns 80% of income. Impose our OWN longer cooldown so the drain stays
        // under a chosen fraction:
        //     minSeconds = cost / (income * maxDrainFraction)
        // At 30%: 9000 / (750*0.30) = 40s, exactly his worked example. Applies to EVERY
        // gadget, not just meteor -- a cheap gadget yields minSeconds below its real cooldown
        // and is therefore unaffected, which is the desired behaviour.
        //
        // TWO OVERRIDES, both his:
        //  1. NO LIMIT WHEN THE CASTLE IS UNDER ATTACK. If enemies are actually on us,
        //     survival outranks savings and the bot should spend whatever it needs.
        //  2. NO LIMIT WHEN THE CAST PAYS FOR ITSELF. If the enemy value the gadget can
        //     realistically kill exceeds its cost, the cast is a positive economic swing and
        //     the drain argument does not apply -- e.g. 10 meteors doing up to 25,000 damage
        //     reliably wipe tier 6/7 units, so $9,000+ of those on screen justifies it.
        //     This reuses the existing estimatedEnemyValue the value gates already compute,
        //     so it costs nothing extra and stays consistent with them.
        public bool GadgetIncomeDrainCap { get; init; } = true;
        // Maximum share of income a single gadget family may consume over its own cooldown.
        public double GadgetMaxIncomeDrainFraction { get; init; } = 0.30;
        // How close an enemy must be to our castle to count as "under attack" and lift the
        // cap entirely. Matches EnemyIsCloseDistance's scale.
        public float GadgetDrainCapCastleThreat { get; init; } = 500f;

        // ── DRAIN-CAP "UNDER ATTACK" TEST (2026-08-20) ──────────────────────────────
        //
        // 0 = the original PROXIMITY rule: any enemy unit within GadgetDrainCapCastleThreat
        // (500px) of our castle disables the drain cap entirely. Above 0, the test becomes
        // time-to-death instead: underAttack = LastTimeToDeathSeconds < this many seconds.
        //
        // WHY IT NEEDED CHANGING, measured on E32DEB. The proximity rule is a latch in
        // practice -- once a human applies sustained pressure there is always SOMETHING
        // within 500px, so the cap is off for the rest of the game. With it off, the bot
        // cast reinforcements_2 on cooldown: $180 every 8.0s against $158 of income per
        // cycle, i.e. NET NEGATIVE income while casting. It spent $1512 on that gadget
        // against $290 on investing, and never again afforded the $474 rung -- the drain is
        // worth almost exactly the two investment rungs separating it from Marc.
        //
        // Time-to-death is the right question because the override exists for "I am about to
        // die, stop saving". Proximity cannot distinguish a lethal wave from one scout
        // loitering; the TTD estimator already fuses observed HP drain with projected DPS of
        // whatever is actually in contact, and it is the same number inDanger is built on.
        //
        // NOTE this READS LastTimeToDeathSeconds, which Decide() sets before any gadget call
        // in the same pass, so it is current rather than a tick stale.
        // ── MEASURED 2026-08-20: THE LARGEST SINGLE WIN OF THE ARC. SHIPPED AT 5s. ──
        //
        // ladder 200 (400 games/rung), seed 4242, both modes, all five arms paired:
        //
        //   arm          nostart   headstart   invests(ns/hs)
        //   proximity     88.18%     83.96%     5.46 / 3.72
        //   ttd 3s        92.00%     90.18%     5.61 / 3.87
        //   ttd 5s        91.89%     90.21%     5.61 / 3.87
        //   ttd 8s        91.93%     90.04%     5.61 / 3.86
        //   ttd 15s       91.89%     89.71%     5.61 / 3.86
        //
        // +3.8 points nostart (p = 3.5e-06) and +6.3 headstart (p = 3.1e-12), with earned
        // invests up on both. 3s / 5s / 8s are statistically indistinguishable; only 15s is
        // measurably worse, and only in headstart. 5s is shipped because it sits in the flat
        // middle of the plateau.
        //
        // THE FLATNESS IS THE FINDING. If the threshold barely matters, the old rule was not
        // mis-tuned -- it was firing in states with no danger at all. Proximity cannot tell a
        // lethal wave from a scout loitering near the wall; any sane time-to-death cut does.
        //
        // WHERE THE GAIN LANDS confirms the mechanism rather than just the number:
        //   Investor    87.5% -> 100.0% (ns), 73.0% -> 89.2% (hs)  -- the pure-economy rung,
        //               exactly where leaking income into gadgets is fatal
        //   HumanClone  93.0% -> 100.0%, 92.2% -> 100.0%           -- the Marc-shaped rung
        //   mirror      53.8% ->  61.0%, 44.2% ->  52.8%
        //   Tier4Spam   unchanged at 83.0% nostart                 -- sustained real pressure,
        //               where TTD genuinely IS short and the cap correctly stays off
        public double DrainCapTtdSeconds { get; init; } = 5;

        // (19) EAGER DIVINE -- see the divine case in TryUseSignatureGadget. Divine needs
        // FIFTEEN casts to reach divine_3 (400 XP then 1100, at 100 XP per cast), and
        // divine_3 is what makes the yellow team strong -- total invulnerability for castle
        // and units. The committed trigger fires so rarely the upgrade is unreachable, so
        // the gadget's own power gates itself off. Cast it on a looser threat read.
        public bool EagerDivine { get; init; } = false;
        public float DivineEagerHpThreshold { get; init; } = 0.6f;   // was 0.3
        public int DivineEagerEnemyCount { get; init; } = 2;         // was 3

        // (20) WAVE-WIPE PURCHASE -- KEPT, default on at margin 0.35 as of 2026-07-31.
        // Two seeds, 250 setups x 2 sides x 2 modes:
        //   Tier1Spam ns   98.0 -> 100.0  |  96.6 -> 100.0
        //   Tier1Spam hs   95.6 ->  97.2  |  96.6 ->  99.6
        //   Tier4Spam ns   81.2 ->  84.0  |  83.0 ->  85.6
        //   Tier4Spam hs   74.8 ->  79.8  |  80.4 ->  84.6
        //   mirror ns      49.4 ->  51.0  |  48.0 ->  51.6
        //   Balanced hs    92.0 ->  93.2  |  92.4 ->  94.0
        //   Investor       -4.4 / -2.0    |  -0.6 / -1.4        <- the cost
        //   invests vs T4Spam  4.67 -> 4.67  |  4.84 -> 4.84    <- unchanged, both seeds
        //
        // THE MARGIN IS THE INTERESTING PART, and it vindicates Marc's own hedge. His rule
        // was "if the wiper costs less than the total value of attackers it is a positive
        // investment", with an aside that letting more build up increases the swing "so maybe
        // that should be factored in as well". The aside matters more than the rule: at
        // margin 1.0 (fire on ANY positive swing) invests drop 4.67 -> 4.06 against Tier4Spam
        // and most rungs are worse; at 0.35 (demand a ~3x swing) invests are untouched and
        // every rung is better. Firing on marginal wipes bleeds money. The trend TURNS below
        // that -- 0.20 is worse than 0.35 everywhere tested -- so this is a real interior
        // optimum, not "tighter is always better".
        //
        // FIRST VERSION WAS WRONG-SHAPED AND IS WORTH RECORDING. It framed this as "wiper
        // INSTEAD OF repair" and gated on `repairWouldHelp`, which is
        // `castleHpPct < 0.75 || inDanger` -- and inDanger is true most of the time against
        // an active opponent, so it substituted units for repairs constantly rather than in
        // the narrow case intended: Investor -10.0/-9.6, Tier1Spam hs -7.0, invests -0.88.
        // Marc's correction was to decouple them entirely -- "if we need to repair we should;
        // the focus should be on the stack of attacking units" -- which is what made it work.
        // Repair is now untouched and this is a standalone economic test.
        //
        // Original motivation, unchanged:
        // HP for time correctly (which he taught it) but takes that trade even when a much
        // cheaper unit would end the threat outright: "I usually only send $5-$10 worth of
        // [purple tier 1s], which are easily wiped by a ~$5 tier 2 unit for a positive
        // economic swing, but the bot typically chooses to simply let the tier 1s attack...
        // it ends up spending $50 on HP upgrades instead of $5 on a tier 2 wiper, and in the
        // early game that $50 makes a big difference."
        //
        // WHY IT HAPPENS: a handful of tier-1s against a 2000 HP castle leaves a long
        // time-to-death, so investmentRunwayIsSafe stays true, inDanger stays false, and the
        // reactive-spend path never runs at all. The only thing that does fire is the repair
        // check at castleHpPct < 0.75. So the bot's choice is never "wiper vs repair" -- the
        // wiper was never on the menu.
        //
        // THE RULE, as shipped: on any decision where enemies are committed against our
        // castle, find the cheapest unit that one-shots the TOUGHEST of them (health plus
        // shield, since ApplyDamage spends the shield first) -- which full-damage AoE makes
        // a real question, since one swing hits every enemy in contact -- and buy it if it
        // costs at most WiperMaxCostVsStackValue (0.35) of the stack's total value.
        //
        // Note what it is NOT priced against: the repair. An earlier version of this
        // paragraph described the purchase as happening "right before repairing" and being
        // bounded by only firing where a repair was about to happen. That is the ABANDONED
        // design described in the correction above -- gating on repairWouldHelp is exactly
        // what produced Investor -10.0/-9.6 and forced the decoupling. The shipped purchase
        // is standalone and runs whether or not a repair happened.
        //
        // What actually bounds it is WiperMinIntervalSeconds (4.0): a wiper has to walk in
        // and swing before we can judge whether another is needed, and without that gap the
        // check re-fires every decision -- which is precisely how wave-wipe attempt 1
        // collapsed (re-buying 6x/second, Investor 98.5 -> 60.5).
        //
        // This is Marc's "be satisfied with smaller economic swings earlier in the game"
        // expressed as a direct price comparison rather than a tunable swing threshold.
        public bool WiperOverRepair { get; init; } = true;
        // The wiper must cost at most this fraction of the stack's value. 1.0 is Marc's plain
        // rule ("if the wiper costs less than the total value of attackers it is a positive
        // investment"); below 1.0 demands a wider margin, which is the "let more build up for
        // a bigger swing" instinct expressed as a price rather than a wait.
        public double WiperMaxCostVsStackValue { get; init; } = 0.35;
        // Minimum gap between wiper purchases. A wiper has to walk in and swing before we can
        // judge whether another is needed; without this the check re-fires every decision,
        // which is exactly how wave-wipe attempt 1 collapsed.
        public double WiperMinIntervalSeconds { get; init; } = 4.0;

        // Whether a wipe is priced on the MARGIN -- what a purchase adds over the units already
        // deployed -- or on the whole pile as if the field were empty. Exists so the coverage
        // rule can be measured against its own absence on identical seeds; a bare rate
        // difference at n=160 has SE ~3.9pp and cannot resolve what this is worth.
        public bool WiperCountsFieldCoverage { get; init; } = true;
        // Floor on the attack flow as a fraction of income, so a very expensive upcoming
        // investment (the InvestmentCount 7/8 overrides price at 40,000/121,221) can't
        // reduce offensive spending to literally zero for a minute at a time.
        public float MinAttackFlowFraction { get; init; } = 0.15f;
        // Replacement definition of killer instinct: how many seconds our units currently
        // in contact with the enemy castle would need to finish it off. See the
        // KillerInstinctHpThreshold comment for why the absolute-HP version is broken.
        public float KillerInstinctSeconds { get; init; } = 12f;

        // (3) TESTED AND TABLED -- rejected as built, worth a second attempt SPLIT UP.
        // Income advantage as the signal to commit to a real attack. Win rate came back
        // flat everywhere (mirror 49.1/50.5 vs the 49.3/49.5 reference), but earned
        // investments dropped hard and consistently -- 4.59->3.85, 4.50->3.82,
        // 4.19->3.61, 4.82->4.14 -- with game lengths collapsing to match (85->68s,
        // 154->126s). It converts economy into earlier aggression at roughly break-even,
        // which is the exact trade this bot is not supposed to make.
        //
        // The flaw is that this flag bundles TWO effects and only one of them is
        // implicated. Opening the attack gate before Income >= 50 is what costs the
        // investments; raising the burst allowance cap from 10s to 25s was never
        // measured on its own and is not obviously bad -- a burst that arrives together
        // is worth more than the same money trickled out one unit per decision, and it
        // does not move the gate at all. A fair retest splits this into two flags and
        // layers the cap-only half on top of PaceAttackSpendForInvestment.
        public bool IncomeAdvantageAttack { get; init; } = false;
        public float IncomeAdvantageRatio { get; init; } = 1.75f;   // our income / theirs
        public float IncomeAdvantageMinIncome { get; init; } = 12f; // don't call 4-vs-2 an "advantage"
        public float IncomeAdvantageAllowanceSeconds { get; init; } = 25f; // bank a bigger burst when ahead

        // (4) TESTED AND TABLED at 12 SECONDS, THEN RETESTED AT 8 AND 6 -- all three fail.
        // The retest this comment used to recommend has now been done (2026-07-31), seed
        // 12345, 300 setups x 2 sides x 2 modes, against the k=1.7 + wave-wipe bot:
        //          ref    Surv6   Surv8
        //   mir ns 47.7    47.8    47.8      <- flat
        //   mir hs 46.7    46.5    46.7      <- flat
        //   Inv ns 99.8    99.7    98.8
        //   Inv hs 88.5    88.0    87.7
        //   T1  ns 98.3    95.0    95.0      <- -3.3, the real cost
        //   T4  ns 79.3    80.7    80.3
        // Judged as this comment itself directs -- on Investor and the mirror -- it is
        // flat-to-negative at every trigger tried. Tightening from 12s did NOT convert the
        // trade into a gain, so the trigger was not the problem.
        //
        // DO NOT read this as "the mechanism is wrong". The hole it targets is real and is
        // arithmetic: once Money >= InvestmentPrice, timeToInvest ~ 0, so
        // reactiveSpendBudget = max(0, timeToInvest - timeToDeath) * income * 1.5 collapses
        // to $0 -- and every time-buying gadget (wave/blackhole/freeze/wall) is gated on
        // `cost <= reactiveSpendBudget`. Full wallet, real threat, nothing castable. What
        // is missing is an INSTRUMENT: the ladder cannot manufacture the outmatched-with-a-
        // full-wallet state, and after 2026-07-31 the bot wins 98-100% on four of six
        // rungs, so it can produce it even less often than when this was first written.
        // A retest needs an opponent that applies sustained competent pressure while
        // playing its own economy -- not a third trigger value.
        //
        // Historical note, 12-second result:
        // Absolute survival override, independent of the investment race. The hole it
        // closes is real and is documented at the survivalEmergency block in Decide()
        // (investmentRunwayIsSafe degenerates to "time-to-death >= 2 seconds" once money
        // reaches InvestmentPrice, and the reactive budget degenerates to $0 in the same
        // situation). But at a 12-second trigger it measured as a consistent TRADE, not
        // a gain, in both seeds: Investor +1.2/+3.4, Tier1Spam -1.0/-2.0, mirror
        // -0.9/-2.4. Earned investments barely moved, so it is not costing economy --
        // it is spending on defense in situations where the EV cap was right, i.e. the
        // trigger is too generous. 6s and 8s are the obvious retests.
        //
        // Note also that the ladder is a poor instrument for this specific change: the
        // bot wins 80-100% against every rung, so these opponents almost never produce
        // the "outmatched, dying with a full wallet" state it exists to fix. Investor is
        // the only rung that saves money and can build a real threat, and Investor is
        // the one rung that improved -- which is weak evidence FOR the mechanism even
        // though the aggregate says no. Judge a retest on Investor and the mirror.
        // ── GEOMETRIC t_death (arm 1, 2026-08-19) ────────────────────────────────────
        //
        // Adds GameState.TimeToCastleDeathSeconds as a THIRD input to the existing
        // Math.Max that produces timeToDeathSeconds. Built for the board evaluator, where
        // it measured -20 points -- but that arm both DROPPED the army term and ADDED
        // t_death, so it licenses "not an adequate substitute for army", not "harmful".
        // See GameState.cs's own note, which says exactly that.
        //
        // WHY IT MIGHT WORK HERE WHEN IT DID NOT THERE. The evaluator used it as a linear
        // positional feature competing for weight share at rollout leaves, and that whole
        // feature class is unmeasurable on calibration data (dropping army costs +0.0011
        // logloss and 20 points of play). HeuristicBot uses it as a THRESHOLD input: one
        // comparison against timeToInvest, one subtraction for reactiveSpendBudget. A
        // quantity can be a poor linear score and a good trigger.
        //
        // WHAT IT ADDS over the two existing estimators, which between them see only
        // (a) damage that already landed in a ~3s HP window and (b) units LITERALLY
        // touching the wall -- Range is 0 for every roster unit, so the projected
        // estimator's contact test is `distance <= 0`:
        //   travel time for units not yet arrived, a piecewise-linear DPS staircase
        //   rather than a constant rate, Siege doubling, Rage, Slow/Speed/freeze,
        //   friendly blockers as an interception delay, and castle invulnerability.
        //
        // IT REPLACES THE EXISTING Max, IT IS NOT ADDED TO IT. The first version of this
        // arm Max'd it in as a third estimator and came back BYTE-IDENTICAL on 6 of 7
        // ladder rungs -- a bug signal, not a null. Math.Max can only RAISE timeToDeath,
        // and both existing estimators return EffectivelyInfiniteSeconds (999999) whenever
        // the HP window is not full or HP is flat, which is exactly the "a wave is inbound
        // but has not landed yet" state the geometric model was built to see. Max(999999,
        // geo) = 999999, so the flag was inert precisely where it mattered. A Max over
        // estimators is a noise guard between two WEAK ones; a better estimator must not be
        // defended by it.
        //
        // TWO COMBINATION MODES, because "replace" hides a second variable. The observed
        // estimator is the only one of the three that sees gadget, hazard and DoT damage --
        // the geometric model counts units and nothing else -- so dropping it outright
        // changes coverage as well as accuracy:
        //   GeometricTDeathKeepObserved = false -> timeToDeath = geo alone. The literal
        //       "does t_death help on its own" reading. Loses gadget/DoT visibility.
        //   GeometricTDeathKeepObserved = true  -> timeToDeath = MIN(observed, geo).
        //       Min, not Max: it keeps observed's unique coverage (gadget damage makes
        //       observed read short, and Min takes it) while letting geo speak whenever
        //       observed is infinite. This is the principled combination, and also the one
        //       most exposed to the risk below, since it removes the optimism guard.
        //
        // THE RISK, stated up front so a negative result is interpretable. t_death's
        // documented bias is PESSIMISTIC (friendly units contribute HP but not damage, so
        // a winning blocker's hold time is understated). GameStateTimeFeatures calls that
        // "tolerable in a differential (both sides computed identically)" -- true in
        // TDeathComponent, where it largely cancels. It does NOT cancel here: t_death is
        // compared against timeToInvest, an absolute clock with no matching bias. A
        // pessimistic reading makes investmentRunwayIsSafe false, which makes inDanger
        // true, and this file documents FOUR previous inDanger triggers that collapsed to
        // "true almost always" and cost the economy the game. Watch EARNED INVESTS, not
        // just win rate: the economy collapses first and invests/game is the leading
        // indicator.
        //
        // NOTE the enemyIsClose AND-gate limits how much of this can express: inDanger is
        // `enemyIsClose && !investmentRunwayIsSafe` at 700px, so the headline capability
        // (pricing units that have not arrived yet) is largely gated away. That gate is
        // arm 3's subject and is deliberately untouched here.
        // ── MEASURED 2026-08-19: NEGATIVE ON BOTH SUB-ARMS. SHIPPED OFF. ────────────
        //
        // ladder 400 (800 games/rung), seed 4242, both modes, all three contenders paired
        // inside ONE run on identical specs. Overall and the mirror rung, which is the rung
        // that moves:
        //
        //   mode       contender        overall    mirror     mirror earned invests
        //   nostart    control           88.30%    54.1%              6.94
        //   nostart    GeoTDeathPure     86.38%    45.4%  (-8.8)      6.80
        //   nostart    GeoTDeathMin      86.34%    43.9% (-10.3)      6.74
        //   headstart  control           84.70%    50.0%              4.85
        //   headstart  GeoTDeathPure     84.36%    42.8%  (-7.3)      4.72
        //   headstart  GeoTDeathMin      84.16%    41.8%  (-8.3)      4.67
        //
        // Two-proportion z on the mirror rung: p = 4.7e-4, 4.1e-5, 3.6e-3, 9.3e-4. Four
        // independent negatives (2 modes x 2 sub-arms), same sign, same magnitude. This is
        // not noise.
        //
        // (The mirror rung's control reads 54.1% rather than 50% because the contender
        // always draws loadout A and the opponent loadout B -- a documented ladder property,
        // see CLEANUP_BACKLOG.md. It is shared by all three contenders, so the DELTAS stand.)
        //
        // THE PREDICTED PATHOLOGY IS EXACTLY WHAT HAPPENED, confirmed directly rather than
        // inferred. `trace <opp> --variant <profile>`, fraction of decisions with inDanger
        // true, and fraction with a finite TTD reading:
        //
        //   opponent   contender        danger    finite TTD
        //   investor   control            0.0%          7.5%
        //   investor   GeoTDeathPure      0.4%        100.0%
        //   investor   GeoTDeathMin       1.2%        100.0%
        //   spam4      control           10.2%         43.5%
        //   spam4      GeoTDeathPure     49.4%        100.0%
        //   spam4      GeoTDeathMin      17.0%        100.0%
        //
        // TTD stops ever being infinite (geo caps at remaining game time, 600s, where the
        // old estimators returned 999999), and the danger rate against spam4 goes 10.2% ->
        // 49.4%. Earned invests fall on the mirror rung in every arm. That is the FIFTH
        // instance of this file's dominant failure mode -- an inDanger trigger that is true
        // too often, draining money into reactive defence and losing the investment race.
        //
        // WHAT THIS DOES AND DOES NOT LICENCE. It says the geometric estimator is not a
        // drop-in replacement for the existing pair AT THE CURRENT THRESHOLD. It does NOT
        // say t_death is the wrong quantity: the estimator passes 41/41 oracle checks, and
        // the failure is entirely consistent with its DOCUMENTED pessimistic bias (friendly
        // units contribute HP but not damage) meeting a threshold that was tuned against
        // two optimistic estimators. investmentRunwayIsSafe's 1.4x margin and 2s buffer were
        // fitted to the old readings and were not re-tuned here; doing so is a separate arm
        // and is the obvious next thing to try before abandoning the direction.
        //
        // THROUGHPUT: 4-8% slower (nostart 359,876 -> 330,597 / 345,690 ticks/s; headstart
        // 218,384 -> 205,634 / 209,547). Part of that is consequence rather than cost -- the
        // arms play longer, unit-denser games (mirror avg_ticks +6%), and denser boards make
        // every tick more expensive regardless of the flag. The identical-game DoNothing rung
        // is too noisy at this n to isolate it (-9.7% nostart vs -0.8% headstart). No step
        // change: nothing here threatens search throughput.
        /// <summary>
        /// Single unified time-to-death: discard estimators sitting at their no-information
        /// sentinel, then MAX the rest. Replaces the observed/projected Math.Max and the
        /// GeometricTimeToDeath arms. See the block in Decide() for the full reasoning.
        /// </summary>
        // ── MEASURED 2026-08-21: NEUTRAL, AND SHIPPED FOR STRUCTURE. ───────────────
        // ladder 200 (400 games/rung), seed 4242, paired:
        //
        //   arm                nostart   headstart   invests(ns/hs)
        //   dual (old)          90.68%     88.50%     5.60 / 3.84
        //   unified m1.4        90.82%     88.50%     5.57 / 3.82   p 0.85 / 1.00
        //   unified m1.0        90.71%     88.25%     5.58 / 3.84
        //   unified m0.7        90.07%     88.75%     5.59 / 3.83
        //
        // A clean null on strength, taken for correctness: it removes the second estimator
        // and the standing risk of forgetting which one a given call site reads.
        //
        // THE SENTINEL RULE IS WHY THIS IS NEUTRAL WHERE THE GEOMETRY-ONLY ARM WAS NOT. That
        // arm (GeoTDeathPure/Min, 2026-08-19) lost 8-10 points on the mirror rung because geo
        // is systematically pessimistic -- friendly units contribute HP but never damage --
        // and feeding that into an absolute threshold made inDanger fire constantly. Here geo
        // is consulted ONLY when the observed estimator has nothing to say, so the pessimism
        // never overrides a real measurement.
        //
        // SafetyMarginMultiplier does NOT need to move with it: the margin sweep above is
        // flat, unlike the geometry-only arm which recovered monotonically as the margin
        // loosened.
        public bool UnifiedTimeToDeath { get; init; } = true;

        public bool GeometricTimeToDeath { get; init; } = false;
        /// <summary>See GeometricTimeToDeath: false = geo alone, true = Min(observed, geo).</summary>
        public bool GeometricTDeathKeepObserved { get; init; } = false;
        public bool SurvivalInstinct { get; init; } = false;
        public float SurvivalEmergencySeconds { get; init; } = 12f;

        // (5) ARMAGEDDON RUSH -- TESTED AND TABLED. Correctly targeted, but at a regime
        // too rare to matter. Ladder 300 setups x 2 sides x 2 modes vs the default
        // reference: mirror 44.3%/44.5% against the reference's 44.8%/46.0%, earned
        // invests 5.3983 vs 5.3967. A 3-game move in 600. Null.
        //
        // WHY, and this is the useful part: the floor this removes only BINDS at the two
        // hardcoded price overrides. For the formula prices, price(n) = income(n)*(4n+8),
        // so right after investing the floor engages only when 4n+8 > 45, i.e. n > 9.25 --
        // never. Only InvestmentCount 7 (40,000 vs 45*750) and 8 (121,221 vs 45*2500)
        // clear it. Average earned invests is 5.4, so the bot is essentially never in this
        // state. The $117k hoard that motivated this was traced in `mirror-fixed` with
        // IDENTICAL teams on both sides -- a degenerate, unusually long game, not the
        // typical one. DON'T re-judge this on a normal ladder; it needs games that
        // actually reach investment 7+, and it is only worth revisiting if some other
        // change starts getting the bot there.
        //
        // The 26% mirror timeout rate is therefore NOT caused by this. Timeouts happen at
        // investment 5-6, where the residual pacing (not the floor) is what governs.
        //
        // Original reasoning, still accurate as mechanism:
        // MEASURED PROBLEM: `mirror` mode, 400 games, committed defaults -- 103 timeouts
        // (26%) at an average length of 387s against the 600s cap. Tracing one of them
        // (mirror-fixed White nuke wall) shows both castles pinned at EXACTLY 90.0% HP
        // from 78s to 294s while money climbs to $117,531 and each army sits at ~40 units
        // against a 120 cap. Neither side can break through and neither side converts.
        //
        // MECHANISM: the flow pacing in SpendOnUnits reserves `stillNeeded / 45` per second
        // for the next investment. At InvestmentCount 7 (income 750, next price 40,000) and
        // 8 (income 2500, next price 121,221) that rate EXCEEDS income outright, so
        // MinAttackFlowFraction catches and the army is funded at a flat 15% of income --
        // 375/s out of 2500/s -- with the rest banked. Unit count only climbs past 100 once
        // money nears the target and the reserve finally releases.
        //
        // WHY ZEROING THE FLOOR IS THE FIX RATHER THAN RAISING IT: ArmageddonEffect's own
        // doc states the design intent outright -- "with both players on max income the late
        // game degenerated into a spam stalemate, because neither side could convert an
        // economic lead into a win. ARMAGEDDON is the conversion... meant to close the game
        // out within a few seconds in favour of whoever reached the threshold first." The
        // late game is therefore a RACE, not a fight. Money spent on an army that is already
        // in stalemate equilibrium is not buying a breakthrough, it is delaying the only
        // move that actually ends the game. Marc confirmed this reading directly.
        //
        // The bot also physically cannot spend income 2500/s on units: SpendOnUnits buys at
        // most ONE unit per decision and decisions run ~6/sec (see MaxOwnUnitsOnField's
        // comment for why that cap is deliberate and staying). So above a few hundred
        // dollars per second, banking is not a choice between army and savings -- it is the
        // only thing the surplus CAN do.
        //
        // Deliberately does NOT touch reactive defense. SpendOnUnits(preferDefense:true)
        // runs off reactiveSpendBudget on a separate path that never sees attackFlowRate,
        // so a real threat is still answered at full strength while the rush is on; what
        // stops is only the non-reactive, `!inDanger`, army-building spend.
        public bool RushArmageddon { get; init; } = false;
        // InvestmentCount at or above which the remaining ladder steps are treated as a race
        // to ARMAGEDDON rather than as economy investments. 7 is where the price overrides
        // (40,000 then 121,221) first make investPaceRate exceed income, i.e. exactly where
        // the floor starts binding; below it the residual pacing is still meaningful (at
        // count 6, income 252 vs price 8077, the attack still gets ~29% of income, unfloored).
        public int ArmageddonRushMinInvestment { get; init; } = 7;
        // Offensive flow permitted while rushing, as a fraction of income -- replaces
        // MinAttackFlowFraction in that regime only. 0 banks everything; this is a knob
        // rather than a hardcoded zero so a sweep can find the real hold-the-line level if
        // it turns out the standing army needs topping up to avoid conceding the front.
        public float ArmageddonRushAttackFraction { get; init; } = 0f;

        // (6) POST-ARMAGEDDON RESERVE RELEASE -- BUILT, MEASURED, REMOVED. Recorded here so
        // it does not get reinvented. GameEngine.Invest leaves InvestmentPrice parked at
        // 121,221 and returns false forever once ArmageddonUsed is set, so on paper the
        // pacing keeps reserving income for an impossible purchase. Releasing that reserve
        // produced a BYTE-IDENTICAL ladder (269/310/21 and 276/303/21, matching the
        // reference exactly in both modes) -- the state is reached too rarely, and Marc
        // confirms the design reason: once ARMAGEDDON fires the game ends within seconds,
        // so nothing the bot does afterwards has time to matter. Not a bug worth code.
        //
        // (7) CONCENTRATED BURST -- TESTED AND TABLED, NULL. This closes out the retest
        // that flag (3) explicitly asked for, so (3)'s "not obviously bad, never measured
        // on its own" caveat is now answered: measured on its own, it does nothing.
        //
        // Ladder 300 setups x 2 sides x 2 modes, mirror rung, vs a 44.83%/46.00% reference:
        //   Burst10 (control) 44.83% / 46.00%  -- BYTE-IDENTICAL, 269/310/21 and 276/303/21
        //   Burst25           44.83% / 44.50%
        //   Burst60           45.33% / 44.50%
        // The control reproducing the reference exactly is what makes the other two
        // trustworthy as nulls rather than as a broken flag.
        //
        // WHY IT IS NULL, which is the part worth keeping: the cap was never the binding
        // constraint. The allowance is DRAINED EVERY DECISION -- SpendOnUnits buys the
        // best-scoring affordable unit each time and ScoreUnit is cost-efficiency, which in
        // this roster favours the cheapest tier. At investment 5 the allowance accrues
        // 22.7/s and a tier-5 unit costs ~81, so it oscillates in a 0-91 band and never
        // approaches even the 227 cap, let alone 1360. Raising a ceiling the balance never
        // reaches cannot do anything. The fix has to stop the DRAIN, not raise the ceiling
        // -- which is what flag (8) does.
        //
        // Original reasoning, kept because the mechanism description is still accurate:
        // This is the allowance-cap
        // half of flag (3), split out exactly as that flag's own comment asks ("a fair
        // retest splits this into two flags and layers the cap-only half on top of
        // PaceAttackSpendForInvestment"). It does NOT touch the attack gate, so it cannot
        // reproduce the investment collapse that sank (3) -- the gate stays at Income >= 50
        // and the pacing stays exactly as committed.
        //
        // WHY THIS TARGETS THE MEASURED PROBLEM: the mirror trace shows both armies parked
        // at ~40 units with both castles pinned at exactly 90.0% HP for 200+ seconds. That
        // is a melee/cleave meat grinder at the front line -- units die as fast as they are
        // produced, so a steady trickle can never break through no matter how long it runs.
        // AttackAllowanceCapSeconds (10) caps banked allowance at 10s of flow, which keeps
        // spending in exactly that trickle shape. Raising the cap lets allowance accumulate
        // into a real burst, which matters for composition as much as timing: `spendable`
        // is what gates the `outclassing` and richMode/RawPower picks in SpendOnUnits, so a
        // larger banked allowance shifts the army toward units that can actually break a
        // line rather than the cheapest thing affordable this decision.
        //
        // This is the rejected variant-2 experiment's one real finding (sustained
        // concentrated aggression wins games) without the thing that sank it (committing to
        // a fixed TIER forever). The one-purchase-per-decision pacing is untouched.
        public bool ConcentratedBurst { get; init; } = false;
        public float BurstAllowanceCapSeconds { get; init; } = 25f;

        // (8) TECH ESCALATION -- TESTED AND REJECTED at two seeds. The diagnosis behind it
        // (below, and at the implementation site) is still correct and still worth acting
        // on; the BANKING half is what fails.
        //
        // Mirror rung, 300 setups x 2 sides, Tech40 vs reference:
        //   nostart    seed 12345  44.83 -> 49.33  (+4.5)
        //              seed 777    47.67 -> 44.67  (-3.0)   <-- SIGN FLIPS
        //   headstart  seed 12345  46.00 -> 42.67  (-3.3)
        //              seed 777    50.83 -> 43.83  (-7.0)
        // Replicated across both seeds: headstart mirror down, earned invests down ~0.21
        // (5.44->5.22 and 5.40->5.20), Investor headstart up ~+4.5 to +5.2 on one rung.
        // That is the IncomeAdvantageAttack shape again -- economy converted into
        // aggression at roughly break-even, which is the trade this bot must not make.
        //
        // READ THIS BEFORE TRUSTING ANY MIRROR-RUNG A/B: the same committed reference bot
        // scored 44.83 / 47.67 (nostart) and 46.00 / 50.83 (headstart) on the two seeds.
        // The rung moves 3-5 points on SEED ALONE, because the contender always draws
        // loadout A and the opponent loadout B, so it partly measures which loadout set
        // drew better. Ladder.cs's claim that it is "pinned at 50% by construction" is
        // wrong, and a single-seed mirror delta under ~5 points is not resolvable. The
        // +4.5 above looked like a clean win and was noise.
        //
        // WHAT SURVIVES: the diagnosis. See flag (9), which keeps the power-ranking half
        // (correct, and free) and drops the banking half (which costs the tempo and the
        // investments). Original argument, with the roster numbers it rests on, is at the
        // implementation site in SpendOnUnits.
        //
        // Short version: ScoreUnit is cost-efficiency, and in this roster cost-efficiency
        // falls monotonically with tier while raw power explodes. That makes the existing
        // outclass-by-one-tier preference dead in practice (its 0.9 score guard always
        // prefers the cheaper match) and makes richMode's RawPower path unreachable (its
        // trigger is 3x the tier-8 price, ~$69,000). So the bot mirrors the enemy's tier
        // with the cheapest efficient unit forever, which is the measured 26%-timeout
        // stalemate. This flag lets it bank allowance and buy a line-breaker instead.
        //
        // Bundles a banking rule and a power ranking ON PURPOSE, against the usual
        // one-mechanism-per-flag rule: they are not separable. Ranking by power without
        // banking cannot afford the target; banking without a power ranking just buys the
        // same chaff a moment later. Splitting them would measure two null results and
        // conclude the idea is dead. What CAN be attributed separately is the cap -- see
        // flag (7), measured on its own.
        public bool TechEscalation { get; init; } = false;
        // How long the bot may go without buying while saving for the stronger unit. Also
        // raises the allowance cap to match, since the cap would otherwise block the bank.
        public float TechHoldSeconds { get; init; } = 20f;
        // Only hold if the target is this many times stronger than what we would buy now.
        // Above 1.0 so this cannot fire on a marginal upgrade and stall production for it.
        public float TechPowerRatio { get; init; } = 2.5f;

        // (9) POWER PICK -- TESTED AT TWO SEEDS, REPLICATES, NOT YET DEFAULT. The only
        // candidate of five tried on 2026-07-30 that survived a second seed. Recommended
        // for default-on; left off only because the SearchBot session was mid-benchmark
        // against this bot and flipping it would move their baseline underneath them.
        //
        // 300 setups x 2 sides, seed 12345 / seed 777, contender vs the default reference:
        //   Investor  headstart  80.50 -> 86.67 (+6.2)  |  79.67 -> 85.00 (+5.3)
        //   Balanced  headstart  92.67 -> 94.17 (+1.5)  |  93.33 -> 93.83 (+0.5)
        //   Investor  nostart    97.50 -> 98.33 (+0.8)  |  95.00 -> 95.50 (+0.5)
        //   Tier4Spam headstart  79.17 -> 79.50         |  80.83 -> 81.00      (flat)
        //   Tier1Spam both       flat                   |  flat                (flat)
        //   mirror    both       +1.2 / +0.7            |  -0.7 / -2.8         (noise)
        //   earned invests       -0.14                  |  -0.18
        //
        // TIER1/TIER4 STAYING FLAT IS THE LOAD-BEARING NEGATIVE RESULT. Buying dearer units
        // means fielding fewer, and the `score * 0.9` guard exists because "always outclass"
        // once lost a production race to cheap spam. That regression did not reproduce, so
        // the guard is over-protective rather than necessary.
        //
        // Do NOT judge this on the mirror: that rung moves 3-5 points on seed alone (the
        // reference itself scored 44.83/47.67 nostart and 46.00/50.83 headstart), so its
        // flatness here is an absence of resolution, not an absence of effect. Investor is
        // the rung that moved, it is unsaturated at ~80%, and it is the opponent that
        // actually saves and invests -- the closest baseline to competent human play.
        //
        // The -0.16 investment cost is real but is NOT the break-even trade that sank (8)
        // and IncomeAdvantageAttack: it buys +5.3 to +6.2 on a live rung, not ~0.
        //
        // The half of (8) that survives its rejection, isolated so it pays none of (8)'s
        // costs.
        //
        // (8) bundled two things: rank by POWER instead of cost-efficiency, and BANK
        // allowance to afford the result. The measurement says the banking is what hurts --
        // it consistently costs ~0.21 earned investments and consistently loses the
        // headstart mirror, because not buying for 10-80s concedes tempo now for an army
        // that needs time to convert. The ranking costs nothing: it changes WHICH unit is
        // bought with money already spendable this decision, never WHETHER to buy.
        //
        // The underlying defect it targets is unchanged and is real (see the implementation
        // site for the roster numbers): ScoreUnit is cost-efficiency, cost-efficiency falls
        // monotonically with tier in this roster, so the outclass-by-one-tier preference's
        // `score * 0.9` guard almost always takes the cheaper match. Concretely at
        // investment 6 with spendable ~729: tier 6 costs 338 and is affordable RIGHT NOW
        // with RawPower 1884, but the current rule buys tier 5 (RawPower 496) because 5.89
        // beats 5.14 on cost-efficiency. No banking is needed to fix that -- the money is
        // already there.
        //
        // RISK, and the reason this is a flag rather than a fix: buying dearer units means
        // fielding fewer of them, and the `score * 0.9` guard was added precisely because
        // "always outclass" once lost a production race to cheap tier-1 spam. Tier1Spam and
        // Tier4Spam are the rungs to watch, not the mirror.
        public bool PowerPickAffordable { get; init; } = false;

        // (10) MULTIPLICATIVE UNIT VALUE -- KEPT. Committed default behaviour as of
        // 2026-07-31, at UnitValueCostExponent 1.7. Supersedes the diagnosis behind (8) and
        // (9) by fixing its actual root cause rather than working around it, and it came
        // from Marc, who balanced the roster.
        //
        // MEASURED at k=1.7, two seeds x 300 setups x 2 sides x 2 modes, vs the additive
        // reference. Gains replicate in every cell that matters:
        //   Investor  headstart  80.5 -> 87.8 (+7.3)  |  79.7 -> 86.2 (+6.5)
        //   Investor  nostart    97.5 -> 98.5 (+1.0)  |  95.0 -> 97.0 (+2.0)
        //   mirror    nostart    44.8 -> 47.7 (+2.9)  |  47.7 -> 50.5 (+2.8)
        //   mirror    headstart  46.0 -> 48.7 (+2.7)  |  50.8 -> 54.7 (+3.9)
        //   Balanced  headstart  +1.6                 |  +0.2
        //   Tier1Spam            flat                 |  flat
        //   Tier4Spam nostart    -2.7                 |  -1.4     <-- the one cost
        //   earned invests       -0.21                |  -0.11
        // OVERALL up in all four mode x seed cells (+0.1, +1.5, +0.7, +1.6).
        //
        // ALL FOUR MIRROR CELLS POSITIVE is the load-bearing evidence, and it is worth
        // knowing why that is trustworthy despite this file's own warning that the mirror
        // rung moves 3-5 points on seed alone: that noise is in the REFERENCE across seeds.
        // Contender-vs-reference WITHIN a seed is paired over identical specs, so it is far
        // tighter. Compare like-for-like and do not mix the two.
        //
        // NOTE this also changes REACTIVE spending: the multiplicative branch ignores
        // preferDefense, so defensive buys now use the same ranking as offensive ones. That
        // was included in everything measured above, but it means the old defensive tilt
        // (dps*1.5 + hp) is gone -- if a future regression looks defence-shaped, that is the
        // first thing to re-examine.
        //
        // ScoreUnit's cost-efficiency term is a weighted SUM -- (dps*1.8 + hp*0.8)/cost.
        // The game is balanced on a PRODUCT: effective HP x DPS / cost. That is not a
        // tuning difference, it is a different quantity, and the sum was measuring
        // something the roster was never balanced around.
        //
        // Under the sum, cost-efficiency FALLS with tier (white 7.64 at t2 -> 4.21 at t7),
        // which is what makes the outclass guard's `score * 0.9` test almost always take
        // the cheaper match (blocked at t5->t6 for 7 of 8 teams) and what makes the bot
        // mirror the enemy's tier into a permanent stalemate.
        //
        // Under the product it is STRICTLY MONOTONIC for all 8 teams, and the teams agree
        // to within ~1% at every tier -- which is how you can tell this is the real balance
        // axis rather than a plausible-looking alternative:
        //     t1     t2     t3     t4     t5     t6      t7      t8
        //   ~20    ~36    ~71   ~133   ~411  ~1125   ~6225  ~93166
        // Cost-efficiency rises ~4000x from t1 to t8, which matches the design intent that
        // top-tier units are hugely strong and often end the game.
        //
        // If this is right, most of the machinery built around the old formula stops
        // mattering: the outclass preference starts firing on its own (the higher tier now
        // always scores higher), and richMode/RawPower's unreachable $69,000 trigger stops
        // being the only path to fielding a strong unit. Measure it ALONE first -- flags (9)
        // and (10) both push toward higher tiers and would confound each other.
        //
        // Deliberately keeps SurvivabilityMultiplier and RangeMultiplier: both address
        // different questions (one-shot cleave vulnerability, melee re-engage downtime),
        // both were validated separately, and changing them here would confound this test.
        public bool MultiplicativeUnitValue { get; init; } = true;

        // MEASURED at exponent 1.0 (Marc's formula exactly), two seeds, 300 setups x 2
        // sides. A large, cleanly replicated TRADE rather than a fix:
        //   Tier4Spam nostart   82.00 -> 70.00 (-12.0)  |  79.67 -> 66.67 (-13.0)
        //   Investor  headstart 80.50 -> 88.33 (+7.8)   |  79.67 -> 87.00 (+7.3)
        //   Tier1Spam nostart   -0.5                    |  -2.0
        //   mirror    both      +2.5 / +1.5             |  +0.7 / -1.3  (noise)
        // MultValuePlusPower came back indistinguishable from MultValue on every rung,
        // which confirms flag (9) is redundant once the scorer itself is multiplicative.
        //
        // WHY IT IS A TRADE, and why the exponent exists: HP x DPS / cost is the right
        // measure of a UNIT'S QUALITY, and the roster is balanced on it to within ~1%
        // across teams. But the bot is not choosing a unit, it is choosing how to spend a
        // BUDGET, and those differ by a factor of cost. For budget B, N = B/cost units have
        // combined HP x DPS of B^2 * hp * dps / cost^2 -- so the budget-level metric is
        // hp*dps/cost^2, which ranks the roster in almost the OPPOSITE order (white peaks
        // at tier 2). Concretely $338 buys one tier-6 (380k HP x DPS) or ~19 tier-4s
        // (837k) -- the swarm is 2.2x better per dollar. Cleave cuts the other way (one
        // attacker hits everything in contact, which punishes swarms), so the truth sits
        // between the two, which is exactly what the Tier4Spam-vs-Investor split shows.
        //
        // So sweep the exponent rather than pick a side: 1.0 is Marc's unit-quality view,
        // 2.0 is the pure budget/square-law view, and the OLD additive scorer behaved like
        // something in between (which is why it survived spam matchups).
        //
        // SWEEP RESULT -- the optimum is interior, at 1.7, and the curve is not flat:
        //             ref    k=1.0   k=1.4   k=1.7   k=2.0
        //   Tier4 ns  82.0    70.0    74.3    79.3    79.3
        //   Inv  hs   80.5    88.3    89.0    87.8    80.7
        //   mir  ns   44.8    47.3    47.5    47.7    44.7
        //   mir  hs   46.0    47.5    49.5    48.7    44.5
        // k=1.7 keeps nearly all of k=1.0's Investor gain while shrinking its Tier4Spam
        // loss from -12.0 to -2.7. k=2.0 collapses back to roughly the reference on every
        // rung, which is the direct confirmation that the old additive formula was an
        // implicit k~2 -- arrived at by accident rather than by design.
        //
        // WHY 1.7 WORKS, in one table (white, hp*dps/cost^k):
        //   k=1.0   22   36   74  132  411 1127 6229 93168   <- 4000x spread, always buys
        //                                                       the top tier, gets swarmed
        //   k=1.7 10.4 13.8 16.0 17.4 19.0 19.1 29.8   82.4  <- tiers 3-6 nearly TIED, so
        //                                                       buy on affordability with a
        //                                                       mild upward lean, and go
        //                                                       big only when truly rich
        //   k=2.0  7.5  9.1  8.3  7.3  5.1  3.3  3.0    4.1  <- peaks at t2, pure swarm
        // That is a defensible policy in its own right, not just a number that scored well.
        public double UnitValueCostExponent { get; init; } = 1.7;

        // (11) SEPARATE DEFENSIVE EXPONENT -- TESTED AND REJECTED. Kept at parity with
        // UnitValueCostExponent, i.e. a no-op, and retained only so the result is recorded
        // and the knob exists if a future change makes the question live again.
        //
        // Seed 12345, 300 setups x 2 sides x 2 modes. Def17 (control, defensive exponent =
        // the committed 1.7) came back BYTE-IDENTICAL to the reference on all 12 rungs,
        // which proves the split itself is inert and that the columns below are the
        // exponent alone:
        //            ref    Def20   Def23
        //   mir ns   44.8    45.5    44.8
        //   mir hs   45.5    45.2    45.2
        //   Inv ns   98.5    98.3    97.5
        //   Inv hs   87.8    87.5    86.8
        //   T4  ns   79.3    79.3    80.7
        //   T4  hs   76.8    76.5    78.8
        // Def23 does exactly what the theory predicted on Tier4Spam (+1.4/+2.0) and pays
        // for it on Investor (-1.0/-1.0) while leaving the mirror flat. That is the trade
        // this project has explicitly decided NOT to take -- see the judging note below.
        // Not replicated at a second seed: the bar for rejecting is lower than for landing.
        //
        // USEFUL NULL: this retires an open risk left by flag (10). That flag's
        // multiplicative branch ignores preferDefense, silently dropping the old defensive
        // tilt, which was flagged as "first thing to re-examine if a defence-shaped
        // regression appears". Splitting it back out gains nothing, so the loss was
        // harmless and a SINGLE global exponent is genuinely correct. Plausible mechanism:
        // cleave punishes cheap swarms hardest exactly when you are the one being hit,
        // which cancels the "cheap units replace faster" advantage that motivated this.
        //
        // Original reasoning, kept because the argument against opponent-classification is
        // still the right guidance for whatever gets tried next:
        //
        // WHY THIS SHAPE, and explicitly why NOT an opponent classifier. The obvious read
        // of the k sweep is "swarm beats quality against spam, quality beats swarm against
        // an economic opponent", which invites keying k off a detected opponent TYPE. Marc
        // rejected that, correctly: the two baselines are deliberate extremes, real players
        // sit between them, and a real player sends a wave of one unit and then CHANGES
        // tactic -- so an opponent-identity read would be wrong for most of a real game.
        // The project has also already tried exactly that mechanism and it underdelivered
        // (the confidentStaticSpammer gate, rejected variant 3 -- see the long note in
        // Decide()). Do not revive it.
        //
        // The better hypothesis is that opponent type was never the operative variable --
        // BOARD STATE was. What makes swarm correct against spam is sustained continuous
        // pressure on our own line, which is a condition a human produces too during an
        // attack wave, and which ENDS when they switch tactics. The bot already measures
        // that condition and calls it preferDefense:
        //   - Reactive defence needs continuous replacement across a line being hit right
        //     now. Cheap units arrive sooner and spread wider => higher exponent.
        //   - An offensive push is massing to break a line, where concentration is the
        //     whole point => lower exponent.
        // So it keys off what is happening, not off who is playing, and it tracks a human
        // who alternates between the two within a single game.
        //
        // This also repairs collateral damage from flag (10): the multiplicative branch
        // ignores preferDefense, so the old defensive tilt (dps*1.5 + hp + shield vs
        // dps*1.8 + hp*0.8) was silently lost. This restores that distinction in the new
        // formula's own terms rather than by reintroducing the additive one.
        //
        // JUDGE THIS ON THE MIRROR, not on Tier4Spam. Marc's point stands: a decent
        // economic game should already beat a spammer, the spam rungs are not a proxy for
        // human play, and the mirror is both the most balanced opponent available and the
        // thing RolloutSearchBot actually rolls out against.
        public double UnitValueCostExponentDefense { get; init; } = 1.7;

        // (12) WAVE-WIPE VALUE -- attempt 1 TESTED AND CATASTROPHIC; attempt 2 (this one)
        // untested. Marc's description of the single biggest economic swing in the game,
        // which the bot currently cannot see.
        //
        // ATTEMPT 1 made this an INDEPENDENT trigger for reactive spending and also
        // suppressed the non-reactive attack branch while it was active. Seed 12345,
        // nostart, at fraction 0.25 unless noted:
        //   Investor   98.5 -> 60.5   (-38.0)
        //   mirror     44.8 -> 26.3   (-18.5)
        //   Tier1Spam  93.3 -> 80.3   (-13.0, at fraction 0.50)
        //   earned invests 5.19 -> 4.82 -> 3.60
        // Three compounding errors, all mine, none of them a problem with the strategy:
        //   1. "3+ enemies within 500 of our castle" is an ORDINARY board state, not a
        //      committed wave, so the thing fired almost continuously.
        //   2. No notion of ENOUGH. The play is that ONE unit wipes the wave; the code let
        //      the bot re-buy a wiper every decision (6/sec) for as long as the stack sat
        //      there. The budget was meant to be spent once and was never bounded.
        //   3. Suppressing the attack branch meant the bot stopped pressing entirely
        //      whenever anything was near its castle.
        // Together these reproduced the permanent-reactive-mode pathology documented all
        // over this file. A textbook instance of it.
        //
        // The premise behind the independent trigger was also just wrong: when a wave
        // genuinely commits, timeToDeath drops, investmentRunwayIsSafe goes false, and
        // inDanger is ALREADY true. The ordinary path reaches the purchase by itself. What
        // it lacks is permission to spend enough, not a new reason to act.
        //
        // ATTEMPT 2 therefore does one thing only: raise reactiveSpendBudget on the
        // existing inDanger path. No new trigger, no change to the attack branch.
        //
        // THE MECHANIC (verified in GameEngine, not assumed): every attack is full-damage
        // AoE with no falloff and no target cap. MoveAndFight calls FindTargetsFast, which
        // returns a LIST, and then does `for each enemy: pendingUnitDamage.Add(enemy,
        // def.Damage, ...)`. So one defender hits EVERY enemy in range at full damage, and
        // its effective DPS multiplies by the size of the stack it is hitting.
        //
        // THE PLAY: let a wave commit against your castle, tanking with castle HP (repair
        // multiplies MaxHealth, 2000 -> 12000 on the first one, so time is cheap to buy).
        // Then ONE unit a tier or two above the wave wipes the entire stack. Their $300 of
        // army dies to your $50 unit. That swing compounds into a winning economy, and the
        // symmetric risk -- your own committed wave can be erased the same way -- is what
        // makes choosing when to attack hard.
        //
        // WHY THE BOT CANNOT DO THIS TODAY, and it is a missing term rather than a
        // mistuned one. Reactive spending is budgeted purely as
        //     reactiveSpendBudget = max(0, timeToInvest - timeToDeath) * income * 1.5
        // which asks only "what is buying time toward MY investment worth?" It contains no
        // term for enemy value DESTROYED. Worse, the whole reactive path is gated behind
        // inDanger, which is itself a runway question -- so when the runway looks fine the
        // bot never even reaches the purchase, no matter how much enemy money is stacked in
        // front of it. This flag adds both: the value term, and an independent trigger.
        //
        // NOT a fifth rerun of the rejected reactive-spend experiments. All four of those
        // CAPPED or REALLOCATED defensive spending (see SpendOnUnits' history). This adds a
        // REASON to spend that was never modelled.
        //
        // TIMING FALLS OUT FOR FREE, which is why the value is measured over enemies
        // already near our own castle rather than anywhere on the map: the term is only
        // large once the wave is bunched and committed, so the bot naturally holds instead
        // of walking a defender out to meet a spread-out wave piecemeal and forfeiting the
        // AoE stacking. No explicit "wait" rule is needed.
        // ATTEMPT 2 MEASURED AND KEPT -- default as of 2026-07-31, at fraction 0.50.
        // Two seeds x 300 setups x 2 sides x 2 modes, vs the k=1.7 reference:
        //   Tier1Spam ns  93.3 -> 98.3 (+5.0)  |  93.7 -> 97.3 (+3.6)
        //   Tier1Spam hs  95.7 -> 98.7 (+3.0)  |  95.3 -> 99.0 (+3.7)
        //   Investor  ns  98.5 -> 99.8 (+1.3)  |  97.0 -> 99.3 (+2.3)
        //   Investor  hs  87.8 -> 88.5 (+0.7)  |  86.2 -> 87.8 (+1.6)
        //   Tier4Spam hs  +0.5                 |  +1.4
        //   mirror        +0.7 / +0.7          |  -1.1 / -1.1   (noise)
        //   earned inv    5.19 -> 5.18         |  5.21 -> 5.21  (NO COST)
        // No consistent regression on any rung. Investing untouched is the decisive
        // contrast with attempt 1, which crashed it to 3.60.
        //
        // Tier1Spam moving most is mechanically right rather than incidental: it stacks the
        // most cheap units against the castle, which is precisely where full-damage AoE
        // makes one defender's effective DPS multiply hardest. The mirror is flat, so by
        // the usual "judge on the mirror" rule this is neutral -- it is kept because it is
        // strictly free (no rung down, no investments lost) and because it implements a
        // mechanic Marc identifies as central to real play that the benchmark CANNOT fully
        // exercise: no rung commits big waves the way a human does. Tier1Spam is the
        // closest proxy available and it is the rung that moved.
        public bool WaveWipeValue { get; init; } = true;

        // (13) TIME GADGETS ENGAGE EARLY AND FAR -- TESTED AND REJECTED for freeze; the
        // blackhole half remains UNTESTED. Kept off.
        //
        // Measured twice. Unpinned (300 setups x 2 sides x 2 modes) it was a near-total
        // no-op, several rungs byte-identical. Then re-run with `--offense freeze` pinned on
        // BOTH sides, i.e. 100% exposure instead of ~25% (250 setups): STILL byte-identical
        // on Investor / Tier1Spam / Tier4Spam nostart, mirror -1.0.
        //
        // THE DIAGNOSIS BEHIND IT WAS SIMPLY WRONG, and the error is worth recording because
        // it is easy to repeat. The claim was "freeze cannot fire until the threat arrives,
        // because buyTimeJustifies requires inDanger which requires enemyIsClose". But
        // buyTimeJustifies is ONE OF THREE justifications and by far the least binding:
        //   - killValueJustifies needs no danger at all. Freeze deals flat BaseValue damage
        //     (10 at level 1), which one-shots every team's tier-1 unit, so against any
        //     cheap swarm the kill-value test passes immediately.
        //   - multiplierJustifies needs only myUnits.Count > 0 and BigSpendJustified, and
        //     the latter waves anything through once Income >= 50.
        // Freeze was therefore ALREADY casting early and often. There was nothing to
        // unblock. Reading one clause of a multi-clause trigger and generalising to the
        // gadget is the mistake; check the siblings first.
        //
        // The blackhole targeting inversion (preferFarFromMyCastle) may still be right --
        // its rationale is independent and untouched by the above -- but blackhole is a
        // single team's signature gadget, so it stays diluted even under --offense pinning,
        // and Ladder.cs cannot pin a signature without pinning the team. Measuring it needs
        // a --team pin first. Do not judge that idea by this result.
        //
        // Original reasoning, still correct as a description of the MECHANIC:
        //
        // THE DISTINCTION (Marc): DAMAGE gadgets want the FRONT of the enemy formation --
        // the most threatening cluster, closest to our castle. That is what
        // FindBestAoeTarget's threatWeight encodes and it is correct for nuke / firebomb /
        // meteor / poison. TIME gadgets are the opposite. Freeze and Blackhole buy the SAME
        // amount of time wherever they land, so landing them while the army is still at ITS
        // OWN end of the map buys the stall PLUS the whole march back across the screen --
        // and that is long enough for the cooldown to return, so the same gadget can be
        // used again. Engaging early is what makes the loop close.
        //
        // Marc's worked example (blue team): freeze the force as it spawns, start a trickle
        // of cheap units to slow it further, drop a wall, freeze again as they break
        // through, then Wave them back to their own castle when they finally arrive --
        // by which point freeze is up again. Near-infinite stalling, while the economy
        // keeps compounding toward the next investment.
        //
        // WAVE IS DELIBERATELY EXCLUDED and stays castle-anchored: its value is the
        // knockback distance, so it is worth most when they have already arrived. It is the
        // one time gadget that genuinely wants them close.
        //
        // TWO DEFECTS THIS FIXES, both currently the wrong way round:
        //  1. freeze's buyTimeJustifies is `inDanger && ...`, and inDanger requires
        //     enemyIsClose (within EnemyIsCloseDistance of OUR castle). So the bot
        //     structurally cannot freeze a force while it is still marching -- exactly the
        //     cast that starts the loop.
        //  2. blackhole routes through FindBestAoeTarget, whose threatWeight pulls the aim
        //     point TOWARD our own castle. Right for damage, backwards for CC.
        //
        // Bundled as one flag on purpose despite the usual one-mechanism rule: freeze is 1
        // of 4 offense options and blackhole is a single team's signature, so each alone
        // appears in a small minority of games and neither would be resolvable on its own.
        // They are the same principle applied twice.
        public bool StallGadgetsEngageEarly { get; init; } = false;
        // How many enemy units make a "force" worth spending a stall gadget on, regardless
        // of where they are. Below this it is a skirmish, not an attack.
        public int StallForceMinUnits { get; init; } = 5;
        // Don't wreck the economy to stall: cap an early cast at this fraction of money.
        // The whole point of stalling is to keep saving, so a cast that empties the wallet
        // defeats its own purpose.
        public double StallGadgetMaxMoneyFraction { get; init; } = 0.4;
        // How much of the enemy's committed value we are willing to spend to erase it.
        // Marc's own example is ~$50 against ~$300 (0.17); this is a CAP, not a target --
        // SpendOnUnits still buys the cheapest sufficient unit under it.
        public double WaveWipeValueFraction { get; init; } = 0.5;
        // Distance from our castle within which an enemy counts as "committed". Tighter
        // than EnemyIsCloseDistance (700), which means "approaching" rather than "arrived".
        public float WaveWipeRadius { get; init; } = 500f;
        // AoE value comes from hitting several things at once, so require a real stack --
        // a lone straggler is not a wave and does not justify the spend.
        public int WaveWipeMinUnits { get; init; } = 3;

        // (8b) TIME-AWARE HOLD -- BUILT, RUN, AND THE TEST WAS INVALID. Recorded so nobody
        // reads the flat numbers as evidence either way.
        //
        // Tech40T measured 49.83/42.33 against Tech40's 49.33/42.67 -- i.e. no effect. That
        // is NOT a disconfirmation of the remaining-time hypothesis; the gate never fired.
        // TechTimeSafetyFactor=3 with a 40s hold demands only 120s of remaining game, while
        // headstart games start at tick 30*30*timeSkip (max 7200) against MAX_TICKS 18000
        // and so have 360-600s left. The threshold was never within reach of binding.
        // A real test needs a factor large enough to actually engage, or a gate expressed
        // as a FRACTION of remaining time rather than a multiple of the hold. Moot unless
        // flag (8)'s banking is revived, which the two-seed rejection argues against.
        //
        // Original motivation, unchanged and still untested:
        //
        // MOTIVATION: the seed-12345 sweep split cleanly by MODE -- nostart mirror +3.2 to
        // +4.5, headstart mirror -3.3 to -5.2, consistently across all three hold lengths.
        // The likely cause is that headstart games START at tick 30*30*timeSkip while
        // GameEngine.MAX_TICKS is a fixed 18,000, so they have materially LESS time left to
        // play. A 10-80s production hold is a much larger share of a short remaining game,
        // and its cost (no units produced now) is immediate while its payoff (a stronger
        // army) needs time to convert into castle damage. Holding 40s with 60s left is a
        // bad trade no matter how good the unit is.
        //
        // This is a real game rule the bot is entitled to know -- the 10-minute limit is
        // visible to a human player -- so reading it is not a hidden-information liberty.
        public bool TechTimeAware { get; init; } = false;
        // The hold may consume at most 1/this of the remaining game.
        public float TechTimeSafetyFactor { get; init; } = 3f;

        // ── DEFENCE-ONLY MODE (2026-08-22) ──────────────────────────────────────
        //
        // Removes offensive unit spawning entirely and buys bodies purely in response to
        // what the enemy has on the field, sized by the measured survival law (see
        // ThreatModel and CastleDefense.BotArena/stall/FINDINGS.md).
        //
        // DEFAULT FALSE ON PURPOSE. The attacking bot stays the shipped one, byte for byte:
        // it is the ladder rung, the counter-table was fitted from it, and RolloutSearchBot
        // uses it as BOTH its policy prior and its rollout policy for both sides -- flipping
        // this by default would silently change every one of those at once.
        //
        // The win condition is unchanged and does NOT depend on attacking: buying the invest
        // at InvestmentCount 8 triggers ARMAGEDDON (GameEngine.Invest -> ArmageddonEffect),
        // which shields us with divine_3 and rains meteors, firebombs, waves and nukes on the
        // enemy castle. So the whole job is: survive long enough to invest eight times.
        /// <summary>
        /// (1) CHARGE-AWARE PURCHASE FALLBACK. When the ranked pick has no charge left,
        /// fall through to the best-scoring unit that is both affordable AND charged,
        /// instead of making one attempt that silently fails.
        ///
        /// THE BUG THIS FIXES. Every filter in SpendOnUnits' pick pipeline -- RankPool,
        /// outclassPick, matchedPick, the anyAffordable fallback, PowerPickAffordable and
        /// TechEscalation -- tests `def.Cost &lt;= spendable` and nothing else. The method
        /// then makes exactly ONE attempt, `Act(() =&gt; engine.SpawnUnit(_side, pick.def.Id))`,
        /// with no fallback. Since unit charges shipped (2026-09-01) SpawnUnit refuses a
        /// purchase with no charge left, so the bot converges on one unit id, drains its
        /// five charges in under a second at six decisions/sec, and then re-picks that same
        /// uncharged id for most of the rest of the game while a fully-charged second choice
        /// sits unused.
        ///
        /// IT FAILS SILENTLY, which is why it survived. LastUnitsPurchased, ActionCounts[],
        /// GameEngine.UnitsPurchased[] and MoneySpentOnUnits[] all increment on SUCCESS
        /// only, so a refused purchase leaves no trace in any counter or log.
        ///
        /// MEASURED BEFORE THE FIX: `bot-checksum --games 4` gave 0.94-1.41 units/sec
        /// (mean 1.14) against a six-decisions/sec cadence, with $39k-$55k left unspent at
        /// the final tick. 1.14/sec is almost exactly PlayerState.UnitChargeRegenMs = 1000,
        /// i.e. the refill rate of a SINGLE unit id -- the signature of a bot that never
        /// rotates. CLAUDE.md independently records purchases falling 101.9 -> 35.4 per
        /// 1000 ticks (-67%) when charges shipped.
        ///
        /// DELIBERATELY A FALLTHROUGH, NOT A PRE-FILTER. Filtering the ranked pools on
        /// charges would change which unit is chosen even when the top pick IS available,
        /// silently re-tuning the outclass rule and TechEscalation. This engages only on the
        /// decisions that would otherwise have bought nothing at all, so on every other
        /// decision the bot is byte-identical to the reference.
        ///
        /// SCOPED TO SpendOnUnits. The wiper purchase and DefensiveResponse's block spawn
        /// have the same shape and are NOT covered here, so this iteration measures one
        /// mechanism. See BOT_BACKLOG.md.
        /// </summary>
        /// PROMOTED TO DEFAULT 2026-09-02 on Marc's instruction, after iteration 1 measured
        /// +2.6 to +7.3 points head-to-head across four arms (two seeds x two modes,
        /// 5,600 games per arm) with earned investments unmoved to two decimals. Set
        /// ChargeAwareFallback = false, or use HeuristicBotSettings.PreChargeAware, to
        /// reproduce anything measured before that date.
        public bool ChargeAwareFallback { get; init; } = true;

        /// <summary>
        /// (8) THE SAME CHARGE TEST ON THE THREE SPAWN PATHS ITERATION 1 LEFT OUT: the wiper
        /// purchase in Decide(), FindWiper, and DefensiveResponse's blocking body.
        ///
        /// Iteration 1 was deliberately scoped to SpendOnUnits so one mechanism was measured
        /// at a time. These three have the identical shape -- pick on price, one attempt, no
        /// fallback -- and DefensiveResponse's case is the worst of the four, because
        /// _blockCredit is only decremented inside `if (Act(...))`. A refused spawn there
        /// banks credit up to MaxBlockCredit that can never be spent, so the bot believes it
        /// is blocking at the survival law's rate while actually delivering one unit id's
        /// charge regeneration. Its own doc comment still claims a ceiling of 6 bodies/sec.
        ///
        /// A RE-ALLOCATION, NOT A NEW CHANNEL -- it changes which unit is bought and never
        /// how much is spent, which is the shape iterations 1-7 found to be the safe one.
        /// </summary>
        public bool ChargeAwareEverywhere { get; init; } = false;

        /// <summary>
        /// (3) BLOCK A LONE CHIPPING UNIT. Buy one cheap body whenever an enemy is standing
        /// on our castle with nothing of ours in contact with it -- regardless of what the
        /// EV budget says.
        ///
        /// THE HOLE. Marc's own report, 2026-09-01, after watching RolloutSearchBot exploit
        /// it: the bot almost never clears a single unit off its castle. A lone attacker is
        /// refused by all four defence paths at once, and each refusal is correct in
        /// isolation:
        ///   - time-to-death against one unit is 100+ s, so `investmentRunwayIsSafe` is true,
        ///     `inDanger` is false, and SpendOnUnits(preferDefense) is never even called;
        ///   - if it were, `runwayDeficitSeconds = max(0, timeToInvest - timeToDeath)` is 0,
        ///     so `reactiveSpendBudget` is $0, `spendable` is 0, and nothing is affordable;
        ///   - `waveWipeOpportunity` requires WaveWipeMinUnits = 3;
        ///   - `survivalEmergency` requires time-to-death &lt;= 12 s.
        ///
        /// THIS IS A BAND-AID THAT OUTLIVED ITS PURPOSE. `reactiveSpendBudget` was added
        /// after playtest D3596E, where the bot spent hundreds of dollars defending against a
        /// single unit that DPS-vs-HP math showed was barely a threat. The cap was right; its
        /// PRICE BASIS is not. It values the threat against TIME-TO-DEATH, when the correct
        /// comparison is a $1-4 chump against the CUMULATIVE castle HP the unit will remove.
        /// Only Repair heals a castle, so that damage is permanent and unbounded -- a rate
        /// model cannot see a stock. Precisely the incoming-nuke blind spot inverted.
        ///
        /// WHY A BODY AND NOT DAMAGE. MoveAndFight attacks the castle only in an
        /// `else if (castleInRange)` branch: one enemy body in contact means zero castle
        /// damage that tick. Blocking is a hard stop, not a damage race, and a body absorbs
        /// one swing whatever it costs -- so the cheapest unit is strictly the right buy.
        ///
        /// RATE-LIMITED BY THE SURVIVAL LAW, not by a flat interval. Credit accrues at the
        /// summed attack rate of the unblocked chippers, so the bot buys exactly one body per
        /// enemy swing and no more. Without that this becomes the permanent-reactive-spend
        /// pathology this file documents four separate times.
        /// </summary>
        public bool BlockSingleChipper { get; init; } = false;

        /// <summary>How close to our own wall an enemy must be to count as "on the castle".</summary>
        public float ChipBlockDistance { get; init; } = 50f;

        /// <summary>Edge gap under which one of our units counts as already blocking a chipper.</summary>
        public float ChipBlockContactPad { get; init; } = 10f;

        /// <summary>
        /// Affordability discipline: the blocker may cost at most this many seconds of
        /// income. At the opening income of $2/s this allows $4, which covers every team's
        /// tier-1 unit ($1-$4), and it scales itself out of relevance as income grows. This
        /// is what stops the rule competing with the first investment rung.
        /// </summary>
        public double ChipBlockIncomeSeconds { get; init; } = 2.0;

        /// <summary>Cap on banked blocking credit, so a quiet spell cannot fund a burst.</summary>
        public float ChipBlockMaxCredit { get; init; } = 2f;

        /// <summary>
        /// (3b) THE SPEND-RATE CAP v1 LACKED. Chip blocking may consume at most this
        /// fraction of income, as a banked dollar allowance.
        ///
        /// WHY v1 FAILED, precisely. The rate limit is the survival law -- one body per enemy
        /// SWING -- and swing rate is exactly what the roster clamps hardest.
        /// GameDataManager recomputes AttackSpeed and clamps it to [0.2, 5.0]; tier-8 units
        /// clamp at the BOTTOM (0.20/s) and tier-4 units clamp at the TOP (5.0/s). So the law
        /// asks for ~0.2 bodies/sec against a tier 8 and up to 5 bodies/sec against tier 4 --
        /// a 25x spread. The stall findings' "$2/sec holds anything" is specifically about a
        /// lone tier 8.
        ///
        /// v1 capped the PRICE OF EACH BODY (ChipBlockIncomeSeconds) but never the RATE of
        /// spending, so against tier-4 pressure it bought ~5 bodies/sec on a $2/sec income
        /// and drained the wallet continuously. Earned investments against Tier4Spam more
        /// than halved (4.72 -> 2.28) and that rung fell 80.4% -> 27.8%.
        ///
        /// A FLOW CAP IS THE ESTABLISHED SHAPE for this in this file -- see ReactiveFlowCap
        /// and the attack allowance. It banks, so a quiet stretch still funds a real burst
        /// when a chipper actually arrives, but cumulative chip spending can never outrun
        /// cumulative income. When the law asks for more than the cap allows, the bot buys
        /// what it can afford and otherwise does what it did before: out-economies the
        /// opponent instead of trading with it.
        /// </summary>
        public double ChipBlockIncomeFraction { get; init; } = 0.25;

        /// <summary>Seconds of chip allowance that may bank while nothing is attacking.</summary>
        public double ChipAllowanceCapSeconds { get; init; } = 8.0;

        /// <summary>
        /// (2) BUY THE AUTO-SPAWNER. No bot in the project can currently do this: action 14
        /// is absent from GetActionMask, and RolloutSearchBot builds its candidate list from
        /// `a = 8..1` plus {9,10,11,12,13}. But HeuristicBot bypasses the action space
        /// entirely -- it calls engine.Invest / Repair / SpawnUnit / UseGadget directly -- so
        /// engine.UpgradeAutoSpawn is reachable with NO mask change, no observation-vector
        /// change and no ONNX checkpoint invalidated.
        ///
        /// WHY IT IS WORTH A RUNG. It buys BODIES PER SECOND, which since the 2026-09-01
        /// charge change is a resource money alone can no longer buy: auto-spawner units are
        /// `ignoreCost`, so they consume no charge and are not subject to the
        /// one-purchase-per-decision pacing. It is the only lever that raises the ceiling
        /// iteration 1 merely stopped wasting.
        ///
        /// THE PRICES MAKE THE CASE. Level 1 is $102 for one free body per second, forever.
        /// The survival law says one body per enemy SWING holds anything, and every tier-8
        /// unit's AttackSpeed clamps at the BOTTOM of its range (0.20/s, one swing per five
        /// seconds) -- so level 1 alone is five times the rate needed to neutralise a lone
        /// tier 8, permanently, for a one-off $102. Level 5 is $860 cumulative for three
        /// bodies per second.
        ///
        /// PRICED AGAINST THE INVESTMENT RUNG, NOT AGAINST MONEY. Every previous "spend
        /// earlier" experiment in this file died by competing with investing, so the rule
        /// only buys a level when it is cheap RELATIVE to the next rung and the runway is
        /// already safe -- i.e. out of the same surplus the early-invest claim has declined
        /// to take. AutoSpawnMaxLevel caps the ladder deliberately low so this measures
        /// "cheap early bodies" rather than "convert the whole economy into the machine".
        /// </summary>
        /// <summary>
        /// (9) Let the gadget layer commit to the investment rung at EVERY count, not just
        /// the first three. See DeferForInvestment for the measurement that motivated it.
        /// </summary>
        /// PROMOTED TO DEFAULT 2026-09-02. Measured +5.0 to +5.6 points head-to-head in all
        /// four arms (two seeds x two modes) with earned investments UP 0.11-0.15 -- the
        /// mechanism working as designed, since committing to the rung is what buys the rung.
        public bool CommitToRung { get; init; } = true;

        /// <summary>
        /// How close to the next rung counts as committed. 0.6 holds non-urgent gadget casts
        /// once the bot is 60% of the way there. Lower is stricter.
        /// </summary>
        public double RungCommitFraction { get; init; } = 0.6;

        /// <summary>
        /// (10) Stop funding unit attacks once ARMAGEDDON is the only rung left. See the
        /// block in SpendOnUnits for why RushArmageddon does not do this on the shipped path.
        /// </summary>
        public bool ArmageddonCommit { get; init; } = false;

        public bool BuyAutoSpawner { get; init; } = false;

        /// <summary>
        /// Highest auto-spawner level this rule will buy. 5 is $860 cumulative for 3 free
        /// bodies/sec; the ladder runs to 19 and $280,427, which is a different hypothesis.
        /// </summary>
        public int AutoSpawnMaxLevel { get; init; } = 5;

        /// <summary>
        /// Buy a level only when it costs at most this fraction of the next investment
        /// price. Keeps the purchase inside the surplus rather than in front of the rung --
        /// the failure mode of every rejected early-spend variant in this file.
        /// </summary>
        public double AutoSpawnMaxFractionOfRung { get; init; } = 0.5;

        /// <summary>
        /// Refuse to buy once money has climbed past this fraction of the next investment
        /// price. Spending EARLY in an accumulation cycle costs the rung a little; spending
        /// just before the rung lands costs it the whole cycle. Same shape as
        /// UpgradeSpamInvestCommitFraction, and the same reason.
        /// </summary>
        public double AutoSpawnInvestCommitFraction { get; init; } = 0.5;

        /// <summary>
        /// (2b) BUY THE AUTO-SPAWNER BY SUBSTITUTION, out of the money already earmarked for
        /// units, instead of out of savings.
        ///
        /// THIS IS THE NIGHT'S MAIN FINDING APPLIED. Four hypotheses were rejected in a row
        /// and they shared one mechanism: each opened a NEW spending channel and each lost
        /// earned investments, because every cap in this file (AttackSpendFraction,
        /// InvestPaceTargetSeconds, ReactiveFlowCap, AttackGateMinInvestment,
        /// reactiveSpendBudget) was tuned assuming no other channel exists. The one change
        /// that was kept -- ChargeAwareFallback -- only re-allocated WHICH unit was already
        /// being bought, and moved earned invests by 0.00.
        ///
        /// v1 of the auto-spawner spent money the investment claim had declined, i.e. savings,
        /// and cost 0.81 earned investments on the mirror rung. This version spends the ATTACK
        /// ALLOWANCE instead. That is the honest place for it: the machine produces units, so
        /// it should compete with buying a unit, not with buying a rung.
        ///
        /// PRICED IN ROSTER DOLLARS PER SECOND, not in units per second. Units/sec cannot tell
        /// level 2 ([1,1]) from level 3 ([2,1]) -- both deliver 2/s and only the tier mix
        /// differs -- so a rate-based value model stalls at the first rung that buys quality.
        /// Summing the cycle's roster costs prices both at once, and it is the same currency
        /// the purchase is made in.
        /// </summary>
        public bool AutoSpawnFromAttackBudget { get; init; } = false;

        /// <summary>
        /// A level must repay its price in free unit value within this many seconds. Games
        /// average ~250 s, so 45 s is deliberately strict -- it buys only the rungs that are
        /// obviously cheap rather than betting on the game running long.
        /// </summary>
        public double AutoSpawnPaybackSeconds { get; init; } = 45.0;

        /// <summary>
        /// (4) FARM CHEAP GADGET UPGRADES. Replaces GadgetUpgradeSpam's income test with an
        /// absolute-cost one. See the gate itself in TryUpgradeSpam for the reasoning: XP is
        /// a flat 100 per cast for every gadget, so an upgrade is a FINITE purchase of
        /// `ceil((UpgradeCost - xp) / 100)` casts, and pricing it as an infinite drain is
        /// what defers it until it no longer matters. Requires GadgetUpgradeSpam = true.
        /// </summary>
        public bool CheapGadgetUpgrades { get; init; } = false;

        /// <summary>
        /// Total cost of finishing the next gadget upgrade, expressed in seconds of income.
        /// At 25 s and the opening $2/s that allows a $50 ladder; it opens up as income
        /// climbs, which is the intended shape -- cheap ladders early, expensive ones later.
        /// </summary>
        public double CheapUpgradeIncomeSeconds { get; init; } = 25.0;

        public bool DefenceOnly { get; init; } = false;

        // Incoming castle DPS at which the attack is worth answering at all.
        //
        // THIS REPLACED "WAIT UNTIL THE FORCE STOPS GROWING", WHICH WAS ANTI-ADAPTIVE. Against
        // an opponent who reinforces continuously the force NEVER stops growing, so the settle
        // rule never released and the only thing that ever triggered a response was the panic
        // override at a few seconds from death. Traced: a 45-unit wave walked in over four
        // seconds while the bot sat on full credit and full pockets and spawned nothing,
        // starting its defence with 4.7s to live instead of 10.6s.
        //
        // A DPS threshold is the right shape because it is what actually distinguishes a
        // threat from traffic: a stream of low-tier units may never stop arriving and never
        // matter, while one high-tier unit matters immediately. 850 is Marc's own read of
        // where that line sits -- roughly "ignore a tier 6, always answer a tier 7".
        public float ThreatEngageDps { get; init; } = 850f;

        // Read the force anyway, settled or not, once predicted survival with NO defence
        // falls under this. Waiting for a read we cannot afford is how a defensive bot dies
        // tidily.
        public float DefenceReadPanicSeconds { get; init; } = 6f;

        // Safety margin on the survival target. The law predicts survival TIME well but the
        // critical rate less well (measured r_crit/S ran 0.11-0.14 against tier 5 and 1.9-2.6
        // against tier 7 in numbers), so aim past the target rather than exactly at it.
        public float DefenceTargetMultiplier { get; init; } = 1.35f;

        // How far ahead the defence plans, in seconds, capped by the real remaining runway
        // to ARMAGEDDON.
        //
        // THIS REPLACED "SURVIVE UNTIL THE NEXT INVESTMENT", WHICH WAS BACKWARDS. Time-to-next
        // -investment goes to ZERO exactly when the bot is rich enough to already afford it --
        // so the target collapsed to the safety buffer alone (~2s) and the defence went thin at
        // the precise moment it could most afford not to. Measured: the bot was dying at
        // investment 7-8, one purchase short of the win condition, while out-trading the
        // attacker 6-39x on dollars. It was not overspending, it was barely spending.
        //
        // Capped rather than set to the whole runway because a wave is transient: the force on
        // the board now will not still be there in three minutes, and sizing against the full
        // runway would buy defence against an attack that has already died.
        public float DefenceHorizonSeconds { get; init; } = 45f;

        // Seconds a wipe needs between the decision and the swing landing: the unit has to
        // be bought, walk to the pile and get one attack off. The deadline guard refuses to
        // keep waiting inside this window, because a wipe ordered too late is just a wasted
        // purchase on top of the damage.
        // The only lead constant. A `WipeLeadMarginSeconds` (3f) sat here alongside it,
        // documented as "extra headroom, because the survival law predicts TIME well but the
        // exact cliff less well" -- and was never read by anything. Deleted 2026-08-29. If
        // that headroom is wanted, widen WipeLeadSeconds or add the margin AT the subtraction
        // in the deadline guard; do not reintroduce a settings field that nothing consumes,
        // because a knob a sweep can randomise but no code reads reports "this parameter does
        // not matter" when the truth is "this parameter is not connected" (the same trap
        // KillerInstinctHpThreshold is already documented for, above).
        public float WipeLeadSeconds { get; init; } = 4f;

        // Smallest share of the law's required block rate that is worth paying for at all.
        //
        // FOLLOWS DIRECTLY FROM THE MEASURED CONVEXITY, which the first implementation ignored:
        // dt/dr = K/(S-r)^2 GROWS as r approaches S, so a fraction of the needed rate buys
        // almost nothing. Feeding two bodies a second into a wave that needs two hundred is not
        // a partial defence, it is a donation -- the blockers walk out, get picked off
        // piecemeal mid-field, and the wave arrives anyway.
        //
        // Below this share the bot deliberately saves the money instead, for a wiper or for the
        // gadget layer, which are the tools that actually scale to a swarm.
        public float MinBlockEffectiveness { get; init; } = 0.5f;

        // Bodies-owed the response may bank while it waits for money or the decision clock.
        // Without a cap a long quiet stretch would accrue a debt the bot then dumps all at
        // once, which is neither useful nor affordable.
        public float MaxBlockCredit { get; init; } = 3f;

        public static readonly HeuristicBotSettings Default = new HeuristicBotSettings();

        /// <summary>The 100% defensive bot. Everything else is the shipped attacking config.</summary>
        public static readonly HeuristicBotSettings DefenceOnlyProfile =
            new HeuristicBotSettings { DefenceOnly = true, WiperMinIntervalSeconds = 1.0 };

        /// <summary>
        /// The flagship plus the three repair fixes. Everything else is untouched, so a
        /// difference in outcome against the flagship is attributable to repair alone.
        ///
        /// Floor at 45%: the two under-repair games sat at 41% and 19% of maximum for ten
        /// seconds or more. 1.0s interval: long enough that a repair has landed and the threat
        /// has been re-read before another can fire, short enough to still stack under real
        /// pressure.
        /// </summary>
        public static readonly HeuristicBotSettings RepairFixProfile = new HeuristicBotSettings
        {
            RepairPriceCheck = true,
            RepairHpFloorPct = 0.45f,
            RepairMinIntervalSeconds = 1.0,
        };

        /// <summary>The repair fixes plus the enemy-CC attack blackout. What singleplayer plays
        /// while these are being evaluated against Marc.</summary>
        public static readonly HeuristicBotSettings RepairFixPlusHazardProfile = new HeuristicBotSettings
        {
            RepairPriceCheck = true,
            RepairHpFloorPct = 0.45f,
            RepairMinIntervalSeconds = 1.0,
            HazardAttackBlackout = true,
        };

        /// <summary>Everything above plus the two killerInstinct brakes. The lockout is 5s on
        /// Marc's call: the 0A7658 stall happened at 2.8 seconds from the rung, so 5 covers it
        /// with margin without blocking a genuine finish from further out.</summary>
        public static readonly HeuristicBotSettings EconomyBrakeProfile = new HeuristicBotSettings
        {
            RepairPriceCheck = true,
            RepairHpFloorPct = 0.45f,
            RepairMinIntervalSeconds = 1.0,
            HazardAttackBlackout = true,
            KillerInstinctInvestLockoutSeconds = 5.0,
            KillerInstinctPushLatch = true,
        };

        // ── Named presets for temporary ladder contenders ──
        // Each layers ONE tabled change on top of current default behaviour, so a retest
        // measures that change alone rather than re-litigating what is already committed.
        //
        // HOW TO USE: Ladder.cs deliberately registers a single default-settings
        // HeuristicBot contender, because that list is the project's stable yardstick and
        // permanent variants would make runs incomparable over time. To A/B a change,
        // temporarily add a contender next to it --
        //
        //   ("Heuristic-PreInvest", side => new HeuristicBotAdapter(side, HeuristicBotSettings.PreInvestFlowCap)),
        //
        // -- run with `--only Heuristic` to skip the ONNX checkpoints, then remove the
        // extra entry once the question is answered. Every contender plays the same
        // pre-generated specs and the "HeuristicBot" ladder RUNG is also default-settings,
        // so one run yields both a paired comparison against the reference and a direct
        // head-to-head against it.
        //
        // PreInvestFlowCap is the historical bot as it stood before 2026-07-30 -- the
        // regression guard for the one change that was kept. If a future edit makes the
        // committed bot lose to THIS, the edit has undone the investment-pacing win.
        public static readonly HeuristicBotSettings PreInvestFlowCap =
            new HeuristicBotSettings { PaceAttackSpendForInvestment = false };
        public static readonly HeuristicBotSettings FirebombFix =
            new HeuristicBotSettings { FirebombSweptFriendlyFireCheck = true };
        public static readonly HeuristicBotSettings IncomeEdge =
            new HeuristicBotSettings { IncomeAdvantageAttack = true };
        // Arm 1 of the t_death port, in two sub-arms. enemyIsClose is untouched in both.
        public static readonly HeuristicBotSettings GeoTDeathPure =
            new HeuristicBotSettings { GeometricTimeToDeath = true };
        public static readonly HeuristicBotSettings GeoTDeathMin =
            new HeuristicBotSettings { GeometricTimeToDeath = true, GeometricTDeathKeepObserved = true };

        // ── THRESHOLD SWEEP on top of GeoTDeathMin (2026-08-19) ──────────────────────
        //
        // Arm 1 measured -8 to -10 points on the mirror rung and the trace showed why: the
        // danger rate against spam4 went 10.2% -> 17.0%, i.e. inDanger fires far more often.
        // investmentRunwayIsSafe is `timeToDeath >= timeToInvest * 1.4 + 2`, and BOTH of
        // those constants were fitted against the OLD pair of estimators, which are
        // optimistic by construction (Max, and 999999 whenever the HP window is flat).
        // Feeding a PESSIMISTIC estimator into a margin sized for optimistic ones
        // double-counts the caution. A more accurate estimator should need LESS margin, so
        // the sweep goes down.
        //
        // PlainM070 IS THE LOAD-BEARING CONTROL, not a filler arm. If lowering the margin
        // helps the plain bot just as much, then any gain on the geo arms is a threshold
        // improvement that has nothing to do with t_death, and attributing it to the port
        // would be exactly the confound that made the evaluator's -20 uninterpretable.
        // Read the geo arms as deltas against this row, not only against the 1.4 control.
        // Repair threshold sweep (2026-08-20). The trace in DefenderTrace showed the
        // simulated defender repairing the moment its castle dipped under 0.75 while nothing
        // was actually threatening it -- Marc plays through that chip damage and keeps
        // saving. NOTE the gate is "castleHpPct < RepairHpThreshold || inDanger", so lowering
        // the threshold only changes decisions where inDanger is FALSE; it cannot suppress a
        // repair that danger already justifies. In the traced case danger was already true at
        // the repair, so this flag would NOT have changed that particular decision -- it acts
        // on the quieter ones, where HP is between the new and old thresholds and nothing is
        // actually attacking.
        // ── MEASURED 2026-08-20: NO IMPROVEMENT. BOTH SHIPPED OFF. ──────────────────
        // ladder 200 (400 games/rung), seed 4242, both modes, paired in one run:
        //
        //   contender      nostart overall   headstart overall   Tier4Spam (nostart)
        //   control 0.75       88.11%             84.18%            83.0%  inv 4.57
        //   Repair50           86.54% (p=0.077)   83.57% (p=0.537)  69.0%  inv 4.13
        //   Repair60           88.39% (p=0.740)   84.11% (p=0.942)  83.0%  inv 4.55
        //
        // Repair60 is a clean NULL -- identical on most rungs, byte-for-byte on DoNothing
        // and Tier4Spam. Repair50 is NEGATIVE, and the damage is concentrated in one place:
        // Tier4Spam collapses 83.0% -> 69.0% with earned invests 4.57 -> 4.13.
        //
        // THAT COLLAPSE IS THE INFORMATIVE PART. Tier4Spam is precisely the sustained
        // chip-damage matchup, and it is where the 0.75 threshold turns out to be
        // load-bearing: sitting between 50% and 75% without repairing against a steady
        // stream is how the bot dies. Marc can tank that damage because he judges when it is
        // survivable; HeuristicBot has no such judgement, so the blunt threshold is doing
        // real work for it.
        //
        // SCOPE NOTE: this measures HeuristicBot's OWN ladder strength. It does not test the
        // original motivation, which was whether a less repair-happy ROLLOUT OPPONENT makes
        // search's simulated futures more realistic. Those are different questions and a
        // profile could lose here while still helping there.
        // Drain-cap under-attack test as time-to-death rather than proximity. Marc's
        // suggestion, 5s his starting guess; swept because the right threshold is not
        // obvious and the cost of being wrong in either direction is large.
        /// <summary>Restores the pre-2026-08-20 proximity rule, for regression checks.</summary>
        /// <summary>Pre-2026-08-21 repair rule, for regression checks.</summary>
        /// <summary>Pre-2026-08-21 dual-estimator Math.Max, for regression checks.</summary>
        public static readonly HeuristicBotSettings DualTtd =
            new HeuristicBotSettings { UnifiedTimeToDeath = false };
        public static readonly HeuristicBotSettings Unified =
            new HeuristicBotSettings { UnifiedTimeToDeath = true };
        // The unified estimator reads SHORTER, so inDanger fires more; these pair it with a
        // looser safety margin, which is the knob the earlier geometry sweep showed has to
        // move with the estimator.
        public static readonly HeuristicBotSettings UnifiedM100 =
            new HeuristicBotSettings { UnifiedTimeToDeath = true, SafetyMarginMultiplier = 1.0f };
        public static readonly HeuristicBotSettings UnifiedM070 =
            new HeuristicBotSettings { UnifiedTimeToDeath = true, SafetyMarginMultiplier = 0.7f };
        public static readonly HeuristicBotSettings RepairLegacy =
            new HeuristicBotSettings { RepairTtdSeconds = 0 };
        // Re-sweep after UnifiedTimeToDeath shipped. The old sweep (3/5.5/8s) was run
        // against the CONTACT-ONLY estimator, which read infinity at six of seven repairs
        // in 3225A7 -- so every one of those thresholds refused essentially all repairs and
        // the trend just measured how fast Tier4Spam collapses. With the unified estimator
        // the same repairs read 8.9s to 422s, so the useful range is far higher.
        public static readonly HeuristicBotSettings RepairTtd15 =
            new HeuristicBotSettings { RepairTtdSeconds = 15 };
        public static readonly HeuristicBotSettings RepairTtd25 =
            new HeuristicBotSettings { RepairTtdSeconds = 25 };
        public static readonly HeuristicBotSettings RepairTtd45 =
            new HeuristicBotSettings { RepairTtdSeconds = 45 };
        public static readonly HeuristicBotSettings RepairTtd3 =
            new HeuristicBotSettings { RepairTtdSeconds = 3 };
        public static readonly HeuristicBotSettings RepairTtd8 =
            new HeuristicBotSettings { RepairTtdSeconds = 8 };
        public static readonly HeuristicBotSettings Reactive60 =
            new HeuristicBotSettings { ReactiveSpendFractionOfIncome = 0.6 };
        public static readonly HeuristicBotSettings Reactive45 =
            new HeuristicBotSettings { ReactiveSpendFractionOfIncome = 0.45 };
        public static readonly HeuristicBotSettings Reactive20 =
            new HeuristicBotSettings { ReactiveSpendFractionOfIncome = 0.20 };
        public static readonly HeuristicBotSettings DrainProximity =
            new HeuristicBotSettings { DrainCapTtdSeconds = 0 };
        public static readonly HeuristicBotSettings DrainTtd3 =
            new HeuristicBotSettings { DrainCapTtdSeconds = 3 };
        public static readonly HeuristicBotSettings DrainTtd5 =
            new HeuristicBotSettings { DrainCapTtdSeconds = 5 };
        public static readonly HeuristicBotSettings DrainTtd8 =
            new HeuristicBotSettings { DrainCapTtdSeconds = 8 };
        public static readonly HeuristicBotSettings DrainTtd15 =
            new HeuristicBotSettings { DrainCapTtdSeconds = 15 };
        public static readonly HeuristicBotSettings Repair50 =
            new HeuristicBotSettings { RepairHpThreshold = 0.50f };
        // 0.60 IS NOW THE DEFAULT (2026-08-20) -- kept as a named profile only so the sweep
        // stays runnable. Repair75 restores the pre-2026-08-20 shipped behaviour, which every
        // HeuristicBot number recorded before that date was measured against.
        public static readonly HeuristicBotSettings Repair60 =
            new HeuristicBotSettings { RepairHpThreshold = 0.60f };
        public static readonly HeuristicBotSettings Repair75 =
            new HeuristicBotSettings { RepairHpThreshold = 0.75f };
        public static readonly HeuristicBotSettings GeoTDeathMin_M100 =
            new HeuristicBotSettings { GeometricTimeToDeath = true, GeometricTDeathKeepObserved = true,
                                       SafetyMarginMultiplier = 1.0f };
        public static readonly HeuristicBotSettings GeoTDeathMin_M070 =
            new HeuristicBotSettings { GeometricTimeToDeath = true, GeometricTDeathKeepObserved = true,
                                       SafetyMarginMultiplier = 0.7f };
        public static readonly HeuristicBotSettings GeoTDeathMin_M050 =
            new HeuristicBotSettings { GeometricTimeToDeath = true, GeometricTDeathKeepObserved = true,
                                       SafetyMarginMultiplier = 0.5f };
        // Buffer as well as margin: the +2s is an absolute floor that does not scale with
        // timeToInvest, so it dominates exactly when the investment is nearly affordable.
        public static readonly HeuristicBotSettings GeoTDeathMin_M070B0 =
            new HeuristicBotSettings { GeometricTimeToDeath = true, GeometricTDeathKeepObserved = true,
                                       SafetyMarginMultiplier = 0.7f, SafetyBufferSeconds = 0f };
        // ── SWEEP RESULT 2026-08-19: THE DIRECTION IS EXHAUSTED. ALL ARMS SHIPPED OFF. ──
        //
        // ladder 400, seed 4242, all seven contenders paired in ONE run. Mirror rung
        // (the only rung that moves), delta vs the m1.4 control, and earned invests:
        //
        //   contender        nostart mirror   inv      headstart mirror   inv
        //   control  m1.4      54.1%  +0.0   6.94        50.0%  +0.0   4.85
        //   geo      m1.4      43.9% -10.3   6.74        41.8%  -8.3   4.67
        //   geo      m1.0      46.4%  -7.8   6.82        43.9%  -6.1   4.72
        //   geo      m0.7      47.0%  -7.1   6.85        45.1%  -4.9   4.77
        //   geo      m0.5      49.6%  -4.5   6.86        48.0%  -2.0   4.80
        //   geo   m0.7 b0      50.6%  -3.5   6.86        47.5%  -2.5   4.81
        //   PLAIN    m0.7      55.2%  +1.1   6.94        49.5%  -0.5   4.85
        //
        // THE HYPOTHESIS WAS RIGHT ABOUT DIRECTION AND WRONG ABOUT UPSIDE. Lowering the
        // margin recovers the loss MONOTONICALLY in both modes, and earned invests recover
        // with it -- a genuine dose-response, not noise. But it converges to the control
        // from BELOW and never crosses it. Best geo arm: -3.5 (p=0.2) nostart, -2.0 (p=0.4)
        // headstart. Overall win rate best-geo vs control: 87.34% vs 88.30% nostart,
        // 84.75% vs 84.70% headstart. Parity at best, for a 4-7% throughput cost.
        //
        // PlainM070 IS WHY THIS IS A VERDICT RATHER THAN A MISTUNE. Lowering the margin on
        // the plain bot does essentially nothing (+1.1 / -0.5, both p>0.7), so the margin is
        // not a binding constraint on its own -- which means the geo arms' recovery is not a
        // generic threshold improvement, it is geo-specific damage being undone. Attribution
        // is clean in both directions: the loss was geo's, and so is the repair.
        //
        // AND THE REPAIR WORKS BY NEUTRALISING THE SIGNAL, which is the decisive point.
        // `trace spam4` danger rate: control 10.2%, geo m1.4 17.0%, geo m0.5 14.6%,
        // PlainM070 4.1%. On `investor`: control 0.0%, geo m1.4 1.2%, geo m0.5 0.0%. Every
        // step that recovers win rate pulls the danger rate back toward the value the OLD
        // estimators produced. The tuned arm is not using t_death better; it is using it
        // less. A more accurate estimator that only helps by being ignored is not a
        // stronger input, and no further margin reduction changes that -- extrapolating the
        // trend reaches parity, not advantage.
        //
        // CONSEQUENCE: arm 3 (relaxing enemyIsClose) was conditional on arm 1 or 2 being
        // positive. Neither was. Do not run it. The geometric estimator remains correct
        // (41/41 oracle) and is still deployed as an evaluator feature at zero weight; what
        // is now measured is that HeuristicBot's danger threshold has no headroom to give it.
        // NINTH failed direction if the evaluator's eight are counted alongside.
        //
        // (Single-game traces are noisy: the m0.7-b0 spam4 trace ended after 83 decisions at
        // 49.4% danger, an outlier game, not a rate comparable to the 300+ decision rows.)
        //
        // No geo. Isolates "is a lower margin just better anyway?" from "does geo need one?"
        public static readonly HeuristicBotSettings PlainM070 =
            new HeuristicBotSettings { SafetyMarginMultiplier = 0.7f };
        public static readonly HeuristicBotSettings SurvivalFirst =
            new HeuristicBotSettings { SurvivalInstinct = true };
        // Retest at the two triggers flag (4)'s own note recommends ("6s and 8s are the
        // obvious retests"), now additionally motivated by Marc's doctrine: reaching the
        // next investment is THE win condition, so when it is plainly unreachable the bot
        // should stop optimising for it and switch to surviving/matching. 12s measured as a
        // trade because the trigger was too generous, not because the mechanism is wrong.
        public static readonly HeuristicBotSettings Survival6 =
            new HeuristicBotSettings { SurvivalInstinct = true, SurvivalEmergencySeconds = 6f };
        public static readonly HeuristicBotSettings Survival8 =
            new HeuristicBotSettings { SurvivalInstinct = true, SurvivalEmergencySeconds = 8f };
        public static readonly HeuristicBotSettings ArmageddonRush =
            new HeuristicBotSettings { RushArmageddon = true };
        // Burst at several cap lengths. 10s is the committed AttackAllowanceCapSeconds, so
        // Burst10 is a CONTROL: it must reproduce the reference exactly, which is what
        // proves the flag's off-path and on-path agree where they should and that any
        // movement in the others is the cap and not the plumbing.
        public static readonly HeuristicBotSettings Burst10 =
            new HeuristicBotSettings { ConcentratedBurst = true, BurstAllowanceCapSeconds = 10f };
        public static readonly HeuristicBotSettings Burst25 =
            new HeuristicBotSettings { ConcentratedBurst = true, BurstAllowanceCapSeconds = 25f };
        public static readonly HeuristicBotSettings Burst60 =
            new HeuristicBotSettings { ConcentratedBurst = true, BurstAllowanceCapSeconds = 60f };
        // Tech escalation at three hold lengths. The hold is the risky half -- too long and
        // the bot stops producing while an enemy push is building -- so sweep it rather
        // than trusting one guess.
        public static readonly HeuristicBotSettings Tech10 =
            new HeuristicBotSettings { TechEscalation = true, TechHoldSeconds = 10f };
        public static readonly HeuristicBotSettings Tech20 =
            new HeuristicBotSettings { TechEscalation = true, TechHoldSeconds = 20f };
        public static readonly HeuristicBotSettings Tech40 =
            new HeuristicBotSettings { TechEscalation = true, TechHoldSeconds = 40f };
        // Added after the seed-12345 sweep came back monotonic in hold length (40 > 20 >=
        // 10) in BOTH modes -- the sweep had not found the ceiling, so extend it rather
        // than assume 40 is the answer.
        public static readonly HeuristicBotSettings Tech80 =
            new HeuristicBotSettings { TechEscalation = true, TechHoldSeconds = 80f };
        // Time-aware variants -- same holds, plus flag (8b). Paired with the plain Tech40 /
        // Tech80 above so the gate's contribution is isolated at matching hold lengths.
        public static readonly HeuristicBotSettings PowerPick =
            new HeuristicBotSettings { PowerPickAffordable = true };
        // REGRESSION GUARD for the change kept on 2026-07-31, same role PreInvestFlowCap
        // plays for the 2026-07-30 one: the additive cost-efficiency scorer exactly as it
        // stood before. If a future edit makes the committed bot lose to THIS, that edit has
        // undone the balance-formula win.
        public static readonly HeuristicBotSettings AdditiveUnitValue =
            new HeuristicBotSettings { MultiplicativeUnitValue = false };
        // Cost-exponent sweep points, kept so the curve can be re-derived without rebuilding
        // the sweep. 1.7 is the committed default; see UnitValueCostExponent for the table.
        public static readonly HeuristicBotSettings MultK10 =
            new HeuristicBotSettings { UnitValueCostExponent = 1.0 };
        public static readonly HeuristicBotSettings MultK14 =
            new HeuristicBotSettings { UnitValueCostExponent = 1.4 };
        public static readonly HeuristicBotSettings MultK20 =
            new HeuristicBotSettings { UnitValueCostExponent = 2.0 };
        // Flag (11) sweep: swarmier when reacting, committed 1.7 when pushing. Def17 is the
        // CONTROL -- it sets the defensive exponent to the committed value, so it must come
        // back identical to the reference and thereby prove the split's plumbing is inert.
        public static readonly HeuristicBotSettings Def17 =
            new HeuristicBotSettings { UnitValueCostExponentDefense = 1.7 };
        public static readonly HeuristicBotSettings Def20 =
            new HeuristicBotSettings { UnitValueCostExponentDefense = 2.0 };
        public static readonly HeuristicBotSettings Def23 =
            new HeuristicBotSettings { UnitValueCostExponentDefense = 2.3 };
        // Flag (12) at three willingness-to-spend fractions. Marc's own worked example is
        // ~$50 spent against a ~$300 wave (0.17), so 0.25 is near his actual play and 0.75
        // is deliberately over-generous -- if the aggressive one wins, the term matters
        // more than his example suggests; if only the tight one wins, it is the discipline
        // that matters rather than the permission.
        // REGRESSION GUARD for the wave-wipe change: the bot without any enemy-value term
        // in its reactive budget, i.e. exactly as it stood before 2026-07-31's second
        // landing. Same role PreInvestFlowCap and AdditiveUnitValue play for theirs.
        // Flag (13) at two force thresholds. 5 is "a real attack"; 3 fires more eagerly and
        // risks burning the cooldown on a skirmish, which would leave nothing available
        // when the actual wave lands -- the obvious failure mode for this idea.
        public static readonly HeuristicBotSettings Stall5 =
            new HeuristicBotSettings { StallGadgetsEngageEarly = true, StallForceMinUnits = 5 };
        public static readonly HeuristicBotSettings Stall3 =
            new HeuristicBotSettings { StallGadgetsEngageEarly = true, StallForceMinUnits = 3 };
        // Flag (14) at three savings levels. 0.6 banks 40% of income under fire; 0.4 is
        // aggressive saving; 0.8 is a light touch. The failure mode to watch is the bot
        // being overrun because defence was rate-limited below what the threat required.
        // Isolates the COST of the nuke suicide guard. Margin 0 makes `CastleHealth >
        // selfBlast * 0` trivially true, i.e. the guard is off and the bot nukes exactly as
        // it did before the fix -- so this measures what the fix gave up. Margin 1.2 is a
        // looser guard: still refuses a nuke that would outright kill it, but allows the
        // marginal casts a 2.0 margin declines.
        public static readonly HeuristicBotSettings NukeGuardOff =
            new HeuristicBotSettings { NukeSelfDamageMargin = 0f };
        // Isolates the COST of the incoming-nuke repair, i.e. reproduces the pre-2026-08-20
        // bot, which had no reaction to an enemy nuke at all.
        public static readonly HeuristicBotSettings IncomingNukeRepairOff =
            new HeuristicBotSettings { IncomingNukeRepair = false };
        public static readonly HeuristicBotSettings NukeGuard12 =
            new HeuristicBotSettings { NukeSelfDamageMargin = 1.2f };
        // REGRESSION GUARD for the reactive flow cap -- defensive spending unbounded by
        // income, exactly as it stood before 2026-07-31's third landing.
        // Flag (15): bounded wave, at three sizes. At fraction f the bot spends f x the next
        // InvestmentPrice on the wave and then saves, so f directly sets how much of each
        // economic cycle goes to aggression. 0.5 = half a cycle, 1.0 = a full one.
        public static readonly HeuristicBotSettings Wave50 =
            new HeuristicBotSettings { AttackBudgetPerInvestment = 0.5 };
        public static readonly HeuristicBotSettings Wave100 =
            new HeuristicBotSettings { AttackBudgetPerInvestment = 1.0 };
        public static readonly HeuristicBotSettings Wave200 =
            new HeuristicBotSettings { AttackBudgetPerInvestment = 2.0 };
        // Flag (16): delay the attack gate by one full investment (income ~252 instead of
        // ~60), which is Marc's "if the bot was 1 investment level higher it could afford
        // this stream" suggestion.
        // REGRESSION GUARD for the delayed attack gate: the bot attacking from investment 5
        // as it did before 2026-07-31's fourth landing.
        // Flag (17): dynamic invest pace, at three extra-time allowances. 0.20 is Marc's
        // suggestion (investments take 20% longer than the theoretical minimum, attack gets
        // 16.7% of income); 0.35 and 0.50 trade economy for aggression.
        // REGRESSION GUARDS for the two changes kept on 2026-07-31 (flags 17 and 18), and
        // for both together -- the old static-45s pacing and uncapped gadget firing.
        public static readonly HeuristicBotSettings StaticInvestPace =
            new HeuristicBotSettings { DynamicInvestPace = false };
        public static readonly HeuristicBotSettings NoGadgetDrainCap =
            new HeuristicBotSettings { GadgetIncomeDrainCap = false };
        public static readonly HeuristicBotSettings PreDynamicPacing =
            new HeuristicBotSettings { DynamicInvestPace = false, GadgetIncomeDrainCap = false };
        // More aggressive settings, if live play says the bot has become too passive:
        // Pace35 roughly doubles the attack's income share (16.7% -> 26%), Drain50 lets a
        // gadget consume half of income instead of 30%.
        // Flags (19) and (20) from Marc's 2026-07-31 live play, separately and together.
        // The rage/speed wall exclusions are NOT flagged -- buffing a unit that cannot
        // attack or move is a bug, not a trade-off.
        public static readonly HeuristicBotSettings Divine =
            new HeuristicBotSettings { EagerDivine = true };

        // -- (21)-(27) GADGET DOCTRINE, 2026-08-12 -----------------------------------
        // The whole package from Marc's play notes. Register alongside the default
        // contender in Ladder.cs to A/B it; the individual flags are separable so a
        // positive result can be decomposed afterwards rather than shipped as one blob.
        public static readonly HeuristicBotSettings GadgetDoctrine =
            new HeuristicBotSettings
            {
                AoeTradeRule = true,
                DivineShieldsUnits = true,
                RageOnSiege = true,
                BlackholeBuyTime = true,
                SiegePreCast = true,
                GadgetUpgradeSpam = true,
            };
        // Doctrine WITHOUT the XP farming, to separate "cast better" from "upgrade
        // faster" -- they are independent claims and the k sweep only concerns the second.
        public static readonly HeuristicBotSettings GadgetDoctrineNoSpam =
            new HeuristicBotSettings
            {
                AoeTradeRule = true,
                DivineShieldsUnits = true,
                RageOnSiege = true,
                BlackholeBuyTime = true,
                SiegePreCast = true,
            };
        // XP farming ALONE, at the shipped k, for the same reason.
        public static readonly HeuristicBotSettings UpgradeSpamOnly =
            new HeuristicBotSettings { GadgetUpgradeSpam = true };

        // -- ABLATION of GadgetDoctrineNoSpam, 2026-08-12 ----------------------------
        // The bundle measured 86.9% overall / 43.8% head-to-head against a 87.4% / 48.2%
        // reference: roughly flat overall, ~4 points down in direct play. Five independent
        // changes, so that could be one real win cancelled by one real loss. These isolate
        // each, plus a margin sweep on the one tunable number in the set.
        //
        // POWER WARNING. A signature gadget follows the TEAM, so a change touching one
        // signature family appears in ~1/8 of games; divine/rage/blackhole ablations are
        // therefore diluted ~8x and only a large effect will clear the noise. AoeTrade
        // (nuke + firebomb = 2 of 4 offence draws) and SiegePreCast (3 of 8 signatures)
        // are the two well-powered arms. Read a null on the diluted ones as "unmeasured",
        // not "neutral".
        public static readonly HeuristicBotSettings AoeTradeOnly =
            new HeuristicBotSettings { AoeTradeRule = true };
        // Break-even on UNITS still loses on castles: nuke and firebomb chip both castles,
        // so a 1.0 margin takes trades that are net negative overall. These demand better.
        public static readonly HeuristicBotSettings AoeTrade15 =
            new HeuristicBotSettings { AoeTradeRule = true, AoeTradeMargin = 1.5 };
        public static readonly HeuristicBotSettings AoeTrade20 =
            new HeuristicBotSettings { AoeTradeRule = true, AoeTradeMargin = 2.0 };
        public static readonly HeuristicBotSettings AoeTrade30 =
            new HeuristicBotSettings { AoeTradeRule = true, AoeTradeMargin = 3.0 };
        public static readonly HeuristicBotSettings DivineOnly =
            new HeuristicBotSettings { DivineShieldsUnits = true };
        public static readonly HeuristicBotSettings RageSiegeOnly =
            new HeuristicBotSettings { RageOnSiege = true };
        public static readonly HeuristicBotSettings BlackholeBuyTimeOnly =
            new HeuristicBotSettings { BlackholeBuyTime = true };
        public static readonly HeuristicBotSettings SiegePreCastOnly =
            new HeuristicBotSettings { SiegePreCast = true };
        // Siege threshold itself. Marc specified 2; 3 asks whether a stricter flag pays.
        public static readonly HeuristicBotSettings SiegeMin3 =
            new HeuristicBotSettings { SiegePreCast = true, RageOnSiege = true, SiegeMinUnits = 3 };

        // ── BOT_BACKLOG.md iteration presets ──
        // One flag each, layered on default behaviour, so `ladder --variant <name>` measures
        // that change alone against the committed reference on identical specs.
        // Identical to Default since the 2026-09-02 promotion. Kept so the commands in
        // BOT_ITERATION_LOG.md still run; use PreChargeAware for the other arm now.
        public static readonly HeuristicBotSettings ChargeAware =
            new HeuristicBotSettings { ChargeAwareFallback = true };

        public static readonly HeuristicBotSettings ChargeAwareAll =
            new HeuristicBotSettings { ChargeAwareFallback = true, ChargeAwareEverywhere = true };

        // Iteration 3 is A/B'd against Accepted (which already carries iteration 1), not
        // against bare defaults -- see Accepted's comment on why the stack is the baseline.
        // v1, kept ONLY so the reverted result in BOT_ITERATION_LOG.md stays reproducible.
        // ChipBlockIncomeFraction now defaults to 0.25, so v1's defining property -- no
        // spend-rate cap at all -- has to be restated explicitly or this silently becomes v2.
        public static readonly HeuristicBotSettings ChipBlock =
            new HeuristicBotSettings
            {
                ChargeAwareFallback = true,
                BlockSingleChipper = true,
                ChipBlockIncomeFraction = 1000.0,   // effectively uncapped
            };

        // v2: the flow cap v1 lacked. v1 is kept above so the pair can be re-run together.
        public static readonly HeuristicBotSettings ChipBlockV2 =
            new HeuristicBotSettings
            {
                ChargeAwareFallback = true,
                BlockSingleChipper = true,
                ChipBlockIncomeFraction = 0.25,
            };

        // A tighter arm, in case 0.25 of income is still too much against fast swingers.
        public static readonly HeuristicBotSettings ChipBlockV2Tight =
            new HeuristicBotSettings
            {
                ChargeAwareFallback = true,
                BlockSingleChipper = true,
                ChipBlockIncomeFraction = 0.10,
            };

        public static readonly HeuristicBotSettings AutoSpawn =
            new HeuristicBotSettings { ChargeAwareFallback = true, BuyAutoSpawner = true };

        // Self-awareness fixes from Marc's 2026-09-02 replays -- see BOT_BACKLOG.md item 7.
        public static readonly HeuristicBotSettings RungCommit =
            new HeuristicBotSettings { CommitToRung = true };

        public static readonly HeuristicBotSettings ArmaCommit =
            new HeuristicBotSettings { ArmageddonCommit = true };

        public static readonly HeuristicBotSettings SelfAware =
            new HeuristicBotSettings { CommitToRung = true, ArmageddonCommit = true };

        public static readonly HeuristicBotSettings AutoSpawnSub =
            new HeuristicBotSettings
            {
                ChargeAwareFallback = true,
                AutoSpawnFromAttackBudget = true,
            };

        public static readonly HeuristicBotSettings CheapUpgrades =
            new HeuristicBotSettings
            {
                ChargeAwareFallback = true,
                GadgetUpgradeSpam = true,     // CheapGadgetUpgrades only replaces its gate
                CheapGadgetUpgrades = true,
            };

        /// <summary>
        /// EVERY FLAG ACCEPTED BY THE ITERATION LOOP SO FAR, stacked.
        ///
        /// Deliberately separate from the single-flag presets above, and deliberately NOT
        /// promoted into the defaults. Flipping a default moves `bot-checksum`, every ladder
        /// baseline, `FLAGSHIP_BASELINE.md` and the shipped singleplayer opponent all at
        /// once -- that is Marc's call, not the loop's. Keeping the accepted set here means
        /// the default bot stays byte-identical (checksum 47EC146D660B0D721B4DC224D8ACB7F9)
        /// while one profile name turns the whole accepted set on.
        ///
        /// Stacking also matters for measurement: from iteration 2 onward each new flag is
        /// A/B'd against THIS profile rather than against bare defaults, so interactions
        /// between accepted changes are inside the comparison instead of outside it.
        ///
        /// See BOT_ITERATION_LOG.md for what each flag bought and what it cost.
        /// </summary>
        public static readonly HeuristicBotSettings Accepted = new HeuristicBotSettings();

        /// <summary>
        /// The bot as it behaved BEFORE the 2026-09-02 promotion of ChargeAwareFallback.
        /// Kept for the same reason RepairLegacy is: every benchmark, FLAGSHIP_BASELINE
        /// figure and pinned checksum recorded before that date describes THIS bot, and a
        /// historical comparison needs to be able to reproduce it.
        /// Its checksum is 47EC146D660B0D721B4DC224D8ACB7F9 at `bot-checksum --games 24`.
        /// </summary>
        /// <summary>The bot before CommitToRung was promoted (2026-09-02).</summary>
        public static readonly HeuristicBotSettings PreRungCommit =
            new HeuristicBotSettings { CommitToRung = false };

        public static readonly HeuristicBotSettings PreChargeAware =
            new HeuristicBotSettings { ChargeAwareFallback = false };
        // k SWEEP. At k=0.30 (income >= cost/(cooldown*k), i.e. $20/s for speed) XP farming
        // costs 0.85 earned investments per game and loses 15 points head-to-head, so the
        // failure mode is ECONOMIC. Lower k = stricter = start spamming later, at higher
        // income. Sweeping downward is therefore the informative direction.
        public static readonly HeuristicBotSettings UpgradeSpamK15 =
            new HeuristicBotSettings { GadgetUpgradeSpam = true, UpgradeSpamK1 = 0.15, UpgradeSpamK2 = 0.15 };
        public static readonly HeuristicBotSettings UpgradeSpamK08 =
            new HeuristicBotSettings { GadgetUpgradeSpam = true, UpgradeSpamK1 = 0.08, UpgradeSpamK2 = 0.08 };
        public static readonly HeuristicBotSettings UpgradeSpamK04 =
            new HeuristicBotSettings { GadgetUpgradeSpam = true, UpgradeSpamK1 = 0.04, UpgradeSpamK2 = 0.04 };
        // Same k as shipped, but defer XP farming much earlier in the savings cycle -- the
        // other lever on the same economic cost.
        public static readonly HeuristicBotSettings UpgradeSpamDefer25 =
            new HeuristicBotSettings { GadgetUpgradeSpam = true, UpgradeSpamInvestCommitFraction = 0.25 };
        // REGRESSION GUARD for the wave-wipe purchase.
        public static readonly HeuristicBotSettings NoWiper =
            new HeuristicBotSettings { WiperOverRepair = false };
        // Reproduces the pre-fix targeting that could aim behind our own castle -- see
        // ClampProjectionToCastle.
        public static readonly HeuristicBotSettings NoCastleClamp =
            new HeuristicBotSettings { ClampProjectionToCastle = false };
        // Margin sweep points, kept so the interior optimum can be re-derived. 1.0 is the
        // plain "any positive swing" rule and is measurably worse; 0.20 is past the turn.
        public static readonly HeuristicBotSettings Wiper100 =
            new HeuristicBotSettings { WiperMaxCostVsStackValue = 1.0 };
        public static readonly HeuristicBotSettings Wiper20 =
            new HeuristicBotSettings { WiperMaxCostVsStackValue = 0.20 };
        public static readonly HeuristicBotSettings Pace35 =
            new HeuristicBotSettings { InvestPaceExtraTimeFraction = 0.35 };
        public static readonly HeuristicBotSettings Drain50 =
            new HeuristicBotSettings { GadgetMaxIncomeDrainFraction = 0.50 };
        public static readonly HeuristicBotSettings GateAt5 =
            new HeuristicBotSettings { AttackGateMinInvestment = 0 };
        public static readonly HeuristicBotSettings GateAt7 =
            new HeuristicBotSettings { AttackGateMinInvestment = 7 };
        // Kept so the gate-vs-cap complementarity can be re-derived without rebuilding the
        // 2x2: this is the gate WITHOUT the reactive cap, which trades ~8 points of
        // Tier4Spam for ~2 points of mirror. See AttackGateMinInvestment.
        public static readonly HeuristicBotSettings GateNoReact =
            new HeuristicBotSettings { ReactiveFlowCap = false };
        public static readonly HeuristicBotSettings NoReactiveFlowCap =
            new HeuristicBotSettings { ReactiveFlowCap = false };
        public static readonly HeuristicBotSettings Reactive40 =
            new HeuristicBotSettings { ReactiveSpendFractionOfIncome = 0.4 };
        public static readonly HeuristicBotSettings Reactive80 =
            new HeuristicBotSettings { ReactiveSpendFractionOfIncome = 0.8 };
        public static readonly HeuristicBotSettings NoWaveWipe =
            new HeuristicBotSettings { WaveWipeValue = false };
        public static readonly HeuristicBotSettings Wipe25 =
            new HeuristicBotSettings { WaveWipeValueFraction = 0.25 };
        public static readonly HeuristicBotSettings Wipe75 =
            new HeuristicBotSettings { WaveWipeValueFraction = 0.75 };
        public static readonly HeuristicBotSettings Tech40Timed =
            new HeuristicBotSettings { TechEscalation = true, TechHoldSeconds = 40f, TechTimeAware = true };
        public static readonly HeuristicBotSettings Tech80Timed =
            new HeuristicBotSettings { TechEscalation = true, TechHoldSeconds = 80f, TechTimeAware = true };
    }

    // Rule-based opponent. Drives a side entirely through GameEngine's public API
    // (SpawnUnit / Invest / Repair / UseGadget) -- the same surface a human player
    // uses via the SignalR hub -- so it plays by the exact same rules a human does.
    public class HeuristicBot : IRolloutPolicy
    {
        private readonly int _side;
        private readonly HeuristicBotSettings _settings;

        // Debug/test visibility into the last decision -- not used by the bot itself.
        public bool LastDecisionWasDanger { get; private set; }
        public int LastUnitsPurchased { get; private set; }
        public string LastSpendDebug { get; private set; } = "";

        /// <summary>
        /// DIAGNOSTIC ONLY. Which branch of Decide() bought the most recent unit, so a
        /// trace can attribute a spawn to a rule instead of guessing from the outside.
        /// Set at every SpawnUnit call site; never read by the bot itself.
        /// </summary>
        public string LastSpawnReason { get; private set; } = "";
        public float LastThreatScore { get; private set; }
        public float LastDefenseScore { get; private set; }
        public float LastTimeToDeathSeconds { get; private set; }
        public float LastTimeToInvestSeconds { get; private set; }
        public bool LastAttackDisengaged { get; private set; }
        public bool LastKillerInstinct { get; private set; }
        public bool LastIncomeAdvantage { get; private set; }
        public bool LastSurvivalEmergency { get; private set; }
        // Castle damage queued against us by pending nuke detonations, and whether that is
        // currently lethal. Exposed for tracing/telemetry the same way the danger flags are.
        public int LastIncomingCastleDamage { get; private set; }
        public bool LastNukeEmergency { get; private set; }
        // Enemy $ currently committed within WaveWipeRadius of our castle -- the AoE
        // wipe opportunity. 0 when flag (12) is off.
        public double LastWaveWipeValue { get; private set; }

        // Last geometric t_death reading, for diagnostics. Untouched when the flag is off.
        public float LastGeoTimeToDeathSeconds { get; private set; } = EffectivelyInfiniteSeconds;

        // Running per-game tally of every successful action actually taken, indexed by
        // the same 14-action ID space as GetActionMask/ApplyAction (0=wait unused here,
        // 1-8=spawn tier, 9=invest, 10=repair, 11-13=gadget slots) -- lets a harness
        // compare the bot's real action mix against recorded human play. Counted directly
        // at each call site (not sampled from GameEngine.LastActionP1/P2) because a single
        // Decide() can call SpawnUnit many times in a row -- sampling LastAction once per
        // tick would only ever see the last of those and badly undercount.
        public readonly long[] ActionCounts = new long[14];

        /// <summary>Tick of the last repair, for RepairMinIntervalSeconds.</summary>
        private long _lastRepairTick = long.MinValue / 2;

        // killerInstinct push-latch state. See KillerInstinctPushLatch.
        private bool _killerPushLive;
        private int _killerPushPeak;
        private bool _killerLockedOut;

        /// <summary>Summed DPS of our units in contact with the enemy castle and unengaged.</summary>
        public float LastOwnPushDps { get; private set; }

        /// <summary>killerInstinct's own estimate: seconds to kill the enemy castle.</summary>
        public float LastKillerSeconds { get; private set; }

        /// <summary>The killerInstinct trigger BEFORE the brakes, for attribution.</summary>
        public bool LastKillerInstinctRaw { get; private set; }

        /// <summary>Which brake suppressed killerInstinct this decision, or null.</summary>
        public string LastKillerLockReason { get; private set; }

        /// <summary>Decisions on which a brake suppressed killerInstinct.</summary>
        public long KillerLockedDecisions { get; private set; }

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

        // --- ATTACK-VS-SAVINGS BALANCE ---
        // Marc's design, refined over two rounds of feedback:
        // 1. Enemy investment landing while we're pushing their castle is the single
        //    highest-confidence "this attack has failed strategically" signal -- an
        //    instant swing regardless of any HP progress made. Always disengages.
        // 2. Enemy castle HP is a REAL secondary signal, not a bad one -- Marc's own
        //    correction to a first draft that dropped it entirely: "the more HP the
        //    enemy is losing, the more effective the attack is... I still want the bot
        //    to have that killer instinct." Essentially-zero HP loss over a real
        //    evaluation window (EnemyHpEvaluationSeconds) is a clear stall signal; any
        //    real, meaningful drain is a "keep the pressure on" signal and never forces
        //    a disengage by itself.
        // 3. If the enemy castle drops to a low absolute HP (KillerInstinctHpThreshold)
        //    during an attack, that's "go for the kill" territory -- ignore savings
        //    discipline (and even an active disengage cooldown) and spend freely to
        //    finish it.
        // 4. Never spend 100% of resources on offense -- but this must be a FLOW cap
        //    (spending capped as a fraction of the INCOME RATE, so savings strictly
        //    accrue over time), not a fraction of the current money stockpile. Marc's
        //    own clarifying question caught this: an earlier version reserved 25% of
        //    the current money pile, which only protects a snapshot -- it doesn't
        //    guarantee spending stays below income, so it doesn't guarantee savings
        //    actually GROW over time the way "10-20% of income" does.
        //
        // First cut of this system (a hard stall detector purely off enemy-castle-HP
        // trajectory, no investment signal, no flow-based savings) was tried and
        // reverted after regressing v23/Tier4 with no compensating win -- seeing HP as
        // the ONLY signal, evaluated too strictly, was the problem. Second cut (enemy-
        // investment signal + a STOCK-based 25%-of-money reserve, no HP signal at all)
        // was a wash (real gains on Tier3/Tier8, real losses on Tier4/v21/v25/v7,
        // v23 still down some) -- not committed, superseded by this version.
        //
        // The four knobs below (EnemyHpEvaluationSeconds, MinMeaningfulEnemyHpLossPct-
        // PerSecond, KillerInstinctHpThreshold, AttackSpendFraction) live on
        // HeuristicBotSettings, NOT as consts here -- Marc's own words when he gave
        // them: "those numbers I mentioned are pulled out of thin air," meant to be
        // swept (CastleDefense.BotArena's "paramsearch-attack" mode) rather than
        // trusted as committed values. AttackEngageDistance/AttackDisengageCooldown-
        // Seconds/AttackAllowanceCapSeconds stayed as plain consts -- Marc didn't flag
        // those three as guesses, so they weren't included in the sweep.
        private const float AttackEngageDistance = 700f; // matches EnemyIsCloseDistance's scale, mirrored toward the enemy castle
        private const float AttackDisengageCooldownSeconds = 15f; // full savings mode for this long after a stall/instant-swing trigger
        private const float AttackAllowanceCapSeconds = 10f; // cap how much unspent allowance can bank while idle, so this stays a flow limit rather than turning into an unbounded stockpile reserve
        private long _attackStartTick = -1; // -1 = not currently tracking a push
        private int _enemyHpAtAttackStart;
        private int _enemyInvestCountAtAttackStart = -1;
        private long _disengageUntilTick = 0;
        private double _attackSpendAllowance = 0;
        // Same flow-allowance shape as _attackSpendAllowance, but for REACTIVE defence --
        // see ReactiveFlowCap. Separate accumulator on purpose: defence must still be able
        // to burst when a wave actually lands, so it banks while quiet and spends on demand.
        private double _reactiveSpendAllowance = 0;
        // Cumulative NON-REACTIVE spend since the last investment landed, and the count we
        // last saw. Together these bound one "wave" so the bot has to stop and go back to
        // saving. See AttackBudgetPerInvestment.
        private double _attackSpentThisCycle = 0;
        private int _lastSeenInvestmentCount = -1;
        // Tick of the last wave-wipe purchase, bounding how often that check may fire.
        private long _lastWiperTick = long.MinValue / 4;

        // The one wall each side may have on the field (WallEffect enforces the limit).
        // Refreshed once per decision and read by ProjectedPosition, which must not lead a
        // unit straight through a blocker -- see ClampProjectionToCastle. Cached rather than
        // scanned per call because FindBestAoeTarget is already O(units^2) in projections.
        private Unit _side1Wall;
        private Unit _side2Wall;

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

        // ── ONE ACTION PER TICK (2026-08-20) ────────────────────────────────────────
        //
        // Decide() can take several actions in one pass -- it calls TryUseOffenseGadget,
        // TryUseDefenseGadget and TryUseSignatureGadget in sequence, and repair, wave-wipe,
        // reactive spending and the fallback investment all sit in the same pass without
        // returning. Measured over 20 self-play games: 3.3% of acting ticks took more than
        // one action (341 ticks with 2, 5 with 3).
        //
        // TWO REASONS THAT IS WRONG, and the fairness one is the reason it changed:
        //  1. A HUMAN CANNOT DO IT. A tick is 33ms; Marc gets one action per tick at best.
        //     A bot firing three gadgets inside one tick is using input bandwidth no player
        //     has, which is not the kind of difficulty this project is trying to build.
        //  2. The replay format stores exactly ONE action id per side per tick, so every
        //     extra action was silently dropped from the recording -- which is why rebuilt
        //     games diverged and the bot died early in every reconstruction.
        //
        // So extra actions are QUEUED and played out on subsequent ticks, one per tick, in
        // the order Decide() chose them. There is room: decisions are DecisionIntervalTicks
        // (5) apart and the observed maximum is 3 actions, so the queue always drains before
        // the next decision.
        //
        // RE-VALIDATED ON EXECUTION, not fired blind. A queued action runs 1-2 ticks later
        // and the world has moved; engine.Invest/Repair/SpawnUnit/UseGadget all re-check
        // money and cooldowns and return false without side effects, so a no-longer-legal
        // action is skipped cleanly rather than half-applied.
        //
        // KNOWN WRINKLE, deliberately accepted: Act() returns true for a QUEUED action, so
        // the rest of that Decide() proceeds as though the money were already spent. It has
        // not been, so a later check in the same pass can see stale money and queue a
        // purchase it cannot actually afford -- which then fails cleanly above. The
        // alternative, returning false, would make Decide() retry the same action down a
        // different branch and is worse.
        //
        // The queue lives on the BOT, never on the engine. GameEngine.Clone is shallow and
        // any mutable reference field added there is silently shared with every one of
        // search's ~231x rollouts per decision (see [[project_engine_clone_hazard]]). Bots
        // are constructed fresh per rollout, so this is inert there.
        private readonly Queue<Func<bool>> _pendingActions = new Queue<Func<bool>>();
        private bool _actedThisDecision;

        /// <summary>Number of actions still queued from an earlier decision. Diagnostic.</summary>
        public int PendingActionCount => _pendingActions.Count;

        /// <summary>
        /// Performs an action now if this decision has not acted yet, otherwise queues it for
        /// a later tick. Returns true when the action happened OR was queued, so callers keep
        /// their existing control flow.
        /// </summary>
        private bool Act(Func<bool> act)
        {
            if (!_actedThisDecision)
            {
                bool ok = act();
                if (ok) _actedThisDecision = true;
                return ok;
            }
            _pendingActions.Enqueue(act);
            return true;
        }

        public void Update(GameEngine engine)
        {
            var state = engine._state;
            if (state.IsGameOver) return;

            // Drain first: one queued action per tick, ahead of any new decision. The queue
            // is shorter than the decision interval, so this never starves Decide().
            if (_pendingActions.Count > 0)
            {
                _pendingActions.Dequeue()();
                return;
            }

            if (state.CurrentTick < _nextDecisionTick) return;
            _nextDecisionTick = state.CurrentTick + DecisionIntervalTicks;

            _actedThisDecision = false;
            _castsThisDecision.Clear();
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

            // Refresh the wall cache for ProjectedPosition's blocker clamp. One per side max.
            (_side1Wall, _side2Wall) = Gadgets.GadgetTargeting.FindWalls(state);

            foreach (var u in enemyUnits)
            {
                if (_observedEnemyUnitIds.Add(u.InstanceId))
                {
                    _observedEnemyTiers.Add(u.Tier);
                    _enemyValueSeen += EstimateUnitCost(engine, u);
                }
            }

            // Sample the commitment rate on a ~1s cadence. Shorter and it is dominated by the
            // burstiness of individual spawns; longer and it lags a wave that is still forming.
            if (_enemyValueSampleTick < 0) { _enemyValueSampleTick = state.CurrentTick; _enemyValueSample = _enemyValueSeen; }
            else if (state.CurrentTick - _enemyValueSampleTick >= 30)
            {
                double dt = (state.CurrentTick - _enemyValueSampleTick) / 30.0;
                _enemyValueRate = (_enemyValueSeen - _enemyValueSample) / dt;
                _enemyValueSampleTick = state.CurrentTick;
                _enemyValueSample = _enemyValueSeen;
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

            // Built here rather than after the gadget layer: repair takes its turn before the
            // gadgets, and its price check needs the true incoming damage rate. The older
            // EstimateProjectedThreatDps counts only units already in contact, which during a
            // collapse reads far lower than the army actually swinging -- so the check silently
            // approved every repair it was meant to refuse.
            //
            // ALSO BUILT WHEN RepairPriceCheck IS ON, and that is not optional. Measured on
            // game F66E23 (2026-08-24): with `threat` null the price check falls back to
            // EstimateProjectedThreatDps, which counts only units already touching the castle.
            // While an army is walking in that reads ZERO, and RepairBuysItsPrice opens with
            // `if (incomingDps <= 0.01f) return true` -- so it rubber-stamped every repair in
            // exactly the situation it exists to refuse. Meanwhile the geometric time-to-death
            // DOES see the approaching army and opens the gate, so the bot repaired a castle at
            // 100% health four times, the last for $8,837.
            ThreatModel threat = (_settings.DefenceOnly || _settings.RepairPriceCheck)
                ? ThreatModel.Build(engine, _side, enemyUnits, me.CastleHealth)
                : null;

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
            // re-accumulated to InvestmentPrice (169.4 at that point; that same rung is $170
            // since prices became whole dollars on 2026-08-29) for the rest of the
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

            // Complement the OBSERVED-drain estimate above with a PROACTIVE one computed
            // directly from the current enemy roster's own stats -- Marc's own explicit
            // ask, from a direct playtest report: "it is important to try and do some of
            // this math in-game to determine an accurate threat level." His example: a
            // single tier-5 unit at 120 DPS against a 12,000 HP castle is ~100 seconds of
            // runway, comfortably enough to reach the next investment -- but the
            // OBSERVED-drain model only reacts to damage that has ALREADY landed inside
            // the short rolling window (HpHistoryWindow), so a single, isolated, genuinely
            // weak threat that lands a few real hits can look scarier in that short window
            // than it truly is over the long run, triggering reactive spending that isn't
            // actually warranted. The projected estimate below is a clean instant read of
            // "given exactly what's in range of my castle right now, how long until it
            // falls" -- immune to that short-window noise. Take whichever of the two
            // estimates says we have MORE runway: a real, escalating threat will show a
            // short time-to-death on BOTH estimates once it's actually in range and
            // dealing damage (so this doesn't blind the bot to genuine danger), but an
            // isolated weak threat that's already landed a stray hit or two no longer
            // forces a falsely short reading just because of recent noise.
            var enemyState = _side == 1 ? state.Player2 : state.Player1;
            var enemyRoster = GameDataManager.Teams.FirstOrDefault(t => t.Color == enemyState.Team)?.Roster;
            float projectedDps = EstimateProjectedThreatDps(engine, enemyUnits, enemyRoster);
            float projectedTimeToDeathSeconds = projectedDps > 0.01f
                ? me.CastleHealth / projectedDps
                : EffectivelyInfiniteSeconds;

            float observedTimeToDeathSeconds = EstimateTimeToDeathSeconds(me.CastleHealth);
            float timeToDeathSeconds;
            if (_settings.UnifiedTimeToDeath)
            {
                // ── ONE TIME-TO-DEATH, 2026-08-21 (Marc's design) ───────────────────
                //
                // Two estimators with different blind spots were being combined with a plain
                // Math.Max, which let a BLIND estimator win. Each has a "no information"
                // sentinel and the old Max treated that sentinel as a genuine reading of
                // "essentially forever":
                //
                //   observed  -> EffectivelyInfiniteSeconds when the 18-sample HP window is
                //                not full, or when both drain rates are under 0.5 HP/s. It
                //                only ever sees damage that has ALREADY LANDED.
                //   geometric -> its cap (remaining game time) when nothing on the board can
                //                reach the castle. It counts UNITS ONLY, so gadget, hazard
                //                and DoT damage are invisible to it.
                //
                // Measured consequence of letting the sentinel win: --repair-audit on 3225A7
                // found the contact-only estimator reading INFINITY at six of seven repairs,
                // because a wave walking in is invisible until it makes contact. That is why
                // the RepairTtdSeconds gate refused nearly every repair and Tier4Spam
                // collapsed 89.2% -> 72.2% as the threshold tightened.
                //
                // THE RULE: discard any estimator sitting at its sentinel, then take the MAX
                // of whatever is left. Max is kept deliberately -- it is the established
                // optimistic choice, and a real escalating threat reads short on BOTH once it
                // is genuinely in range, so this does not blind the bot. If NOTHING is
                // informative, the answer is genuinely "no measurable threat" and we return
                // the infinite sentinel rather than the geometric cap. Returning the cap
                // would make investmentRunwayIsSafe permanently false in the last ~76 seconds
                // of every game at investment 7, which is a new endgame pathology.
                //
                // The contact-only PROJECTED estimator is dropped entirely here: the
                // geometric one already counts contact units (their travel time is zero) and
                // additionally handles Siege doubling, Rage, Slow/freeze and blockers, so it
                // strictly supersedes it.
                //
                // EXPECT THIS TO READ SHORTER THAN THE OLD Math.Max, by construction -- that
                // is the fix, but it means inDanger fires more readily and
                // reactiveSpendBudget grows. Both effects push against the economy, which is
                // exactly how the earlier geometry-only arm lost 10 points on the mirror
                // rung. Measure before trusting.
                float geo = (float)state.TimeToCastleDeathSeconds(_side);
                LastGeoTimeToDeathSeconds = geo;
                float geoCap = (GameEngine.MAX_TICKS - state.CurrentTick) / (float)GameEngine.TICKS_PER_SECOND;
                bool observedInformative = observedTimeToDeathSeconds < EffectivelyInfiniteSeconds;
                // Geo returns exactly its cap when it has nothing to report; the epsilon
                // guards float noise rather than admitting near-cap readings.
                bool geoInformative = geo < geoCap - 0.01f;

                timeToDeathSeconds =
                      observedInformative && geoInformative ? Math.Max(observedTimeToDeathSeconds, geo)
                    : observedInformative ? observedTimeToDeathSeconds
                    : geoInformative      ? geo
                                          : EffectivelyInfiniteSeconds;
            }
            else if (_settings.GeometricTimeToDeath)
            {
                // The geometric estimator REPLACES the optimistic Max above -- see the flag's
                // own comment for why Max'ing it in measured as a no-op. Guarded on the flag
                // rather than computed and discarded: HeuristicBot is RolloutSearchBot's
                // rollout policy for BOTH sides, so per-decision work here is multiplied by
                // ~64M evaluations per n=20 search benchmark.
                float geo = (float)state.TimeToCastleDeathSeconds(_side);
                LastGeoTimeToDeathSeconds = geo;
                timeToDeathSeconds = _settings.GeometricTDeathKeepObserved
                    ? Math.Min(observedTimeToDeathSeconds, geo)
                    : geo;
            }
            else
            {
                timeToDeathSeconds = Math.Max(observedTimeToDeathSeconds, projectedTimeToDeathSeconds);
            }

            double moneyStillNeeded = Math.Max(0, me.InvestmentPrice - me.Money);
            float timeToInvestSeconds = me.Income > 0.01 ? (float)(moneyStillNeeded / me.Income) : EffectivelyInfiniteSeconds;

            // Require real headroom, not just "barely more time than needed" -- decisions
            // only run ~6/sec and an enemy's incoming mass can keep growing, so the drain
            // rate measured this instant is a floor on how bad it gets, not a guarantee.
            bool investmentRunwayIsSafe = timeToDeathSeconds >= timeToInvestSeconds * _settings.SafetyMarginMultiplier + _settings.SafetyBufferSeconds;

            // How many seconds of runway we're ACTUALLY short, ignoring the conservative
            // safety margin above -- the raw gap between "how long until I can invest"
            // and "how long until I die if nothing changes." This is what reactive
            // defensive spending should be judged against: Marc's own framing, from a
            // direct playtest breakdown of a single tier-5 unit dealing 120 DPS against a
            // 12,000 HP castle (~100s of real runway, comfortably enough to just keep
            // investing through) -- "we need to remember *why* we want to slow [an
            // attack] down... if we spend all our money on defensive units and slowing
            // down the enemy attack, we haven't actually accomplished anything except
            // delaying the inevitable." Converting the deficit into a dollar figure at our
            // own income rate answers "is this specific defensive spend actually worth
            // the savings-progress it costs" directly, rather than spending unconditionally
            // the moment `inDanger` is true. If there's no deficit at all (we'd reach the
            // investment safely even without the margin), this is 0 -- no defensive spend
            // is worth anything since nothing needs bridging.
            float runwayDeficitSeconds = Math.Max(0f, timeToInvestSeconds - timeToDeathSeconds);
            double reactiveSpendBudget = runwayDeficitSeconds * me.Income * _settings.ReactiveSpendEVMultiplier;

            // --- SURVIVAL INSTINCT ---
            // Marc's report: "the bot dies with plenty of money when it is outmatched...
            // it feels like it gives up as I'm attacking." That is a real logic hole, not
            // a subjective impression, and it has two distinct halves -- both stemming
            // from the fact that EVERY defensive decision above is expressed relative to
            // the INVESTMENT RACE rather than to survival:
            //
            //  1. `investmentRunwayIsSafe` is `timeToDeath >= timeToInvest * 1.4 + 2`.
            //     When money is already at or past InvestmentPrice, timeToInvest is ~0, so
            //     the whole test collapses to `timeToDeath >= 2 seconds`. A bot three
            //     seconds from losing its castle is therefore classified SAFE, inDanger is
            //     false, no reactive defense fires at all -- and worse, the early-exit
            //     invest check at the top of Decide() fires and hands the entire money pile
            //     to an investment it will not live long enough to collect on.
            //  2. `reactiveSpendBudget` is `max(0, timeToInvest - timeToDeath) * income`.
            //     The same near-zero timeToInvest drives the deficit to zero, so even when
            //     inDanger does manage to fire, the budget authorising defensive spending
            //     is $0 and SpendOnUnits/wall/wave/goo/freeze all decline to spend a cent.
            //
            // Both are the EV framing working exactly as designed and being asked the
            // wrong question. That framing ("is this defensive dollar worth the savings
            // progress it costs?") is correct while the bot is choosing between economy
            // and defense -- but below some absolute time-to-death there is no choice left
            // to make. An investment is worth nothing to a dead castle, so at that point
            // the marginal value of a defensive dollar is unbounded and no budget should
            // gate it. This flag adds that floor, and nothing else: an absolute
            // time-to-death trigger that forces danger handling on, uncaps reactive
            // spending, and stops the bot from investing while it is being killed.
            //
            // Note this deliberately does NOT touch the one-purchase-per-decision pacing.
            // The money does not go unspent because the bot buys too slowly (6 buys/sec
            // over a 12-second window is 70+ units); it goes unspent because the budget was
            // computed as zero. Relaxing the pacing would reintroduce the batch-buying
            // divergence from human play that the SpendOnUnits comment documents fixing.
            // --- WAVE-WIPE OPPORTUNITY (flag 12) ---
            // Total $ the enemy has COMMITTED against our castle -- the stack a single AoE
            // defender would hit all at once. See WaveWipeValue for the mechanic and why
            // this is measured near our own castle rather than map-wide.
            double committedEnemyValue = 0;
            int committedEnemyCount = 0;
            if (_settings.WaveWipeValue)
            {
                foreach (var u in enemyUnits)
                {
                    if (Math.Abs(u.Position - myCastlePos) > _settings.WaveWipeRadius) continue;
                    committedEnemyCount++;
                    committedEnemyValue += EstimateUnitCost(engine, u);
                }
            }
            // ATTEMPT 1 WAS CATASTROPHIC -- see WaveWipeValue's comment for the numbers.
            // It made this an INDEPENDENT trigger for reactive spending (firing even when
            // inDanger was false) and additionally suppressed the non-reactive attack
            // branch. Result: Investor 98.5 -> 60.5, mirror 44.8 -> 26.3, earned invests
            // 5.19 -> 4.82. It recreated the permanent-reactive-mode pathology this file
            // documents at length, because "3+ enemies within 500" is an ordinary board
            // state rather than a committed wave, and because the budget could be re-spent
            // EVERY decision (6/sec) instead of once per wave.
            //
            // This version only RAISES THE BUDGET on the existing inDanger path. The
            // premise behind the independent trigger was wrong anyway: when a wave really
            // commits to our castle, timeToDeath falls, investmentRunwayIsSafe goes false,
            // and inDanger is already true -- so the ordinary path reaches the purchase on
            // its own and needs permission to spend, not a new reason to act.
            bool waveWipeOpportunity = _settings.WaveWipeValue
                && committedEnemyCount >= _settings.WaveWipeMinUnits
                && committedEnemyValue > 0;
            if (waveWipeOpportunity)
            {
                // Raise rather than replace: whichever justification is larger wins, so
                // this can only ever ADD permission to spend, never remove it.
                reactiveSpendBudget = Math.Max(reactiveSpendBudget,
                                               committedEnemyValue * _settings.WaveWipeValueFraction);
            }
            LastWaveWipeValue = committedEnemyValue;

            bool survivalEmergency = _settings.SurvivalInstinct
                && timeToDeathSeconds <= _settings.SurvivalEmergencySeconds;
            if (survivalEmergency) reactiveSpendBudget = me.Money;
            LastSurvivalEmergency = survivalEmergency;

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
            // survivalEmergency ORs in rather than replacing the existing trigger: it is a
            // strictly-additional floor. It needs no `enemyIsClose` companion because a
            // short time-to-death already implies real damage is landing -- the observed
            // estimator requires a measured HP drain and the projected one only counts
            // enemies already inside contact range of our own castle.
            bool inDanger = (enemyIsClose && !investmentRunwayIsSafe) || survivalEmergency;
            LastDecisionWasDanger = inDanger;
            LastThreatScore = threatScore;
            LastDefenseScore = defenseScore;
            LastTimeToDeathSeconds = timeToDeathSeconds;
            LastTimeToInvestSeconds = timeToInvestSeconds;

            // --- INCOMING NUKE ---
            // A queued nuke detonation is the one threat none of the machinery above can
            // see. Every danger signal in this method is a rate (HP over observed or
            // projected drain from units in contact); a nuke is a single instantaneous hit
            // that is already committed and cannot be blocked, killed or outrun. So this is
            // checked separately, and BEFORE the investment early-exit below -- otherwise
            // the bot's highest-priority action, on the last decision it will ever make, is
            // to hand its entire pile to an economy upgrade it is about to be deleted with.
            // See IncomingNukeRepair for the full reasoning.
            //
            // The blast is read from the engine rather than assumed, so it is correct for
            // whichever nuke level is actually in flight (100 / 1500 / 12000), sums several
            // in the air at once, and counts our own as readily as the enemy's.
            //
            // Fires at FULL health too, where DamageCastle's 1-shot prevention floors us at
            // 1 HP instead of killing us. That is deliberate: surviving on 1 HP against an
            // opponent free to finish the job is not surviving, and the repair both clears
            // the blast outright and raises CastleMaxHealth permanently.
            int incomingBlast = _settings.IncomingNukeRepair ? engine.IncomingCastleDamage(_side) : 0;
            bool nukeEmergency = incomingBlast > 0
                && me.CastleHealth <= incomingBlast * _settings.IncomingNukeSurvivalMargin;
            LastIncomingCastleDamage = incomingBlast;
            LastNukeEmergency = nukeEmergency;

            if (nukeEmergency)
            {
                // No 1.25x money reserve here (unlike the ordinary repair below): the
                // alternative to this purchase is losing the castle, so there is nothing
                // left for the reserve to protect. Same principle as survivalEmergency
                // uncapping the reactive-spend budget.
                //
                // One repair may not be enough against a nuke_3 at low HP, and that is
                // fine: this re-runs every decision, and 48 ticks of delay is ~10 of them,
                // so the bot keeps buying HP for as long as the blast is still lethal and
                // it can still afford to. Returning yields the rest of the decision so no
                // gadget or unit purchase can spend the money the next repair needs.
                if (me.Money >= me.RepairPrice && Act(() => engine.Repair(_side)))
                {
                    ActionCounts[10]++;
                    return;
                }
                // Can't afford it. Fall through and play normally -- but the investment
                // early-exit below is now gated on !nukeEmergency, so the money stays
                // available for a repair on a later decision inside the same window.
            }

            // Claim a safely-affordable investment before ANYTHING else this decision gets
            // a chance to spend the money, rather than checking it last (as below) and
            // hoping nothing else got to it first. Found via trace (hunt v4 headstart) that
            // this race is real and not just theoretical: money visibly reached $32.32
            // against a $31.20 InvestmentPrice while inDanger was false, yet InvestmentCount
            // never moved -- because unlike the first investment ($18, landed on exactly by
            // clean $2 increments from zero), later thresholds are not values the income
            // accrual lands on exactly, so money can jump straight PAST the price in
            // a single decision.
            //
            // THE MECHANISM MOVED, THE HAZARD DID NOT (2026-08-29). Prices are whole dollars
            // now, so it is no longer true that "the thresholds aren't round numbers" -- the
            // fractional side is the INCOME. At InvestmentCount 1 income is 2.6484/s, so
            // money steps 31.78 -> 34.43 and still skips straight over the $32 price without
            // ever equalling it. The overshoot this paragraph exists to defend against is
            // unchanged; only the reason it happens is. The quoted trace numbers are the
            // pre-rounding ones and are left as recorded. DeferForInvestment's <= boundary guard (see its own
            // comment) only protects gadgets up to the exact crossover value -- once money
            // overshoots it in one step, that guard is already inactive the very same
            // decision a gadget or reactive spend could also fire and steal the dollar
            // investing needed. Checking investment first eliminates that whole class of
            // race at the root instead of patching each specific competing spend. Gated on
            // investmentRunwayIsSafe (not just affordability) so a genuine emergency still
            // gets first claim on the money via the normal repair/reactive-spend path below
            // -- this is Marc's framing directly: "if you can get away with an investment
            // before you need to upgrade your HP, take it."
            // Yielding the turn is conditional on the buy LANDING. Invest returns false
            // for good once ARMAGEDDON has been bought, and it leaves InvestmentPrice
            // untouched, so `money >= price` stays true forever after -- returning anyway
            // would stall the bot completely for the rest of the game.
            // `!survivalEmergency` is the survival-instinct half of this check (see the
            // survivalEmergency block above): investmentRunwayIsSafe degenerates to
            // "timeToDeath >= 2 seconds" once money has already reached InvestmentPrice,
            // so without this guard the highest-priority action in the entire bot is to
            // spend the whole pile on economy while the castle is seconds from falling.
            // ── REPAIR: TIME-TO-DEATH ONLY, AND FIRST IN LINE (2026-08-21) ──────────
            //
            // WAS: `castleHpPct < RepairHpThreshold || inDanger`, then `money >= price*1.25`,
            // evaluated AFTER the three gadget casts. Two things were wrong with that.
            //
            //  1. NO OPPORTUNITY COST. Every other inDanger-authorised purchase in this file
            //     is additionally gated on `def.Cost <= reactiveSpendBudget` or on
            //     BigSpendJustified. Repair was the ONLY one that asked merely "can I afford
            //     it". Measured on 3225A7: the bot bought its 7th repair for $8,837 while
            //     $40,000 from the investment rung that leads to ARMAGEDDON, and finished the
            //     game $16,419 short of it.
            //
            //  2. THE PRICE IS SUPER-EXPONENTIAL AND THE BENEFIT IS FLAT. RepairPrice is
            //     e^(0.0109n^3 + ...) * (5n+5), doubling again at n>=8, while every repair
            //     buys exactly +11,000 CastleMaxHealth. The schedule is 20 / 26 / 66 / 169 /
            //     493 / 1,796 / 8,837 / 126,390 -- repair 7 is 443x worse value per HP than
            //     repair 1, and repair 8 costs MORE THAN ARMAGEDDON ($121,221). The old gate
            //     treated all of them identically.
            //
            //  3. inDanger IS THE WRONG QUANTITY, and gets wronger as the game goes on.
            //     It is `enemyIsClose && !investmentRunwayIsSafe`, and the runway test is
            //     `timeToDeath >= timeToInvest*1.4 + 2`. At investment 7 the rung costs
            //     $40,000 against income 750, so timeToInvest is ~53s and the bar becomes 76
            //     SECONDS. Anything on the board flips it. So the flag that authorises
            //     unbounded repair spending gets LOOSER exactly as the rung gets dearer --
            //     it protects the investment race by spending the investment.
            //
            // NOW: a single absolute survival test. Repair when death is imminent, never
            // otherwise. This mirrors the drain-cap fix (DrainCapTtdSeconds), which replaced
            // a proximity proxy with the same estimator and gained +3.8/+6.3 points.
            //
            // AND IT RUNS FIRST. Marc's requirement: repair outranks everything, so it is
            // evaluated ahead of the investment claim and ahead of all gadget/unit spending.
            // At a 5.5s time-to-death an investment cannot pay for itself before the castle
            // falls, so losing the rung to a repair in that window is the correct trade -- and
            // because the gate is this tight, it cannot preempt investing in normal play.
            //
            // The 1.25x affordability cushion is gone too: at this threshold the bot is about
            // to die, so a repair it can exactly afford is one it should buy.
            if (_settings.RepairTtdSeconds > 0)
            {
                // REPAIR KEEPS ITS FIRST CLAIM. Moving it into the defensive comparison was
                // measured and cost 11 points (35.0% -> 23.8%) -- the ordering comment above is
                // load-bearing, and the one purchase that actually prevents death should not be
                // competing for leftovers.
                //
                // What defence-only adds is a PRICE CHECK in the same slot: a repair is worth
                // buying only if the seconds it buys are worth more than it costs, where a
                // second is worth a second of income. Traced against a real collapse the old
                // rule bought six repairs in seven seconds for $11,398, the last at $8,837 for
                // 0.89 seconds of life; this refuses everything past the fourth.
                bool worthRepairing = !(_settings.DefenceOnly || _settings.RepairPriceCheck)
                    || RepairBuysItsPrice(me, threat?.UnblockedDps ?? projectedDps);

                // The absolute floor. Deliberately ORed with the time-to-death gate rather
                // than folded into it: the whole point is that it fires in the case the rate
                // estimate calls safe, so it must not be filtered by that estimate.
                float hpPct = me.CastleMaxHealth > 0
                    ? (float)me.CastleHealth / me.CastleMaxHealth : 1f;
                bool onTheFloor = _settings.RepairHpFloorPct > 0f
                    && hpPct < _settings.RepairHpFloorPct;

                bool burstOk = _settings.RepairMinIntervalSeconds <= 0
                    || (engine._state.CurrentTick - _lastRepairTick)
                       / (double)GameEngine.TICKS_PER_SECOND >= _settings.RepairMinIntervalSeconds;

                if ((timeToDeathSeconds < _settings.RepairTtdSeconds || onTheFloor)
                    && me.Money >= me.RepairPrice
                    && worthRepairing && burstOk
                    && Act(() => engine.Repair(_side)))
                {
                    _lastRepairTick = engine._state.CurrentTick;
                    ActionCounts[10]++;
                    return;
                }
            }
            else if ((castleHpPct < _settings.RepairHpThreshold || inDanger)
                     && me.Money >= me.RepairPrice * 1.25)
            {
                // Legacy path, kept so RepairLegacy reproduces the pre-2026-08-21 bot.
                if (Act(() => engine.Repair(_side))) ActionCounts[10]++;
            }

            if (investmentRunwayIsSafe && !survivalEmergency && !nukeEmergency && me.Money >= me.InvestmentPrice && Act(() => engine.Invest(_side)))
            {
                ActionCounts[9]++;
                return;
            }

            // --- AUTO-SPAWNER (flag 2) ------------------------------------------------
            // Deliberately placed AFTER the investment claim, so a rung we can afford is
            // always taken first and this can only ever spend money investing has declined.
            // Reaching this line means the claim above did not fire, i.e. money is below
            // InvestmentPrice (or invest is dead because ARMAGEDDON has been bought).
            //
            // Called directly on the engine like Invest and Repair, NOT through ApplyAction:
            // that is what keeps action 14 out of the mask, the observation vector unchanged
            // and every ONNX checkpoint valid. See BuyAutoSpawner.
            if (_settings.BuyAutoSpawner
                && investmentRunwayIsSafe && !survivalEmergency && !nukeEmergency
                && me.AutoSpawnLevel < Math.Min(_settings.AutoSpawnMaxLevel, PlayerState.MaxAutoSpawnLevel)
                && me.Money >= me.AutoSpawnPrice
                // Cheap relative to the rung it delays...
                && me.AutoSpawnPrice <= me.InvestmentPrice * _settings.AutoSpawnMaxFractionOfRung
                // ...and bought EARLY in the accumulation cycle rather than on its doorstep.
                && me.Money < me.InvestmentPrice * _settings.AutoSpawnInvestCommitFraction
                && Act(() => engine.UpgradeAutoSpawn(_side)))
            {
                AutoSpawnLevelsBought++;
                return;
            }

            // --- GADGETS: cheap relative to overall spend, high impact, own cooldowns ---
            TryUseOffenseGadget(engine, me, myUnits, enemyUnits, myCastlePos, inDanger, reactiveSpendBudget);
            TryUseDefenseGadget(engine, me, myUnits, enemyUnits, myCastlePos, inDanger, reactiveSpendBudget);
            TryUseSignatureGadget(engine, me, myUnits, enemyUnits, myCastlePos, inDanger, castleHpPct, reactiveSpendBudget);

            // ── SHARED THREAT MODEL ────────────────────────────────────────────────
            // Built here, AFTER the three gadget methods have chosen, so it can net off the
            // relief they just bought before the spawn logic prices anything. That is the
            // whole integration: the gadget code is untouched and knows nothing about units,
            // the spawn code knows nothing about gadgets, and they meet in this one object.
            if (threat != null)
            {
                // Relief is applied AFTER the gadget methods have chosen, so the spawn logic
                // prices only the residual. The model itself is built earlier -- see above --
                // because the repair price check needs the real incoming damage rate before
                // repair takes its turn.
                foreach (var (cdef, cpos) in _castsThisDecision)
                {
                    var (sup, killS, killD) = ThreatModel.EstimateRelief(cdef, cpos, enemyUnits);
                    threat.ApplyRelief(sup, killS, killD);
                }
                LastThreatDebug = threat.ToString();
            }

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
            // REPAIR MOVED. It now runs FIRST, before the early-investment claim and before
            // any gadget or unit spending -- see the RepairTtdSeconds block above. Leaving it
            // here meant three gadget casts had already had a shot at the wallet, so the one
            // purchase that actually prevents death competed for leftovers.

            // WAVE-WIPE PURCHASE (flag 20). Marc's correction to a first version that framed
            // this as "wiper INSTEAD OF repair": "It's not so much the wiper vs repair debate.
            // If we need to repair we should. The focus should be on the stack of attacking
            // units. If the wiper unit costs less than the total value of attackers then it
            // is a positive investment... once it's a positive economic decision to wipe the
            // attacking force, we should do it most of the time."
            //
            // So this is now a standalone economic test, independent of repair: does ONE unit
            // clear the committed stack, and does it cost less than the stack is worth? Full-
            // damage AoE is what makes that a real question -- one swing hits every enemy in
            // contact, so a unit whose Damage covers the toughest attacker kills all of them
            // together. Their $50 of army for our $5 is the swing Marc built the game around.
            //
            // BOUNDED BY AN INTERVAL, which is the lesson from wave-wipe attempt 1 (it could
            // re-buy 6x/second and collapsed Investor 98.5 -> 60.5). A wiper needs time to
            // walk in and swing before we can tell whether another is needed, so refuse to
            // buy a second one until it has had that time.
            bool boughtWiper = false;
            if (!_settings.DefenceOnly
                && _settings.WiperOverRepair && committedEnemyCount > 0 && committedEnemyValue > 0
                && (state.CurrentTick - _lastWiperTick) / 30.0 >= _settings.WiperMinIntervalSeconds)
            {
                // Toughest thing in the stack. Shield absorbs before health (ApplyDamage), so
                // a real one-shot has to cover both.
                double toughest = 0;
                foreach (var u in enemyUnits)
                {
                    if (Math.Abs(u.Position - myCastlePos) > _settings.WaveWipeRadius) continue;
                    double ehp = u.CurrentHealth + u.CurrentShield;
                    if (ehp > toughest) toughest = ehp;
                }

                // Cheapest unit that one-shots that, and is worth less than the stack it kills.
                double maxWorth = committedEnemyValue * _settings.WiperMaxCostVsStackValue;
                UnitDefinition wiper = null;
                foreach (var d in teamDef.Roster)
                {
                    if (d.Cost <= 0 || d.Cost > me.Money || d.Cost > maxWorth) continue;
                    // Same silent failure iteration 1 fixed in SpendOnUnits: without this the
                    // wiper is chosen on price alone, SpawnUnit refuses it for want of a
                    // charge, and the whole decision buys nothing while a charged alternative
                    // that also one-shots the stack sits unconsidered.
                    if (_settings.ChargeAwareEverywhere && !me.HasUnitCharge(d.Id)) continue;
                    if (d.Damage < toughest) continue;
                    if (wiper == null || d.Cost < wiper.Cost) wiper = d;
                }

                if (wiper != null
                    && engine._state.Units.Count(u => u.Side == _side) < MaxOwnUnitsOnField
                    && Act(() => engine.SpawnUnit(_side, wiper.Id)))
                {
                    boughtWiper = true;
                    LastSpawnReason = "wiper";
                    _lastWiperTick = state.CurrentTick;
                    LastUnitsPurchased++;
                    if (wiper.Tier >= 1 && wiper.Tier <= 8) ActionCounts[wiper.Tier]++;
                }
            }

            // Gated on inDanger ONLY. Making waveWipeOpportunity an independent trigger
            // here was measured and was catastrophic -- see the note at its computation.
            // `!boughtWiper` keeps the one-purchase-per-decision pacing this file
            // deliberately maintains -- without it a wiper and a reactive unit could both
            // land on the same tick.
            if (_settings.DefenceOnly)
            {
                // Owns unit spawning entirely in this mode. Still yields to the wiper, which
                // is the same trade priced a different way -- one unit that clears the whole
                // committed stack for less than the stack is worth.
                DefensiveResponse(engine, me, teamDef.Roster, threat, enemyUnits,
                                  myCastlePos, committedEnemyValue, state.CurrentTick);
            }
            else if (inDanger && !boughtWiper)
            {
                SpendOnUnits(engine, me, teamDef.Roster, preferDefense: true, enemyUnits, reactiveSpendBudget: reactiveSpendBudget);
            }

            // --- BLOCK A LONE CHIPPER (flag 3) ---------------------------------------
            // Gated on !inDanger deliberately: when inDanger is true the reactive branch
            // above already owns the response, and layering a second purchase on the same
            // decision is what the one-purchase-per-decision pacing exists to prevent. This
            // rule is for the case that branch never sees -- see BlockSingleChipper.
            if (_settings.BlockSingleChipper && !_settings.DefenceOnly && !inDanger && !boughtWiper)
            {
                // An enemy is "chipping" if it has reached our wall AND nothing of ours is in
                // contact with it. The second half is what keeps this from re-buying against
                // an attacker that is already held -- a blocked unit deals no castle damage,
                // so it needs nothing further spent on it.
                int unblocked = 0;
                float chipSwingRate = 0f;
                foreach (var e in enemyUnits)
                {
                    // e's "enemy castle" IS ours, so the engine's own geometry answers this
                    // rather than a second copy of the leading-edge convention that has
                    // already been got backwards once (see GetDistanceToEnemyCastle).
                    if (engine.GetDistanceToEnemyCastle(e) > _settings.ChipBlockDistance) continue;

                    bool blocked = false;
                    foreach (var m in myUnits)
                    {
                        float gap = _side == 1
                            ? e.Position - (m.Position + m.Width)
                            : m.Position - (e.Position + e.Width);
                        if (gap <= _settings.ChipBlockContactPad) { blocked = true; break; }
                    }
                    if (!blocked) { unblocked++; chipSwingRate += e.AttackSpeed; }
                }

                LastChipperCount = unblocked;

                // SPEND-RATE CAP (v2). Accrues whether or not anything is attacking, so a
                // quiet stretch funds a real burst when a chipper arrives -- but cumulative
                // chip spending can never outrun its share of cumulative income. This is the
                // term v1 lacked; see ChipBlockIncomeFraction for what that cost.
                double chipRate = me.Income * _settings.ChipBlockIncomeFraction;
                _chipAllowance = Math.Min(
                    _chipAllowance + chipRate * (DecisionIntervalTicks / 30f),
                    chipRate * _settings.ChipAllowanceCapSeconds);

                if (unblocked > 0 && chipSwingRate > 0f)
                {
                    // One body per enemy SWING -- the survival law, applied directly. Credit
                    // rather than a flat interval so a fast-swinging chipper is answered at
                    // its own rate instead of an arbitrary one.
                    _chipCredit = Math.Min(_chipCredit + chipSwingRate * (DecisionIntervalTicks / 30f),
                                           _settings.ChipBlockMaxCredit);

                    if (_chipCredit >= 1f)
                    {
                        // Cheapest body that is affordable AND has a charge. The charge test
                        // is iteration 1's lesson applied at the point of use: without it this
                        // rule would inherit exactly the silent-failure mode it was written
                        // after.
                        // Bounded by money, by the per-body price cap, AND by the flow
                        // allowance. The allowance is the binding one against fast-swinging
                        // attackers, which is the whole v1 -> v2 change.
                        double budget = Math.Min(me.Money,
                                        Math.Min(me.Income * _settings.ChipBlockIncomeSeconds,
                                                 _chipAllowance));
                        UnitDefinition cheapest = null;
                        foreach (var d in teamDef.Roster)
                        {
                            if (d.Cost <= 0 || d.Cost > budget) continue;
                            if (!me.HasUnitCharge(d.Id)) continue;
                            if (cheapest == null || d.Cost < cheapest.Cost) cheapest = d;
                        }

                        if (cheapest != null
                            && engine._state.Units.Count(u => u.Side == _side) < MaxOwnUnitsOnField
                            && Act(() => engine.SpawnUnit(_side, cheapest.Id)))
                        {
                            _chipCredit -= 1f;
                            // Debit the allowance, or the cap is a ceiling never paid down:
                            // it would bank to full and then permit every purchase, which is
                            // no cap at all. Same reasoning as the reactive allowance debit.
                            _chipAllowance = Math.Max(0, _chipAllowance - cheapest.Cost);
                            ChipBlocksBought++;
                            LastSpawnReason = "chipblock";
                            LastUnitsPurchased++;
                            if (cheapest.Tier >= 1 && cheapest.Tier <= 8) ActionCounts[cheapest.Tier]++;
                        }
                    }
                }
                else
                {
                    // Nothing to block. Drop the credit rather than banking it, so the rule
                    // cannot save up during a quiet game and dump bodies later.
                    _chipCredit = 0f;
                }
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
            // Conditional on success -- see the note on the other Invest callsite.
            // Also blocked during a survival emergency, for the same reason as that
            // callsite: this fallback has no runway check at all, so it is the LAST thing
            // standing between a full money pile and an investment bought moments before
            // the castle falls. Repair and reactive spending have already had their turn
            // above by the time we reach here, so blocking this doesn't strand the money.
            if (!survivalEmergency && !nukeEmergency && me.Money >= me.InvestmentPrice && Act(() => engine.Invest(_side)))
            {
                ActionCounts[9]++;
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

            // --- ATTACK-VS-SAVINGS EVALUATION --- (see field comments above for the design)
            var enemy = _side == 1 ? state.Player2 : state.Player1;
            int enemyCastlePos = _side == 1 ? GameEngine.MAP_WIDTH - 200 : 200;
            bool pushingEnemyCastle = myUnits.Any(u => Math.Abs(u.Position - enemyCastlePos) < AttackEngageDistance);

            if (pushingEnemyCastle && _attackStartTick < 0)
            {
                _attackStartTick = state.CurrentTick;
                _enemyHpAtAttackStart = enemy.CastleHealth;
                _enemyInvestCountAtAttackStart = enemy.InvestmentCount;
            }
            else if (!pushingEnemyCastle)
            {
                // No push in progress -- the next one gets its own fresh snapshot
                // rather than inheriting a stale comparison point.
                _attackStartTick = -1;
                _enemyInvestCountAtAttackStart = -1;
            }
            else
            {
                // Signal 1 (highest confidence): the enemy successfully invested
                // while we were pushing their castle -- an instant swing regardless
                // of any HP progress made.
                if (_enemyInvestCountAtAttackStart >= 0 && enemy.InvestmentCount > _enemyInvestCountAtAttackStart)
                {
                    _disengageUntilTick = state.CurrentTick + (long)(AttackDisengageCooldownSeconds * 30);
                    _enemyInvestCountAtAttackStart = enemy.InvestmentCount;
                }

                // Signal 2: after a real evaluation window, is this attack actually
                // hurting them? Essentially-zero HP loss over the window is a clear
                // stall; any real, meaningful drain (fast or slow) is a "keep the
                // pressure on" signal and never forces a disengage by itself.
                float elapsedSeconds = (state.CurrentTick - _attackStartTick) / 30f;
                if (elapsedSeconds >= _settings.EnemyHpEvaluationSeconds)
                {
                    float enemyHpLostPct = enemy.CastleMaxHealth > 0
                        ? 100f * (_enemyHpAtAttackStart - enemy.CastleHealth) / enemy.CastleMaxHealth
                        : 0f;
                    // Rate * window, not a flat total -- lets the sweep tune the rate
                    // independently of how long the window is.
                    float minMeaningfulPct = _settings.MinMeaningfulEnemyHpLossPctPerSecond * _settings.EnemyHpEvaluationSeconds;
                    if (enemyHpLostPct < minMeaningfulPct)
                    {
                        _disengageUntilTick = state.CurrentTick + (long)(AttackDisengageCooldownSeconds * 30);
                    }
                    // Rebaseline either way -- keep judging fresh progress against a
                    // recent snapshot, not an increasingly stale starting point.
                    _attackStartTick = state.CurrentTick;
                    _enemyHpAtAttackStart = enemy.CastleHealth;
                }
            }

            // Signal 3: killer instinct -- finishing them off outweighs normal savings
            // discipline, overriding even a just-triggered disengage cooldown.
            //
            // The original formulation compares enemy castle HP against a fixed absolute
            // number, which is broken in a way that only shows up once you check it
            // against the starting state: the default (2676) is HIGHER than the 2000 HP
            // every castle starts with, and only Repair() ever raises MaxHealth. Against
            // an opponent that never repairs, this predicate is true on literally every
            // decision of every game -- see the long warning on
            // KillerInstinctHpThreshold. Killer instinct switched permanently on is not a
            // small mistuning: it is a bypass around the flow allowance, the gadget
            // reserve AND the disengage system simultaneously, i.e. all three of the
            // mechanisms that exist to keep investing while attacking.
            //
            // Replace it with the question the flag was always trying to ask -- "can we
            // actually finish this castle off from here?" -- expressed in the same
            // time-to-X currency the rest of this file already reasons in. Sum the DPS
            // our own units currently in contact with the enemy castle are applying, and
            // ask how many seconds that would need to bring it down. This scales
            // correctly with castle repairs, cannot be true before we have committed a
            // real push, and reads directly as "we are N seconds from winning, stop
            // saving and close it out."
            bool killerInstinct;
            if (_settings.PaceAttackSpendForInvestment)
            {
                float ownPushDps = EstimateOwnCastleDps(engine, myUnits, enemyUnits);
                // Diagnostics only -- no behaviour change. The flag's own confidence is the
                // obvious feature for auditing it and was not previously observable.
                LastOwnPushDps = ownPushDps;
                LastKillerSeconds = ownPushDps > 0.01f
                    ? enemy.CastleHealth / ownPushDps : float.PositiveInfinity;
                killerInstinct = ownPushDps > 0.01f
                    && enemy.CastleHealth / ownPushDps <= _settings.KillerInstinctSeconds;
            }
            else
            {
                killerInstinct = enemy.CastleHealth <= _settings.KillerInstinctHpThreshold;
            }
            // ── TWO BRAKES ON killerInstinct, both default-off ────────────────────────
            // Applied AFTER the trigger rather than folded into it, so the raw signal stays
            // readable in LastKillerInstinctRaw and the brakes can be attributed separately.
            LastKillerInstinctRaw = killerInstinct;
            LastKillerLockReason = null;

            // (a) NEAR A RUNG. Refuse to bypass the savings discipline when the thing being
            // bypassed is about to pay out anyway.
            if (killerInstinct && _settings.KillerInstinctInvestLockoutSeconds > 0)
            {
                double stillNeeded = Math.Max(0, me.InvestmentPrice - me.Money);
                double secondsToRung = me.Income > 0.01
                    ? stillNeeded / me.Income : EffectivelyInfiniteSeconds;
                if (secondsToRung <= _settings.KillerInstinctInvestLockoutSeconds)
                {
                    killerInstinct = false;
                    LastKillerLockReason = "near-invest";
                }
            }

            // (b) THE PUSH FAILED. Track the high-water mark of our army while a push is live;
            // if it more than halves, the push has been cleared and pressing again is throwing
            // good money after bad. Clears when we hold an income advantage again.
            //
            // Uses LastIncomeAdvantage -- the PREVIOUS decision's value -- deliberately:
            // hasIncomeAdvantage is computed further down this method, and a one-decision lag
            // (about 0.17s) is not worth reordering a block this load-bearing for.
            if (_settings.KillerInstinctPushLatch)
            {
                int ownNow = myUnits.Count;
                if (killerInstinct)
                {
                    _killerPushLive = true;
                    if (ownNow > _killerPushPeak) _killerPushPeak = ownNow;
                }
                if (_killerPushLive && _killerPushPeak > 0 && ownNow * 2 < _killerPushPeak)
                {
                    _killerLockedOut = true;
                    _killerPushLive = false;
                    _killerPushPeak = 0;
                }
                if (_killerLockedOut)
                {
                    if (LastIncomeAdvantage) _killerLockedOut = false;
                    else if (killerInstinct)
                    {
                        killerInstinct = false;
                        LastKillerLockReason = "push-failed";
                    }
                }
            }

            if (LastKillerLockReason != null) KillerLockedDecisions++;
            LastKillerInstinct = killerInstinct;

            bool attackDisengaged = state.CurrentTick < _disengageUntilTick && !killerInstinct;
            LastAttackDisengaged = attackDisengaged;

            // --- INCOME ADVANTAGE: the signal for WHEN to commit to an attack ---
            // Marc's framing: the bot has a system to CALL OFF an attack (the enemy
            // investing mid-push, or a stalled HP trend) and a system to keep saving
            // DURING one (the flow allowance), but nothing that decides when to launch a
            // substantial attack in the first place. The existing trigger is `me.Income
            // >= 50`, which is a statement about our own economy in isolation -- it says
            // nothing about whether we are actually ahead. Being rich is not the same as
            // being ahead, and attacking while merely rich against an opponent who is
            // richer is how a push gets counter-punched.
            //
            // An income lead is the cleanest read available on "the economic race is
            // already won, so the compounding argument for holding back no longer
            // applies." Two things follow from it: the attack gate can open before Income
            // 50, and the burst we are willing to assemble can be larger.
            //
            // FAIRNESS NOTE: this reads enemy.Income, which the human player cannot see.
            // That is not a new liberty -- signal 1 above already reads
            // enemy.InvestmentCount, and Income is a pure deterministic function of
            // InvestmentCount (PlayerState.ApplyInvestmentStep), so the two carry
            // identical information. If hidden-information purity ever becomes a
            // requirement, both need replacing together, and the honest substitute is
            // already half-built: _observedEnemyUnitIds could accumulate the roster cost
            // of every enemy unit ever seen, giving a lower bound on their spend rate.
            //
            // Deliberately does NOT relax the disengage system or inDanger. This decides
            // when to START committing, not whether to keep going once committed -- the
            // three existing signals still own that, and this is exactly the "gate the
            // early pivot on detecting what the OPPONENT is doing, not on our own economy
            // state alone" mechanism that the rejected-variant-2 writeup above called for
            // and variant 3 only half-delivered (it read unit-tier diversity, which says
            // nothing about the economy actually being raced).
            bool hasIncomeAdvantage = _settings.IncomeAdvantageAttack
                && me.Income >= _settings.IncomeAdvantageMinIncome
                && me.Income >= enemy.Income * _settings.IncomeAdvantageRatio;
            LastIncomeAdvantage = hasIncomeAdvantage;

            // Deliberately does NOT exclude waveWipeOpportunity. Attempt 1 did, and that
            // stopped the bot attacking whenever anything was near its castle -- a large
            // part of why it collapsed. The reactive block above is inDanger-gated again,
            // so the two branches remain mutually exclusive on their own.
            // Reset the per-wave budget whenever an investment lands -- that is the event
            // that earns the bot another wave. Done here rather than inside SpendOnUnits so
            // it still fires on decisions where no attack spending happens at all.
            if (me.InvestmentCount != _lastSeenInvestmentCount)
            {
                _lastSeenInvestmentCount = me.InvestmentCount;
                _attackSpentThisCycle = 0;
            }

            // Flag (16): optionally require a higher InvestmentCount before the attack gate
            // may open. At 0 this is vacuously true and the gate is exactly Income >= 50.
            bool investmentGateOpen = me.InvestmentCount >= _settings.AttackGateMinInvestment;

            // DEFENCE-ONLY closes this branch outright -- no offensive unit ever gets bought.
            // The win condition does not need it: invest eight times and ARMAGEDDON ends the
            // game (GameEngine.Invest -> ArmageddonEffect).
            // An enemy Wave or Blackhole makes an attack purchase unconvertible -- see
            // HazardAttackBlackout. Saving through it is strictly better than spending into it.
            bool hazardBlackout = _settings.HazardAttackBlackout && AttackBlockingHazardActive(engine);
            LastHazardBlackout = hazardBlackout;
            if (hazardBlackout) HazardBlackoutDecisions++;

            if (!_settings.DefenceOnly
                && !inDanger && investmentGateOpen && (me.Income >= 50 || hasIncomeAdvantage) && !attackDisengaged
                && !hazardBlackout)
            {
                // Diagnostic: the offensive branch was ENTERED. Distinguishes a gate leak from
                // action-queue latency -- Update drains one queued action per tick BEFORE
                // Decide() runs, so money can leave under an "attack" label on a tick where the
                // blackout is already true, without the offensive branch having been entered.
                OffensiveSpendDecisions++;
                if (LastHazardBlackout) OffensiveWhileBlackedOut++;
                SpendOnUnits(engine, me, teamDef.Roster, preferDefense: false, enemyUnits, killerInstinct,
                             hasIncomeAdvantage: hasIncomeAdvantage);
            }
        }

        /// <summary>
        /// Buys blocking bodies at the rate the survival law says is needed to outlast the wait
        /// for the next investment -- and nothing more.
        ///
        /// THE OBJECTIVE IS THE INVESTMENT CLOCK, not the enemy. Every second bought is only
        /// worth buying because it is a second of income compounding toward the next invest,
        /// and eight investments is the whole game (ARMAGEDDON). So the target is "outlive the
        /// time it takes to afford the next one", and once that is already true for free the
        /// right move is to spend nothing at all.
        ///
        /// THREE THINGS THE MEASUREMENTS DICTATE HERE:
        ///  - Buy the CHEAPEST body. A body absorbs one swing whatever it is, so per unit of
        ///    blocking only price matters -- tier 4 costs 10x a tier 1 for the same job, tier 5
        ///    30x, tier 6 166x.
        ///  - Aim at the rate, not proportionally to the threat. dt/dr = K/(S-r)^2 accelerates,
        ///    so a fraction of the needed rate buys almost nothing; this either commits to the
        ///    computed rate or stays home.
        ///  - Wait for the force to settle before reading it, unless waiting is what kills us.
        ///
        /// KNOWN CEILING: DecisionIntervalTicks is 5 and this buys at most one unit per
        /// decision, so the bot physically cannot exceed 6 bodies/sec. The sweeps say that
        /// covers most unescorted forces but NOT an escorted tier 7, which wanted 15-30/s.
        /// Against those the credit simply saturates and the gadget layer has to carry it.
        /// </summary>
        /// <summary>Seconds of income needed to buy every remaining investment through ARMAGEDDON.</summary>
        public float LastDefenceTarget { get; private set; }

        /// <summary>Which branch of the trade won last decision: free / wait / wipe / block.</summary>
        public string LastDefenceChoice { get; private set; } = "";

        /// <summary>Dollar price of one castle HP, from the repair ladder. Diagnostic.</summary>
        public double LastDollarsPerHp { get; private set; }

        /// <summary>Block rate the law last asked for, bodies/sec. Diagnostic.</summary>
        public float LastRequiredRate { get; private set; }

        /// <summary>Block rate the bot can actually deliver, after the spawn ceiling.</summary>
        public float LastAchievableRate { get; private set; }

        /// <summary>Net dollar swing of each option last decision. Diagnostic.</summary>
        public string LastDefenceDebug { get; private set; } = "";

        /// <summary>Why this decision did not wipe. Diagnostic only.</summary>
        public string LastWipeVeto { get; private set; } = "";

        /// <summary>
        /// What a wipe would ACTUALLY reach, against what it is being credited for.
        ///
        /// The value side of the wipe assumes one swing clears the pile, which is only true
        /// when ClampToContact has parked the attackers at a single contact point. Neither
        /// FindWiper nor committedEnemyValue checks that -- both simply sum everything inside
        /// WaveWipeRadius (500px), while a swing reaches only Range beyond the attacker's own
        /// sprite edge. These record both so the gap can be measured rather than assumed.
        /// </summary>
        public int LastWipeInRadius { get; private set; }
        public int LastWipeInReach { get; private set; }
        public double LastWipeValRadius { get; private set; }
        public double LastWipeValReach { get; private set; }
        public float LastWipeSpread { get; private set; }

        /// <summary>
        /// Running tally of what wipes actually bought, for calibrating the re-buy gate.
        /// Recorded at the moment of purchase: the value a single swing can reach, the price
        /// paid, and the count. The reach figure is the honest one -- the value sitting inside
        /// the radius but outside the swing is credited by the scoring and never collected.
        /// </summary>
        public int WipeCount { get; private set; }
        public double WipeValueReached { get; private set; }
        public double WipeValueCredited { get; private set; }
        public double WipeSpend { get; private set; }

        /// <summary>
        /// What the BEST available wiper would have collected, against what we actually bought.
        ///
        /// FindWiper insists on one-shotting the TOUGHEST unit in the band, which can force a
        /// tier-7 purchase to clear a pile that is mostly tier-4 chaff. A cheaper unit that
        /// one-shots the chaff and leaves the one tough survivor may be the far better trade.
        /// These record both so the rule can be judged rather than assumed.
        /// </summary>
        public double WipeBestAltKill { get; private set; }
        public double WipeBestAltCost { get; private set; }
        public int WipeBestAltCount { get; private set; }

        /// <summary>Bodies currently owed. Diagnostic.</summary>
        public float LastBlockCredit => _blockCredit;

        /// <summary>Survival the law predicts with no defence at all. Diagnostic.</summary>
        public float LastBareSurvival { get; private set; }

        /// <summary>
        /// How long until this economy can buy ARMAGEDDON, assuming it spends on nothing else.
        ///
        /// Walks the real ladder on a CLONE rather than reimplementing the curve, because
        /// PlayerState.ApplyInvestmentStep is deliberately the single source of truth for the
        /// income/price formula (including its hardcoded overrides at counts 7 and 8) and a
        /// second copy here would drift from it exactly the way the time-machine constructor
        /// once did.
        /// </summary>
        private static float SecondsToArmageddon(PlayerState me)
        {
            if (me.ArmageddonUsed) return 0f;

            var sim = me.Clone();
            float seconds = 0f;
            for (int guard = 0; guard <= PlayerState.ArmageddonInvestmentCount + 1; guard++)
            {
                if (sim.Money < sim.InvestmentPrice)
                {
                    if (sim.Income <= 0.01) return EffectivelyInfiniteSeconds;
                    seconds += (float)((sim.InvestmentPrice - sim.Money) / sim.Income);
                    sim.Money = sim.InvestmentPrice;
                }
                sim.Money -= sim.InvestmentPrice;

                // At the top of the ladder the purchase IS Armageddon -- it does not step the
                // economy, it ends the game (GameEngine.Invest).
                if (sim.InvestmentCount >= PlayerState.ArmageddonInvestmentCount) return seconds;
                sim.ApplyInvestmentStep();
            }
            return seconds;
        }

        /// <summary>
        /// Dollar price of one point of castle HP, taken from what it actually costs to buy HP
        /// back: a repair.
        ///
        /// Uses the HEALING component of the preview, not the whole delta. PreviewRepairStep
        /// also raises CastleMaxHealth, so at full HP the raw delta is mostly a max-health
        /// increase and would price HP at almost nothing precisely when the castle is healthy.
        /// The heal is 20% of the new maximum, and that is the part that replaces damage taken.
        ///
        /// Rises naturally over a game, because RepairPrice climbs faster than max health does.
        /// That is the right shape: HP genuinely is more precious later, when there is less
        /// time left to earn back what replacing it costs.
        /// </summary>
        /// <summary>
        /// Is one more repair worth what it costs, in the only currency that matters here?
        ///
        /// The health a repair restores buys `restored / incomingDps` seconds, and every second
        /// survived is a second of income compounding toward the investment that ends the game.
        /// So the test is simply: seconds bought x income > price.
        ///
        /// Deliberately does NOT credit the permanent max-health increase a repair also buys.
        /// That is real value, so this under-values repair -- the safe direction, given the
        /// failure it exists to stop is repairing far too often.
        /// </summary>
        private static bool RepairBuysItsPrice(PlayerState me, float incomingDps)
        {
            if (incomingDps <= 0.01f) return true;          // no attack to price against
            var (previewHp, _) = me.PreviewRepairStep();
            double restored = previewHp - me.CastleHealth;
            if (restored <= 0) return false;
            return (restored / incomingDps) * me.Income > me.RepairPrice;
        }

        private static double DollarsPerHp(PlayerState me)
        {
            var (nextHealth, nextMax) = me.PreviewRepairStep();
            double healed = nextHealth - me.CastleHealth;
            if (healed < 1.0) healed = 0.2 * nextMax;
            return me.RepairPrice / Math.Max(1.0, healed);
        }

        /// <summary>
        /// Cheapest unit that one-shots the toughest thing in the pile, and is worth less than
        /// the pile is. Shield absorbs before health (ApplyDamage), so a real one-shot has to
        /// cover both.
        ///
        /// Damage-based rather than "one tier above the wave": it is the property that actually
        /// makes the swing clear the stack, and a tier is only a proxy for it. Works because
        /// ClampToContact parks every attacker at the same contact point, so one swing reaches
        /// all of them -- which is also why letting them pile up first is what makes this pay.
        /// </summary>
        /// <summary>
        /// Compares what a wipe is credited with clearing to what one swing can actually touch.
        /// The frontmost enemy is where the wiper will come to rest (ClampToContact), and its
        /// swing reaches Range beyond its own sprite edge -- so anything further back than
        /// width + range is in the radius but not in the swing.
        /// </summary>
        private void MeasureWipeReach(GameEngine engine, PlayerState me, UnitDefinition wiper,
                                      List<Unit> enemyUnits, int myCastlePos, float radius)
        {
            LastWipeInRadius = 0; LastWipeInReach = 0;
            LastWipeValRadius = 0; LastWipeValReach = 0; LastWipeSpread = 0;
            LastAltKill = 0; LastAltCost = 0; LastAltId = "-";
            if (wiper == null) return;

            float front = float.MaxValue, back = float.MinValue;
            foreach (var u in enemyUnits)
            {
                if (Math.Abs(u.Position - myCastlePos) > radius) continue;
                float d = Math.Abs(u.Position - myCastlePos);
                if (d < front) front = d;
                if (d > back) back = d;
            }
            if (front == float.MaxValue) return;
            LastWipeSpread = back - front;

            float reach = wiper.Width + wiper.Range;
            foreach (var u in enemyUnits)
            {
                float d = Math.Abs(u.Position - myCastlePos);
                if (d > radius) continue;
                LastWipeInRadius++;
                LastWipeValRadius += EstimateUnitCost(engine, u);
                if (d - front <= reach)
                {
                    LastWipeInReach++;
                    LastWipeValReach += EstimateUnitCost(engine, u);
                }
            }

            // What every OTHER roster unit would have collected from the same pile: the value of
            // everything in reach that its single swing would actually kill, less its price.
            // A cheap unit that one-shots forty tier-4s and leaves one tier-7 standing can beat
            // the expensive one that kills all forty-one.
            double bestNet = double.NegativeInfinity;
            var team = GameDataManager.Teams.FirstOrDefault(t => t.Color == me.Team);
            if (team == null) return;
            foreach (var d in team.Roster)
            {
                if (d.Cost <= 0 || d.Cost > me.Money) continue;
                double kills = 0;
                foreach (var u in enemyUnits)
                {
                    float dist = Math.Abs(u.Position - myCastlePos);
                    if (dist > radius || dist - front > reach) continue;
                    if (u.CurrentHealth + u.CurrentShield <= d.Damage)
                        kills += EstimateUnitCost(engine, u);
                }
                if (kills - d.Cost > bestNet)
                {
                    bestNet = kills - d.Cost;
                    LastAltKill = kills; LastAltCost = d.Cost; LastAltId = d.Id;
                }
            }
        }

        /// <summary>Best alternative wiper this decision -- what it would kill, and its price.</summary>
        public double LastAltKill { get; private set; }
        public double LastAltCost { get; private set; }
        public string LastAltId { get; private set; } = "-";

        private static UnitDefinition FindWiper(GameEngine engine, PlayerState me, List<UnitDefinition> roster,
                                                List<Unit> enemyUnits, int myCastlePos, float radius,
                                                out double killValue, bool ignoreMoney = false,
                                                List<Unit> myUnits = null, bool countCoverage = true,
                                                // Optional so the existing callers are unchanged; null means
                                                // "no charge filtering", which is the committed behaviour.
                                                HeuristicBotSettings settings = null)
        {
            killValue = 0;

            // Where the pile is. A wiper walks out and stops at the nearest enemy
            // (ClampToContact), so the front of the pile is where its swing lands.
            float front = float.MaxValue;
            foreach (var u in enemyUnits)
            {
                float d0 = Math.Abs(u.Position - myCastlePos);
                if (d0 <= radius && d0 < front) front = d0;
            }
            if (front == float.MaxValue) return null;

            // WHAT WE ALREADY HAVE OUT THERE.
            //
            // A unit on the field is OBSERVED, not predicted: it stands at a known position with
            // a known Damage, so "can it already kill this enemy" needs no model of the future.
            // Anything it covers is not worth buying a second unit to kill, and the wipe should
            // be priced on the MARGIN -- what a new purchase adds over what is already deployed.
            //
            // This is what stops the bot buying the tier 7 its own reinforcements gadget is
            // handing it. Measured on the pinned mirror before this existed: between 160s and
            // 210s it paid $22,726 for 11 tier-7 units while 25 identical tier-7 units arrived
            // free from its own reinforcements_3, and tier-7 purchases were 96% of its entire
            // unit budget for the game. ReinforcementsEffect spawns the first of its five
            // immediately, so from the moment a wave is cast it is on the field to be counted --
            // which is why the observed-only rule catches almost all of what a predictive one
            // would, without having to forecast a battle.
            //
            // The coverage test deliberately mirrors the purchase test below, including its
            // optimism: both credit a single unit with everything it one-shots in reach, though
            // it can only swing at one target at a time. The absolute numbers are therefore too
            // high on BOTH sides -- and it is the difference between them that is being used.
            // The coverage test must score an existing unit EXACTLY as the purchase test below
            // scores a candidate, or the comparison is rigged. The first version of this scored
            // an existing unit only where it currently stands, while candidates are priced as if
            // they were already at the front of the pile -- and since our reinforcements spawn at
            // our own castle and have to walk, that version credited them with nothing and
            // changed almost no decisions (50.0% vs 53.1%, wipes 38.7, spend unmoved).
            //
            // So: credit a live friendly unit with what it will kill WHEN IT ARRIVES, from the
            // same front, using its own reach -- identical treatment. A unit counts if it has not
            // already passed the pile.
            var covered = new HashSet<Guid>();
            if (myUnits != null && countCoverage)
            {
                foreach (var m in myUnits)
                {
                    if (m.CurrentHealth <= 0) continue;
                    float mreach = m.Width + m.Range;
                    if (Math.Abs(m.Position - myCastlePos) > front + mreach) continue;  // past it
                    foreach (var u in enemyUnits)
                    {
                        float dist = Math.Abs(u.Position - myCastlePos);
                        if (dist > radius || dist - front > mreach) continue;
                        if (u.CurrentHealth + u.CurrentShield <= m.Damage) covered.Add(u.InstanceId);
                    }
                }
            }

            // Pick the unit that maximises VALUE KILLED MINUS PRICE.
            //
            // THIS REPLACED "cheapest unit that one-shots the toughest thing present", which was
            // not an economic rule at all and was the reason wipes lost money. Against forty
            // tier-4s plus one tier-7 it forced a $2,066 purchase to clear mostly $18 chaff,
            // when an $81 tier-5 one-shots all forty and leaves the tier-7 standing. Measured
            // over 1,600 wipes: what that rule bought killed $715 for $1,528 (net -$813), while
            // the best available alternative killed $361 for $171 (net +$189) -- and a different
            // unit was the better buy in 66% of them.
            //
            // Clearing the whole pile is not the objective and never was; the objective is the
            // trade. A cheaper unit that kills half as much for a ninth of the price wins.
            UnitDefinition best = null;
            double bestNet = double.NegativeInfinity;
            foreach (var d in roster)
            {
                if (d.Cost <= 0) continue;
                if (!ignoreMoney && d.Cost > me.Money) continue;
                // Charges gate a purchase exactly as money does, so they belong beside the
                // money test -- and behind !ignoreMoney for the same reason. The ignoreMoney
                // caller is the "what would the best wiper be at any price" DIAGNOSTIC, and
                // filtering that would make the alt-comparison it feeds report a cheaper
                // alternative than the one it actually rejected.
                if (!ignoreMoney && settings != null && settings.ChargeAwareEverywhere
                    && !me.HasUnitCharge(d.Id)) continue;

                // One swing reaches Range beyond this unit's own sprite edge, so a wide unit
                // sweeps a deeper slice of the pile than a narrow one.
                float reach = d.Width + d.Range;
                double kills = 0;
                foreach (var u in enemyUnits)
                {
                    float dist = Math.Abs(u.Position - myCastlePos);
                    if (dist > radius || dist - front > reach) continue;
                    if (covered.Contains(u.InstanceId)) continue;   // already handled, not marginal
                    if (u.CurrentHealth + u.CurrentShield <= d.Damage)
                        kills += EstimateUnitCost(engine, u);
                }

                double net = kills - d.Cost;
                if (net > bestNet)
                {
                    bestNet = net;
                    best = d;
                    killValue = kills;
                }
            }
            if (best == null) killValue = 0;
            return best;
        }

        private void DefensiveResponse(GameEngine engine, PlayerState me, List<UnitDefinition> roster,
                                       ThreatModel threat, List<Unit> enemyUnits, int myCastlePos,
                                       double committedEnemyValue, long nowTick)
        {
            if (threat == null || threat.Idle) { _blockCredit = 0f; return; }

            float bareSurvival = threat.SurvivalSeconds(0f);
            LastBareSurvival = bareSurvival;

            // ENGAGE ON DANGER, NOT ON THE FORCE HOLDING STILL. Either the attack is big enough
            // to be worth answering, or it is close enough to killing us that the size no
            // longer matters.
            bool worthAnswering = threat.UnblockedDps >= _settings.ThreatEngageDps
                               || bareSurvival < _settings.DefenceReadPanicSeconds;
            if (!worthAnswering) { _blockCredit = 0f; LastDefenceChoice = "watch"; return; }

            // ── THE DEATH CONSTRAINT ───────────────────────────────────────────────
            // Above this line HP is cheap and SHOULD be traded: the early repairs cost almost
            // nothing against a running income, and giving up health to keep investing is
            // straightforwardly good play. Below it, survival stops being a trade at all and
            // the dollar economics are simply switched off.
            //
            // Keyed to RepairTtdSeconds deliberately, so the defence and the repair agree about
            // where the line is instead of each carrying its own threshold. KNOWN GAP: repair
            // still reads the older EstimateProjectedThreatDps clock while this reads the
            // measured survival law, so the two numbers can still differ even though the
            // threshold is now shared. Unifying them changes the SHIPPED bot's repair timing,
            // so it needs its own measurement rather than a quiet ride-along here.
            bool survivalCritical = bareSurvival <= _settings.RepairTtdSeconds;

            // Plan against the remaining runway to ARMAGEDDON, capped to a horizon a single
            // wave can plausibly span. Deliberately NOT time-to-next-investment: that goes to
            // zero the moment the bot can afford the next one, which made the defence thinnest
            // exactly when the wallet was fullest.
            float runway = SecondsToArmageddon(me);
            float target = Math.Min(runway, _settings.DefenceHorizonSeconds)
                         * _settings.DefenceTargetMultiplier
                         + _settings.SafetyBufferSeconds;
            LastDefenceTarget = target;

            if (bareSurvival >= target) { _blockCredit = 0f; LastDefenceChoice = "free"; return; }

            // ── THE TRADE ──────────────────────────────────────────────────────────
            // Three options, all priced in dollars over the same window: do nothing, block at
            // the rate we can actually achieve, or wipe the pile. Whichever swing is biggest
            // wins.
            //
            // THE SPAWN CEILING IS AN INPUT, NOT AN AFTERTHOUGHT. One purchase per decision at
            // DecisionIntervalTicks is a hard six units per second -- and deliberately so, since
            // no human clicks faster. Pricing the block option at the rate the law ASKS for
            // rather than the rate we can DELIVER made blocking look like it solved problems it
            // cannot touch: traced against a 45-unit wave the law wanted 100-280 bodies/sec
            // against a ceiling of 6. Valuing it honestly is what lets the other options win
            // when they deserve to.
            float ceiling = GameEngine.TICKS_PER_SECOND / (float)DecisionIntervalTicks;

            float required = survivalCritical ? threat.SwingRate : threat.RequiredBlockRate(target);
            float achievable = Math.Min(required, ceiling);
            LastRequiredRate = required;
            LastAchievableRate = achievable;

            double perHp = DollarsPerHp(me);
            LastDollarsPerHp = perHp;

            UnitDefinition cheapest = null;
            for (int i = 0; i < roster.Count; i++)
            {
                var d = roster[i];
                if (d.Cost <= 0 || d.Cost > me.Money) continue;
                // The block rate this method computes is meaningless if the body cannot be
                // bought: _blockCredit is only decremented inside `if (Act(...))`, so a
                // refused spawn banks credit to MaxBlockCredit that can never be spent, and
                // the bot believes it is blocking at the survival law's rate while delivering
                // one unit id's charge regen -- 1/sec against a documented ceiling of 6.
                if (_settings.ChargeAwareEverywhere && !me.HasUnitCharge(d.Id)) continue;
                if (cheapest == null || d.Cost < cheapest.Cost) cheapest = d;
            }

            // Price everything over the same window: how long this decision is meant to cover.
            float window = Math.Min(target, _settings.DefenceHorizonSeconds);
            if (window < 1f) window = 1f;

            // VALUE THE TIME, NOT THE HEALTH.
            //
            // Pricing each option by the health it saves looks equivalent and is not. During any
            // serious attack `D x window` exceeds the whole castle, so every option's health term
            // clamps to the same number and CANCELS -- the comparison collapses to cost alone and
            // "do nothing", at $0, beats blocking exactly when blocking is needed. Measured: that
            // formulation took the bot from 36.9% to 15.6%.
            //
            // Seconds do not have that failure. Every second survived is a second of income
            // compounding toward the investment that ends the game, so a second is worth a
            // second of income, and each option is worth the seconds it ADDS over standing
            // still. Capped at the planning window so an option that holds forever does not
            // return an infinite score.
            double baseSurvival = Math.Min(bareSurvival, window);
            double netNothing = 0.0;   // the baseline every other option is measured against

            double survBlock = Math.Min(threat.SurvivalSeconds(achievable), window);
            double blockSpend = achievable * (cheapest?.Cost ?? 0) * window;
            double netBlock = cheapest == null ? double.NegativeInfinity
                : (survBlock - baseSurvival) * me.Income - blockSpend;

            // WIPE -- one unit clears the whole pile, so it destroys everything committed and
            // pays for exactly one unit, against the damage that still lands while it walks in.
            var myUnitsNow = engine._state.Units.Where(u => u.Side == _side).ToList();
            var wiper = FindWiper(engine, me, roster, enemyUnits, myCastlePos,
                                  _settings.WaveWipeRadius, out double wiperKillValue,
                                  myUnits: myUnitsNow,
                                  countCoverage: _settings.WiperCountsFieldCoverage,
                                  settings: _settings);
            MeasureWipeReach(engine, me, wiper, enemyUnits, myCastlePos, _settings.WaveWipeRadius);
            bool wipeReady = wiper != null
                && (nowTick - _lastWiperTick) / 30.0 >= _settings.WiperMinIntervalSeconds;
            // WHAT THE WIPE IS WORTH, priced off two things it used to get wrong.
            //
            // FIRST, the material: use the value one swing can actually REACH, not everything
            // that happens to be inside WaveWipeRadius. Measured over 1,631 wipes, 86% of the
            // credited value was reachable and the rest was never collected.
            //
            // SECOND, and much larger: the old version credited a wipe with clearing the threat
            // for the whole planning window -- about 41 seconds of income, which swamped every
            // other term and approved anything. Against a swarm the pile regrows in seconds. So
            // the seconds a wipe really buys is the time the opponent needs to rebuild what it
            // destroyed, which we already track as the rate they commit money at. That collapses
            // toward zero exactly when the attack is dense enough for it to matter, which is the
            // behaviour a fixed cooldown was crudely approximating.
            //
            // Measured before this change: the average wipe collected $736 against a $1,383
            // price -- half its cost -- and the scoring approved it every time.
            // What the chosen unit actually kills, not everything standing in reach of it --
            // the selection above deliberately leaves survivors when they are not worth the
            // price of killing them.
            double reachValue = wiperKillValue;
            double regrowSeconds = _enemyValueRate > 1.0
                ? reachValue / _enemyValueRate
                : window;                     // not reinforcing, so the kill stands
            double maxWipeGain = Math.Max(0, window - _settings.WipeLeadSeconds - baseSurvival);
            double gainWipe = Math.Min(maxWipeGain, regrowSeconds);
            double netWipe = wipeReady
                ? gainWipe * me.Income + reachValue - wiper.Cost
                : double.NegativeInfinity;

            // Never wait when survival is already critical, however good the trade looks -- and
            // waiting only pays while the opponent commits value faster than we bleed it.
            bool waitingPays = !survivalCritical
                && _enemyValueRate > threat.UnblockedDps * perHp;
            if (!waitingPays) netNothing -= 1e-6;   // break exact ties away from standing still

            LastDefenceDebug = $"need={required:F1} can={achievable:F1} base={baseSurvival:F1}s " +
                               $"block={netBlock:F0} wipe={netWipe:F0}";

            // WHY NO WIPE, when there was none. Wipers are NOT gated by the critical state --
            // this check runs before it -- so every decision that ends up labelled "critical" is
            // one where a wipe was considered and rejected. Knowing which of the three reasons
            // it was is the difference between tuning the cooldown, the budget, or the scoring.
            if (wiper == null)
            {
                var anyPrice = FindWiper(engine, me, roster, enemyUnits, myCastlePos,
                                         _settings.WaveWipeRadius, out _, ignoreMoney: true,
                                         myUnits: myUnitsNow,
                                         countCoverage: _settings.WiperCountsFieldCoverage);
                // Distinguish "nothing in the roster one-shots it" from "nothing is close
                // enough to wipe yet" -- FindWiper only looks inside WaveWipeRadius, so a fight
                // our blockers are holding out at midfield produces no wipe target at all.
                int inRadius = 0;
                foreach (var u in enemyUnits)
                    if (Math.Abs(u.Position - myCastlePos) <= _settings.WaveWipeRadius) inRadius++;
                LastWipeVeto = anyPrice != null ? "too-poor"
                             : inRadius == 0 ? "out-of-radius"
                             : "nothing-one-shots";
            }
            else if (!wipeReady) LastWipeVeto = "cooldown";
            else if (wiperKillValue <= 0) LastWipeVeto = "already-covered";
            else if (netWipe < netBlock || netWipe < netNothing) LastWipeVeto = "lost-scoring";
            else LastWipeVeto = "-";

            if (wipeReady && netWipe >= netBlock && netWipe >= netNothing)
            {
                if (engine._state.Units.Count(u => u.Side == _side) < MaxOwnUnitsOnField
                    && Act(() => engine.SpawnUnit(_side, wiper.Id)))
                {
                    _lastWiperTick = nowTick;
                    _blockCredit = 0f;
                    WipeCount++;
                    WipeValueReached += wiperKillValue;
                    WipeValueCredited += committedEnemyValue;
                    WipeSpend += wiper.Cost;
                    WipeBestAltKill += LastAltKill;
                    WipeBestAltCost += LastAltCost;
                    if (LastAltId != wiper.Id) WipeBestAltCount++;
                    LastSpawnReason = "wiper";
                    LastDefenceChoice = "wipe";
                    LastUnitsPurchased++;
                    if (wiper.Tier >= 1 && wiper.Tier <= 8) ActionCounts[wiper.Tier]++;
                    return;
                }
            }

            if (!survivalCritical && netNothing > netBlock)
            {
                _blockCredit = 0f;
                LastDefenceChoice = waitingPays ? "wait" : "outmatched";
                return;
            }

            float rate = achievable;
            if (rate <= 0f) { _blockCredit = 0f; LastDefenceChoice = "free"; return; }
            LastDefenceChoice = survivalCritical ? "critical" : "block";

            _blockCredit = Math.Min(_blockCredit + rate * (DecisionIntervalTicks / 30f),
                                    _settings.MaxBlockCredit);
            if (_blockCredit < 1f) return;

            if (cheapest == null) return;
            if (engine._state.Units.Count(u => u.Side == _side) >= MaxOwnUnitsOnField) return;

            if (Act(() => engine.SpawnUnit(_side, cheapest.Id)))
            {
                _blockCredit -= 1f;
                LastSpawnReason = "block";
                LastUnitsPurchased++;
                if (cheapest.Tier >= 1 && cheapest.Tier <= 8) ActionCounts[cheapest.Tier]++;
            }
        }

        // Sums the DPS our own units are currently applying to the ENEMY castle -- the
        // mirror image of EstimateProjectedThreatDps, and gated by the same contact test
        // (GameEngine.GetDistanceToEnemyCastle vs the unit's Range) that MoveAndFight
        // uses before it damages a castle, so only units actually landing hits on
        // the castle count. Reads the live Unit's own stats rather than the roster
        // definition because those already reflect any active buffs (rage, speed) the
        // roster row does not know about.
        //
        // The second condition matters and is easy to miss: MoveAndFight attacks the
        // castle in an `else if (castleInRange)` branch, i.e. ONLY when the unit has no
        // enemy unit to fight. A unit standing at the enemy castle while trading with
        // defenders is contributing zero castle damage, and counting it would make killer
        // instinct fire during exactly the grinding stalemate where committing the last of
        // the money is worst. Replicates FindTargetsFast's edge-to-edge contact test
        // (centre distance minus both half-widths, against the attacker's Range);
        // deliberately skips its flying/ranged exclusion, which would only ever make this
        // estimate more conservative.
        private float EstimateOwnCastleDps(GameEngine engine, List<Unit> myUnits, List<Unit> enemyUnits)
        {
            float dps = 0f;
            foreach (var u in myUnits)
            {
                if (engine.GetDistanceToEnemyCastle(u) > u.Range) continue;

                bool engagedWithUnits = false;
                foreach (var e in enemyUnits)
                {
                    float edgeToEdge = Math.Abs(u.Position - e.Position) - (u.Width / 2f) - (e.Width / 2f);
                    if (Math.Max(0f, edgeToEdge) <= u.Range) { engagedWithUnits = true; break; }
                }
                if (engagedWithUnits) continue;

                dps += u.Damage * u.AttackSpeed;
            }
            return dps;
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

            if (Act(() => engine.SpawnUnit(_side, waveBreaker.Id)))
            {
                LastSpawnReason = "wavebreaker";
                LastUnitsPurchased++;
                if (targetTier >= 1 && targetTier <= 8) ActionCounts[targetTier]++;
            }
        }

        private static float Power(Unit u) => Gadgets.GadgetTargeting.Power(u);

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

        private void SpendOnUnits(GameEngine engine, PlayerState me, List<UnitDefinition> roster, bool preferDefense, List<Unit> enemyUnits, bool killerInstinct = false, double reactiveSpendBudget = double.MaxValue, bool hasIncomeAdvantage = false)
        {
            LastUnitsPurchased = 0;
            int ownUnitCount = engine._state.Units.Count(u => u.Side == _side);

            // Once the cost-efficient swarm is already at full size and the economy is
            // still piling up money beyond what it needs, cost-per-value stops mattering
            // -- more cheap units just stalemates against an equally cheap trickle from
            // the other side. Switch to buying pure raw power instead, and make room for it.
            //
            // richMode used to ALSO require ownUnitCount >= MaxOwnUnitsOnField (120) --
            // traced 12 of Marc's own recorded wins against the models the bot struggles
            // with most (v4/v14/v25, 4 games each, --trace-human tooling) and found the
            // shadow bot's own counterfactual read at every logged decision: whenever it
            // suggested a concrete unit purchase at all (13 instances across 12 games), it
            // was ALWAYS Tier1 or Tier4 -- even at Income 60-252, with the human buying
            // Tier5-8 at those exact same moments. Root cause: at typical game lengths
            // (~2-3 minutes even in these hard-fought matchups), buying one unit per
            // ~0.167s decision never gets remotely close to 120 units on the field, so
            // richMode/RawPower were structurally unreachable in practice -- ScoreUnit's
            // cost-per-value ranking (which chronically favors the cheapest viable tier,
            // per its own doc comment above) was the ONLY pool ever actually used, no
            // matter how much money piled up. Dropped the unit-count requirement --
            // richMode now fires on affordability alone (comfortably afford the best unit
            // several times over), matching what a human clearly does once money stops
            // being the constraint: buy the best unit, not the most cost-efficient one.
            double topCost = roster.Where(u => u.Cost > 0).Select(u => (double)u.Cost).DefaultIfEmpty(1).Max();
            bool richMode = me.Money >= topCost * 3;
            int cap = (richMode || ownUnitCount >= MaxOwnUnitsOnField) ? MaxOwnUnitsOnField * 2 : MaxOwnUnitsOnField;

            // First cut of this fix applied richMode's RawPower scoring during REACTIVE
            // spending too (preferDefense:true) -- validated at full two-replicate
            // discipline and while it delivered the hoped-for spam gains (Tier1-4 all up,
            // Tier4 +5.4), it consistently regressed the three hardest, most adaptive
            // models: v4 -4.95, v7 -5.15, v3 -3.0 (repeatable both replicates). All the
            // trace evidence motivating this fix came from the human's NON-reactive
            // econ-dump phase (buying Tier5-8 once safely rich, never from an urgent
            // defend-right-now moment) -- nothing said a human facing an active threat
            // should spend a big pile of money on ONE expensive unit instead of several
            // cheaper ones that arrive sooner and spread the defense. Restricting
            // RawPower to !preferDefense (this version) keeps reactive spending on the
            // original, already-tuned cost-efficient ScoreUnit path, and re-validated
            // clean: spam still gained across the board (Tier1 +1.5, Tier2 +2.95, Tier3
            // +3.8, Tier4 +3.6, Tier5-8 flat), and critically v4 -- the actual top-
            // priority matchup -- came back to flat (50.3%->50.5%, no longer regressed).
            // v20/v21/v16 gained 2.8-4.15, v3/v7/v25 kept small residual dips (2.5-3.5,
            // both replicates) that didn't clear on this pass but are minor next to the
            // broad gains elsewhere -- worth another look if those three specifically
            // become the priority again, but not blocking this fix.
            bool useRawPower = richMode && !preferDefense;

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
                    .Select(def => (def, score: useRawPower
                        ? RawPower(def, enemyHitDamage)
                        : ScoreUnit(def, preferDefense, enemyHitDamage,
                                    _settings.MultiplicativeUnitValue, _settings.UnitValueCostExponent,
                                    _settings.UnitValueCostExponentDefense)))
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
            // Only during non-reactive spending -- while actively clearing a wave off the
            // castle (preferDefense), survival comes first and nothing should be held back.
            //
            // Used to ALSO require ownUnitCount >= 10 before bothering with a reserve at
            // all. Marc's report (white team stops using its always-positive-EV cash
            // gadget the moment it starts attacking): traced two fresh recordings and
            // confirmed cash (15s cooldown) fired like clockwork every ~15s for the whole
            // early/mid game, then stopped completely the moment a sustained tier6/7
            // push began, for the rest of a 150-260s game -- exactly the reported bug.
            // Root cause: SpendOnUnits(preferDefense:false) is only ever reached once
            // Income>=50 (Decide()'s own gate), already a very mature economy -- but the
            // ownUnitCount>=10 condition measures the STANDING army on the field, which
            // during an active push can stay under 10 indefinitely (units die at the
            // front about as fast as they're produced), so the reserve that's supposed to
            // protect gadget affordability never engaged for the entire push. Dropped the
            // unit-count requirement -- the Income>=50 gate upstream already establishes
            // "this is not the early game" far more reliably than a live battlefield
            // headcount combat losses can suppress at will.
            double gadgetReserve = 0;
            double spendable = me.Money;
            // Hoisted out of the block below so the tech-escalation check after the pick
            // can ask "how long would banking take at the current rate". Stays 0 on the
            // reactive/killerInstinct paths, which is what disables that check there.
            double attackFlowRate = 0;
            // The most the allowance can EVER hold (flow x cap). Tech escalation must size
            // its target against this rather than against "allowance + flow * hold": the
            // allowance is itself capped, so any target priced above this ceiling can never
            // become affordable and the hold below would stall production permanently.
            // Sized wrong, that deadlock lands exactly on tier 7 in the mid game.
            double allowanceCeiling = 0;
            if (!preferDefense && !killerInstinct)
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
                gadgetReserve = gadgetGap != double.MaxValue ? Math.Min(gadgetGap, me.Money * 0.7) : 0;

                // Flow-based savings cap (Marc's explicit correction to an earlier
                // stock-based version): accrue a spending "allowance" from the INCOME
                // RATE at AttackSpendFraction, not from the current money pile. Spending
                // is capped by this allowance and the allowance is debited by exactly
                // what's spent below, so cumulative attack spending can never outpace
                // cumulative income -- the complement fraction (~15%) is guaranteed net
                // savings growth over time regardless of how big the stockpile is.
                // Capped at AttackAllowanceCapSeconds worth so unspent allowance can't
                // bank indefinitely while idle, which would turn this back into a
                // stockpile-shaped mechanic instead of a flow-shaped one.
                float decisionSeconds = DecisionIntervalTicks / 30f;

                // The flow RATE funding the attack. Originally a flat fraction of income
                // (AttackSpendFraction = 0.91 out of the paramsearch), which guarantees
                // savings grow but says nothing about whether they grow fast enough to
                // ever buy anything. That distinction is the whole problem Marc reports as
                // "the bot stops prioritising investment around 5":
                //
                //   At InvestmentCount 5, Income is ~59.9 and the next InvestmentPrice is
                //   Income * (5*4 + 8) = ~1677. Saving at the leftover 9% of income is
                //   ~5.4/sec, so the next investment is ~310 seconds away -- longer than
                //   most games last. The bot has not decided to stop investing; it has
                //   set a savings rate at which the sixth investment is unreachable, and
                //   the reason it bites at exactly 5 is that Income >= 50 (the gate on
                //   this whole branch) first becomes true at InvestmentCount 5.
                //
                // So make the flow the RESIDUAL after investment, rather than investment
                // the residual after the flow. Reserve the income rate needed to afford
                // InvestmentPrice within InvestPaceTargetSeconds, and fund the attack from
                // whatever is left. The attack now automatically throttles itself hardest
                // right after an investment (when the next price is furthest away) and
                // opens up as the target is approached, which is also just how a human
                // paces a push.
                //
                // MinAttackFlowFraction floors it: the InvestmentCount 7/8 price overrides
                // (40,000 and 121,221) are large enough to zero the residual outright, and
                // an attack budget of exactly zero for a minute is its own failure mode.
                //
                // With the flag off, investPaceRate is 0 and this reduces to
                // min(income * 0.91, income) = income * 0.91 -- the committed behavior,
                // arithmetically unchanged.
                double investPaceRate = 0;
                if (_settings.DynamicInvestPace && _settings.PaceAttackSpendForInvestment)
                {
                    // See InvestPaceExtraTimeFraction. Reserve a CONSTANT rate sized so the
                    // next investment lands in baseTime * (1 + extra), where baseTime is the
                    // zero-spend time price/Income. The price cancels:
                    //     price / (price/Income * (1+extra))  ==  Income / (1+extra)
                    // so the attack gets Income * extra/(1+extra) -- a constant share, and
                    // one that needs no floor because it is strictly positive.
                    investPaceRate = me.Income / (1.0 + Math.Max(0, _settings.InvestPaceExtraTimeFraction));
                    // Nothing left to save for if we can already afford it; the invest check
                    // at the top of Decide() will take it on this same decision anyway.
                    if (me.Money >= me.InvestmentPrice) investPaceRate = 0;
                }
                else if (_settings.PaceAttackSpendForInvestment && _settings.InvestPaceTargetSeconds > 0)
                {
                    double stillNeeded = Math.Max(0, me.InvestmentPrice - me.Money);
                    investPaceRate = stillNeeded / _settings.InvestPaceTargetSeconds;
                }

                // While racing for ARMAGEDDON the floor is the thing being removed, not
                // relied on: at InvestmentCount 7/8 it is the ONLY term keeping the attack
                // funded (the residual is negative there), so it is precisely what diverts
                // income away from the race. See RushArmageddon for the trace and the
                // design-intent argument.
                bool rushingArmageddon = _settings.RushArmageddon
                    && _settings.PaceAttackSpendForInvestment
                    && !me.ArmageddonUsed
                    && me.InvestmentCount >= _settings.ArmageddonRushMinInvestment;
                float flowFloorFraction = rushingArmageddon
                    ? _settings.ArmageddonRushAttackFraction
                    : _settings.MinAttackFlowFraction;

                // ARMAGEDDON IS THE WIN CONDITION, SO STOP FUNDING ANYTHING ELSE (flag 10).
                //
                // RushArmageddon already existed for this and DOES NOT WORK on the shipped
                // path -- its own comment says the MinAttackFlowFraction floor "is the ONLY
                // term keeping the attack funded (the residual is negative there)", which was
                // true of the old InvestPaceTargetSeconds path. Under DynamicInvestPace
                // (the default) investPaceRate is Income/(1+0.20), so the residual is always
                // exactly Income/6 -- positive at every rung, however far away. At income
                // 2500 that is $416.7/s of unit buying that continues regardless, and the
                // floor (Income * 0.15 = 375/s) never binds, so zeroing it changes nothing.
                //
                // Once InvestmentCount has reached ArmageddonInvestmentCount there is nothing
                // left on the ladder to buy but the end of the game. Units bought in that
                // window are being bought INSTEAD of winning.
                if (_settings.ArmageddonCommit && !me.ArmageddonUsed
                    && me.InvestmentCount >= PlayerState.ArmageddonInvestmentCount)
                {
                    _attackSpendAllowance = 0;
                    attackFlowRate = 0;
                    allowanceCeiling = 0;
                    spendable = 0;
                    ArmageddonCommitDecisions++;
                    return;
                }

                attackFlowRate = Math.Min(
                    me.Income * _settings.AttackSpendFraction,
                    Math.Max(me.Income - investPaceRate,
                             _settings.PaceAttackSpendForInvestment ? me.Income * flowFloorFraction : 0));

                // How much unspent allowance may bank before it stops accruing. Raised
                // while we hold a clear income advantage: that is precisely the state in
                // which a bigger, more concentrated push is affordable, and a burst that
                // arrives together is worth far more than the same money trickled out one
                // unit per decision (the rejected variant-2 experiment's real finding was
                // that sustained concentrated aggression wins games -- what sank it was
                // committing to a fixed TIER forever, not the concentration itself).
                // ConcentratedBurst raises the non-advantage cap only -- see flag (7). Off,
                // this is exactly AttackAllowanceCapSeconds, the committed constant.
                double capSeconds = hasIncomeAdvantage
                    ? _settings.IncomeAdvantageAllowanceSeconds
                    : (_settings.ConcentratedBurst ? _settings.BurstAllowanceCapSeconds : AttackAllowanceCapSeconds);
                // TechEscalation's whole mechanism is banking allowance until a genuinely
                // stronger unit is affordable, so the cap has to permit at least as much
                // banking as it is willing to wait for -- otherwise the hold below would
                // stall forever against a ceiling it can never cross. See flag (8).
                if (_settings.TechEscalation)
                    capSeconds = Math.Max(capSeconds, _settings.TechHoldSeconds);

                allowanceCeiling = attackFlowRate * capSeconds;
                _attackSpendAllowance = Math.Min(
                    _attackSpendAllowance + attackFlowRate * decisionSeconds,
                    allowanceCeiling);

                spendable = Math.Max(0, Math.Min(me.Money - gadgetReserve, _attackSpendAllowance));

                // BOUNDED WAVE (flag 15). The rate cap above says how FAST the wave may be
                // funded; this says how BIG it may get in total before the bot has to stop
                // and save again. Without it the gate opens at investment 5 and never closes.
                if (_settings.AttackBudgetPerInvestment > 0)
                {
                    double cycleBudget = me.InvestmentPrice * _settings.AttackBudgetPerInvestment;
                    spendable = Math.Min(spendable, Math.Max(0, cycleBudget - _attackSpentThisCycle));
                }
            }
            else if (preferDefense && !killerInstinct)
            {
                // Per-spend EV cap (Marc's explicit ask): reactive spending used to have
                // "no investment reserve at all, by design, since reactive defense
                // shouldn't hold back when actually threatened" -- true when the threat
                // is genuinely severe, but his own D3596E playtest breakdown showed the
                // bot spending hundreds of dollars defending against a single unit that
                // basic DPS-vs-HP math shows was barely a threat at all. reactiveSpendBudget
                // (computed in Decide() from the actual runway deficit x income) answers
                // "how much is genuinely worth spending to bridge to the next investment"
                // directly -- cap spendable to it here rather than leaving reactive
                // spending totally unconstrained. killerInstinct still bypasses this
                // entirely (finishing an already-winning fight isn't a savings question).
                spendable = Math.Min(me.Money, reactiveSpendBudget);

                // REACTIVE FLOW CAP (flag 14) -- Marc's explicit ask from live play: "it
                // spends a bit too much of its money and can't keep up with me
                // economically... a knob to get it saving a larger percent of its income,
                // especially around income level 5 which is where it stalls."
                //
                // THE MECHANISM, made specific by the replays: the existing flow cap governs
                // ONLY the non-reactive attack branch, and that branch requires !inDanger.
                // Against ladder opponents the bot is rarely in danger, so the cap binds and
                // savings accrue. Against a human applying sustained pressure it is in danger
                // almost continuously, so spending is governed by reactiveSpendBudget --
                // which has NO income-rate limit at all. 8 of 10 recorded losses ended at
                // exactly investment 5 (income 59.877) while the human reached 7-8. The bot
                // is not choosing to stop investing; its defensive spending is simply
                // unbounded relative to income.
                //
                // Banks like the attack allowance rather than being a hard per-decision cap,
                // so a quiet period funds a real burst when a wave lands -- a flat rate limit
                // would just get it killed. killerInstinct still bypasses everything above.
                //
                // NOTE ON THE FOUR PRIOR REJECTIONS in this domain (see SpendOnUnits'
                // history): every one was judged on the ladder, which we now know cannot
                // produce sustained competent pressure -- the exact state where this binds.
                // This is not a fifth attempt at the same measurement; it is the first with
                // evidence the instrument was blind.
                if (_settings.ReactiveFlowCap && me.Income > 0)
                {
                    double reactiveRate = me.Income * _settings.ReactiveSpendFractionOfIncome;
                    _reactiveSpendAllowance = Math.Min(
                        _reactiveSpendAllowance + reactiveRate * (DecisionIntervalTicks / 30f),
                        reactiveRate * _settings.ReactiveAllowanceCapSeconds);
                    spendable = Math.Min(spendable, _reactiveSpendAllowance);
                }
            }

            LastSpendDebug = $"money={me.Money:F1} spendable={spendable:F1} allowance={_attackSpendAllowance:F1} killerInstinct={killerInstinct} dominantTier={dominantEnemyTier} anyAffordableCount={anyAffordable.Count(x => x.def.Cost > 0 && x.def.Cost <= spendable)} cheapestAny={(anyAffordable.Count > 0 ? anyAffordable.Min(x => x.def.Cost) : -1)}";
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

            // --- POWER PICK (flag 9) -------------------------------------------------
            // Buy the most POWERFUL unit already affordable this decision, rather than the
            // most cost-efficient one. Never changes whether we buy, only what -- so unlike
            // flag (8) it concedes no tempo and costs no investments. See flag (9)'s comment
            // for why the committed outclass rule cannot do this on its own.
            if (_settings.PowerPickAffordable && !preferDefense && !killerInstinct)
            {
                UnitDefinition strongest = null;
                double strongestPower = 0;
                foreach (var def in roster)
                {
                    if (def.Cost <= 0 || def.Cost > spendable) continue;
                    double p = RawPower(def, enemyHitDamage);
                    if (p > strongestPower) { strongestPower = p; strongest = def; }
                }
                if (strongest != null) pick = (strongest, strongestPower);
            }

            // --- TECH ESCALATION (flag 8) --------------------------------------------
            // Everything above ranks by ScoreUnit, which is cost-efficiency, and in THIS
            // roster cost-efficiency falls monotonically with tier (white: 7.64 at tier 2
            // down to 4.21 at tier 7) while raw power explodes (RawPower 496 at tier 5,
            // 7434 at tier 7 -- 15x the power for 25x the cost). Two things follow, and
            // both were verified against the CSV rather than assumed:
            //
            //  1. The outclass-by-one-tier preference above is DEAD IN PRACTICE. Its guard
            //     takes matchedPick whenever `outclassPick.score < matchedPick.score * 0.9`,
            //     and since score always falls with tier that test is essentially always
            //     true. At a tier-5 standoff: matched 5.89, best affordable outclass 5.14,
            //     and 5.14 < 5.30. So the bot mirrors the enemy's tier instead of escalating.
            //  2. richMode/RawPower cannot rescue it: richMode needs `money >= topCost * 3`
            //     and topCost is the tier-8 price (23,000 for white), i.e. $69,000.
            //
            // Net effect, and it matches the trace exactly: both sides converge on the
            // cheapest cost-efficient unit at the shared dominant tier, the front line
            // becomes a melee/cleave meat grinder that neither side can break, and 26% of
            // mirrors run to the 600s cap with both castles parked at 90% HP.
            //
            // The missing behaviour is the one a human does automatically: stop buying
            // chaff and SAVE for a unit that actually breaks the line. That needs two
            // things together, which is why they share one flag -- neither works alone.
            // Ranking by power without banking cannot afford the pick; banking without a
            // power ranking just buys the same chaff later. Judge them as one mechanism.
            //
            // Deliberately scoped to non-reactive spending only. Reactive defense must not
            // sit on its hands while the castle is being hit -- that is the exact failure
            // the four rejected reactive-spend experiments kept producing (see the
            // SpendOnUnits history above), and attackFlowRate is 0 on that path anyway,
            // which makes the reachability test below fail closed.
            // TechTimeAware: refuse to hold when the wait would eat too much of what is
            // left of the game -- see flag (8b). Off, this is always true and the check
            // below is exactly the unrefined flag (8).
            bool techTimeAllows = !_settings.TechTimeAware
                || (GameEngine.MAX_TICKS - engine._state.CurrentTick)
                       >= _settings.TechHoldSeconds * 30f * _settings.TechTimeSafetyFactor;

            if (_settings.TechEscalation && !preferDefense && !killerInstinct && attackFlowRate > 0.01
                && techTimeAllows)
            {
                double nowPower = RawPower(pick.def, enemyHitDamage);
                // What the allowance can actually reach. Bounded by the allowance CEILING,
                // not by "allowance + flow * hold" -- see allowanceCeiling's comment for the
                // deadlock that the looser bound causes. Money is a hard bound too: banking
                // allowance we do not actually have is fantasy.
                double reachable = Math.Min(me.Money - gadgetReserve, allowanceCeiling);

                UnitDefinition techTarget = null;
                double techPower = 0;
                foreach (var def in roster)
                {
                    if (def.Cost <= 0 || def.Cost > reachable) continue;
                    double p = RawPower(def, enemyHitDamage);
                    if (p > techPower) { techPower = p; techTarget = def; }
                }

                if (techTarget != null && techPower > nowPower * _settings.TechPowerRatio)
                {
                    // Affordable right now -- take the stronger unit over the cheaper one.
                    if (techTarget.Cost <= spendable) pick = (techTarget, techPower);
                    // Not yet -- bank this decision rather than spending the allowance on
                    // chaff that would push the target further out of reach.
                    else return;
                }
            }

            // --- CHARGE-AWARE FALLBACK (flag 1) --------------------------------------
            // The pick above was chosen on price alone. If it has no charge left, SpawnUnit
            // will refuse it and this decision buys nothing -- so re-pick the best-scoring
            // unit that is affordable AND charged. See ChargeAwareFallback's own comment for
            // the measurement and for why this is a fallthrough rather than a pre-filter.
            //
            // Ranked by the SAME scorer that produced the original pick, so the fallback is
            // "the next thing this bot wanted" rather than a differently-motivated choice:
            // RawPower when the power pick or rich mode is in force, ScoreUnit otherwise.
            if (_settings.ChargeAwareFallback && !me.HasUnitCharge(pick.def.Id))
            {
                bool byPower = useRawPower
                    || (_settings.PowerPickAffordable && !preferDefense && !killerInstinct);

                UnitDefinition best = null;
                double bestScore = double.NegativeInfinity;
                foreach (var def in roster)
                {
                    if (def.Cost <= 0 || def.Cost > spendable) continue;
                    if (!me.HasUnitCharge(def.Id)) continue;
                    double sc = byPower
                        ? RawPower(def, enemyHitDamage)
                        : ScoreUnit(def, preferDefense, enemyHitDamage,
                                    _settings.MultiplicativeUnitValue, _settings.UnitValueCostExponent,
                                    _settings.UnitValueCostExponentDefense);
                    if (sc > bestScore) { bestScore = sc; best = def; }
                }

                // Nothing affordable has a charge -- every buyable unit is drained. Return
                // rather than falling through to the doomed attempt below: the outcome is
                // the same either way, but returning makes the decision honest and keeps
                // ChargeFallbackEmpty a usable diagnostic.
                if (best == null) { ChargeFallbackEmpty++; return; }

                pick = (best, bestScore);
                ChargeFallbacks++;
            }

            // --- AUTO-SPAWNER AS A SUBSTITUTE PURCHASE (flag 2b) ---------------------
            // Same budget, different buy. Placed here rather than in Decide() so it competes
            // with the unit this method was about to purchase, which is the whole point --
            // see AutoSpawnFromAttackBudget for why the funding source is the finding.
            if (_settings.AutoSpawnFromAttackBudget && !preferDefense && !killerInstinct
                && me.AutoSpawnLevel < Math.Min(_settings.AutoSpawnMaxLevel, PlayerState.MaxAutoSpawnLevel))
            {
                double price = me.AutoSpawnPrice;   // captured: UpgradeAutoSpawn moves it
                if (price > 0 && price <= spendable)
                {
                    double gain = AutoSpawnValuePerSecond(roster, me.AutoSpawnLevel + 1)
                                - AutoSpawnValuePerSecond(roster, me.AutoSpawnLevel);
                    if (gain > 0 && price <= gain * _settings.AutoSpawnPaybackSeconds
                        && Act(() => engine.UpgradeAutoSpawn(_side)))
                    {
                        AutoSpawnLevelsBought++;
                        _attackSpendAllowance = Math.Max(0, _attackSpendAllowance - price);
                        _attackSpentThisCycle += price;
                        return;
                    }
                }
            }

            if (Act(() => engine.SpawnUnit(_side, pick.def.Id)))
            {
                LastSpawnReason = preferDefense ? "reactive" : killerInstinct ? "killerInstinct" : "attack";
                LastUnitsPurchased++;
                if (!preferDefense && !killerInstinct)
                {
                    _attackSpendAllowance = Math.Max(0, _attackSpendAllowance - pick.def.Cost);
                    // Charge the per-wave budget too, so it actually depletes and the bot
                    // eventually falls back to saving. See AttackBudgetPerInvestment.
                    _attackSpentThisCycle += pick.def.Cost;
                }
                // Debit the reactive allowance too, or the cap above would be a ceiling the
                // bot never actually pays down -- it would bank to full and then permit
                // every purchase, which is no cap at all.
                if (preferDefense && !killerInstinct) _reactiveSpendAllowance = Math.Max(0, _reactiveSpendAllowance - pick.def.Cost);
                if (pick.def.Tier >= 1 && pick.def.Tier <= 8) ActionCounts[pick.def.Tier]++;
            }
        }

        // Below ~1.5x the enemy's average hit, a unit is one-or-two-shot fodder that
        // never gets to swing back enough to matter -- crush its score rather than
        // excluding it outright (so it's still a fallback if literally nothing survives).
        /// <summary>
        /// Roster dollars of free units the auto-spawner delivers per second at
        /// <paramref name="level"/>. The cycle repeats exactly once per second by
        /// construction (units/sec IS the cycle length), so summing the cycle's unit costs
        /// gives a per-second figure directly with no rate term.
        /// </summary>
        private static double AutoSpawnValuePerSecond(List<UnitDefinition> roster, int level)
        {
            double total = 0;
            foreach (int tier in PlayerState.AutoSpawnCycle(level))
                if (tier >= 1 && tier <= roster.Count) total += roster[tier - 1].Cost;
            return total;
        }

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

        private static double ScoreUnit(UnitDefinition def, bool preferDefense, float enemyHitDamage,
                                        bool multiplicative = false, double costExponent = 1.0,
                                        double defenseCostExponent = 1.0)
        {
            double cost = Math.Max(1, def.Cost);
            double dps = def.Damage * (def.AttackSpeed > 0 ? def.AttackSpeed : 0.3f) * RangeMultiplier(def);

            double baseScore;
            if (multiplicative)
            {
                // THE GAME'S OWN BALANCE FORMULA -- effective HP x DPS / cost (Marc, who
                // balanced the roster to it). See MultiplicativeUnitValue for why the
                // additive version below was measuring the wrong thing entirely.
                //
                // Multiplicative because durability and damage COMPOUND: a unit deals its
                // DPS for a length of time proportional to how long it survives, so combat
                // value is the product, not a weighted sum. A sum cannot express that, and
                // is what made the additive score fall with tier.
                //
                // Shield is added to health rather than given its own term: GameEngine's
                // ApplyDamage spends shield before health, so it is simply more effective
                // HP, which is exactly how it enters an HP x DPS product.
                // Reactive defence and offensive pushes want DIFFERENT points on the
                // quality-vs-quantity curve -- see UnitValueCostExponentDefense. Defaults
                // are equal, which makes this arithmetically identical to a single global
                // exponent until the defensive one is deliberately moved.
                double k = preferDefense ? defenseCostExponent : costExponent;
                baseScore = (def.MaxHealth + def.MaxShield) * dps / Math.Pow(cost, k);
            }
            else
            {
                // Defense leans harder into durability (blocking/trading); offense still wants
                // real HP too -- a pure glass cannon dies before it ever reaches the castle,
                // and a fragile army stops replacing its losses the moment money gets tight.
                baseScore = preferDefense
                    ? (dps * 1.5 + def.MaxHealth + def.MaxShield) / cost
                    : (dps * 1.8 + def.MaxHealth * 0.8 + def.MaxShield * 0.8) / cost;
            }
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

        // Walls are spawned units but behave nothing like one: they never move or attack,
        // WaveHazard refuses to knock them back, and CC has nothing to disable. Several
        // gadget checks need to exclude them so a lone wall doesn't read as a live threat
        // or a legitimate AoE target. Matches WallEffect's ids ("wall", "wall_2", "wall_3").
        private static bool IsWall(Unit u) => Gadgets.GadgetTargeting.IsWall(u);

        /// <summary>
        /// How many of our units are actually attacking the enemy castle, using the
        /// ENGINE's own definition of in-range (GetDistanceToEnemyCastle vs the unit's
        /// Range) rather than a hand-picked distance, so this cannot drift from what the
        /// combat step does. Walls excluded -- they never advance.
        /// </summary>
        private int SiegeUnitCount(GameEngine engine, List<Unit> myUnits, List<UnitDefinition> myRoster)
        {
            int n = 0;
            foreach (var u in myUnits)
            {
                if (IsWall(u)) continue;
                var d = myRoster?.FirstOrDefault(x => x.Id == u.DefinitionId);
                if (engine.GetDistanceToEnemyCastle(u) <= (d?.Range ?? 0f)) n++;
            }
            return n;
        }

        /// <summary>
        /// Friendly-fire TRADE test for the three gadgets that damage both sides in the
        /// blast (nuke, firebomb, blackhole). True when the cast is worth taking: the enemy
        /// value caught is at least AoeTradeMargin times our own.
        ///
        /// Replaces "no ally may be in radius", which is safe but refuses every good trade
        /// -- including the one Marc names as the whole point of these gadgets: our army
        /// holding at OUR castle while the enemy streams in, blast dropped at THEIR end
        /// where the stream is dense and our units cannot arrive in time.
        /// </summary>
        private bool AoeTradeOk(GameEngine engine, List<Unit> myUnits, List<Unit> enemyUnits,
                                float target, int radius)
            => Gadgets.GadgetTargeting.AoeTradeOk(engine, myUnits, enemyUnits, target, radius,
                                                  _settings.AoeTradeMargin);

        // Tick of our last cast of ANY gadget, for the upgrade-spam stagger. Distinct from
        // _lastGadgetCastTick, which is per-family and only rate-limits a gadget against
        // itself -- nothing in the committed bot stops all three slots firing on one tick.
        private long _lastAnyGadgetCastTick = -1;

        /// <summary>
        /// Deliberate XP farming to reach the next gadget tier. See GadgetUpgradeSpam.
        /// Returns true if it cast. Only ever ADDS casts the tactical gates would refuse;
        /// it never suppresses one.
        /// </summary>
        private bool TryUpgradeSpam(GameEngine engine, PlayerState me, GadgetDefinition def,
                                    int myCastlePos, List<Unit> myUnits, List<Unit> enemyUnits, bool inDanger)
        {
            if (!_settings.GadgetUpgradeSpam || def == null) return false;
            // MAX TIER: AddGadgetXp returns early with no NextTierId, so every further cast
            // buys literally nothing. The drain cap alone does not know this and happily
            // spams a maxed gadget once income is high enough.
            if (string.IsNullOrEmpty(def.NextTierId)) return false;
            if (inDanger) return false;                       // fight first, farm later
            if (me.Income <= 0.01 || def.Cost <= 0) return false;

            // Defer while committed to the next investment.
            if (me.InvestmentPrice > 0 &&
                me.Money >= me.InvestmentPrice * _settings.UpgradeSpamInvestCommitFraction)
                return false;

            // Income must make the sustained spend irrelevant. An underscore in the id means
            // this is the level-2 gadget, i.e. we are farming the 2->3 upgrade.
            double cooldownSeconds = def.CooldownMs / 1000.0;
            if (cooldownSeconds <= 0) return false;
            double k = def.Id.Contains('_') ? _settings.UpgradeSpamK2 : _settings.UpgradeSpamK1;

            // ── CHEAP-UPGRADE GATE (flag 4) ────────────────────────────────────────
            // The income test above asks "can we afford to spam this FOREVER". That is the
            // wrong question for an upgrade, which is a FINITE purchase: XP is a flat 100
            // per cast for every gadget regardless of effect, so the next tier costs exactly
            // ceil((UpgradeCost - xp) / 100) casts and not one more. Pricing a finite
            // purchase as an infinite drain is why the k gate only ever fires once the bot
            // is rich, which is after the upgrade has stopped mattering.
            //
            // The cheapest ladders are enormous and land early: wall -> wall_2 is 3 casts x
            // $50 = $150 to turn a 400 HP wall into a 6,000 HP one; reinforcements_3 ends at
            // five FREE tier-7 units per cast on a 10 s cooldown, charge-free.
            //
            // Every self-harm guard below is untouched and still runs. That is not optional:
            // the first version of this path lost 73% of games to an opponent that does
            // NOTHING, by farming XP with the nuke -- which damages both castles.
            if (_settings.CheapGadgetUpgrades)
            {
                string famCost = def.Id.Split('_')[0].ToLowerInvariant();
                int xpNow = me.GadgetXp.TryGetValue(famCost, out var gx) ? gx : 0;
                int castsLeft = Math.Max(1, (int)Math.Ceiling((def.UpgradeCost - xpNow) / 100.0));
                double totalToUpgrade = castsLeft * (double)def.Cost;
                if (totalToUpgrade > me.Income * _settings.CheapUpgradeIncomeSeconds) return false;
            }
            else if (k <= 0 || me.Income < def.Cost / (cooldownSeconds * k)) return false;

            // STAGGER against every other slot, so one gadget is always available.
            if (_lastAnyGadgetCastTick >= 0 &&
                (engine._state.CurrentTick - _lastAnyGadgetCastTick) / 30.0 < _settings.UpgradeSpamStaggerSeconds)
                return false;

            // ── SELF-HARM GUARDS ────────────────────────────────────────────────────
            // This path deliberately skips the per-gadget switch, which is where every
            // safety check lives. That is a bypass, and the first version of it lost 73%
            // of games to an opponent that does NOTHING: nuke damages BOTH castles by
            // BaseValue/2 (100 / 1500 / 12000 by level), so farming XP with it simply
            // killed the bot. Exactly the failure `survivesOwnBlast` exists to prevent --
            // reintroduced by routing around the case that holds it.
            //
            // Anything added here that can hurt our own side needs its guard repeated, or
            // the ladder's DoNothing rung will find it again.
            string fam = def.Id.Split('_')[0].ToLowerInvariant();
            if (fam == "nuke")
            {
                int selfBlast = (int)def.BaseValue / 2;
                if (me.CastleHealth <= selfBlast * _settings.NukeSelfDamageMargin) return false;
            }

            // Aim AWAY from our own army. myCastlePos is the worst choice available: it is
            // exactly where our units are, so an XP cast of firebomb or blackhole would
            // farm the upgrade by killing our own board.
            int enemyCastlePos = _side == 1 ? GameEngine.MAP_WIDTH - 200 : 200;
            int xpTarget = enemyCastlePos;
            if (myUnits.Any(u => Math.Abs(u.Position - xpTarget) <= Math.Max(150, def.Radius)))
                return false;

            return TryCast(engine, me, def, xpTarget, 0, enemyUnits, myCastlePos);
        }

        // Tick of our last cast per gadget FAMILY ("meteor", "nuke", ...), for the
        // self-imposed cooldown in GadgetIncomeDrainCap. Keyed by family rather than by id
        // so an upgrade mid-game (meteor -> meteor_2 -> meteor_3) does not reset the clock.
        // Gadget casts made THIS decision, so the defensive spawn logic can price only the
        // residual threat. Recorded here rather than returned from the three TryUse* methods
        // because every cast in this file already funnels through TryCast -- one choke point,
        // nothing to miss, and the gadget DECISION logic is left completely untouched.
        private readonly List<(GadgetDefinition def, int position)> _castsThisDecision
            = new List<(GadgetDefinition, int)>();

        // Running total of the roster price of every DISTINCT enemy unit ever seen, and a
        // sample of it a moment ago. The difference is the rate the opponent is committing
        // money at, which is the whole basis for deciding whether waiting pays: waiting is
        // profitable exactly while they commit value faster than we bleed it.
        private double _enemyValueSeen;
        private double _enemyValueSample;
        private long _enemyValueSampleTick = -1;
        private double _enemyValueRate;

        /// <summary>Bodies the defensive response owes but has not yet been able to spawn.</summary>
        private float _blockCredit;

        /// <summary>Last threat read, for tracing. Never used to decide anything.</summary>
        public string LastThreatDebug { get; private set; } = "";

        private readonly Dictionary<string, long> _lastGadgetCastTick = new Dictionary<string, long>();

        /// <summary>
        /// Single funnel for every gadget cast. Applies the income-drain cap (flag 18) and
        /// records the cast tick. Returns whether the cast actually happened.
        ///
        /// Centralised deliberately: the bot casts from ~13 sites across three methods, and
        /// gating them individually is how one gets missed. Every `UseGadget` in this file
        /// goes through here.
        /// </summary>
        private bool TryCast(GameEngine engine, PlayerState me, GadgetDefinition def, int position,
                             double estimatedEnemyValue, List<Unit> enemyUnits, int myCastlePos)
        {
            if (_settings.GadgetIncomeDrainCap && me.Income > 0.01 && def.Cost > 0)
            {
                // Override 1 -- the castle is actually under attack. Survival outranks
                // savings; spend whatever it takes.
                bool underAttack = _settings.DrainCapTtdSeconds > 0
                    ? LastTimeToDeathSeconds < _settings.DrainCapTtdSeconds
                    : enemyUnits.Any(u =>
                        Math.Abs(u.Position - myCastlePos) <= _settings.GadgetDrainCapCastleThreat);

                // Override 2 -- the cast pays for itself. If it destroys more enemy value
                // than it costs, it is a positive economic swing and the drain argument does
                // not apply, however expensive it is.
                bool paysForItself = estimatedEnemyValue >= def.Cost;

                if (!underAttack && !paysForItself)
                {
                    // Self-imposed minimum interval so this gadget cannot consume more than
                    // GadgetMaxIncomeDrainFraction of income: cost / (income * fraction).
                    // A cheap gadget yields a value below its real cooldown and is unaffected.
                    double minSeconds = def.Cost / (me.Income * _settings.GadgetMaxIncomeDrainFraction);
                    string fam = def.Id.Split('_')[0].ToLowerInvariant();
                    if (_lastGadgetCastTick.TryGetValue(fam, out var lastTick)
                        && (engine._state.CurrentTick - lastTick) / 30.0 < minSeconds)
                        return false;
                }
            }

            if (!Act(() => engine.UseGadget(_side, def.Id, position))) return false;
            _castsThisDecision.Add((def, position));
            _lastGadgetCastTick[def.Id.Split('_')[0].ToLowerInvariant()] = engine._state.CurrentTick;
            // Stagger clock covers TACTICAL casts too -- the point is that the three slots
            // are never all on cooldown at once, whatever put them there.
            _lastAnyGadgetCastTick = engine._state.CurrentTick;
            return true;
        }

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
        private bool DeferForInvestment(PlayerState me)
        {
            // ORIGINAL RULE, unchanged: hold a gadget while the FIRST few rungs are still
            // being bought, because a gadget priced at the same $18 as the first rung
            // competes with it directly.
            if (me.InvestmentCount < 3 && me.Money <= me.InvestmentPrice) return true;

            // COMMIT TO THE RUNG (flag 9, 2026-09-02). The rule above is bounded to
            // InvestmentCount < 3, so from the fourth rung onward the gadget layer has NO
            // AWARENESS OF THE RUNG IT IS SAVING FOR and fires purely on cooldown. That is
            // most expensive at the top of the ladder, where the rung is ARMAGEDDON at
            // $121,221 and buying it ends the game outright.
            //
            // Found in Marc's three recorded games (2026-09-02): in both games he won, the
            // bot reached InvestmentCount 8 -- max income, ARMAGEDDON the only thing left to
            // buy -- and then finished holding $65,253 and $77,332 against that rung. Its
            // gadget cast pattern was nearly IDENTICAL across won and lost games, which is
            // the signature of a cadence that is not reading the game state at all.
            //
            // Deliberately a COMMIT fraction rather than a blanket hold: holding whenever
            // money <= price would silence gadgets for the entire endgame, and gadgets are
            // how the bot survives long enough to reach the rung. This only engages once the
            // rung is genuinely close.
            //
            // The caller's own inDanger check still runs first everywhere this is used --
            // see each callsite -- so this never withholds a defensive cast under threat.
            if (_settings.CommitToRung && me.InvestmentPrice > 0 && !me.ArmageddonUsed
                && me.Money >= me.InvestmentPrice * _settings.RungCommitFraction)
                return true;

            return false;
        }

        // Sums the real, current incoming damage-per-second against OUR castle: only
        // enemy units already within their own attack Range of it count (the same
        // castleInRange check GameEngine.MoveAndFight uses before it damages a castle),
        // so a large force still marching in from across the map isn't
        // counted as an active threat before it actually is one -- matching the same
        // "react to what's real, not what might happen" philosophy the rest of this
        // file already uses (see inDanger's own history of over-eager triggers). DPS is
        // Damage * AttackSpeed directly (MoveAndFight's castle-attack branch sets
        // AttackCooldown to 1000f/AttackSpeed ms per hit, so AttackSpeed is already
        // attacks/second).
        private static float EstimateProjectedThreatDps(GameEngine engine, List<Unit> enemyUnits, List<UnitDefinition> enemyRoster)
        {
            if (enemyRoster == null || enemyRoster.Count == 0) return 0f;
            float dps = 0f;
            foreach (var u in enemyUnits)
            {
                var def = enemyRoster.FirstOrDefault(d => d.Id == u.DefinitionId);
                if (def == null) continue;
                // Every unit in this roster is melee (Range is always 0 -- there's no
                // Range column in master_roster.csv), so a raw position comparison
                // against myCastlePos would need EXACT equality to ever trigger and
                // would in practice never fire at all. Reuse GameEngine's own
                // GetDistanceToEnemyCastle (already accounts for the unit's Width and
                // which side it's attacking) so this matches the SAME contact-distance
                // test the real castleInRange check uses before damaging a castle
                // -- found via an n=50 sanity run where this bug silently
                // zeroed out the projected estimate for every matchup (Math.Max against
                // an always-infinite projected TTD unconditionally discarded the real,
                // working observed-drain estimate), most visibly on Tier1 spam (58% vs
                // the ~90% baseline) since that matchup leans hardest on real reactive
                // defense.
                if (engine.GetDistanceToEnemyCastle(u) <= def.Range)
                    dps += u.Damage * def.AttackSpeed;   // instance, not definition (random-stat unit)
            }
            return dps;
        }

        // Looks up an enemy unit's actual spawn cost from its own team's roster --
        // Unit (the runtime instance) only carries Tier/DefinitionId, not Cost, so this
        // needs the owning player's Team to find the right roster.
        /// <summary>
        /// True while a hazard is live that makes an attacking purchase unconvertible.
        ///
        /// THE TWO HAZARDS ARE NOT SYMMETRIC AND THE CHECK MUST NOT BE EITHER — this is the
        /// whole subtlety, and the first version got it wrong by treating both as enemy-only.
        ///
        ///   WAVE       `WaveHazard.ProcessEffect` filters to `u.Side != this.Side`, so it only
        ///              knocks back the CASTER'S ENEMIES. Our own wave is free to us; only an
        ///              enemy-cast one blocks our attack.
        ///
        ///   BLACKHOLE  `BlackholeHazard.ProcessEffect` iterates `state.Units` with no side
        ///              filter at all. It drags EVERY unit toward its centre — and at level 3
        ///              the event horizon sets `CurrentHealth = 0` on any unit that is not tier
        ///              8, ours included. So OUR OWN blackhole eats our own attackers, and it
        ///              counts whoever cast it. (Marc, 2026-08-24.)
        /// </summary>
        private bool AttackBlockingHazardActive(GameEngine engine)
        {
            var hazards = engine._state.Hazards;
            for (int i = 0; i < hazards.Count; i++)
            {
                var h = hazards[i];
                if (h is BlackholeHazard) return true;              // either side's
                if (h is WaveHazard && h.Side != _side) return true; // enemy-cast only
            }
            return false;
        }

        /// <summary>Diagnostic: was the attack suppressed by an enemy CC hazard this decision.</summary>
        public bool LastHazardBlackout { get; private set; }

        /// <summary>Decisions on which the attack was suppressed by an enemy CC hazard.</summary>
        public long HazardBlackoutDecisions { get; private set; }

        /// <summary>Times the offensive spend branch was entered.</summary>
        /// <summary>
        /// Decisions on which ChargeAwareFallback re-picked because the ranked choice had no
        /// charge. Under the reference bot every one of these was a decision that bought
        /// nothing at all and said so nowhere.
        /// </summary>
        public long ChargeFallbacks { get; private set; }

        /// <summary>
        /// Decisions where EVERY affordable unit was out of charges. This is the genuine
        /// production ceiling; if it is large the roster is drained and the answer is a free
        /// spawn source (the auto-spawner, reinforcements), not a better pick.
        /// </summary>
        public long ChargeFallbackEmpty { get; private set; }

        /// <summary>Decisions on which ArmageddonCommit closed the attack budget. Diagnostic.</summary>
        public long ArmageddonCommitDecisions { get; private set; }

        /// <summary>Auto-spawner levels bought this game. Diagnostic.</summary>
        public long AutoSpawnLevelsBought { get; private set; }

        /// <summary>Blocking bodies bought by BlockSingleChipper. Diagnostic.</summary>
        public long ChipBlocksBought { get; private set; }

        /// <summary>Unblocked enemies on our castle last decision. Diagnostic.</summary>
        public int LastChipperCount { get; private set; }

        /// <summary>Blocking credit owed, in bodies. See BlockSingleChipper.</summary>
        private float _chipCredit;

        /// <summary>Dollars available for chip blocking. See ChipBlockIncomeFraction.</summary>
        private double _chipAllowance;

        public long OffensiveSpendDecisions { get; private set; }

        /// <summary>Times it was entered while the blackout was up. Must stay 0.</summary>
        public long OffensiveWhileBlackedOut { get; private set; }

        private static double EstimateUnitCost(GameEngine engine, Unit u)
            => Gadgets.GadgetTargeting.UnitCost(engine, u);

        // Total $ value of enemy units within radius of position -- used to estimate
        // whether an AOE gadget cast is actually worth its cost (see BigSpendJustified).
        private static double EstimateEnemyValueNear(GameEngine engine, List<Unit> enemyUnits, float position, int radius)
            => Gadgets.GadgetTargeting.ValueNear(engine, enemyUnits, position, radius);

        // Marc's direct playtest feedback: watched the bot spend $200+ (a huge fraction
        // of its money, with income nowhere near able to support it) on goo healing a
        // dying wall against a single cheap attacking unit, while he calmly saved for
        // his next investment and won the economy race unopposed. His framing: "the
        // model should almost never spend a large proportion (80+%) of its money on a
        // gadget unless it knows it is worth it" -- e.g. 5 tier4 units worth $50+ each
        // justifies a $20 nuke, but 3 tier1 units worth $3 each do not, unless income is
        // already high enough that upgrading the gadget's own XP is worth it on its own.
        //
        // estimatedEnemyValue should be 0 for gadgets that don't target enemies at all
        // (heal/reinforcements/speed/wall/rage/wave/goo) -- there's no comparable $
        // payoff to estimate for those, so a big spend there is only justified by
        // already-high income (same carve-out Marc described), never by target value.
        private const float BigSpendMoneyFraction = 0.8f;
        private static bool BigSpendJustified(PlayerState me, GadgetDefinition def, double estimatedEnemyValue)
        {
            if (def.Cost < me.Money * BigSpendMoneyFraction) return true; // not actually a big spend relative to our money
            return estimatedEnemyValue >= def.Cost || me.Income >= 50;
        }

        // For gadgets whose value is a genuine, estimable dollar trade against
        // specific enemy unit(s) -- snipe (single target), nuke/firebomb/meteor/
        // poison/blackhole (AOE) -- Marc's own framing: "if there is an enemy force
        // that costs $100, that's $100 of value that could be gained from a $20
        // nuke" / "a $30 snipe to take out a $50 unit is always a great trade" /
        // "when the enemy spawns 3 tier1 units for $3, a $20 nuke isn't worth it."
        // That's a DIRECT cost-vs-value comparison, unlike BigSpendJustified's "is
        // this a big fraction of my current money" test -- a bot sitting on $200
        // sniping a single $1 ant for $30 is still a terrible trade even though $30
        // is nowhere near 80% of $200, and BigSpendJustified would wave it through
        // on exactly that basis. Found via Marc's own playtest report: the bot spent
        // a $30 snipe (single-target) on a $1 ant, failing to even clear the wave
        // that kept chipping the castle -- a firebomb (AOE) would have cleared the
        // whole ant swarm for $18. These gadgets should never get BigSpendJustified's
        // "not actually a big spend" pass; only a real value comparison (or the same
        // already-won-the-economy income carve-out) justifies the cost.
        private static bool TargetValueJustified(PlayerState me, GadgetDefinition def, double estimatedEnemyValue)
        {
            if (estimatedEnemyValue >= def.Cost) return true;
            return me.Income >= 50;
        }

        // The offense slot can be any of "nuke" / "firebomb" / "snipe" / "freeze" --
        // the loadout isn't fixed, so all four need real usage logic, not just whichever
        // one happened to be equipped when this was written.
        private void TryUseOffenseGadget(GameEngine engine, PlayerState me, List<Unit> myUnits, List<Unit> enemyUnits, int myCastlePos, bool inDanger, double reactiveSpendBudget)
        {
            if (_settings.DisableOffenseGadget) return;   // measurement switch, see the setting
            var def = me.OffensiveGadget;
            if (!IsReady(me, def)) return;
            // BEFORE the no-enemies bail: XP farming does not need a target.
            if (TryUpgradeSpam(engine, me, def, myCastlePos, myUnits, enemyUnits, inDanger)) { ActionCounts[11]++; return; }
            if (enemyUnits.Count == 0) return; // nothing to hit -- don't burn the cooldown/cost for free

            string family = def.Id.Split('_')[0].ToLowerInvariant();
            int radius = Math.Max(150, def.Radius);
            bool used = false;

            switch (family)
            {
                case "snipe":
                {
                    // No splash, no friendly fire, no self-castle-damage. SnipeEffect
                    // targets whichever enemy is nearest the given position, so aiming at
                    // our own castle makes it snipe whichever enemy is closest to reaching
                    // it -- directly preventing the exact chip damage that ends games,
                    // rather than chasing whoever hits hardest somewhere else on the field.
                    var nearest = enemyUnits.OrderBy(u => Math.Abs(u.Position - myCastlePos)).First();

                    // TRIED AND REJECTED (2026-07-24): adding `|| inDanger` here, mirroring
                    // freeze's already-validated `buyTimeJustifies = inDanger` fix, on the
                    // theory that snipe|wall's status as this project's single weakest
                    // offense/defense combo (~82.6% vs HeuristicBot's usual 90%+) was caused
                    // by snipe's single-target value check rarely clearing its cost bar
                    // against cheap early swarms (unlike nuke/firebomb, whose splash sums
                    // value across every unit hit). Validated at full two-replicate
                    // discipline (spam n=400x2, models n=300x2, headstart): spam was flat/
                    // slightly positive (no regression), but v4 -- the intended beneficiary
                    // -- showed NO consistent gain (+2.7 then -1.7, net +0.5, noise) while
                    // v16 (-8.55 avg), v21 (-6.4 avg), and v25 (-6.4 avg) all regressed
                    // CONSISTENTLY in both replicates, not just one. Reverted. Same lesson
                    // as the earlier rejected snipe `DeferForInvestment` attempt: a change
                    // motivated by sound-sounding reasoning about this case's own doc
                    // comment can still be a net loss against adaptive opponents once
                    // actually measured -- don't re-attempt an inDanger-based snipe-firing
                    // relaxation without a genuinely different angle.
                    if (TargetValueJustified(me, def, EstimateUnitCost(engine, nearest)))
                        used = TryCast(engine, me, def, myCastlePos, EstimateUnitCost(engine, nearest), enemyUnits, myCastlePos);
                    break;
                }

                case "freeze":
                {
                    // A wall is not a threat and cannot be usefully frozen -- it does not
                    // move or attack, so the whole effect is wasted (Marc: "I've seen the
                    // bot use a freeze ray on my wall when I had no other units on the
                    // field"). Same guard the wave case already has, for the same reason;
                    // snipe and poison are deliberately NOT given it, since killing a wall
                    // outright IS a good use of those.
                    if (!enemyUnits.Any(u => !IsWall(u))) break;

                    // Hits and freezes EVERY enemy unit on the field regardless of
                    // position -- no friendly fire. Frozen units skip their whole
                    // attack/move step, so they take free hits while stunned. This is what
                    // actually breaks a chokepoint stalemate: a small steady trickle of
                    // defenders can otherwise permanently pin a much bigger army just by
                    // always having *something* in contact range.
                    //
                    // CORRECTION (Marc): freeze also deals a real, flat amount of direct
                    // damage to every enemy hit -- FreezeEffect.Execute unconditionally
                    // calls ApplyDamage(enemy, BaseValue, ...) before applying the Freeze
                    // status, and BaseValue scales hard with level (10 / 150 / 1200 per
                    // master_gadgets.csv). Against a wave of units with less HP than that
                    // (every team's tier-1 unit has <=10 HP, so base-level freeze is a
                    // guaranteed kill on any tier-1 swarm regardless of army size), it's
                    // effectively a guaranteed-kill AOE, not just a CC multiplier -- value
                    // it the same way nuke/firebomb value their blast: the $ cost of every
                    // enemy actually killed outright by the flat damage (shield absorbs
                    // damage before health -- see GameEngine.ApplyDamage -- so both must be
                    // covered for a real kill). This is IN ADDITION to, not instead of, the
                    // multiplier case below: a cast can be justified by either.
                    double freezeKillValue = enemyUnits
                        .Where(u => u.CurrentHealth + u.CurrentShield <= def.BaseValue)
                        .Sum(u => EstimateUnitCost(engine, u));
                    bool killValueJustifies = TargetValueJustified(me, def, freezeKillValue);

                    // Beyond guaranteed kills, freeze's remaining value is a MULTIPLIER on
                    // other units of ours capitalizing on the free hits against whatever
                    // survives -- Marc's framing: "against a $100 army it would see very
                    // little value by itself, but if you couple it with a solid unit, that
                    // could multiply the value." Require an army of our own on the field to
                    // capitalize with (this also still covers the original chokepoint-
                    // stalemate case, which by definition already has our own units pinned
                    // in contact). Previously had no economic gate of any kind -- add the
                    // standard not-a-big-spend-or-income-is-high check too.
                    bool multiplierJustifies = myUnits.Count > 0 && BigSpendJustified(me, def, 0);

                    // A third, genuinely independent justification, found from Marc's
                    // direct playtest report: the bot was sending cheap defenders into a
                    // strong enemy unit FIRST, only casting freeze once those defenders
                    // were already dying or dead -- backwards, since the whole point of
                    // freezing is to buy time BEFORE spending on units to capitalize on
                    // it, not after. The `multiplierJustifies` gate above requires
                    // myUnits.Count>0, which structurally can't fire until after that
                    // reactive spend has already happened -- a chicken-and-egg ordering
                    // bug. But freeze doesn't actually need an army to be worth casting on
                    // its own: per FreezeEffect, a frozen enemy skips its whole
                    // attack/move step for the status duration, which is a real,
                    // standalone "buy time" effect exactly like wall/wave/goo's slow --
                    // not something that requires our own units present to have value.
                    // Justify it the same way those do: whenever the runway model says
                    // we're genuinely pressed for time (inDanger), freeze the threat
                    // BEFORE (or instead of) committing money to units, so any defenders
                    // sent afterward land their hits on an immobilized target instead of a
                    // fully-active one that's already killed them.
                    //
                    // Also require the cost to fit the per-spend EV budget (see Decide()'s
                    // reactiveSpendBudget) -- inDanger alone says a real deficit exists,
                    // but not that THIS specific cast is worth its cost given how small
                    // that deficit actually is (Marc's D3596E point: a weak, isolated
                    // threat shouldn't justify unlimited defensive spend just because some
                    // deficit exists at all).
                    bool buyTimeJustifies = inDanger && def.Cost <= reactiveSpendBudget;

                    // EARLY STALL (flag 13). Every justification above requires the threat
                    // to have already arrived -- buyTimeJustifies via inDanger's
                    // enemyIsClose, multiplierJustifies via our own army being in contact.
                    // Freeze hits every enemy on the field regardless of position and buys
                    // the same time wherever it lands, so the best cast is the EARLIEST one:
                    // stall them at their own end and the march back is free time on top,
                    // with the cooldown returning before they arrive. See
                    // StallGadgetsEngageEarly.
                    bool earlyStallJustifies = _settings.StallGadgetsEngageEarly
                        && enemyUnits.Count >= _settings.StallForceMinUnits
                        && def.Cost <= me.Money * _settings.StallGadgetMaxMoneyFraction;

                    if (killValueJustifies || multiplierJustifies || buyTimeJustifies || earlyStallJustifies)
                        used = TryCast(engine, me, def, 0, freezeKillValue, enemyUnits, myCastlePos);
                    break;
                }

                case "nuke":
                {
                    // Always damages BOTH castles by BaseValue/2 and hits ALL units (any
                    // side) in the blast radius -- a real cost, not a free chip. Only
                    // worth it against an actual cluster, and only where none of our own
                    // units would eat the same blast.
                    //
                    // SUICIDE GUARD (2026-07-31, Marc's top-priority report: "I've seen the
                    // bot kill itself with a nuke 3 times"). CONFIRMED IN THE REPLAYS, not
                    // just plausible: NukeEffect calls DamageCastle on BOTH players for
                    // BaseValue/2 -- 100 / 1500 / 12000 by level -- and it lands def.Delay
                    // ticks AFTER the cast. nuke's Delay is 48, and games 97D761 and F5C3C3
                    // both ended exactly 48 ticks after the bot's own last nuke.
                    //
                    // Note how large this gets: CastleMaxHealth is 1000 + 11000*RepairCount,
                    // so a level-3 nuke's 12,000 self-damage ONE-SHOTS a 12,000 HP castle at
                    // full health. There was no self-HP check of any kind.
                    //
                    // Margin, not a bare comparison, because the blast lands 1.6s later and
                    // the enemy keeps hitting us in the meantime -- surviving it "exactly"
                    // at cast time is not surviving it on arrival.
                    int selfBlast = Gadgets.NukeEffect.CastleBlastFor(def);
                    bool survivesOwnBlast = me.CastleHealth > selfBlast * _settings.NukeSelfDamageMargin;

                    // Walls are near-immune value sinks here (Marc: "a freeze ray or a Nuke
                    // does basically nothing to it"), so they must not count toward either
                    // the cluster requirement or the target value -- otherwise a lone wall
                    // reads as a legitimate nuke target.
                    var nukeable = enemyUnits.Where(u => !IsWall(u)).ToList();
                    int? target = nukeable.Count > 0
                        ? FindBestAoeTarget(nukeable, radius, myCastlePos, def.Delay, clampToCastle: _settings.ClampProjectionToCastle)
                        : null;
                    // FRIENDLY FIRE: committed rule is "no ally in radius", which never
                    // trades. AoeTradeRule replaces it with Marc's comparison -- take the
                    // cast when the enemy army caught outweighs ours. See AoeTradeOk.
                    // MUST short-circuit on target first: the committed expression relied on
                    // `target.HasValue &&` guarding target.Value, and hoisting it out of that
                    // conjunction dereferenced a null on the very first no-cluster decision.
                    bool nukeFriendlyOk = target.HasValue && (_settings.AoeTradeRule
                        ? AoeTradeOk(engine, myUnits, nukeable, target.Value, radius)
                        : !myUnits.Any(u => Math.Abs(u.Position - target.Value) <= radius));
                    if (survivesOwnBlast && target.HasValue && nukeable.Count >= 2
                        && nukeFriendlyOk
                        && TargetValueJustified(me, def, EstimateEnemyValueNear(engine, nukeable, target.Value, radius)))
                        used = TryCast(engine, me, def, target.Value, EstimateEnemyValueNear(engine, nukeable, target.Value, radius), enemyUnits, myCastlePos);
                    break;
                }

                case "firebomb":
                {
                    // Leaves a damage-over-time zone that burns ANYONE standing in it,
                    // ally or enemy (FireHazard doesn't filter by side). Prefer the densest
                    // enemy cluster, but if that overlaps our own units, retarget instead
                    // of skipping the cast outright -- still a valid burn, and this gadget
                    // needs real usage to ever earn enough XP to upgrade past its weak
                    // base tier.
                    // Base-tier cost ($18) matches the first InvestmentPrice exactly, and
                    // this fires off a single enemy unit anywhere -- the most permissive
                    // trigger of any offense gadget. Same trap shape as reinforcements/wave/
                    // goo (see DeferForInvestment); defer it while that first foothold is
                    // still being built.
                    // FRIENDLY FIRE (fixed 2026-07-30, gated on
                    // FirebombSweptFriendlyFireCheck). Marc's report: "the bot constantly
                    // burns its own units." The overlap test below is not missing -- it is
                    // asking an instantaneous question about an effect that is anything
                    // but instantaneous, and it is wrong in three separate ways at once:
                    //
                    //  1. NO DELAY LEAD ON ALLIES. The enemy positions fed to
                    //     FindBestAoeTarget are already projected forward over def.Delay
                    //     (48 ticks / 1.6s) -- our own are not. So the cast is aimed at
                    //     where the enemy WILL be and cleared against where our army WAS.
                    //  2. NO HAZARD LIFETIME. FirebombEffect spawns a FireHazard lasting
                    //     def.HazardDuration (120 ticks / 4s, 180 / 6s at level 3) on top
                    //     of the delay. It is not a blast, it is a burning strip of ground
                    //     that sits there for 4-6 seconds -- and our units are marching
                    //     forward through it the entire time. An ally 400px behind the
                    //     target is "clear" by this test and walks straight into the fire
                    //     a second later. FireHazard.ProcessEffect does not filter by
                    //     Side, and the Burn it applies refreshes to 3s past leaving the
                    //     zone, so a unit that merely transits the strip still burns.
                    //  3. WRONG HITBOX. The hazard occupies [pos-Radius, pos+Radius] and
                    //     overlap is tested against a unit's [Position, Position+Width]
                    //     extent, not its centre point.
                    //
                    // Replace the point test with a SWEPT test: does any ally's path,
                    // over the whole window the fire is actually on the ground, intersect
                    // the burning strip? That is the question the cast needs answered.
                    if (DeferForInvestment(me)) break;
                    int? target = FindBestAoeTarget(enemyUnits, radius, myCastlePos, def.Delay, clampToCastle: _settings.ClampProjectionToCastle);
                    bool targetBurnsAllies = target.HasValue && (_settings.FirebombSweptFriendlyFireCheck
                        ? AllyWouldEnterHazard(myUnits, target.Value, radius, def)
                        : myUnits.Any(u => Math.Abs(u.Position - target.Value) <= radius));
                    // Under AoeTradeRule an ally in the fire is acceptable when the trade
                    // is favourable, so only retarget if it is NOT.
                    if (targetBurnsAllies && _settings.AoeTradeRule && target.HasValue
                        && AoeTradeOk(engine, myUnits, enemyUnits, target.Value, radius))
                        targetBurnsAllies = false;
                    if (targetBurnsAllies)
                    {
                        // Retarget used to pick whichever enemy was literally FARTHEST
                        // from our own army to dodge the overlap -- that's just the same
                        // back-most bias again by a different path (our army sits closer
                        // to the front line, so "farthest from us" skews toward the
                        // enemy's back). Pick the most front-most (closest to our castle)
                        // enemy that genuinely won't catch our own units in the blast
                        // instead; skip the cast if no such target exists rather than
                        // deliberately hitting whoever's safest-but-least-threatening.
                        var safe = enemyUnits
                            .Select(u => ProjectedPosition(u, def.Delay, _settings.ClampProjectionToCastle))
                            .Where(p => _settings.FirebombSweptFriendlyFireCheck
                                ? !AllyWouldEnterHazard(myUnits, p, radius, def)
                                : !myUnits.Any(m => Math.Abs(m.Position - p) <= radius))
                            .OrderBy(p => Math.Abs(p - myCastlePos))
                            .ToList();
                        target = safe.Count > 0 ? (int)safe[0] : (int?)null;
                    }
                    if (target.HasValue && TargetValueJustified(me, def, EstimateEnemyValueNear(engine, enemyUnits, target.Value, radius)))
                        used = TryCast(engine, me, def, target.Value, EstimateEnemyValueNear(engine, enemyUnits, target.Value, radius), enemyUnits, myCastlePos);
                    break;
                }
            }
            if (used) ActionCounts[11]++;
        }

        // The defense slot can be any of "heal" / "reinforcements" / "speed" / "wall".
        private void TryUseDefenseGadget(GameEngine engine, PlayerState me, List<Unit> myUnits, List<Unit> enemyUnits, int myCastlePos, bool inDanger, double reactiveSpendBudget)
        {
            if (_settings.DisableDefenseGadget) return;   // measurement switch, see the setting
            var def = me.DefensiveGadget;
            if (!IsReady(me, def)) return;
            if (TryUpgradeSpam(engine, me, def, myCastlePos, myUnits, enemyUnits, inDanger)) { ActionCounts[12]++; return; }

            string family = def.Id.Split('_')[0].ToLowerInvariant();
            bool used = false;

            switch (family)
            {
                case "heal":
                {
                    if (myUnits.Count == 0) return; // nothing to heal
                    float avgHpPct = myUnits.Average(u => u.MaxHealth > 0 ? (float)u.CurrentHealth / u.MaxHealth : 1f);
                    // None of these defense gadgets target enemies, so there's no $ value
                    // to weigh the cost against -- BigSpendJustified(0) only lets a big
                    // spend through when income is already high, same as the others below.
                    if (avgHpPct < 0.85f && BigSpendJustified(me, def, 0))
                        used = TryCast(engine, me, def, myCastlePos, 0, enemyUnits, myCastlePos);
                    break;
                }

                case "reinforcements":
                    // Spawns free units (bypasses cost entirely) regardless of position --
                    // pure value with no downside to OUR ARMY, but the cast itself has a
                    // real cost ($12 base) that competes with saving for the first
                    // InvestmentPrice ($18) -- see DeferForInvestment.
                    if (DeferForInvestment(me)) break;
                    if (BigSpendJustified(me, def, 0))
                        used = TryCast(engine, me, def, myCastlePos, 0, enemyUnits, myCastlePos);
                    break;

                case "speed":
                    // Same wall exclusion as rage, for the same reason: a wall never moves,
                    // so a movement buff on one is wasted. See the rage case.
                    if (myUnits.Any(u => !IsWall(u)) && BigSpendJustified(me, def, 0))
                        used = TryCast(engine, me, def, myCastlePos, 0, enemyUnits, myCastlePos);
                    break;

                case "wall":
                {
                    // Only ONE wall (any level) is ever allowed on the field at a time --
                    // casting again while one is already up just refunds the cost and
                    // grants NO xp, which stalls its upgrade path if we keep trying anyway.
                    // Wait for the existing one to die before recasting.
                    bool alreadyHaveWall = engine._state.Units.Any(u => u.Side == _side && u.DefinitionId.StartsWith("wall"));
                    if (alreadyHaveWall) break;

                    // Wall gives no tangible stat of its own -- it just buys TIME by
                    // tanking hits, and that time is worth the most exactly when time is
                    // actually short (Marc's framing: "these gadgets simply delay the enemy
                    // rather than actually giving you tangible stats... if that time allows
                    // you to get to the next investment threshold, it is invaluable").
                    // Prioritize casting it whenever the runway model says we're genuinely
                    // pressed for time (inDanger); otherwise fall back to the normal
                    // not-a-big-spend gate so it can still be used opportunistically once
                    // cheap/affordable without meaningfully competing with investing.
                    //
                    // Same trap as goo's heal case (see its own comment): BigSpendJustified's
                    // "cost is <80% of current money" branch approves this unconditionally
                    // once money is high enough, with no regard for whether there's anything
                    // to tank at all. A wall with zero enemies on the field delays nothing --
                    // require an enemy present before considering it on that basis.
                    //
                    // Also require the cost to fit the per-spend EV budget (see Decide()'s
                    // reactiveSpendBudget) before letting inDanger alone justify it -- a
                    // real deficit existing doesn't mean THIS specific cast is worth its
                    // cost against how small that deficit actually is.
                    bool dangerJustifies = inDanger && def.Cost <= reactiveSpendBudget;
                    if (!dangerJustifies && (enemyUnits.Count == 0 || !BigSpendJustified(me, def, 0))) break;

                    // WallEffect ignores the position for level 1 (fixed spawn point) but
                    // uses it directly for level 2/3, so place it in our own front line
                    // where it can actually tank alongside the rest of the army.
                    int target = myUnits.Count > 0 ? (int)myUnits.Average(u => u.Position) : myCastlePos;
                    used = TryCast(engine, me, def, target, 0, enemyUnits, myCastlePos);
                    break;
                }
            }
            if (used) ActionCounts[12]++;
        }

        private void TryUseSignatureGadget(GameEngine engine, PlayerState me, List<Unit> myUnits, List<Unit> enemyUnits,
            int myCastlePos, bool inDanger, float castleHpPct, double reactiveSpendBudget)
        {
            var def = me.SignatureGadget;
            if (!IsReady(me, def)) return;

            if (TryUpgradeSpam(engine, me, def, myCastlePos, myUnits, enemyUnits, inDanger)) { ActionCounts[13]++; return; }

            string family = def.Id.Split('_')[0].ToLowerInvariant();
            bool used = false;

            // SIEGE FLAG (Marc, 2026-08-12). "At least two of my units are attacking the
            // enemy castle" -- the shared precondition for rage, and for the poison/meteor/
            // goo pre-cast. Two, not one, so it cannot flip on a lone straggler.
            var myRoster = GameDataManager.Teams.FirstOrDefault(t => t.Color == me.Team)?.Roster;
            int enemyCastlePosSig = _side == 1 ? GameEngine.MAP_WIDTH - 200 : 200;
            bool sieging = SiegeUnitCount(engine, myUnits, myRoster) >= _settings.SiegeMinUnits;

            switch (family)
            {
                case "cash":
                    // Pure economy, no downside to the team's own board state -- always take it.
                    used = TryCast(engine, me, def, myCastlePos, double.MaxValue, enemyUnits, myCastlePos);
                    break;

                case "rage":
                    // A WALL IS NOT A VALID TARGET for a damage buff -- it never attacks, so
                    // raging it does literally nothing (Marc: "I saw the bot spend $120 using
                    // the Rage gadget on just a wall"). `myUnits.Count > 0` counted the wall
                    // as an army. Same class of bug as freeze-on-a-wall; heal and goo are
                    // deliberately NOT given this guard, since healing a wall genuinely
                    // extends how long it tanks.
                    // ON SIEGE (Marc, 2026-08-12): a damage buff is at its best when our
                    // units are already hitting the enemy castle, which the proximity
                    // clause below does not cover -- a castle is not an enemy UNIT, so a
                    // besieging army with no defenders left near it reads as "nothing to
                    // rage" exactly when raging is best.
                    if (myUnits.Any(u => !IsWall(u))
                        && (inDanger
                            || (_settings.RageOnSiege && sieging)
                            || myUnits.Any(u => !IsWall(u) && enemyUnits.Any(e => Math.Abs(e.Position - u.Position) < 250)))
                        && BigSpendJustified(me, def, 0))
                        used = TryCast(engine, me, def, myCastlePos, 0, enemyUnits, myCastlePos);
                    break;

                case "divine":
                    // Deliberately NOT gated by BigSpendJustified -- its own trigger
                    // (critical HP or a genuine incoming mass while already in danger) is
                    // already a strong, self-contained justification for spending big;
                    // survival is the whole point of a panic button, not a cost-effectiveness
                    // question a value gate should second-guess.
                    // DIVINE IS ALSO AN INVESTMENT, not only a panic button (Marc, 2026-07-31:
                    // "a large part of the power for the yellow team comes from Divine_3,
                    // which makes its castle and all its units completely invulnerable. If
                    // the yellow team doesn't reach that gadget upgrade it's pretty weak, and
                    // I haven't seen the bot use the Divine gadget much at all").
                    //
                    // The upgrade path makes this self-fulfilling: divine needs 400 XP (4
                    // casts) to reach divine_2 and a further 1100 (11 casts) to reach
                    // divine_3 -- FIFTEEN casts. The old trigger (below 30% HP, or in danger
                    // with 3+ enemies) fires so rarely that yellow essentially never gets
                    // there, so its best gadget never exists. Loosening the trigger buys both
                    // the immediate defensive value AND the progression.
                    // See DivineEagerHpThreshold.
                    float divineHp = _settings.EagerDivine ? _settings.DivineEagerHpThreshold : 0.3f;
                    int divineMass = _settings.EagerDivine ? _settings.DivineEagerEnemyCount : 3;
                    // CORRECTED 2026-08-12 (Marc): DivineEffect shields UNITS, not the
                    // castle, so castle HP and inDanger are simply the wrong quantities to
                    // trigger on -- the committed rule fires the gadget exactly when our
                    // army is least likely to exist. The right trigger is "we have an army
                    // worth protecting and can afford the cast". Passing 0 rather than
                    // MaxValue also puts it back under the income-drain cap, so it is rate
                    // limited by income like everything else instead of bypassing it.
                    if (_settings.DivineShieldsUnits)
                    {
                        if (myUnits.Any(u => !IsWall(u)))
                            used = TryCast(engine, me, def, myCastlePos, 0, enemyUnits, myCastlePos);
                    }
                    else if (castleHpPct < divineHp || (inDanger && enemyUnits.Count >= divineMass))
                        used = TryCast(engine, me, def, myCastlePos, double.MaxValue, enemyUnits, myCastlePos);
                    break;

                case "wave":
                {
                    // Sweeps the ENTIRE map width and knocks back every enemy it touches --
                    // except WaveHazard.ProcessEffect explicitly skips "wall"-prefixed units
                    // (they can't be knocked back at all) and gives tier-8 units a token
                    // 25-unit knockback instead of the usual 500-3000. Marc's direct report:
                    // "it sent out a tidal wave to knock back my wall. Not only can the wall
                    // not be knocked back, but it's a single unit and poses zero threat to
                    // the castle, so trying to knock it back is totally a waste." Don't even
                    // consider casting if the only enemies on the field are walls -- there's
                    // nothing the cast could possibly affect.
                    if (!enemyUnits.Any(u => !u.DefinitionId.StartsWith("wall"))) break;
                    // Always fires from our own edge regardless of target position -- but
                    // like reinforcements, the cast itself has a real cost that can compete
                    // with saving for the first InvestmentPrice; see DeferForInvestment.
                    if (DeferForInvestment(me)) break;
                    // Like wall, wave buys TIME (knockback) rather than a tangible stat --
                    // most valuable exactly when the runway model says time is actually
                    // short. Prioritize it whenever genuinely in danger (and the cost fits
                    // the per-spend EV budget, see Decide()'s reactiveSpendBudget); otherwise
                    // fall back to the normal not-a-big-spend gate for opportunistic, cheap
                    // casts.
                    if ((inDanger && def.Cost <= reactiveSpendBudget) || BigSpendJustified(me, def, 0))
                        used = TryCast(engine, me, def, myCastlePos, 0, enemyUnits, myCastlePos);
                    break;
                }

                case "goo":
                    {
                        // The canonical case this whole gate was built for: Marc watched
                        // the bot spend $200+ on goo healing a dying wall against one
                        // cheap attacker while his own economy pulled ahead unopposed.
                        // Healing doesn't target enemies, so there's no value to weigh
                        // against the cost -- BigSpendJustified(0) only lets it through
                        // once income is already high enough not to matter.
                        //
                        // A second, distinct bug in the same category, also from Marc's
                        // direct playtest: the bot cast goo (heals allies, slows enemies)
                        // against his own army attacking its castle with ZERO allied units
                        // of its own anywhere nearby -- there was nothing for the heal to
                        // do. Goo's heal value depends on allies surviving to keep
                        // benefiting from it (Marc: "their value can swing wildly depending
                        // if your allied units are able to survive and continue being
                        // healed... or get 1-shot and that value is lost") -- with no allies
                        // present at all, the HEAL half of goo is unconditionally zero.
                        //
                        // But goo has a second, genuinely independent value source: per
                        // GooHazard.ProcessEffect, it unconditionally applies a real 0.5x-
                        // speed Slow to ANY enemy standing in it, allies or not. That's a
                        // "buy time" effect like wall/wave, not something that needs allies
                        // at all -- so don't bail outright with no allies; only require them
                        // for the heal-driven justification, and fall back to the same
                        // inDanger time-bridge reasoning wall/wave use for the slow-only case.
                        //
                        // A third bug, found from Marc's report of the bot repeatedly
                        // casting goo over its own idle wall with ZERO enemies anywhere on
                        // the field (he'd never spawned a single unit): BigSpendJustified's
                        // FIRST branch ("cost is <80% of current money, don't sweat it")
                        // approves goo unconditionally whenever money climbs past ~$87.50
                        // (Cost 70 / 0.8), with no regard to income OR the estimatedEnemyValue
                        // argument passed in -- it was only ever meant as a "this is basically
                        // free, don't overthink a REAL opportunity" shortcut, not a substitute
                        // for there being any value at all. With no enemy on the field, an
                        // idle ally (even just the wall) isn't taking damage and has nothing
                        // to heal -- myUnits.Count>0 alone can't tell "ally is fighting" apart
                        // from "ally is standing there doing nothing." Require an enemy to
                        // actually be present for the heal case, same as the slow case already
                        // requires via inDanger.
                        bool healUseCase = myUnits.Count > 0 && enemyUnits.Count > 0 && BigSpendJustified(me, def, 0);
                        // Also gated on the per-spend EV budget (Decide()'s
                        // reactiveSpendBudget) -- same reasoning as wall/wave/freeze.
                        bool slowUseCase = inDanger && def.Cost <= reactiveSpendBudget;
                        // SIEGE PRE-CAST -- same argument as meteor/poison above.
                        bool gooSiege = _settings.SiegePreCast && sieging;
                        if (DeferForInvestment(me) || (!healUseCase && !slowUseCase && !gooSiege)) break;
                        if (gooSiege && !healUseCase && !slowUseCase)
                        {
                            used = TryCast(engine, me, def, enemyCastlePosSig, 0, enemyUnits, myCastlePos);
                            break;
                        }
                        int target = myUnits.Count > 0 ? (int)myUnits.Average(u => u.Position) : myCastlePos;
                        used = TryCast(engine, me, def, target, 0, enemyUnits, myCastlePos);
                        break;
                    }

                case "meteor":
                case "poison":
                    {
                        // Both of these only ever affect enemy units -- no friendly fire risk.
                        int? target = FindBestAoeTarget(enemyUnits, Math.Max(150, def.Radius), myCastlePos, def.Delay, clampToCastle: _settings.ClampProjectionToCastle);
                        if (target.HasValue && TargetValueJustified(me, def, EstimateEnemyValueNear(engine, enemyUnits, target.Value, Math.Max(150, def.Radius))))
                            used = TryCast(engine, me, def, target.Value, EstimateEnemyValueNear(engine, enemyUnits, target.Value, Math.Max(150, def.Radius)), enemyUnits, myCastlePos);
                        // SIEGE PRE-CAST (Marc, 2026-08-12). These land after def.Delay, so
                        // dropping one on the enemy castle while we are already sieging pays
                        // even with NO enemy unit on the field: the defender's answer spawns
                        // into the effect. Worst case the cast is wasted while our units take
                        // the castle for free, which is not a bad trade.
                        else if (_settings.SiegePreCast && sieging)
                            used = TryCast(engine, me, def, enemyCastlePosSig, 0, enemyUnits, myCastlePos);
                        break;
                    }

                case "blackhole":
                    {
                        // Unlike meteor/poison, a black hole pulls in and damages BOTH sides
                        // in its radius (and can instantly kill non-tier-8 units at its core),
                        // so only fire it where none of our own units would get caught in it.
                        // Exception: "evilguy" (black team's tier 8) is completely immune to
                        // blackhole (see BlackholeHazard.ProcessEffect/OnExpire) -- Marc's own
                        // flagged quirk, "every unit gets sucked into the black hole except
                        // evilguy, hugely effective in the late game since everything else
                        // gets CC'd allowing your strong tier 8 unit to easily take
                        // everything out." Don't let the friendly-fire check needlessly dodge
                        // a cast just because our own evilguy happens to be standing in the
                        // blast -- every OTHER ally still needs to be clear of it.
                        int radius = Math.Max(150, def.Radius);
                        // Blackhole is a TIME gadget, not a damage one -- it buys the same
                        // delay wherever it lands, so aim it at the enemy's OWN end of the
                        // map and collect the march back as free time (and a cooldown
                        // cycle) on top. FindBestAoeTarget's default weighting pulls toward
                        // our castle, which is right for nuke/meteor/poison and backwards
                        // here. See StallGadgetsEngageEarly.
                        int? target = FindBestAoeTarget(enemyUnits, radius, myCastlePos, def.Delay,
                                                        preferFarFromMyCastle: _settings.StallGadgetsEngageEarly,
                                                        clampToCastle: _settings.ClampProjectionToCastle);
                        // evilguy is immune, so it never counts as caught either way.
                        var catchable = myUnits.Where(u => u.DefinitionId != "evilguy").ToList();
                        bool friendlyFire = target.HasValue && (_settings.AoeTradeRule
                            ? !AoeTradeOk(engine, catchable, enemyUnits, target.Value, radius)
                            : catchable.Any(u => Math.Abs(u.Position - target.Value) <= radius));
                        // Same early-engagement argument as freeze: a real force is worth
                        // stalling before it arrives, not only once it is already on us.
                        bool earlyStall = _settings.StallGadgetsEngageEarly
                            && enemyUnits.Count >= _settings.StallForceMinUnits
                            && def.Cost <= me.Money * _settings.StallGadgetMaxMoneyFraction;
                        // BUY TIME (Marc, 2026-08-12): blackhole is a stall tool of the
                        // same family as freeze, which already has this clause. Stalling an
                        // arriving force is worth the cost on its own, independent of the
                        // dollar value caught.
                        bool bhBuyTime = _settings.BlackholeBuyTime && inDanger && def.Cost <= reactiveSpendBudget;
                        if (target.HasValue && !friendlyFire
                            && (earlyStall || bhBuyTime || TargetValueJustified(me, def, EstimateEnemyValueNear(engine, enemyUnits, target.Value, radius))))
                            used = TryCast(engine, me, def, target.Value, EstimateEnemyValueNear(engine, enemyUnits, target.Value, radius), enemyUnits, myCastlePos);
                        break;
                    }
            }
            if (used) ActionCounts[13]++;
        }

        // Projects where a unit will actually BE after leadTicks pass (a gadget's
        // deployment delay, GadgetDefinition.Delay), based on its own current speed and
        // side-determined direction of travel (GameEngine's own movement step: side 1
        // moves toward +X, side 2 toward -X -- see GameEngine.cs's per-tick movement
        // logic). CurrentSpeed is already 0 whenever a unit is engaged in combat or
        // attacking a castle (GameEngine sets it that way every tick), so a
        // stationary/fighting unit is correctly not led at all -- only units still
        // actively marching get projected forward.
        private float ProjectedPosition(Unit u, int leadTicks, bool clampToCastle = true)
            => Gadgets.GadgetTargeting.ProjectedPosition(u, leadTicks, clampToCastle, _side1Wall, _side2Wall);

        // Would any of our own units be standing in a ground hazard centred on `centre`
        // at ANY point during that hazard's life?
        //
        // A persistent hazard (firebomb's FireHazard, and goo's, though goo's heal makes
        // its friendly overlap desirable rather than a hazard) is on the ground from
        // def.Delay through def.Delay + def.HazardDuration. Over that window our units
        // keep marching, so the correct test is whether an ally's swept PATH intersects
        // the hazard's strip -- not whether an ally's centre point happens to sit inside
        // it at the instant of the cast.
        //
        // Movement is linear at CurrentSpeed in the side's fixed direction, so sweeping
        // just means taking the ally's projected position at both ends of the window and
        // treating everything between as occupied. GameEngine zeroes CurrentSpeed for any
        // unit in combat or attacking a castle, so an ally already locked in a fight
        // correctly sweeps to a single point rather than being assumed to walk away.
        //
        // The strip is [centre - radius, centre + radius] (see FirebombEffect: Position =
        // position - Radius, Width = Radius * 2) and a unit occupies [Position, Position
        // + Width] (see FireHazard.ProcessEffect's overlap test), so the ally's extent is
        // widened by its own Width to match how the engine actually resolves contact.
        private bool AllyWouldEnterHazard(List<Unit> myUnits, float centre, int radius, GadgetDefinition def)
        {
            float stripLeft = centre - radius;
            float stripRight = centre + radius;

            int windowStart = Math.Max(0, def.Delay);
            int windowEnd = windowStart + Math.Max(0, def.HazardDuration);

            foreach (var u in myUnits)
            {
                float a = ProjectedPosition(u, windowStart);
                float b = ProjectedPosition(u, windowEnd);
                float lo = Math.Min(a, b);
                float hi = Math.Max(a, b) + u.Width;
                if (hi >= stripLeft && lo <= stripRight) return true;
            }
            return false;
        }

        // Finds the best AOE launch position -- prefers a dense, high-power cluster
        // that's ALSO meaningfully threatening (close to advancing on OUR OWN castle),
        // not just whichever cluster has the most raw power. Previously scored purely by
        // clustered power with no positional preference at all: a freshly-spawned clump
        // of cheap fodder still bunched up near the enemy's own spawn point can easily
        // outscore a more spread-out, more dangerous front line on density alone,
        // systematically biasing every AOE gadget toward the BACK of the enemy
        // formation instead of the front -- Marc's own play flagged exactly this
        // ("targeting is supposed to launch at the front-most enemy unit, but it looks
        // like we're targeting the back-most instead"). Reuses the same threat-proximity
        // weighting formula as the threatScore calculation earlier in Decide().
        //
        // Also leads each unit by its own current speed over the gadget's deployment
        // delay (leadTicks, from GadgetDefinition.Delay -- ~1.6-2.3s across the offense/
        // signature gadgets that use this, per master_gadgets.csv) so the blast lands
        // where units WILL be when it actually goes off, not where they were standing
        // at the moment the cast was queued.
        // preferFarFromMyCastle inverts the positional preference for TIME gadgets (see
        // StallGadgetsEngageEarly): a CC effect buys the same delay wherever it lands, so
        // the best aim point is the one that leaves the most map for the enemy to re-cross.
        // Damage gadgets keep the default, which prefers the threatening front.
        private int? FindBestAoeTarget(List<Unit> targets, int radius, int myCastlePos, int leadTicks = 0,
                                       bool preferFarFromMyCastle = false, bool clampToCastle = true)
            => Gadgets.GadgetTargeting.FindBestAoeTarget(targets, radius, myCastlePos, leadTicks,
                                                         preferFarFromMyCastle, clampToCastle,
                                                         _side1Wall, _side2Wall);
    }
}
