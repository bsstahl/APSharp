# APSharp

## Getting Started

This service is a single ASP.NET Core minimal-API application. The main entry point is `Program.cs`, which acts as the composition root for the whole service: it reads configuration, initializes `AppState`, opens the SQLite `Database`, configures middleware and auth, and maps all HTTP endpoints.

## Local build and run

Build the service with:

```powershell
dotnet build
```

The simplest local run path is to force plain HTTP with environment variables:

```powershell
$env:OPP_ORIGIN='http://localhost:65380'
$env:OPP_PORT='65380'
dotnet run --project .\Fedi.csproj
```

Then verify the service is up with:

```powershell
Invoke-WebRequest -Uri 'http://localhost:65380/live' -UseBasicParsing
```

There is also a checked-in smoke-test helper:

```powershell
.\test-startup.ps1
```

If `OPP_ORIGIN` is left unset, `Program.cs` configures Kestrel for HTTPS using the configured certificate/key files instead.

## Service entry points

The main HTTP entry points are all defined in `Program.cs`:

- `GET /` and `GET /key` expose server identity.
- Session and browser flows live under `/register`, `/login`, and `/logout`.
- Discovery and OAuth routes live under `/.well-known/*` and `/endpoint/oauth/*`.
- Upload and media routes live under `/endpoint/upload` and `/uploads/{*relative}`.
- The most important ActivityPub routes are `GET /{type}/{id}` and `POST /{type}/{id}`. Those are the generic object read/write paths where most ActivityPub behavior converges.

## Where to start when analyzing the code

Start with these files in order:

1. `Program.cs` - startup, middleware, auth, and route mapping.
2. `ActivityObject.cs` - the core domain abstraction for ActivityStreams data, local DB reads, remote fetches, remote caching, expansion/compression, ownership, and permissions.
3. `Activity.cs` and `RemoteActivity.cs` - local vs. remote activity application and federation side effects.
4. `Collection.cs` and `User.cs` - collection behavior and the local actor/activity workflow.
5. `Database.cs` and `HttpSignature.cs` - persistence and inbound signature verification.

If you want the fastest path to understanding request flow, follow this path:

`Program.cs` -> `POST /{type}/{id}` -> `ActivityObject` / `Activity` / `RemoteActivity`

That route shows how the service handles outbox writes, inbox delivery, auth/signature checks, object loading, and activity application.

## Request-flow diagram

### Local outbox write

```text
HTTP request
  -> Program.cs route: POST /{type}/{id}
  -> bearer JWT/session checks
  -> User.DoActivity(...)
  -> Activity.Apply()
  -> Activity.Save()
  -> prepend to outbox + inbox collections
  -> Activity.Distribute()
  -> local inbox updates or signed remote delivery
```

### Remote inbox delivery

```text
HTTP request
  -> Program.cs route: POST /{type}/{id}
  -> Signature header validation with HttpSignature
  -> RemoteActivity.Apply(...)
  -> ActivityObject/Collection reads and permission checks
  -> cache/update inbox state
  -> 202 Accepted
```
