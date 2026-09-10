using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Replays
{
    /// <summary>
    /// One parsed CDRP replay: the binary FORMAT and the rules for rebuilding the exact
    /// starting position from it.
    ///
    /// LIVES IN THE ENGINE, not in CastleDefense.Simulation where it was born, so that the
    /// web project can read replays as well. The dependency arrow only points one way --
    /// CastleDefense.Simulation references CastleDefenseGame2 -- so a reader that lived in
    /// Simulation was unreachable from the server, and the alternative was a second parser
    /// for the same bytes. The corpus-selection half (which replays are Marc's games, and
    /// the win-by-default rule) stayed behind in ReplayCorpus, because it depends on
    /// GameDatabase and is analysis policy rather than file format.
    ///
    /// WHY THIS IS SHARED. Two tools now reconstruct games from these files — `--divergence`
    /// and `--export-policy-table` — and they must agree exactly on which games they read
    /// and how the starting state is rebuilt, or the policy table would be fitted to a
    /// different population than the metric is scored on. A duplicated binary parser is
    /// precisely the sort of code that drifts silently, which this project has already been
    /// burned by twice.
    ///
    /// FORMAT LIMIT, worth repeating at the parse site: the per-tick payload is a single
    /// action id per side and NOTHING ELSE. Gadget target positions were never recorded, so
    /// any reconstruction re-aims every cast with the engine's auto-target. See
    /// GameRecorder's format comment and the fidelity note on Divergence.
    /// </summary>
    public sealed class ReplayFile
    {
        public string GameId;
        public string P1Team, P1Off, P1Def, P1Sig;
        public string P2Team, P2Off, P2Def, P2Sig;
        public byte Winner;
        public long StartingTick;
        public double P1StartMoney, P2StartMoney;
        public byte[] A1, A2;               // one action id per tick, per side
        public int TickCount => A1.Length;

        // ── v3 fields. Version < 3 leaves these at their defaults and HasV3 is false. ──
        public byte Version;
        public bool HasV3 => Version >= 3;
        public TeamColour Map;
        public bool ShadowMap;
        public int EngineSeed;
        /// <summary>Loadout as it was at game START. The P1Off/P1Def/... fields above are
        /// the END-of-game loadout, which is what v2 recorded and what the DB row uses.</summary>
        public string P1StartOff, P1StartDef, P1StartSig;
        public string P2StartOff, P2StartDef, P2StartSig;
        /// <summary>Resolved gadget target per cast, keyed by (tick, side).</summary>
        public Dictionary<(int tick, int side), short> GadgetTargets = new();

        // ── v4 fields. Version < 4 leaves these at their defaults and HasV4 is false. ──
        public bool HasV4 => Version >= 4;

        /// <summary>
        /// Did each side buy ARMAGEDDON? Captured from the live PlayerState at game over,
        /// like the winner byte, because it CANNOT be recovered from the action stream: the
        /// purchase is action 9, indistinguishable from an ordinary invest until you know
        /// the InvestmentCount was already at the cap, and it never reaches gadget_uses
        /// either -- GameEngine.TriggerArmageddon calls the effect directly rather than
        /// through UseGadget, so it raises no OnGadgetCast.
        /// </summary>
        public bool P1Armageddon, P2Armageddon;

        /// <summary>
        /// Content fingerprints of the two master CSVs the game was PLAYED under, from
        /// GameDataManager.GadgetsFileHash / RosterFileHash. Compare against the current
        /// values to know whether a reconstruction can be trusted at all -- see the note on
        /// those properties for the game this was added for.
        /// </summary>
        public string GadgetsHash = "", RosterHash = "";

        /// <summary>
        /// Applies one recorded action, using the RECORDED gadget target when the file has
        /// one. This is the whole point of v3: for actions 11/12/13 a v2 rebuild called
        /// ApplyAction, which routes to UseGadget(-1) and re-aims the cast with the engine
        /// auto-targeter. Every tool that reconstructs a game should call this rather than
        /// engine.ApplyAction so it gets exact aim on v3 files and the old behaviour on v2.
        /// </summary>
        public bool ApplyRecorded(GameEngine engine, int side, int tick, byte action)
        {
            if (action >= 11 && action <= 13 && GadgetTargets.TryGetValue((tick, side), out short pos))
            {
                var p = side == 1 ? engine._state.Player1 : engine._state.Player2;
                string id = action == 11 ? p.OffensiveGadget?.Id
                          : action == 12 ? p.DefensiveGadget?.Id
                                         : p.SignatureGadget?.Id;
                if (id != null) return engine.UseGadget(side, id, pos);
            }
            return engine.ApplyAction(side, action);
        }

        /// <summary>True when the human in seat 1 never acted. Marc re-rolls until he gets
        /// the matchup he wants and the discarded attempts still record; 11 were quarantined
        /// on 2026-08-05 but the pattern recurs, and such a game is all "wait".</summary>
        public bool IsAbandoned => !A1.Any(b => b != 0);

        public static ReplayFile Read(string path)
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.UTF8);

            var magic = r.ReadBytes(4);
            if (magic.Length < 4 || magic[0] != 'C' || magic[1] != 'D' || magic[2] != 'R' || magic[3] != 'P')
                throw new InvalidDataException("not a CDRP replay");

            var f = new ReplayFile();
            byte version = r.ReadByte();
            f.Version = version;
            f.GameId = Encoding.ASCII.GetString(r.ReadBytes(6));
            r.ReadInt64();          // timestamp
            ReadStr(r);             // game_version
            f.P1Team = ReadStr(r); f.P1Off = ReadStr(r); f.P1Def = ReadStr(r); f.P1Sig = ReadStr(r);
            f.P2Team = ReadStr(r); f.P2Off = ReadStr(r); f.P2Def = ReadStr(r); f.P2Sig = ReadStr(r);
            f.Winner = r.ReadByte();

            if (version < 2)
                // v1 needs the time-machine state inferred, which lives in Program's BC
                // exporter. These are the oldest recordings and predate the bot any of this
                // is about, so both consumers skip rather than duplicate that inference.
                throw new InvalidDataException("v1 replay (pre-time-machine-header), skipped");

            f.StartingTick = r.ReadInt64();
            f.P1StartMoney = r.ReadDouble();
            f.P2StartMoney = r.ReadDouble();

            uint tickCount = r.ReadUInt32();
            f.A1 = new byte[tickCount];
            f.A2 = new byte[tickCount];
            for (uint t = 0; t < tickCount; t++) { f.A1[t] = r.ReadByte(); f.A2[t] = r.ReadByte(); }

            // ── v3 tail. Everything above is byte-identical to v2, so an old file simply
            // stops here and HasV3 stays false. Guarded on stream position as well as
            // version so a truncated write cannot throw.
            if (version >= 3 && stream.Position < stream.Length)
            {
                f.Map = (TeamColour)r.ReadByte();
                f.ShadowMap = r.ReadByte() != 0;
                f.EngineSeed = r.ReadInt32();
                f.P1StartOff = ReadStr(r); f.P1StartDef = ReadStr(r); f.P1StartSig = ReadStr(r);
                f.P2StartOff = ReadStr(r); f.P2StartDef = ReadStr(r); f.P2StartSig = ReadStr(r);
                uint casts = r.ReadUInt32();
                for (uint i = 0; i < casts; i++)
                {
                    int tick = r.ReadInt32();
                    int side = r.ReadByte();
                    short pos = r.ReadInt16();
                    f.GadgetTargets[(tick, side)] = pos;
                }
            }

            // ── v4 tail. Appended after v3 for the same reason v3 was appended after v2:
            // every byte above is unchanged, so a v3 reader skips this and a v3 file stays
            // readable forever. Position-guarded so a truncated write degrades to "absent"
            // rather than throwing.
            if (version >= 4 && stream.Position < stream.Length)
            {
                f.P1Armageddon = r.ReadByte() != 0;
                f.P2Armageddon = r.ReadByte() != 0;
                f.GadgetsHash = ReadStr(r);
                f.RosterHash = ReadStr(r);
            }
            return f;
        }

        /// <summary>
        /// Rebuilds the exact starting position, time machine included.
        ///
        /// SEEDED, deliberately. Reconstruction used an unseeded `new GameEngine(state)`,
        /// and the engine's Rng drives unit y-position on spawn — which changes combat
        /// targeting, so two runs of the SAME binary over the SAME replays produced
        /// different castle HP. Measured: 3 of 141 games drifted in the third decimal of
        /// hp_pct. Small, but a tracked metric that is not bit-reproducible cannot support
        /// the same-build paired comparisons this project's benchmark discipline relies on,
        /// and a wandering baseline is exactly how a null result gets read as a real one.
        /// The seed is derived from the game id, so each game is independent and stable and
        /// adding a replay does not perturb the others.
        /// </summary>
        /// <summary>
        /// Rebuilds the opening position. <paramref name="seedOverride"/> and
        /// <paramref name="mapOverride"/> exist for the replay gauntlet, which needs the SAME
        /// action stream played out under DIFFERENT engine randomness -- otherwise the
        /// reconstruction is one deterministic game and carries one bit of information.
        /// Both default to null, so every existing caller reproduces the recorded game exactly.
        /// </summary>
        public (GameState state, GameEngine engine) BuildStart(int? seedOverride = null,
                                                               TeamColour? mapOverride = null)
        {
            int timeSkip = (int)(StartingTick / (30 * 30));
            // v3 records the map that was actually rolled; v2 did not, so a v2 rebuild has
            // always played on a DIFFERENT randomly-drawn map than the recorded game.
            int seed = seedOverride ?? (HasV3 ? EngineSeed : SeedFor(GameId));
            var map = mapOverride ?? Map;
            var state = HasV3 ? new GameState(map, new Random(seed)) : new GameState();
            if (HasV3) { state.Map = map; state.ShadowMap = mapOverride.HasValue ? false : ShadowMap; }
            state.Player1 = new PlayerState(timeSkip);
            state.Player2 = new PlayerState(timeSkip);
            state.Player1.Side = 1;
            state.Player2.Side = 2;
            state.Player1.Money = P1StartMoney;
            state.Player2.Money = P2StartMoney;
            state.CurrentTick = StartingTick;
            state.Player1.Team = Enum.Parse<TeamColour>(P1Team, ignoreCase: true);
            state.Player2.Team = Enum.Parse<TeamColour>(P2Team, ignoreCase: true);
            // START loadout on v3. The P1Off/... fields are the END-of-game loadout, so on a
            // v2 file a gadget that upgraded mid-game is equipped at its final tier from tick
            // 0 -- which for FC1462 meant a $4000 nuke_3 in a game that began on a $20 nuke,
            // and every cast then failed the money check. Callers working with v2 files
            // should keep stripping the tier suffix themselves; there is nothing better
            // available for those.
            var l1 = (HasV3 ? new[] { P1StartOff, P1StartDef, P1StartSig }
                            : new[] { P1Off, P1Def, P1Sig }).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            var l2 = (HasV3 ? new[] { P2StartOff, P2StartDef, P2StartSig }
                            : new[] { P2Off, P2Def, P2Sig }).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            if (l1.Length == 3) state.Player1.SetLoadout(l1);
            if (l2.Length == 3) state.Player2.SetLoadout(l2);
            // v3 replays the ACTUAL engine stream; v2 falls back to the game-id hash, which
            // is stable and independent per game but is not the stream the game was played on.
            return (state, new GameEngine(state, null, seed));
        }

        /// <summary>Stable hash of the 6-char game id. String.GetHashCode is randomised per
        /// process in .NET Core, so it cannot be used here.</summary>
        private static int SeedFor(string gameId)
        {
            unchecked
            {
                int h = 17;
                foreach (char c in gameId ?? "") h = h * 31 + c;
                return h & 0x7fffffff;
            }
        }

        private static string ReadStr(BinaryReader r)
        {
            int len = r.ReadByte();
            return len > 0 ? Encoding.UTF8.GetString(r.ReadBytes(len)) : "";
        }
    }
}
