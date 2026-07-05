# Copilot Instructions for APSharp

## Build, test, and lint

### Build

```powershell
dotnet build
```

### Tests

There are currently no automated test projects in this repository, so there is no single-test or full-test `dotnet test` workflow to use. The only checked-in smoke-test helper is:

```powershell
.\test-startup.ps1
```

That script starts the app and probes `GET /live`.

### Lint

There is no separate lint configuration or lint command checked into this repository.

## High-level architecture

This repository is a single ASP.NET Core 8 minimal-API server. `Program.cs` is the composition root: it loads environment-driven configuration, initializes the SQLite-backed `Database`, seeds shared globals in `AppState`, configures session/JWT/auth middleware, and exposes both browser-oriented routes and ActivityPub/OAuth/media endpoints in one process.

The core model is built around `ActivityObject`, which is the generic wrapper for ActivityStreams JSON. It knows how to load local objects from SQLite, fetch remote objects over HTTP, cache remote data in `remotecache`, expand objects for API output, and enforce read/write checks based on owner and addressee state. `FetchOptions` carries the request subject, per-request cache, and timing counters through this layer, so reads are permission-aware and reuse already-fetched objects.

`Activity` applies side effects for local activities and handles outbound federation via `Distribute()`. `RemoteActivity` applies inbound federated activities after HTTP Signature validation in the inbox route. `Collection` is the shared abstraction for inbox/outbox/followers/following/likes-style collections, including paged collections. `User` provisions local actors, creates their standard collections, hashes passwords, and drives the common local write flow used by outbox-style POSTs.

Persistence is intentionally simple: SQLite stores users, local objects, addressees, uploads, server keys, remote cache entries, and remote-failure backoff state; media bytes are written to disk under `AppState.UploadDir`. The server also runs startup fixups and periodic maintenance tasks directly from `Program.cs` rather than through a separate background worker.

## Key conventions

- Treat `ActivityObject` as the canonical abstraction for ActivityStreams data. Prefer `Helpers.ToId`, `ActivityObject.Get`, `Expanded()`, `Compressed()`, and `CopyAddresseeProps()` instead of hand-rolling JSON/id conversion logic.
- The same logical object is represented in two forms: compressed storage usually keeps related objects as IDs, while API responses typically use `Expanded()`/`Brief()` output. Be careful not to assume a property is always fully expanded.
- `FetchOptions.Subject` is important. Remote fetch caching, permission checks, and some delivery behavior depend on the acting subject, so pass through the current owner/user context when traversing objects and collections.
- Local write flows normalize payloads before saving. Plain objects are commonly wrapped into a `Create` activity, duck-typed bodies may gain `"Activity"`/`"Object"` type markers, and IDs are usually generated with `ActivityObject.MakeId(...)` under `AppState.Origin`.
- The common local activity pipeline is: build/normalize JSON -> `Apply()` -> `Save()` -> prepend to outbox and inbox -> fire-and-forget `Distribute()`. Keep that sequence intact when adding new local write paths.
- Inbox handling distinguishes local and remote actors by route and auth style: local outbox writes use bearer JWTs, while remote inbox writes require `Signature` header validation through `HttpSignature`.
- Access control is addressee-based, not just owner-based. `CanRead()` checks public addressing, direct addressing, collection membership, and block lists; do not bypass those helpers when serving objects or uploads.
- This codebase uses `AppState` as shared process-wide state for origin, DB handle, logger, upload directory, and blocked domains. New lower-level helpers typically read from `AppState` rather than receiving those dependencies explicitly.
