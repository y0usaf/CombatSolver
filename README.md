# Combat Solver, fair fork

A fork of [Combat Solver](https://github.com/Torch1230/CombatSolver) by Torch1230 that makes it fair: the solver plans only from what the player can see, and resamples hidden RNG, draw order and enemy moves before each decision.

Auto Mode also works in co-op. It plays only your side and goes last: it waits until every teammate has ended their turn and the action queue is idle, then solves and plays your hand. The **Run This Turn** button and `Ctrl+F8` solve and play the current turn immediately, then stop Auto Mode; use it when every player is waiting for someone else. Solo play is unchanged. `Ctrl+F9` toggles Auto Mode and `Ctrl+F10` shows solver status.

All the solver work is upstream's. The combat simulation core adapts [Random Foreseer](https://github.com/hotwords123/StS2.RandomForeseer) by hotwords123. MIT; see [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
