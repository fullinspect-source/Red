using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;

namespace InspectionEditor
{
    public partial class MainWindow
    {
        public static void RunOfficialReuseProbe(string root, Action<bool, string> check)
        {
            int serial = 0;
            MainWindow Create(bool duplicate = false)
            {
                string folder = Path.Combine(root, (++serial).ToString());
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "report.ins");
                var json = JObject.Parse("""
                {"InspectionCode":"BWT","Contact":"Current buyer","UnknownRoot":"preserve","Sections":[{"Items":[{"ItemId":1,"Name":"Walk","ControlName":"DocumentButton","Template":"walk.pdf"}]}],"Attachments":[]}
                """);
                if (duplicate)
                {
                    ((JArray)json["Attachments"]!).Add(PdfAttachmentService.Create("walk.pdf", AutofillProbe.SyntheticPdf("")));
                    ((JArray)json["Attachments"]!).Add(PdfAttachmentService.Create("walk.pdf", AutofillProbe.SyntheticPdf("")));
                }
                File.WriteAllText(path, json.ToString());
                var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(folder, "recovery")), saveRegistryRoot: folder);
                return new MainWindow { ProbeRoot = folder, _currentFilePath = path, _currentInspection = saver.Load(path), ProbeSaver = saver };
            }
            string Value(PdfEditMonitor monitor) => AutofillProbe.Values(File.ReadAllBytes(monitor.WorkingPath))["Customer Name_HOI"];
            void Open(MainWindow p, AttachmentsWindow window, bool walk, int index = 0)
            {
                if (walk) p.OpenOrientationPdfButton_Click(p, new System.Windows.RoutedEventArgs());
                else window.Open(index);
            }
            foreach (bool walk in new[] { false, true })
            {
                string route = walk ? "walk" : "manager";
                var p = Create();
                AttachmentsWindow.OnShow = window =>
                {
                    string source = Path.Combine(p.ProbeRoot, "walk.pdf");
                    byte[] original = AutofillProbe.SyntheticPdf(""); File.WriteAllBytes(source, original);
                    check(window.Add(source, _ => true), route + " add succeeds");
                    var monitor = p.FindPdfMonitor(0)!;
                    check(!monitor.OfficialPrepared && !monitor.HasLaunched && Value(monitor) == "", route + " indexed add initially unprepared and unlaunched");
                    byte[] ins = File.ReadAllBytes(p._currentFilePath!); int saves = p.SaveCount;
                    Open(p, window, walk);
                    check(monitor.OfficialPrepared && monitor.HasLaunched && Value(monitor) == "Current buyer", route + " first open prepares reused indexed add");
                    check(p._pdfSessions.Count == 1 && p.LaunchCount == 1, route + " reuses one monitored working copy");
                    check(!monitor.Poll(DateTimeOffset.UtcNow, () => throw new Exception("unexpected save")) && monitor.CanLeave(out _), route + " preparation becomes clean baseline");
                    check(ins.SequenceEqual(File.ReadAllBytes(p._currentFilePath!)) && p.SaveCount == saves && original.SequenceEqual(File.ReadAllBytes(source)), route + " preparation preserves exact INS and source PDF");
                    check(PdfAttachmentService.EmbeddedPdf((JObject)p._currentInspection!.Attachments![0]).SequenceEqual(original), route + " preparation does not stage embedded bytes");
                    byte[] filled = File.ReadAllBytes(monitor.WorkingPath);
                    Open(p, window, walk);
                    check(filled.SequenceEqual(File.ReadAllBytes(monitor.WorkingPath)) && p.SaveCount == saves, route + " repeated prepared open is idempotent");
                    var edited = AutofillProbe.EditWorking(monitor.WorkingPath);
                    check(AutofillProbe.Capture(monitor, () => p.SavePdfMutation(p._currentInspection!, p._currentFilePath!)), route + " subsequent external save captures prepared form");
                    check(PdfAttachmentService.EmbeddedPdf((JObject)p.ProbeSaver.Load(p._currentFilePath!).Attachments![0]).SequenceEqual(edited), route + " persisted captured PDF is byte exact");
                    check(p.FinishOrientationEditing(), route + " prepared session closes cleanly");
                };
                p.AttachmentsButton_Click(p, new System.Windows.RoutedEventArgs());

                // The manager really launches an ambiguous form. Deleting the other copy
                // makes this one official, but disk equality cannot prove editor-memory safety.
                p = Create(duplicate: true);
                AttachmentsWindow.OnShow = window =>
                {
                    window.Open(1);
                    var monitor = p.FindPdfMonitor(1)!;
                    check(!monitor.OfficialPrepared && monitor.HasLaunched && Value(monitor) == "", route + " ambiguous launch intentionally unprepared");
                    check(window.Delete(new[] { 0 }) && ReferenceEquals(p.FindPdfMonitor(0), monitor), route + " delete makes live monitor unique and rebases index");
                    byte[] ins = File.ReadAllBytes(p._currentFilePath!), working = File.ReadAllBytes(monitor.WorkingPath);
                    int launches = p.LaunchCount; System.Windows.MessageBox.Last = "";
                    string error = "";
                    try { Open(p, window, walk); } catch (IOException ex) { error = ex.Message; }
                    error += System.Windows.MessageBox.Last;
                    check(p.LaunchCount == launches && error.Contains("reopen the report"), route + " newly unique previously launched official requires report reopen");
                    check(!monitor.OfficialPrepared && working.SequenceEqual(File.ReadAllBytes(monitor.WorkingPath)) && ins.SequenceEqual(File.ReadAllBytes(p._currentFilePath!)), route + " ambiguous reuse failure never mutates working PDF or INS");
                    check(p.FinishOrientationEditing(), route + " unchanged blocked session can close after editor is closed");
                };
                p.AttachmentsButton_Click(p, new System.Windows.RoutedEventArgs());

                foreach (string obstacle in new[] { "unseen", "observed", "locked" })
                {
                    p = Create();
                    AttachmentsWindow.OnShow = window =>
                    {
                        string source = Path.Combine(p.ProbeRoot, "walk.pdf"); File.WriteAllBytes(source, AutofillProbe.SyntheticPdf(""));
                        check(window.Add(source, _ => true), route + " " + obstacle + " fixture add");
                        var monitor = p.FindPdfMonitor(0)!;
                        byte[] baseline = File.ReadAllBytes(monitor.WorkingPath);
                        byte[] pending = obstacle == "locked" ? baseline : AutofillProbe.SyntheticPdf("External pending");
                        File.WriteAllBytes(monitor.WorkingPath, pending);
                        if (obstacle == "observed") monitor.Poll(DateTimeOffset.UtcNow.AddSeconds(-2), () => throw new Exception("premature capture"));
                        byte[] ins = File.ReadAllBytes(p._currentFilePath!);
                        int saves = p.SaveCount;
                        using (var lease = obstacle == "locked" ? new FileStream(monitor.WorkingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
                        {
                            System.Windows.MessageBox.Last = ""; bool blocked = false;
                            try { Open(p, window, walk); } catch (IOException) { blocked = true; }
                            check((blocked || System.Windows.MessageBox.Last.Length > 0) && p.LaunchCount == 0 && !monitor.HasLaunched && !monitor.OfficialPrepared, route + " " + obstacle + " blocks preparation and launch");
                        }
                        check(pending.SequenceEqual(File.ReadAllBytes(monitor.WorkingPath)) && ins.SequenceEqual(File.ReadAllBytes(p._currentFilePath!)) && p.SaveCount == saves, route + " " + obstacle + " preserves all bytes and no save");
                        if (obstacle == "locked")
                        {
                            Open(p, window, walk);
                            check(monitor.OfficialPrepared && Value(monitor) == "Current buyer", route + " unlocked never-launched copy prepares safely");
                        }
                        else
                        {
                            monitor.Poll(DateTimeOffset.UtcNow.AddSeconds(-2), () => true);
                            check(monitor.RetrySave(() => p.SavePdfMutation(p._currentInspection!, p._currentFilePath!)), route + " pending bytes remain retryable");
                            Open(p, window, walk);
                            check(monitor.OfficialPrepared && Value(monitor) == "External pending", route + " preparation preserves captured nonblank external value");
                        }
                        check(p.FinishOrientationEditing(), route + " resolved obstacle closes cleanly");
                    };
                    p.AttachmentsButton_Click(p, new System.Windows.RoutedEventArgs());
                }
            }
            AttachmentsWindow.OnShow = null;
        }
    }
}
