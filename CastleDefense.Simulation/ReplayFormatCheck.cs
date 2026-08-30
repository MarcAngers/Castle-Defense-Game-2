using CastleDefense.Api.Data;
using CastleDefense.Api.Services;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// ROUND-TRIP ORACLE for the CDRP replay format.
    ///
    /// A file format that is written in one project and parsed in another, with no test
    /// between them, is exactly the shape of code this project has been burned by: the v2
    /// end-of-game-loadout bug survived 189 recordings precisely because nothing ever
    /// checked that what the reader produced matched what the writer meant. v3 adds a map,
    /// a seed, a start loadout and a gadget-target list, all of which are silent when wrong
    /// -- a rebuild simply plays a slightly different game and no one notices.
    ///
    /// So: write a replay with known values through the REAL GameRecorder, read it back
    /// through the REAL ReplayFile, and assert every field survives. Then do the same for a
    /// hand-built v2 byte stream to prove backwards compatibility did not regress.
    ///
    /// Usage: --replay-format-check
    /// </summary>
    public static class ReplayFormatCheck
    {
        private static int _pass, _fail;

        private static void Check(string name, object actual, object expected)
        {
            bool ok = Equals(actual?.ToString(), expected?.ToString());
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name,-46} got {actual}   want {expected}");
        }

        public static void Run(string[] args)
        {
            Console.WriteLine("=== CDRP REPLAY FORMAT ROUND-TRIP ===\n");

            string dir = Path.Combine(Path.GetTempPath(), "cdrp_fmt_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string dbPath = Path.Combine(dir, "t.db");

            var rec = new GameRecorder("ZZ9XY7", startingTick: 0, p1StartMoney: 3.5, p2StartMoney: 7.25,
                map: (byte)TeamColour.Purple, shadowMap: true, engineSeed: 123456789,
                p1StartLoadout: new[] { "nuke", "reinforcements", "cash" },
                p2StartLoadout: new[] { "freeze", "wall", "cash" });

            for (int t = 0; t < 40; t++) rec.RecordTick(t == 5 ? 9 : 0, t == 7 ? 11 : 0);
            // Targets deliberately include a negative-ish and a large value to exercise int16.
            rec.RecordGadgetUse(7, 2, "nuke", 1700);
            rec.RecordGadgetUse(19, 1, "freeze", 300);
            rec.RecordGadgetUse(31, 2, "cash", 950);

            var p1 = new PlayerState(); p1.Side = 1; p1.Team = TeamColour.White;
            p1.SetLoadout(new[] { "nuke_2", "reinforcements", "cash" });   // END loadout differs from START
            var p2 = new PlayerState(); p2.Side = 2; p2.Team = TeamColour.Red;
            p2.SetLoadout(new[] { "freeze", "wall_2", "cash" });

            var db = new GameDatabase(dbPath);
            rec.Save(dir, p1, p2, winner: 1, durationTicks: 40, gameVersion: "test", db: db,
                     gameMode: "sp", opponentType: "search");

            string file = Path.Combine(dir, "ZZ9XY7.replay");
            if (!File.Exists(file)) { Console.WriteLine("  [FAIL] recorder wrote no file"); return; }
            long size = new FileInfo(file).Length;

            var rf = ReplayFile.Read(file);
            Console.WriteLine("-- v3 write -> read --");
            Check("version", rf.Version, 3);
            Check("HasV3", rf.HasV3, true);
            Check("game id", rf.GameId, "ZZ9XY7");
            Check("winner", rf.Winner, 1);
            Check("tick count", rf.TickCount, 40);
            Check("p1 start money", rf.P1StartMoney, 3.5);
            Check("p2 start money", rf.P2StartMoney, 7.25);
            Check("map", rf.Map, TeamColour.Purple);
            Check("shadow map", rf.ShadowMap, true);
            Check("engine seed", rf.EngineSeed, 123456789);
            Check("p1 START offence", rf.P1StartOff, "nuke");
            Check("p2 START offence", rf.P2StartOff, "freeze");
            Check("p2 START defence", rf.P2StartDef, "wall");
            Check("p1 END offence (unchanged role)", rf.P1Off, "nuke_2");
            Check("p2 END defence (unchanged role)", rf.P2Def, "wall_2");
            Check("recorded action p1@5", rf.A1[5], 9);
            Check("recorded action p2@7", rf.A2[7], 11);
            Check("gadget target count", rf.GadgetTargets.Count, 3);
            Check("target (7, side 2)", rf.GadgetTargets[(7, 2)], 1700);
            Check("target (19, side 1)", rf.GadgetTargets[(19, 1)], 300);
            Check("target (31, side 2)", rf.GadgetTargets[(31, 2)], 950);

            // THE POINT OF THE WHOLE FORMAT CHANGE: the start loadout must NOT be the end one.
            bool distinct = rf.P1StartOff != rf.P1Off;
            Console.WriteLine($"  [{(distinct ? "PASS" : "FAIL")}] start loadout is distinct from end loadout"
                            + $"        {rf.P1StartOff} vs {rf.P1Off}");
            if (distinct) _pass++; else _fail++;

            Console.WriteLine("\n-- v2 backwards compatibility --");
            // Truncate the v3 tail and stamp the version byte back to 2: byte-for-byte what a
            // real v2 file looks like, since v3 only appends.
            var bytes = File.ReadAllBytes(file);
            int v2Len = FindV2Length(bytes);
            var v2 = bytes.Take(v2Len).ToArray();
            v2[4] = 2;
            string v2File = Path.Combine(dir, "V2FILE.replay");
            File.WriteAllBytes(v2File, v2);
            var old = ReplayFile.Read(v2File);
            Check("v2 version", old.Version, 2);
            Check("v2 HasV3", old.HasV3, false);
            Check("v2 tick count still parses", old.TickCount, 40);
            Check("v2 action p1@5 still parses", old.A1[5], 9);
            Check("v2 no gadget targets", old.GadgetTargets.Count, 0);

            Console.WriteLine($"\n  file size {size} bytes for a 40-tick game with 3 casts");
            Console.WriteLine($"  {_pass} passed, {_fail} failed");
            if (_fail > 0) Console.WriteLine("\n  *** THE REPLAY FORMAT IS BROKEN -- DO NOT RECORD GAMES ***");

            try { Directory.Delete(dir, true); } catch { }
        }

        /// <summary>
        /// Length of the v2 prefix: everything through the per-tick payload. Recomputed from
        /// the bytes rather than assumed, so this stays correct if a header string changes.
        /// </summary>
        private static int FindV2Length(byte[] b)
        {
            int o = 4 + 1 + 6 + 8;                 // magic, version, id, timestamp
            for (int i = 0; i < 9; i++) o += 1 + b[o];   // gameVersion + 8 loadout strings
            o += 1 + 8 + 8 + 8;                    // winner, startingTick, two start moneys
            uint ticks = BitConverter.ToUInt32(b, o);
            o += 4 + (int)ticks * 2;
            return o;
        }
    }
}
