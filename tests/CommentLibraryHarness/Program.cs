// F-024 INSPECT comment libraries harness (program/features/F-024-comment-libraries/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// A sandbox {IF}\Data folder under %TEMP% with sanitized fixture comments only.
using System.IO;
using System.Security.Cryptography;
using System.Text;
using InspectionEditor.Services;

return Harness.Run();

static class Harness
{
    private static int _pass, _fail;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id}  {what}");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f024-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        try
        {
            void Write(string name, string json) => File.WriteAllText(Path.Combine(data, name), json, new UTF8Encoding(false));
            Write("CompanyComments.cmt", "{\"1615\":[{\"Selected\":false,\"Value\":\"Cylinders taken by lab\"}],\"1616\":[{\"Value\":\"Forms checked\"},{\"Selected\":true,\"Value\":\"  \"}]}");
            Write("UserComments.cmt", "{\"1615\":[{\"Selected\":false,\"Value\":\"Main file entry\"}]}");
            Write("UserComments.temp.cmt", "{\"1615\":[{\"Selected\":false,\"Value\":\"Temp mirror entry\"}]}");
            Write("xCompanyComments-old.cmt", "{\"1615\":[{\"Value\":\"Not INSPECT's file\"}]}");

            // ---------- A-024-01 Lists by ItemId ----------
            var company = InspectCommentLibrary.Entries(root, InspectCommentLibrary.Kind.Company, 1615);
            var other = InspectCommentLibrary.Entries(root, InspectCommentLibrary.Kind.Company, 1616);
            Check("A-024-01", company.SequenceEqual(new[] { "Cylinders taken by lab" }) && other.SequenceEqual(new[] { "Forms checked" }) &&
                              InspectCommentLibrary.CompanyTitle == "Company comments" && InspectCommentLibrary.UserTitle == "My INSPECT comments",
                "\"Company comments\" lists \"Cylinders taken by lab\" for ItemId 1615 (Selected optional, blank values skipped)");

            // ---------- A-024-02 Append format ----------
            Check("A-024-02", InspectCommentLibrary.Append("Truck 3 late", "Cylinders taken by lab") == "Truck 3 late, Cylinders taken by lab" &&
                              InspectCommentLibrary.Append("", "Cylinders taken by lab") == "Cylinders taken by lab" &&
                              InspectCommentLibrary.Append(null, "Cylinders taken by lab") == "Cylinders taken by lab",
                "\"Truck 3 late\" + entry → \"Truck 3 late, Cylinders taken by lab\"; an empty comment becomes the entry");

            // ---------- A-024-03 Temp mirror preferred ----------
            var user = InspectCommentLibrary.Entries(root, InspectCommentLibrary.Kind.User, 1615);
            Write("CompanyComments.temp.cmt", "{ not json");
            var companyAfterBadTemp = InspectCommentLibrary.Entries(root, InspectCommentLibrary.Kind.Company, 1615);
            Check("A-024-03", user.SequenceEqual(new[] { "Temp mirror entry" }) && companyAfterBadTemp.SequenceEqual(new[] { "Cylinders taken by lab" }),
                "UserComments.temp.cmt wins over UserComments.cmt; an unreadable temp mirror falls back to the main file");

            // F3 / B5: other .cmt names ignored; missing files give nothing (the section is hidden).
            string empty = Path.Combine(root, "EmptyIF");
            Directory.CreateDirectory(empty);
            Check("F3/B5", !company.Contains("Not INSPECT's file") && InspectCommentLibrary.Entries(empty, InspectCommentLibrary.Kind.Company, 1615).Count == 0 &&
                           InspectCommentLibrary.Entries(null, InspectCommentLibrary.Kind.User, 1615).Count == 0 &&
                           InspectCommentLibrary.Entries(root, InspectCommentLibrary.Kind.Company, 9999).Count == 0,
                "xCompanyComments*.cmt is ignored; a missing Data folder or ItemId yields no entries");

            // ---------- A-024-04 Read only ----------
            var before = Directory.GetFiles(data, "*.cmt").ToDictionary(f => f, Hash);
            for (int i = 0; i < 5; i++)
            {
                InspectCommentLibrary.Entries(root, InspectCommentLibrary.Kind.Company, 1615);
                InspectCommentLibrary.Entries(root, InspectCommentLibrary.Kind.User, 1615);
            }
            // A file held open by another writer (as INSPECT or Dropbox may) is still readable, and stays untouched.
            using (var held = new FileStream(Path.Combine(data, "UserComments.temp.cmt"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                File.SetLastWriteTimeUtc(Path.Combine(data, "UserComments.temp.cmt"), DateTime.UtcNow.AddMinutes(1)); // defeat the cache
                Check("A-024-04b", InspectCommentLibrary.Entries(root, InspectCommentLibrary.Kind.User, 1615).SequenceEqual(new[] { "Temp mirror entry" }),
                    "a library held open by another program is still read");
            }
            var after = Directory.GetFiles(data, "*.cmt").ToDictionary(f => f, Hash);
            // Nothing in RED's source writes a .cmt file.
            string source = Path.Combine("..", "..");
            var writers = Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}") && !f.Contains("red-release-pre-sync-backups") &&
                            !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Where(f => File.ReadAllLines(f).Any(l => l.Contains(".cmt") && (l.Contains("Write") || l.Contains("Create") || l.Contains("Move") || l.Contains("Delete"))))
                .ToList();
            Check("A-024-04", before.Count == 5 && before.All(p => after[p.Key] == p.Value) && writers.Count == 0,
                $"all .cmt hashes unchanged after use; no RED source line writes a .cmt file ({string.Join(", ", writers.Select(Path.GetFileName))})");
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("FAIL harness  " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
