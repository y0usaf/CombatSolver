# Combat Solver, fair fork

A fork of [Combat Solver](https://github.com/Torch1230/CombatSolver) by Torch1230 that makes it fair: the solver plans only from what the player can see, and resamples hidden RNG, draw order and enemy moves before each decision.

Auto Mode also works in co-op, and only ever plays your side. It has two paces, chosen with the top-bar pace button (`Free play` / `Wait`) or `Ctrl+F11`, and the choice is remembered between games:

- **Free play** (default) plays your hand at its own pace without waiting for teammates. When the board changes under it — a teammate plays something, an enemy dies — it drops the stale plan and replans immediately.
- **Wait** keeps the older behaviour: it waits until every teammate has ended their turn and the action queue is idle, then solves and plays your hand.

The **Run This Turn** button and `Ctrl+F8` solve and play the current turn immediately, then stop Auto Mode; use it when every player is waiting for someone else. Solo play is unchanged. `Ctrl+F10` shows solver status.

`Ctrl+F9` opens a small solver menu (mouse or keyboard: arrow keys, Enter or `1`–`4`, Left/Right on Speed, Esc or `Ctrl+F9` again closes):

1. **Auto Mode** on/off.
2. **Free play** on/off (off is the Wait pace).
3. **Run This Turn**, same as `Ctrl+F8`; closes the menu.
4. **Speed**: Game setting → Normal → Fast → Instant. Normal and Fast apply while the solver plays cards; Instant resolves solo combat instantly (player and enemy actions) and leaves the game's own speed setting untouched. Instant has no effect in online co-op.

All the solver work is upstream's. The combat simulation core adapts [Random Foreseer](https://github.com/hotwords123/StS2.RandomForeseer) by hotwords123. MIT; see [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
