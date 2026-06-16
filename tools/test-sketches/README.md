# Test sketches

Tiny Arduino sketches for exercising the bridge end-to-end. Each subfolder is a
self-contained sketch with its own README — flash it to a bridged slot and confirm
serial flows both ways.

| Sketch | What it's for |
|---|---|
| [`heartbeat/`](heartbeat/) | Prints a `TICK:` once a second and echoes input as `ECHO:` — verifies both serial directions and proves which board/slot booted a flash (via an editable per-board banner). Includes the bench board inventory. |

Build artifacts (`build/`, `build-*/`) are gitignored; the sketch **source** is tracked.
