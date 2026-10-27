# PlayniteBridge rework (1.6.0) — hardened request pipeline

Drop-in replacement for playnite-bridge 1.5.3. Same routes, same JSON.
Only the server internals changed, to stop the flood-crash cycle where fast
phone scrolling produced hundreds of concurrent requests inside the Playnite
process and ended in the *"Extension caused an unrecoverable error → Restart
Playnite"* dialog.

## Root causes fixed

1. **Unhandled escape in the error path (the likely crasher).** When a client
   disconnected mid-request, the `catch` block itself wrote a 500 response,
   which threw again (`response already submitted`) and escaped the ThreadPool
   work item to AppDomain-unhandled. Now: disconnect-type exceptions are
   Debug-level noise, and the 500 writer is nested-guarded so it can never
   throw out of the handler.
2. **Unbounded concurrency.** Every request enumerated the game DB and read
   whole multi-MB image files on its own ThreadPool thread. Now: a
   `RequestGate` semaphore (12) bounds concurrent executions, and identical
   concurrent GETs share one execution (single-flight).
3. **Thread-unsafe DB enumeration.** List reads (`/api/games`, `/query`,
   `/stats`, missing-art) go through `SdkGate`, a single global lock.
4. **Repeated full-file cover reads.** `CoverService` caches covers in memory
   (256 MB cap, validated against last-write ticks on every hit), so art
   changes apply without restart.
5. **Oversized payloads.** `GET /api/games/{id}/cover?width=256` (clamped
   32–1024, JPEG q80; GIF/ICO/TGA always serve the original).
6. **Repeat downloads.** Strong `ETag` on every cover + `304 Not Modified`
   handling in the server.

## What did NOT change

- All routes, query params, JSON shapes, status codes, and messages.
- Auth (Bearer token), sync engine, eval, automation, settings UI.
- Existing unit tests compile unchanged (new params are optional; new
  cover overload coexists with the old one). One new fixture covers the gate.

## Build & install (on the PC)

1. Install .NET SDK, then run `.\build-release.ps1` in this folder
   (builds plugin + runs the 174 existing tests + 4 new gate tests,
   packs `PlayniteBridge_v1.6.0.pext`).
2. Close Playnite, double-click the `.pext` (or drag it onto Playnite),
   restart Playnite. Same extension Id → in-place upgrade, token kept.

## Verify

1. Phone: full scroll through the library, then `extensions.log`:
   expect no `ERROR ... HttpApiServer` lines; disconnects appear at most as
   Debug lines.
2. `curl -H "Authorization: Bearer TOKEN" "http://localhost:19821/api/games/<id>/cover?width=256"`
   → small JPEG; repeat with `-H "If-None-Match: <etag>"` → `304`, empty body.
3. Stress: scroll fast during a Playnite library import (the old crash cocktail).

## Suggested phone-app follow-up (separate)

Point cover URLs at `?width=512` and send `If-None-Match` from the cached
ETag — then re-scrolls cost zero bytes even before the disk cache is warm.
