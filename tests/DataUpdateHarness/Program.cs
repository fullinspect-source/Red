using InspectionEditor.Services;
using System.Net;
using System.Net.Sockets;
using System.Text;

class Program
{
    static int passed, failed;
    static readonly string Root = Path.Combine(Path.GetTempPath(), "red-data-harness-" + Guid.NewGuid().ToString("N"));
    const string Old = "{\"generated\":\"2026-09-01\",\"inspectors\":{\"A\":{}}}";
    const string New = "{\"generated\":\"2026-09-15\",\"inspectors\":{\"A\":{}}}";
    static void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if(ok) passed++; else failed++; }
    static async Task Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            Check(DataUpdateService.IsValidStatsPayload(New), "valid stats");
            foreach(var s in new[]{"<html>Login</html>", "{}", "[]", "{", "{\"generated\":\"bad\",\"inspectors\":{}}", "{\"generated\":\"2026-09-15\",\"inspectors\":[]}", "{\"generated\":\"2026-09-15\",\"inspectors\":null}"})
                Check(!DataUpdateService.IsValidStatsPayload(s), "invalid stats rejected: " + s);
            string p = Path.Combine(Root,"stats.json");
            DataUpdateService.StoreValidatedData(p, New, true);
            Check(File.ReadAllText(p)==New,"initial atomic storage");
            var stamp = new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(p,stamp);
            DataUpdateService.StoreValidatedData(p,Old,true);
            Check(File.ReadAllText(p)==New && File.GetLastWriteTimeUtc(p)==stamp,"downgrade preserves bytes and date");
            DataUpdateService.StoreValidatedData(p,New,true);
            Check(File.ReadAllText(p)==New && File.GetLastWriteTimeUtc(p)>stamp,"unchanged payload refreshes timestamp");
            DataUpdateService.StoreValidatedData(p,Old,false);
            Check(File.ReadAllText(p)==Old,"explicit no-date-preservation replaces");
            string blocked=Path.Combine(Root,"directory-target"); Directory.CreateDirectory(blocked);
            bool threw=false; try { DataUpdateService.StoreValidatedData(blocked,New,true); } catch(IOException) { threw=true; }
            Check(threw && Directory.Exists(blocked) && !Directory.GetFiles(Root,"*.download").Any(),"failed final move throws and cleans temporary file");
            await DownloadCase(p,New,200,true,"network valid",false);
            Check(File.ReadAllText(p)==New,"network changed payload persisted");
            await DownloadCase(p,"<html>Login</html>",200,false,"network HTML",true);
            await DownloadCase(p,"{broken",200,false,"network malformed JSON",true);
            await DownloadCase(p,Old,200,true,"network older preserves newer",true);
            await DownloadCase(p,New,200,true,"network unchanged success",true);
            await DownloadCase(blocked,New,200,false,"network write exception returns false",false);
            await DownloadCase(p,"unavailable",404,false,"network non-success returns false",true);
            var dead = new TcpListener(IPAddress.Loopback,0); dead.Start(); int port=((IPEndPoint)dead.LocalEndpoint).Port; dead.Stop();
            Check(!await DataUpdateService.DownloadIfNewerAsync($"http://127.0.0.1:{port}/stats",p,DataUpdateService.IsValidStatsPayload,true),"connection refusal returns false");
            Check(!await DataUpdateService.DownloadIfNewerAsync("PLACEHOLDER",p),"placeholder returns false");
            Check(!Directory.GetFiles(Root,"*.download").Any(),"no leaked atomic-write files");
            Check(!DataUpdateService.IsValidStatsPayload("{\"metadata\":{\"generated\":\"2026-09-15\"},\"inspectors\":{}}"), "nested generated is not dataset date");
        }
        finally { Directory.Delete(Root,true); }
        Console.WriteLine($"RESULT {passed} passed, {failed} failed");
        Environment.ExitCode=failed==0?0:1;
    }
    static async Task DownloadCase(string path,string body,int status,bool expected,string name,bool preserve)
    {
        string? before=File.Exists(path)?File.ReadAllText(path):null;
        using var listener = new TcpListener(IPAddress.Loopback,0); listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        string? request=null;
        var serve=Task.Run(async()=>{
            using var client=await listener.AcceptTcpClientAsync();
            using var stream=client.GetStream();
            using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
            request=await reader.ReadLineAsync();
            while(!string.IsNullOrEmpty(await reader.ReadLineAsync())) {}
            var bytes=Encoding.UTF8.GetBytes(body);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(bytes);
        });
        bool result=await DataUpdateService.DownloadIfNewerAsync($"http://127.0.0.1:{port}/stats?existing=1",path,DataUpdateService.IsValidStatsPayload,true);
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        Check(result==expected && (!preserve || File.ReadAllText(path)==before),name);
        Check(request?.Contains("?existing=1&_=")==true,name+" cache-bust query");
    }
}
namespace InspectionEditor { internal static class AppIdentity { public const string Version = "0.0.0"; public static bool IsDevBuild => true; public static string LocalAppDataPath => Path.Combine(Path.GetTempPath(),"unused-red-data-harness"); } }
