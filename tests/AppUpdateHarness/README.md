# Production updater regression harness

Links the actual Services/AppUpdateService.cs; only AppIdentity and HTTP/installer boundaries are faked. No network requests or installer processes are launched.

Run: `dotnet run --project tests/AppUpdateHarness/AppUpdateHarness.csproj`

On hosts with only newer .NET runtimes: `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/AppUpdateHarness/AppUpdateHarness.csproj`

Covers HTTP classification/retry limits, transport recovery, header/body deadlines, retry of partial downloads from byte zero, metadata and ZIP validation, marker timing/throttle/force, cancellation, launch failure, and the shared data-updater helper. Windows shell/UAC behavior still requires Windows smoke testing.

Non-cooperative metadata and response-body streams are explicitly tested, along with
late preparation completion and cancellation between preparation and installer handoff.
The companion UpdateUiHarness links the actual coordinator and exercises terminal UI
callbacks for hung app checks, hung stats, both hung, synchronous stalls, and late faults.
Manual preparation has a shared two-minute deadline. Installer launch is synchronous,
owned by the awaiting UI after save/freeze guards, and is never abandoned on a timeout.

To run against .NET 8 without installing a global runtime on an Apple Silicon Mac:
`dotnet run --project tests/AppUpdateHarness -r osx-arm64 --self-contained true`
