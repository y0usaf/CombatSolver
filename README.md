# Combat Solver, fair fork

This mod plans Slay the Spire 2 single-player fights using only what the player can see.

It is a fork of [Combat Solver](https://github.com/Torch1230/CombatSolver) (branch `fair-mode`, version `0.50.1+fair.1`, mod id still `CombatSolver`). Upstream copies the game's hidden RNG, draw order and enemy rolls into its search. This fork keeps the visible state and resamples everything hidden before each decision (`src/Runtime/FairObservation.cs`). The full upstream guide, in Chinese, is the README on `main`.

## What the solver sees

- **Kept:** hand, discard, exhaust, draw pile contents, draw pile positions the player saw set (forgotten on a shuffle or random insert), HP, block, energy, stars, gold, powers, orbs, relics and their counters, potions, enemy intents and move history.
- **Resampled each decision:** every RNG stream, the unknown part of the draw order, and future enemy moves.
- **Turned off:** the potion reward forecast and the pre-combat forecast API.

After any action that rolls RNG or draws a card, the solver plans again from what is now visible.

## How it decides

Each decision draws several possible hidden worlds, plans in each, and plays the next few actions that score best across all of them. The samples are seeded from the visible state, so the same position always gives the same decision. Potions always use the Smart policy.

## Install

Needs Slay the Spire 2 `0.111.0` or later and [RitsuLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3747602295) `0.6.0` or later. Put these files in the game's `mods/CombatSolver/`:

```
CombatSolver.dll
CombatSolver.json
CombatSolver.MemoryCleaner.exe
LICENSE
THIRD_PARTY_NOTICES.md
```

Enable RitsuLib and Combat Solver, then start a single-player fight. The route panel appears once the turn starts. **Play turn** runs one turn; **Auto: On** plays the fight. The UI is in Chinese when the game is, English otherwise.

## Settings

Settings live in `combat_solver_settings.json` and in the in-game Settings panel. The one search setting is **Calculation speed** (`speed`):

| Speed | Samples | Candidates | Beam | Nodes | Time per solve |
|---|---:|---:|---:|---:|---:|
| `Fast` | 8 | 3 | 16 | 800 | 2.5 s |
| `Normal` (default) | 12 | 4 | 20 | 1,500 | 5 s |
| `Thorough` | 16 | 5 | 24 | 2,500 | 10 s |

Online statistics are off unless `fairForkOnlineStatisticsEnabled` is true. Older upstream keys (presets, budgets, parallelism, potion and growth strategies) are ignored and dropped on the next save.

## Build

Needs the .NET 9 SDK and the game installed through Steam.

```
./tools/build/build-local-stack.sh --configuration Release
pwsh -NoProfile -File tools/build/build-local-stack.ps1 -Configuration Release    # Windows
```

The build finds Steam in its usual places. If it can't, copy `local.props.example` to `local.props` and set `SteamRoot`.

## Check fairness

The offline harness plays fights without opening the game:

```
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release
OFFLINE_HARNESS_FAIR_OBSERVATION_CHECKS=1 dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --character SILENT --encounter KNIGHTS_ELITE --milestone M1 --budget-ms 120000 --beam 20 --nodes 1500 --dop 1 --out .local/fair/check
```

It builds a second world with different hidden state and asserts that both give the same decisions and that no search touched the live RNG.

To benchmark whole fights against the real hidden state:

```
python3 -B tools/search/OfflineSearchHarness/run_fair_benchmark.py --set bench --workers 4 \
  --variant '{"name":"v2-normal","mode":"v2","env":{"OFFLINE_HARNESS_FAIR_SPEED":"Normal"}}'
```

Results go to `.local/fair-bench/<set>/<variant>/summary.json`. Mode `clairvoyant` plays with the hidden state visible, as an upper bound.

## Harness environment

| Variable | Effect |
|---|---|
| `OFFLINE_HARNESS_FAIR_OBSERVATION_CHECKS=1` | run the fairness assertions |
| `OFFLINE_HARNESS_FAIR_EXTRA_CARDS=A,B,...` | add random-effect cards to the opening hand |
| `OFFLINE_HARNESS_FAIR_SPEED` | `Fast`, `Normal` or `Thorough` |
| `OFFLINE_HARNESS_FAIR_DETERMINISM=1` | repeat each decision at parallelism 3 and count mismatches |

## License and credit

MIT ([LICENSE](LICENSE)). The combat simulation core adapts parts of [Random Foreseer](https://github.com/hotwords123/StS2.RandomForeseer) by hotwords123, also MIT; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), which ships with every release.
