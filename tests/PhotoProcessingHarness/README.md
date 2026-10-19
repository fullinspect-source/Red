# Photo processing regression harness

Run from the repository root:

```sh
dotnet run --project tests/PhotoProcessingHarness/PhotoProcessingHarness.csproj -c Release
```

The harness links `Services/PhotoProcessingService.cs` and (since F-023)
`Services/WpfPhotoFallback.cs` directly. It targets .NET 8 for Windows with WPF,
because the ImageSharp-missing fallback is WPF, and permits major runtime
roll-forward. It uses the production ImageSharp package version, 3.1.12.

Fifteen checks. F-023 T1 (program/features/F-023-photo-parity, A-023-08..A-023-17,
A-023-15 is in tests/PhotoMarkupHarness): every new photo is oriented, halved to
exactly floor(W/2) x floor(H/2) (no 1984x1116 box, small images too), brightened
x1.25 (clamped), written at 72x72 dpi as JPEG quality 85 (IJG table) with no
sharpening; EXIF is kept with Orientation 1; the fallback gives the same size and
density, no brightening and no EXIF. The original checks remain: corrupt, truncated
and empty input rejected without fallback, the NoInlining boundary, and dependency
failure. The missing-dependency checks reload the compiled production service in a
collectible AssemblyLoadContext which throws for ImageSharp resolution, even when
the normal context already has ImageSharp loaded; A-023-17 runs the production WPF
fallback there.

Camera capture, mirrored EXIF cases and device integration still require a Windows
smoke test.
The main application project must exclude this harness (including generated obj
sources) with:

```xml
<Compile Remove="tests\PhotoProcessingHarness\**\*.cs" />
```

Before F-023: verified on macOS arm64 (11 tests). F-023 (2026-10-19): 15 tests pass on Windows (Debug).

## Verify the actual release DLL

After building the harness, replace its dependency with the managed DLL from the
publish directory or extracted release ZIP, then execute the already-built DLL.
Do not use `dotnet run` without `--no-build` after copying, because building can
restore the package copy over the release copy.

```sh
dotnet build tests/PhotoProcessingHarness/PhotoProcessingHarness.csproj -c Release
cp /absolute/path/to/publish/SixLabors.ImageSharp.dll tests/PhotoProcessingHarness/bin/Release/net8.0-windows/SixLabors.ImageSharp.dll
dotnet tests/PhotoProcessingHarness/bin/Release/net8.0-windows/PhotoProcessingHarness.dll
```

This checks processing against the actual shipped managed dependency. It is not a
Windows device/codec test or proof of the affected machine's installed files.
ImageSharp package version 3.1.12 legitimately has assembly version 3.0.0.0; the
error's assembly version alone does not establish a package-version mismatch.
