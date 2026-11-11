#!/usr/bin/env python3
"""Run the late-run decision fixture (saved deep-run positions) through a built CombatSolver DLL.

Each position is a native state request that recreates a deep act-2 fight taken from the live
CombatSolver logs (24-27 card Ironclad deck, Decimillipede elite, stacked combat powers). The
harness autoplays the position with the fair v2 planner, so every decision runs the real
multi-sample comparison search.

Sets live in sets/tune.json and sets/holdout.json. The held-out set must not be used to pick
thresholds, candidates or settings; it is only scored.

Examples:
    python3 tools/search/OfflineSearchHarness/late-run/run_late_run.py \
        --set holdout --dll .local/late-fixture/bin/head/CombatSolver.dll \
        --out .local/late-fixture/bench/head --workers 4
    python3 .../run_late_run.py --compare .local/late-fixture/bench/head \
        .local/late-fixture/bench/bounded
"""
import argparse
import concurrent.futures
import json
import os
import pathlib
import statistics
import subprocess
import sys
import time

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[3]
HARNESS = ROOT / ".local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll"


def load_set(name):
    path = HERE / "sets" / (name + ".json")
    spec = json.loads(path.read_text())
    return [HERE / "positions" / entry for entry in spec["positions"]]


def count_invalid(out_dir):
    total = 0
    for log in (out_dir / "logs").glob("*/process.jsonl"):
        for line in log.read_text().splitlines():
            if not line.strip():
                continue
            if "FAIR_CANDIDATE_INVALID" in json.loads(line)["Message"]:
                total += 1
    return total


def run_one(position, dll, out_root, budget_ms, speed, decision_turns, timeout):
    label = position.stem
    out_dir = out_root / label
    out_dir.mkdir(parents=True, exist_ok=True)
    state = json.loads(position.read_text())
    turns = int(state.get("decisionTurns", decision_turns))
    stop_turn = int(state.get("initialPlayerTurnNumber") or 1) + turns
    env = dict(
        os.environ,
        OFFLINE_HARNESS_COMBATSOLVER_DLL=str(pathlib.Path(dll).resolve()),
        OFFLINE_HARNESS_FAIR_PLAY="v2",
        OFFLINE_HARNESS_FAIR_SPEED=speed,
        OFFLINE_HARNESS_FAIR_STOP_TURN=str(stop_turn),
    )
    args = [
        "dotnet", str(HARNESS),
        "--request", str(position),
        "--label", label,
        "--out", str(out_dir),
        "--milestone", "M1",
        "--budget-ms", str(budget_ms),
        "--dop", "1",
    ]
    started = time.time()
    with open(out_dir / "run.log", "wb") as log:
        process = subprocess.run(args, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
    result_path = out_dir / "fair-play.json"
    if not result_path.exists():
        return {"position": label, "status": "error", "exitCode": process.returncode,
                "seconds": round(time.time() - started, 1)}
    report = json.loads(result_path.read_text())
    decisions = report.get("decisions", [])
    solves = sum(d.get("Solves", 0) for d in decisions)
    invalid = count_invalid(out_dir)
    return {
        "position": label,
        "status": "ok",
        "seconds": round(time.time() - started, 1),
        "outcome": report.get("outcome"),
        "turns": report.get("turns"),
        "startHp": report.get("startHp"),
        "endHp": report.get("endHp"),
        "hpLost": report.get("hpLost"),
        "decisions": len(decisions),
        "solves": solves,
        "invalidCells": invalid,
        "invalidRate": round(invalid / solves, 4) if solves else None,
        "decisionMs": [round(d["Milliseconds"], 1) for d in decisions],
        "ownPhaseMs": [d.get("OwnPhaseMs") for d in decisions],
        "slowestSolveMs": [d.get("SlowestSolveMs") for d in decisions],
        "safetyStops": sum(d.get("SafetyStops", 0) for d in decisions),
    }


def summarize(rows):
    ok = [row for row in rows if row["status"] == "ok"]
    times = [ms for row in ok for ms in row["decisionMs"]]
    solves = sum(row["solves"] for row in ok)
    invalid = sum(row["invalidCells"] for row in ok)
    return {
        "positions": len(rows),
        "played": len(ok),
        "errors": [row["position"] for row in rows if row["status"] != "ok"],
        "decisions": len(times),
        "solves": solves,
        "invalidCells": invalid,
        "invalidRate": round(invalid / solves, 4) if solves else None,
        "meanDecisionMs": round(statistics.mean(times), 1) if times else None,
        "p50DecisionMs": round(sorted(times)[int(0.5 * (len(times) - 1))], 1) if times else None,
        "p90DecisionMs": round(sorted(times)[int(0.9 * (len(times) - 1))], 1) if times else None,
        "maxDecisionMs": round(max(times), 1) if times else None,
        "meanHpLost": round(statistics.mean(row["hpLost"] for row in ok), 2) if ok else None,
        "totalHpLost": sum(row["hpLost"] for row in ok),
        "safetyStops": sum(row["safetyStops"] for row in ok),
    }


def compare(baseline, candidate):
    def load(directory):
        summary = json.loads((pathlib.Path(directory) / "summary.json").read_text())
        return summary, {row["position"]: row for row in summary["rows"]}

    left, left_rows = load(baseline)
    right, right_rows = load(candidate)
    print(f"{'position':28} {'head ms':>9} {'cand ms':>9} {'head hp':>8} {'cand hp':>8} {'invalid':>14}")
    for name in sorted(left_rows):
        a, b = left_rows[name], right_rows.get(name)
        if not b:
            continue
        print(f"{name:28} {max(a['decisionMs']):9.0f} {max(b['decisionMs']):9.0f} "
              f"{a['hpLost']:8d} {b['hpLost']:8d} {a['invalidCells']:6d}/{b['invalidCells']:<6d}")
    print()
    print(f"head     {json.dumps(left['summary'])}")
    print(f"candidate{json.dumps(right['summary'])}")
    print(f"delta    maxDecisionMs {right['summary']['maxDecisionMs'] - left['summary']['maxDecisionMs']:+.0f} "
          f"meanDecisionMs {right['summary']['meanDecisionMs'] - left['summary']['meanDecisionMs']:+.0f} "
          f"totalHpLost {right['summary']['totalHpLost'] - left['summary']['totalHpLost']:+d} "
          f"invalid {right['summary']['invalidCells'] - left['summary']['invalidCells']:+d}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--set", default="holdout")
    parser.add_argument("--dll", default=str(ROOT / ".godot/mono/temp/bin/Release/CombatSolver.dll"))
    parser.add_argument("--out")
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--budget-ms", type=int, default=5000, help="soft solve budget per sample")
    parser.add_argument("--speed", default="Normal", choices=["Fast", "Normal", "Thorough"])
    parser.add_argument("--decision-turns", type=int, default=2)
    parser.add_argument("--timeout", type=int, default=1800)
    parser.add_argument("--compare", nargs=2, metavar=("BASELINE", "CANDIDATE"))
    args = parser.parse_args()
    if args.compare:
        compare(*args.compare)
        return
    if not args.out:
        parser.error("--out is required unless --compare is used")
    if not HARNESS.exists():
        sys.exit(f"missing harness: {HARNESS} (build it with: dotnet build tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release)")
    positions = load_set(args.set)
    out_root = pathlib.Path(args.out)
    out_root.mkdir(parents=True, exist_ok=True)
    started = time.time()
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = [pool.submit(run_one, position, args.dll, out_root, args.budget_ms, args.speed,
                               args.decision_turns, args.timeout) for position in positions]
        rows = [future.result() for future in futures]
    summary = summarize(rows)
    payload = {
        "set": args.set,
        "dll": str(pathlib.Path(args.dll).resolve()),
        "speed": args.speed,
        "budgetMs": args.budget_ms,
        "decisionTurns": args.decision_turns,
        "workers": args.workers,
        "wallSeconds": round(time.time() - started, 1),
        "summary": summary,
        "rows": rows,
    }
    (out_root / "summary.json").write_text(json.dumps(payload, indent=2) + "\n")
    for row in rows:
        if row["status"] == "ok":
            print(f"{row['position']:28} decisions={row['decisions']:2d} max_ms={max(row['decisionMs']):7.0f} "
                  f"hp_lost={row['hpLost']:2d} invalid={row['invalidCells']}/{row['solves']}")
        else:
            print(f"{row['position']:28} ERROR exit={row['exitCode']}")
    print(json.dumps(summary))


if __name__ == "__main__":
    main()
