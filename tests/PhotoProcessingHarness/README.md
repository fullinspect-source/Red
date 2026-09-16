# Photo processing regression harness

Run from the repository root:

```sh
dotnet run --project tests/PhotoProcessingHarness/PhotoProcessingHarness.csproj -c Release
```

The harness links `Services/PhotoProcessingService.cs` directly. It targets .NET 8
and permits major runtime roll-forward for machines with only a newer runtime.
It uses the production ImageSharp package version, 3.1.12.

Eleven checks cover JPEG brightness and no-upscale behavior, landscape/portrait
bounds, PNG conversion, EXIF orientation normalization, corrupt/truncated/empty
input rejection without fallback, the NoInlining boundary, and dependency failure.
The missing-dependency checks reload the compiled production service in a
collectible AssemblyLoadContext which throws for ImageSharp resolution, even when
the normal context already has ImageSharp loaded. They verify fallback invocation,
original input/result propagation, and fallback error propagation.

The fallback in these cross-platform tests is an injected delegate. The Windows
WPF decoder/resize/JPEG implementation in MainWindow is NOT executed by this
harness. Camera capture, Windows codecs, mirrored EXIF cases and device integration
still require a Windows smoke test.

The main application project must exclude this harness (including generated obj
sources) with:

```xml
<Compile Remove="tests\PhotoProcessingHarness\**\*.cs" />
```

Verified on macOS arm64 using SDK 10.0.201/runtime 10.0.5: all 11 tests passed in both Release and Debug.

## Verify the actual release DLL

After building the harness, replace its dependency with the managed DLL from the
publish directory or extracted release ZIP, then execute the already-built DLL.
Do not use `dotnet run` without `--no-build` after copying, because building can
restore the package copy over the release copy.

```sh
dotnet build tests/PhotoProcessingHarness/PhotoProcessingHarness.csproj -c Release
cp /absolute/path/to/publish/SixLabors.ImageSharp.dll tests/PhotoProcessingHarness/bin/Release/net8.0/SixLabors.ImageSharp.dll
dotnet tests/PhotoProcessingHarness/bin/Release/net8.0/PhotoProcessingHarness.dll
```

This checks processing against the actual shipped managed dependency. It is not a
Windows device/codec test or proof of the affected machine's installed files.
ImageSharp package version 3.1.12 legitimately has assembly version 3.0.0.0; the
error's assembly version alone does not establish a package-version mismatch.
