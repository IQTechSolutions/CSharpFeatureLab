# C# Feature Lab

Companion code for Ivan Rossouw's **C# Feature Lab** video series: beginner-friendly,
production-minded .NET features in under ten minutes.

The course grows one small team task application through vertical slices. Every episode
has a source checkpoint, runnable tests, a transcript, captions and production metadata.

## Run the current checkpoint

```powershell
dotnet restore CSharpFeatureLab.slnx
dotnet tool restore
dotnet ef database update --project src/FeatureLab.Web
dotnet test CSharpFeatureLab.slnx
dotnet run --project src/FeatureLab.Web
```

The API starts with two endpoints:

- `POST /account/register`
- `POST /account/login`
- `POST /api/work-items`
- `GET /api/work-items`

Failed deliveries persist their attempt count, earliest next eligible UTC time,
and one allow-listed failure code. The dispatcher queries only due rows in stable,
bounded order and applies deterministic exponential backoff with jitter, so the
schedule survives a process restart without storing exception text. Attempt start and
retry scheduling are separate short database updates; no transaction remains open
across provider I/O. Multiple-worker claiming and terminal retry outcomes remain later
production layers.

## Course rules

- Every episode leaves the application compiling and tested.
- User and tenant scope come from trusted server context, never request bodies.
- Work item ownership is derived from the authenticated name-identifier claim.
- Database migrations are reviewed source artifacts, not production startup side effects.
- The teaching project uses SQLite to remove setup friction; provider-specific SQL Server
  behaviour is called out and covered in the Production .NET arc.
- Examples stay independent of private product or client code.
