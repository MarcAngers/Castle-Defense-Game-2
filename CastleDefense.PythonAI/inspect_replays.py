"""
Finds recorded games in which a side never took an action.

These are abandoned games -- Marc rerolls the matchup until he gets the team he
wants, and the abandoned attempts still get recorded. They are pure noise for
calibration: a game where P1 never acts is a game where P1 loses to whatever the
opponent does, so the label says nothing about position quality.

READ-ONLY by default. Pass --delete to actually remove, which also writes the
files to a backup directory first. This project has permanently lost ~144
recordings to a cleanup before, so nothing here deletes without a copy.

Replay format (CDRP), from Simulation/Program.cs:1807 ExportEvalForReplay:
    magic "CDRP" (4 bytes)
    version (1 byte)
    game_id (6 ASCII bytes)
    timestamp (int64)
    game_version, p1_team, p1_off, p1_def, p1_sig,
                  p2_team, p2_off, p2_def, p2_sig     -- each: len byte + UTF8
    winner (1 byte)
    [version >= 2] starting_tick (int64), p1_start_money (f64), p2_start_money (f64)
    tick_count (uint32)
    tick_count x (p1_action byte, p2_action byte)

Usage:
    python inspect_replays.py [replay_dir] [--delete]
"""
import shutil
import struct
import sys
import pathlib
import datetime

DEFAULT_DIR = pathlib.Path(__file__).resolve().parents[1] / \
    "CastleDefenseGame2" / "recordings" / "singleplayer"


def read_str(buf, pos):
    n = buf[pos]
    pos += 1
    return buf[pos:pos + n].decode("utf-8", "replace"), pos + n


def parse(path):
    buf = path.read_bytes()
    if buf[:4] != b"CDRP":
        return {"error": "not a CDRP replay"}
    pos = 4
    version = buf[pos]; pos += 1
    game_id = buf[pos:pos + 6].decode("ascii", "replace"); pos += 6
    (timestamp,) = struct.unpack_from("<q", buf, pos); pos += 8

    fields = {}
    for name in ("game_version", "p1_team", "p1_off", "p1_def", "p1_sig",
                 "p2_team", "p2_off", "p2_def", "p2_sig"):
        fields[name], pos = read_str(buf, pos)

    winner = buf[pos]; pos += 1

    starting_tick = 0
    if version >= 2:
        (starting_tick,) = struct.unpack_from("<q", buf, pos); pos += 8
        pos += 16  # two doubles: p1/p2 start money

    (tick_count,) = struct.unpack_from("<I", buf, pos); pos += 4

    avail = (len(buf) - pos) // 2
    n = min(tick_count, avail)
    actions = buf[pos:pos + 2 * n]
    p1 = actions[0::2]
    p2 = actions[1::2]

    return {
        "game_id": game_id, "version": version, "winner": winner,
        "timestamp": timestamp, "ticks": tick_count, "ticks_read": n,
        "truncated": n < tick_count,
        "p1_team": fields["p1_team"], "p2_team": fields["p2_team"],
        "p1_actions": sum(1 for a in p1 if a != 0),
        "p2_actions": sum(1 for a in p2 if a != 0),
    }


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    do_delete = "--delete" in sys.argv
    d = pathlib.Path(args[0]) if args else DEFAULT_DIR
    files = sorted(d.glob("*.replay"))
    print(f"{len(files)} replay files in {d}\n")

    dead_p1, dead_p2, bad, ok = [], [], [], 0
    for f in files:
        info = parse(f)
        if "error" in info:
            bad.append((f, info["error"]))
            continue
        if info["p1_actions"] == 0:
            dead_p1.append((f, info))
        elif info["p2_actions"] == 0:
            dead_p2.append((f, info))
        else:
            ok += 1

    def show(title, rows):
        if not rows:
            return
        print(f"{title}: {len(rows)}")
        print(f"  {'file':<16}{'id':<8}{'v':>2}{'ticks':>8}{'p1act':>7}"
              f"{'p2act':>7}{'win':>5}  {'date':<12} matchup")
        for f, i in rows:
            dt = datetime.datetime.utcfromtimestamp(i["timestamp"]).date() \
                if 0 < i["timestamp"] < 4102444800 else "?"
            print(f"  {f.name:<16}{i['game_id']:<8}{i['version']:>2}"
                  f"{i['ticks']:>8}{i['p1_actions']:>7}{i['p2_actions']:>7}"
                  f"{i['winner']:>5}  {str(dt):<12}{i['p1_team']} vs {i['p2_team']}")
        print()

    show("P1 NEVER ACTED (abandoned rerolls)", dead_p1)
    show("P2 never acted (opponent idle -- listed, NOT removed)", dead_p2)
    if bad:
        print(f"unparseable: {len(bad)}")
        for f, e in bad:
            print(f"  {f.name}: {e}")
        print()
    print(f"games with actions on both sides: {ok}")

    if not do_delete:
        print("\nREAD-ONLY. Re-run with --delete to remove the P1-never-acted files.")
        return

    if not dead_p1:
        print("\nNothing to delete.")
        return

    backup = d.parent / f"quarantine_no_p1_actions_{datetime.date.today():%Y%m%d}"
    backup.mkdir(exist_ok=True)
    for f, _ in dead_p1:
        shutil.copy2(f, backup / f.name)
    for f, _ in dead_p1:
        f.unlink()
    print(f"\nBacked up {len(dead_p1)} file(s) to {backup}")
    print(f"Deleted {len(dead_p1)} file(s) from {d}")
    print("Note: rows remain in recordings/game_records.db -- not touched.")


if __name__ == "__main__":
    main()
