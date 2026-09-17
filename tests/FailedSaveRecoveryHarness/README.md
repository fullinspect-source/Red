# Failed-save recovery production harness

Run from repository root:

```sh
dotnet run --project tests/FailedSaveRecoveryHarness -r osx-arm64
```

If only .NET 10 is installed, prefix with `DOTNET_ROLL_FORWARD=Major`.

Links the actual production surgical saver, atomic writer, recovery service, models, and AppIdentity. Uses a unique temporary recovery root and synthetic reports only; cleans its directory afterward. Injects error 1175 through the production atomic writer's per-call replacement seam, not a reimplementation of its retry logic.

Checks full candidate byte equality; unknown fields, photos, attachments, metadata and result preservation; exact surfaced recovery path and original exception chain; unchanged source JSON, expected saved bytes and source ownership; deduplication of unchanged candidates after fresh byte verification; unique snapshots for changed candidates and missing/altered recovery files; retry success without an extra snapshot; local recovery I/O and pre/post-publish verification failures; invalid JSON rejection; Save As conflict safety.

This is not an autosave or automatic reopen feature. Recovery is attempted only after the main atomic write throws, and a verified recovery never converts that failed save into success. Unverified final snapshots are not advertised as successful and are left available for manual support analysis.
