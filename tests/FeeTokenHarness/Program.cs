using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using InspectionEditor;
using InspectionEditor.Services;

// iPad fee token (ORDERS D-03 = B) acceptance:
//  1. the token decrypts to "0.00" with an independent decryptor written the way Inspect's CryptoManager does it
//     (legacy Rfc2898DeriveBytes(string, byte[]) + AES-256-CBC), not RED's own InspectCipher.Decrypt;
//  2. each token is fresh (new IV);
//  3. making tokens writes nothing to disk (RED's app-data folder, its error log, and the working folder are unchanged).
int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

var roots = new[] { AppIdentity.LocalAppDataPath, Environment.CurrentDirectory }
    .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
string errorLog = Path.Combine(AppIdentity.LocalAppDataPath, "red_errors.log");
var before = Snapshot(roots, errorLog);

var tokens = Enumerable.Range(0, 5).Select(_ => StrandMyListService.CreateFeeToken()).ToList();

var after = Snapshot(roots, errorLog);
Check(before.SequenceEqual(after), $"nothing written to disk while making tokens ({string.Join("; ", roots)})");

foreach (var token in tokens)
{
    string plain = InspectStyleDecrypt(token);
    Check(plain == "0.00", $"token decrypts to \"0.00\" (got \"{plain}\")");
}
Check(tokens.Distinct().Count() == tokens.Count, "each token is fresh (different IV)");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

static List<string> Snapshot(IEnumerable<string> roots, string errorLog)
{
    var rows = new List<string>();
    foreach (string root in roots)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            rows.Add($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        }
    }
    rows.Add(File.Exists(errorLog) ? $"log|{new FileInfo(errorLog).Length}" : "log|none");
    rows.Sort(StringComparer.Ordinal);
    return rows;
}

// Inspect's DecryptStringAES: [int32 IV length][IV][AES-CBC ciphertext], key = Rfc2898DeriveBytes(secret, salt)
// (1000 iterations, SHA1), 32 bytes. The secret and salt are read from RED's cipher by reflection, not copied here.
static string InspectStyleDecrypt(string cipherText)
{
    var cipherType = typeof(StrandMyListService).Assembly.GetType("InspectionEditor.Services.InspectCipher", throwOnError: true)!;
    string secret = (string)cipherType.GetField("SharedSecret", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    byte[] salt = (byte[])cipherType.GetField("Salt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    byte[] data = Convert.FromBase64String(cipherText);
    int ivLength = BitConverter.ToInt32(data, 0);
    byte[] iv = data.Skip(4).Take(ivLength).ToArray();

#pragma warning disable SYSLIB0041 // the legacy constructor is exactly what Inspect uses
    using var derive = new Rfc2898DeriveBytes(secret, salt);
#pragma warning restore SYSLIB0041
    using var aes = Aes.Create();
    aes.Key = derive.GetBytes(32);
    aes.IV = iv;
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.PKCS7;
    using var decryptor = aes.CreateDecryptor();
    byte[] plain = decryptor.TransformFinalBlock(data, 4 + ivLength, data.Length - 4 - ivLength);
    return Encoding.UTF8.GetString(plain);
}
