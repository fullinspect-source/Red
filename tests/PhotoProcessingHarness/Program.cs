using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using InspectionEditor.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

// F-023 T1 photo processing (program/features/F-023-photo-parity/ACCEPTANCE.md A-023-08..A-023-17) plus the original
// robustness checks. Synthetic images only (flat colours and blocks), never photos.
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

    private static byte[] Fixture(int width, int height, bool png = false, ushort orientation = 1, Rgba32? fill = null,
        string? make = null, bool redBlock = false, bool halves = false)
    {
        using var image = new Image<Rgba32>(width, height, fill ?? new Rgba32(80, 80, 80));
        if (redBlock)
            for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++) image[x, y] = new Rgba32(255, 0, 0);
        if (halves)
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) image[x, y] = x < width / 2 ? new Rgba32(64, 64, 64) : new Rgba32(128, 128, 128);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);
        if (make != null) image.Metadata.ExifProfile.SetValue(ExifTag.Make, make);
        using var output = new MemoryStream();
        if (png) image.Save(output, new PngEncoder());
        else image.Save(output, new JpegEncoder { Quality = 95 });
        return Pad(output.ToArray(), png);
    }

    // Below RED's 1024-byte minimum: a JPEG comment (COM) segment or a PNG tEXt chunk pads it (ACCEPTANCE note).
    private static byte[] Pad(byte[] bytes, bool png)
    {
        if (bytes.Length >= 1024) return bytes;
        int extra = 1100 - bytes.Length;
        if (!png)
        {
            var com = new byte[4 + extra];
            com[0] = 0xFF; com[1] = 0xFE; com[2] = (byte)((extra + 2) >> 8); com[3] = (byte)((extra + 2) & 0xFF);
            for (int i = 4; i < com.Length; i++) com[i] = (byte)'x';
            return bytes[..2].Concat(com).Concat(bytes[2..]).ToArray();
        }
        // PNG: insert a tEXt chunk after IHDR (8-byte signature + 25-byte IHDR chunk); the CRC is not checked by readers
        // that ignore ancillary chunks' CRC errors, so compute it properly.
        var data = System.Text.Encoding.ASCII.GetBytes("Comment\0" + new string('x', extra));
        var type = System.Text.Encoding.ASCII.GetBytes("tEXt");
        var chunk = new List<byte>();
        chunk.AddRange(BigEndian(data.Length));
        chunk.AddRange(type);
        chunk.AddRange(data);
        chunk.AddRange(BigEndian((int)Crc32(type.Concat(data).ToArray())));
        return bytes[..33].Concat(chunk).Concat(bytes[33..]).ToArray();
    }

    private static byte[] BigEndian(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static uint Crc32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    private static byte[] NoFallback(byte[] _) => throw new Exception("Unexpected fallback");

    private static byte[] Process(byte[] input) => PhotoProcessingService.Process(input, NoFallback);

    private static Image<Rgba32> Decode(byte[] jpeg)
    {
        Check(jpeg[0] == 0xff && jpeg[1] == 0xd8, "Output is not JPEG");
        return Image.Load<Rgba32>(jpeg);
    }

    private static void Size(byte[] input, int width, int height)
    {
        using var decoded = Decode(Process(input));
        Check(decoded.Width == width && decoded.Height == height, $"Unexpected dimensions {decoded.Width}x{decoded.Height}, expected {width}x{height}");
    }

    private static bool Near(Rgba32 p, int r, int g, int b, int tolerance) =>
        Math.Abs(p.R - r) <= tolerance && Math.Abs(p.G - g) <= tolerance && Math.Abs(p.B - b) <= tolerance;

    // JFIF APP0 density: (units, x, y); units 1 = dots per inch.
    private static (int Units, int X, int Y)? Density(byte[] jpeg)
    {
        foreach (var (marker, offset, length) in Segments(jpeg))
            if (marker == 0xE0 && length >= 14 && jpeg[offset + 4] == 'J' && jpeg[offset + 5] == 'F' && jpeg[offset + 6] == 'I' && jpeg[offset + 7] == 'F')
                return (jpeg[offset + 11], jpeg[offset + 12] << 8 | jpeg[offset + 13], jpeg[offset + 14] << 8 | jpeg[offset + 15]);
        return null;
    }

    private static bool HasApp1(byte[] jpeg) => Segments(jpeg).Any(s => s.Marker == 0xE1);

    private static HashSet<int> Markers(byte[] jpeg) => Segments(jpeg).Select(s => s.Marker).ToHashSet();

    // Marker segments before the scan: (marker, offset of the 0xFF, segment length field).
    private static IEnumerable<(int Marker, int Offset, int Length)> Segments(byte[] jpeg)
    {
        int i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            int marker = jpeg[i + 1];
            int length = jpeg[i + 2] << 8 | jpeg[i + 3];
            yield return (marker, i, length);
            if (marker == 0xDA) yield break;
            i += 2 + length;
        }
    }

    private static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    };

    private static readonly int[] IjgLuminance =
    {
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99
    };

    // Luminance table 0 in natural order.
    private static int[]? LuminanceTable(byte[] jpeg)
    {
        foreach (var (marker, offset, length) in Segments(jpeg))
        {
            if (marker != 0xDB) continue;
            int p = offset + 4, end = offset + 2 + length;
            while (p < end)
            {
                int precision = jpeg[p] >> 4, id = jpeg[p] & 15;
                p++;
                var table = new int[64];
                for (int k = 0; k < 64; k++) { table[ZigZag[k]] = precision == 0 ? jpeg[p + k] : jpeg[p + 2 * k] << 8 | jpeg[p + 2 * k + 1]; }
                p += precision == 0 ? 64 : 128;
                if (id == 0) return table;
            }
        }
        return null;
    }

    private static int[] Ijg(int quality)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        return IjgLuminance.Select(b => Math.Clamp((b * scale + 50) / 100, 1, 255)).ToArray();
    }

    public static int Main()
    {
        try
        {
            // A-023-08 Landscape: halved, brightened, 72 dpi, IJG quality-85 luminance table.
            Test("A-023-08 3968x2232 → 1984x1116, centre (100,100,100) ±3, JFIF 72x72 dpi, IJG q85 luminance table", () =>
            {
                byte[] output = Process(Fixture(3968, 2232));
                using var decoded = Decode(output);
                Check(decoded.Width == 1984 && decoded.Height == 1116, $"size {decoded.Width}x{decoded.Height}");
                Check(Near(decoded[992, 558], 100, 100, 100, 3), $"centre {decoded[992, 558]}");
                Check(Density(output) == (1, 72, 72), $"density {Density(output)}");
                Check(LuminanceTable(output)?.SequenceEqual(Ijg(85)) == true, "luminance table is not IJG quality 85");
            });
            Test("A-023-09 portrait 2232x3968 → 1116x1984 (no 1116 cap)", () => Size(Fixture(2232, 3968), 1116, 1984));
            Test("A-023-10 small 320x240 JPEG → 160x120 and 400x300 PNG → JPEG 200x150", () =>
            {
                Size(Fixture(320, 240), 160, 120);
                Size(Fixture(400, 300, png: true), 200, 150);
            });
            Test("A-023-11 odd sizes round down: 4031x3023 → 2015x1511, 3x3 → 1x1", () =>
            {
                Size(Fixture(4031, 3023), 2015, 1511);
                byte[] tiny = Fixture(3, 3);
                Check(tiny.Length >= 1024, "the 3x3 fixture is not padded to 1024 bytes");
                Size(tiny, 1, 1);
            });
            Test("A-023-12 orientation 6/8/3 applied before halving; red block top-right / bottom-left / bottom-right; tag 1", () =>
            {
                foreach (var (orientation, w, h, x, y) in new[] { ((ushort)6, 150, 200, 140, 10), ((ushort)8, 150, 200, 10, 190), ((ushort)3, 200, 150, 190, 140) })
                {
                    using var decoded = Decode(Process(Fixture(400, 300, orientation: orientation, redBlock: true)));
                    Check(decoded.Width == w && decoded.Height == h, $"orientation {orientation}: {decoded.Width}x{decoded.Height}");
                    var p = decoded[x, y];
                    Check(p.R >= 200 && p.G <= 40 && p.B <= 40, $"orientation {orientation}: block pixel {p}");
                    if (decoded.Metadata.ExifProfile is { } profile && profile.TryGetValue(ExifTag.Orientation, out var tag))
                        Check(tag.Value == 1, $"orientation {orientation}: output tag {tag.Value}");
                }
            });
            Test("A-023-13 brightening is a clamped x1.25: 80 → 100 ±3, 220 → ≥252, 0 → ≤3", () =>
            {
                using var mid = Decode(Process(Fixture(400, 300, fill: new Rgba32(80, 80, 80))));
                using var high = Decode(Process(Fixture(400, 300, fill: new Rgba32(220, 220, 220))));
                using var black = Decode(Process(Fixture(400, 300, fill: new Rgba32(0, 0, 0))));
                Check(Near(mid[100, 75], 100, 100, 100, 3), $"80 → {mid[100, 75]}");
                Check(high[100, 75].R >= 252 && high[100, 75].G >= 252 && high[100, 75].B >= 252, $"220 → {high[100, 75]}");
                Check(black[100, 75].R <= 3 && black[100, 75].G <= 3 && black[100, 75].B <= 3, $"0 → {black[100, 75]}");
            });
            Test("A-023-14 no automatic sharpening: equals orient + halve + x1.25 + encode within ±3, no overshoot at the edge", () =>
            {
                byte[] input = Fixture(400, 300, halves: true);
                using var decoded = Decode(Process(input));
                using var reference = Image.Load<Rgba32>(input);
                reference.Mutate(ctx => ctx.AutoOrient());
                reference.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(200, 150), Mode = ResizeMode.Stretch }).Brightness(1.25f));
                using var refStream = new MemoryStream();
                reference.Save(refStream, new JpegEncoder { Quality = 85 });
                using var expected = Image.Load<Rgba32>(refStream.ToArray());
                int worst = 0, min = 255, max = 0;
                for (int y = 0; y < 150; y++)
                    for (int x = 0; x < 200; x++)
                    {
                        var a = decoded[x, y]; var b = expected[x, y];
                        worst = Math.Max(worst, Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B))));
                        min = Math.Min(min, a.R); max = Math.Max(max, a.R);
                    }
                Check(worst <= 3, $"largest difference {worst}");
                Check(min >= 80 - 4 && max <= 160 + 4, $"range {min}..{max} (sharpening would overshoot 80..160)");
            });
            Test("A-023-16 metadata (owner Q-A 2026-10-20, INSPECT's rule): JFIF 72x72 dpi, no APP1 (EXIF/XMP), no ICC, photo upright", () =>
            {
                // Source: Orientation 6, camera Make, capture time and a GPS position, plus an ICC profile.
                byte[] source;
                using (var image = new Image<Rgba32>(400, 300, new Rgba32(80, 80, 80)))
                {
                    for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++) image[x, y] = new Rgba32(255, 0, 0);
                    image.Metadata.ExifProfile = new ExifProfile();
                    image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
                    image.Metadata.ExifProfile.SetValue(ExifTag.Make, "TestCam");
                    image.Metadata.ExifProfile.SetValue(ExifTag.DateTimeOriginal, "2026:10:20 09:15:00");
                    image.Metadata.ExifProfile.SetValue(ExifTag.GPSLatitudeRef, "N");
                    image.Metadata.ExifProfile.SetValue(ExifTag.GPSLatitude, new[] { new Rational(30, 1), new Rational(15, 1), new Rational(0, 1) });
                    // Windows' own sRGB profile (read only), when present, so the output can be checked for an ICC segment too.
                    string icm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "sRGB Color Space Profile.icm");
                    if (File.Exists(icm))
                        image.Metadata.IccProfile = new SixLabors.ImageSharp.Metadata.Profiles.Icc.IccProfile(File.ReadAllBytes(icm));
                    using var s = new MemoryStream();
                    image.Save(s, new JpegEncoder { Quality = 95 });
                    source = Pad(s.ToArray(), false);
                }
                var sourceMarkers = Markers(source);
                Check(sourceMarkers.Contains(0xE1), "fixture lacks APP1");
                Console.WriteLine($"     (fixture ICC segment: {(sourceMarkers.Contains(0xE2) ? "yes" : "no")})");
                byte[] output = Process(source);
                var markers = Markers(output);
                Check(Density(output) == (1, 72, 72), $"density {Density(output)}");
                Check(!markers.Contains(0xE1), "an APP1 (EXIF/XMP) segment is still there");
                Check(!markers.Contains(0xE2), "an APP2 (ICC) segment is still there");
                Check(!markers.Contains(0xED), "an APP13 (IPTC) segment is still there");
                using var decoded = Decode(output);
                Check(decoded.Metadata.ExifProfile == null && decoded.Metadata.IccProfile == null && decoded.Metadata.XmpProfile == null, "metadata decoded from the output");
                Check(decoded.Width == 150 && decoded.Height == 200, $"oriented size {decoded.Width}x{decoded.Height}");
                var p = decoded[140, 10];
                Check(p.R >= 200 && p.G <= 40 && p.B <= 40, $"the red block is top-right (rotated before stripping): {p}");
            });
            Test("A-023-17 fallback without ImageSharp: 1984x1116, 72 dpi, centre (80,80,80) ±3 (no brightening), no EXIF", FallbackWithoutImageSharp);
            Test("A-023-17b WPF fallback applies EXIF orientation 6/8/3 before halving (red block top-right / bottom-left / bottom-right)", () =>
            {
                foreach (var (orientation, w, h, x, y) in new[] { ((ushort)6, 150, 200, 140, 10), ((ushort)8, 150, 200, 10, 190), ((ushort)3, 200, 150, 190, 140) })
                {
                    using var decoded = Decode(WpfPhotoFallback.Process(Fixture(400, 300, orientation: orientation, redBlock: true)));
                    Check(decoded.Width == w && decoded.Height == h, $"orientation {orientation}: {decoded.Width}x{decoded.Height}");
                    var px = decoded[x, y];
                    Check(px.R >= 200 && px.G <= 50 && px.B <= 50, $"orientation {orientation}: block pixel {px}");
                }
            });            Test("corrupt bytes rejected without fallback", () => RejectCorrupt(new byte[2048]));
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
            Console.WriteLine($"FAIL after {passed} passed: {ex.Message}");
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

    // The production fallback, run where ImageSharp cannot load.
    private static void FallbackWithoutImageSharp()
    {
        var context = new WithoutImageSharp();
        try
        {
            var process = LoadProcess(context);
            var assembly = process.DeclaringType!.Assembly;
            var fallbackMethod = assembly.GetType("InspectionEditor.Services.WpfPhotoFallback")!.GetMethod("Process")!;
            var fallback = (Func<byte[], byte[]>)Delegate.CreateDelegate(typeof(Func<byte[], byte[]>), fallbackMethod);
            byte[] result = (byte[])process.Invoke(null, new object[] { Fixture(3968, 2232), fallback })!;
            Check(context.BlockedLoads > 0, "ImageSharp was not actually blocked");
            Check(Density(result) == (1, 72, 72), $"density {Density(result)}");
            Check(!HasApp1(result), "the fallback output carries an APP1 (EXIF) segment");
            using var decoded = Decode(result);
            Check(decoded.Width == 1984 && decoded.Height == 1116, $"size {decoded.Width}x{decoded.Height}");
            Check(Near(decoded[992, 558], 80, 80, 80, 3), $"centre {decoded[992, 558]} (the fallback must not brighten)");
        }
        finally { context.Unload(); }
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
