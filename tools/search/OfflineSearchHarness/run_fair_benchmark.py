#!/usr/bin/env python3
"""Fair-mode autobattler benchmark: plays whole fights in the offline harness against the real hidden state."""
import argparse, asyncio, itertools, json, os, statistics, sys, time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
HARNESS = ROOT / ".local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll"

SETS = {
    "pool": {
        "characters": ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"],
        "encounters": ["CUBEX_CONSTRUCT_NORMAL", "FLYCONID_NORMAL", "FOGMOG_NORMAL", "INKLETS_NORMAL", "MAWLER_NORMAL",
                       "NIBBITS_NORMAL", "RUBY_RAIDERS_NORMAL", "SLIMES_NORMAL", "SLITHERING_STRANGLER_NORMAL",
                       "SNAPPING_JAXFRUIT_NORMAL", "VINE_SHAMBLER_NORMAL", "BYGONE_EFFIGY_ELITE", "BYRDONIS_ELITE",
                       "PHROG_PARASITE_ELITE", "CEREMONIAL_BEAST_BOSS", "THE_KIN_BOSS", "VANTOM_BOSS"],
        "ascensions": [0],
        "seeds": ["FB1"],
    },
    "bench": {
        "characters": ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"],
        "encounters": ["NIBBITS_NORMAL", "SNAPPING_JAXFRUIT_NORMAL", "RUBY_RAIDERS_NORMAL",
                       "BYGONE_EFFIGY_ELITE", "BYRDONIS_ELITE", "PHROG_PARASITE_ELITE"],
        "ascensions": [0, 10],
        "seeds": ["FB2", "FB3"],
    },
    "boss": {
        "characters": ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"],
        "encounters": ["CEREMONIAL_BEAST_BOSS", "THE_KIN_BOSS", "VANTOM_BOSS"],
        "ascensions": [0],
        "seeds": ["FB2"],
    },
}


def jobs_for(spec):
    if "jobs" in spec:
        return [tuple(job) for job in spec["jobs"]]
    return list(itertools.product(spec["characters"], spec["encounters"], spec["ascensions"], spec["seeds"]))


async def run_job(semaphore, out_dir, variant, job, timeout):
    character, encounter, ascension, seed = job
    label = f"{character}-{encounter}-A{ascension}-{seed}"
    job_dir = out_dir / label
    result_path = job_dir / "fair-play.json"
    if result_path.exists():
        return json.loads(result_path.read_text())
    async with semaphore:
        job_dir.mkdir(parents=True, exist_ok=True)
        env = dict(os.environ, OFFLINE_HARNESS_FAIR_PLAY=variant["mode"], **{k: str(v) for k, v in variant.get("env", {}).items()})
        args = ["dotnet", str(HARNESS), "--character", character, "--encounter", encounter, "--seed", seed,
                "--ascension", str(ascension), "--label", label, "--out", str(job_dir), "--milestone", "M1",
                "--budget-ms", str(variant.get("budgetMs", 60000)), "--beam", str(variant.get("beam", 20)),
                "--nodes", str(variant.get("nodes", 1500)), "--dop", "1"]
        with open(job_dir / "run.log", "wb") as log:
            process = await asyncio.create_subprocess_exec(*args, cwd=ROOT, env=env, stdout=log, stderr=asyncio.subprocess.STDOUT)
            try:
                await asyncio.wait_for(process.wait(), timeout)
            except asyncio.TimeoutError:
                process.kill()
                await process.wait()
        if result_path.exists():
            return json.loads(result_path.read_text())
        return {"label": label, "character": character, "encounter": encounter, "ascension": ascension, "seed": seed,
                "outcome": "error", "exitCode": process.returncode}


def summarize(results):
    played = [r for r in results if r["outcome"] in ("victory", "defeat", "timeout")]
    errors = [r["label"] for r in results if r["outcome"] == "error"]
    decisions = [d["Milliseconds"] for r in played for d in r.get("decisions", [])]
    return {
        "fights": len(results),
        "played": len(played),
        "errors": errors,
        "winRate": round(sum(r["outcome"] == "victory" for r in played) / max(1, len(played)), 4),
        "deathRate": round(sum(r["outcome"] == "defeat" for r in played) / max(1, len(played)), 4),
        "meanHpLost": round(statistics.mean(r["hpLost"] for r in played), 3) if played else None,
        "meanDecisionMs": round(statistics.mean(decisions), 1) if decisions else None,
        "p90DecisionMs": round(sorted(decisions)[int(0.9 * (len(decisions) - 1))], 1) if decisions else None,
        "maxDecisionMs": round(max(decisions), 1) if decisions else None,
        "decisions": len(decisions),
    }


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--set", required=True, help="named set or path to a JSON set")
    parser.add_argument("--variant", required=True, help="JSON: {name, mode, env, beam, nodes, budgetMs}")
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--timeout", type=int, default=1800)
    parser.add_argument("--out", default=str(ROOT / ".local/fair-bench"))
    args = parser.parse_args()
    spec = SETS[args.set] if args.set in SETS else json.loads(Path(args.set).read_text())
    variant = json.loads(args.variant)
    set_name = args.set if args.set in SETS else Path(args.set).stem
    out_dir = Path(args.out) / set_name / variant["name"]
    out_dir.mkdir(parents=True, exist_ok=True)
    started = time.time()
    semaphore = asyncio.Semaphore(args.workers)
    results = await asyncio.gather(*(run_job(semaphore, out_dir, variant, job, args.timeout) for job in jobs_for(spec)))
    summary = summarize(results) | {"variant": variant, "set": set_name, "wallSeconds": round(time.time() - started, 1)}
    (out_dir / "summary.json").write_text(json.dumps({"summary": summary, "fights": [
        {k: r.get(k) for k in ("label", "character", "encounter", "ascension", "seed", "outcome", "hpLost", "turns",
                               "decisionCount", "meanDecisionMs", "selectorFallbacks")} for r in results]}, indent=2))
    print(json.dumps(summary))


if __name__ == "__main__":
    asyncio.run(main())
