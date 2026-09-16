using System.Reflection;
using System.Runtime.Loader;
using InspectionEditor.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

internal static class Program
{
    private static int passed;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Test(string name, Action action)
    {
        action();
        passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static byte[] Fixture(int width, int height, bool png = false, ushort orientation = 1)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(80, 80, 80));
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);
        using var output = new MemoryStream();
        if (png) image.Save(output, new PngEncoder());
        else image.Save(output, new JpegEncoder { Quality = 95 });
        return output.ToArray();
    }

    private static byte[] NoFallback(byte[] _) => throw new Exception("Unexpected fallback");

    private static void VerifyPhoto(byte[] input, int width, int height)
    {
        byte[] result = PhotoProcessingService.Process(input, NoFallback);
        Check(result[0] == 0xff && result[1] == 0xd8, "Output is not JPEG");
        using var decoded = Image.Load<Rgba32>(result);
        Check(decoded.Width == width && decoded.Height == height,
            $"Unexpected dimensions {decoded.Width}x{decoded.Height}");
        Check(Math.Abs(decoded[width / 2, height / 2].R - 100) <= 3, "25% brightness lift changed");
    }

    public static int Main()
    {
        try
        {
            Test("small JPEG: brightness, JPEG output, no upscale", () => VerifyPhoto(Fixture(320, 240), 320, 240));
            Test("large landscape constrained to 1984x1116", () => VerifyPhoto(Fixture(3968, 2232), 1984, 1116));
            Test("large portrait retains aspect ratio", () => VerifyPhoto(Fixture(2232, 3968), 628, 1116));
            Test("PNG converted to JPEG", () => VerifyPhoto(Fixture(400, 300, png: true), 400, 300));
            Test("EXIF orientation normalized without double rotation", () =>
            {
                var output = PhotoProcessingService.Process(Fixture(400, 300, orientation: 6), NoFallback);
                using var decoded = Image.Load<Rgba32>(output);
                Check(decoded.Width == 300 && decoded.Height == 400, "EXIF rotation ignored");
                if (decoded.Metadata.ExifProfile is { } profile && profile.TryGetValue(ExifTag.Orientation, out var orientation))
                    Check(orientation.Value == 1, "Stale orientation in output");
            });
            Test("corrupt bytes rejected without fallback", () => RejectCorrupt(new byte[2048]));
            Test("truncated JPEG rejected without fallback", () => RejectCorrupt(Fixture(320, 240)[..20]));
            Test("empty bytes rejected without fallback", () => RejectCorrupt(Array.Empty<byte>()));
            Test("core has NoInlining JIT boundary", () =>
            {
                var core = typeof(PhotoProcessingService).GetMethod("EnhanceCore", BindingFlags.NonPublic | BindingFlags.Static)!;
                Check((core.GetMethodImplementationFlags() & MethodImplAttributes.NoInlining) != 0, "NoInlining missing");
            });
            Test("isolated missing ImageSharp invokes fallback with original photo", MissingAssembly);
            Test("isolated missing ImageSharp does not swallow fallback decoder errors", MissingAssemblyCorrupt);
            Console.WriteLine($"All {passed} photo processing tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RejectCorrupt(byte[] input)
    {
        int fallbackCalls = 0;
        Exception? failure = null;
        try { PhotoProcessingService.Process(input, bytes => { fallbackCalls++; return bytes; }); }
        catch (Exception ex) { failure = ex; }
        Check(failure != null, "Corrupt photo accepted");
        Check(fallbackCalls == 0, "Decoder failure incorrectly routed to fallback");
    }

    private static void MissingAssembly()
    {
        var context = new WithoutImageSharp();
        try
        {
            var process = LoadProcess(context);
            byte[] input = Fixture(80, 60);
            byte[] fallbackJpeg = Fixture(40, 30);
            int calls = 0;
            Func<byte[], byte[]> fallback = bytes =>
            {
                Check(ReferenceEquals(bytes, input), "Fallback did not receive original input");
                calls++;
                return fallbackJpeg;
            };
            var result = (byte[])process.Invoke(null, new object[] { input, fallback })!;
            Check(context.BlockedLoads > 0, "ImageSharp was not actually blocked");
            Check(!context.Assemblies.Any(a => a.GetName().Name == "SixLabors.ImageSharp"), "ImageSharp leaked into isolated context");
            Check(calls == 1 && ReferenceEquals(result, fallbackJpeg), "Fallback result was not used");
            using var decoded = Image.Load(result);
            Check(decoded.Width == 40 && decoded.Height == 30, "Fallback JPEG invalid");
        }
        finally { context.Unload(); }
    }

    private static void MissingAssemblyCorrupt()
    {
        var context = new WithoutImageSharp();
        try
        {
            var process = LoadProcess(context);
            var expected = new InvalidDataException("Fallback decoder rejected corrupt photo");
            Func<byte[], byte[]> fallback = _ => throw expected;
            try
            {
                process.Invoke(null, new object[] { new byte[2048], fallback });
                throw new Exception("Fallback failure was swallowed");
            }
            catch (TargetInvocationException ex)
            {
                Check(ReferenceEquals(ex.InnerException, expected), "Original fallback failure was lost");
                Check(context.BlockedLoads > 0, "ImageSharp was not actually blocked");
            }
        }
        finally { context.Unload(); }
    }

    private static MethodInfo LoadProcess(WithoutImageSharp context)
    {
        // Load the compiled, linked PRODUCTION service again, not a test reimplementation.
        var assembly = context.LoadFromAssemblyPath(typeof(PhotoProcessingService).Assembly.Location);
        return assembly.GetType("InspectionEditor.Services.PhotoProcessingService")!.GetMethod("Process")!;
    }

    private sealed class WithoutImageSharp : AssemblyLoadContext
    {
        public int BlockedLoads { get; private set; }
        public WithoutImageSharp() : base(isCollectible: true) { }
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "SixLabors.ImageSharp")
            {
                BlockedLoads++;
                // Throw rather than returning null, which could reuse the default context's DLL.
                throw new FileNotFoundException("Deliberately absent image dependency", name.FullName);
            }
            return null;
        }
    }
}
