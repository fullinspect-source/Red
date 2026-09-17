# Production updater regression harness

Links the actual Services/AppUpdateService.cs; only AppIdentity and HTTP/installer boundaries are faked. No network requests or installer processes are launched.

Run: `dotnet run --project tests/AppUpdateHarness/AppUpdateHarness.csproj`

On hosts with only newer .NET runtimes: `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/AppUpdateHarness/AppUpdateHarness.csproj`

Covers HTTP classification/retry limits, transport recovery, header/body deadlines, retry of partial downloads from byte zero, metadata and ZIP validation, marker timing/throttle/force, cancellation, launch failure, and the shared data-updater helper. Windows shell/UAC behavior still requires Windows smoke testing.
