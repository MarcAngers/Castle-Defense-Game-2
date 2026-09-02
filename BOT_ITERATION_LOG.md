# BOT_ITERATION_LOG.md

Append-only ledger of `HeuristicBot` iterations. **Read this instead of a previous
session's transcript.** One entry per run, whether it was kept or reverted — a rejected
result is worth as much as an accepted one and costs a full run to rediscover.

Format, kept deliberately short so a session can read the whole file cheaply:

```
## <n>. <FlagName> — KEPT | REVERTED | INCONCLUSIVE
date · mechanism in one line
predicted: <what should move, what must not>
measured:  <the numbers>
verdict:   <why kept or reverted>
```

Acceptance rule: the predicted rows moved in the predicted direction, nothing unpredicted
regressed past noise, and the mechanism was stated **before** the run. A win with no stated
mechanism is rejected. See `BOT_BACKLOG.md` for why.

---
