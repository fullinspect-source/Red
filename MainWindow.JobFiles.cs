using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;

namespace InspectionEditor
{
    // F-016 Job Files: the Docs viewer lists {IF}\Jobs\{job7}; opening a file attaches a reference-only copy and opens a
    // working copy through the PDF monitor (O1-O5). The shared job file is only ever read (C3).
    public partial class MainWindow
    {
        private void ShowJobFiles()
        {
            if (string.IsNullOrEmpty(_currentFilePath)) return;
            var profile = InspectorProfileService.Load();
            // U1: the exact raw value "PreInspector" blocks Job Files with INSPECT's message.
            if (JobFilesService.IsBlockedFor(profile?.UserType))
            {
                MessageBox.Show(this, JobFilesService.PreInspectorBlockedMessage, "Job Files", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var owner = _currentInspection;
            string path = _currentFilePath;
            string jobNumber = HeaderSectionsAvailable ? HeaderText("JobNumber") : "";
            var host = new JobFilesHost
            {
                JobNumber = jobNumber,
                InspectionsFolder = profile?.InspectionsFolder,
                Attachments = () => owner != null && ReferenceEquals(owner, _currentInspection) ? owner.Attachments : null,
                Open = file => OpenJobFile(owner, path, file),
                RequestFiles = () => JobFilesService.RequestFilesAsync(StrandWebsiteSession.Default, jobNumber, CancellationToken.None)
            };
            new DocsViewerWindow(path, host) { Owner = this }.ShowDialog();
        }

        private Window DialogOwner => Application.Current.Windows.OfType<DocsViewerWindow>().FirstOrDefault(w => w.IsActive) ?? (Window)this;

        private void OpenJobFile(InspectionFile? owner, string path, JobFile file)
        {
            if (owner == null || !ReferenceEquals(owner, _currentInspection) || path != _currentFilePath)
                throw new InvalidOperationException("This report is no longer open.");
            if (_readOnlyMode)
            {
                // A read-only report gets no attachment; the job file still never opens for editing.
                DocsViewerWindow.OpenReadOnlyCopy(file.FullPath);
                return;
            }
            if (!PdfAttachmentService.SafeFilename(file.Filename))
                throw new IOException("This job file's name cannot be used as an attachment name.");
            SyncCurrentItemFromUI();

            var plan = JobFilesService.Plan(owner.Attachments, file.Filename, file.Modified);
            int index;
            switch (plan.Action)
            {
                case JobFileOpenAction.CreateReference:
                {
                    byte[] bytes = PdfAttachmentService.ReadPdf(file.FullPath);
                    if (bytes.LongLength > PdfAttachmentService.WarningBytes && MessageBox.Show(DialogOwner,
                            $"This PDF is {AttachmentsWindow.ReadableSize(bytes.LongLength)}, larger than 15 MiB. Embed the complete PDF in this INS?\n\n{file.Filename}",
                            "Large PDF attachment", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                        return;
                    var expected = PdfAttachmentService.Snapshot(owner);
                    var replacement = (JArray)expected.DeepClone();
                    replacement.Add(JobFilesService.ReferenceAttachment(file.Filename, bytes, DateTime.Now));
                    if (!new PdfAttachmentService(owner).Transact(expected, replacement, () => SavePdfMutation(owner, path)))
                    {
                        MessageBox.Show(DialogOwner, "The report could not be saved, so the job file was not attached.", "Job Files",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    index = replacement.Count - 1;
                    break;
                }
                case JobFileOpenAction.UseExisting:
                    index = plan.Index!.Value;
                    break;
                case JobFileOpenAction.RefreshReference:
                    index = plan.Index!.Value;
                    // A copy that was already opened in the PDF app this session is never replaced without a question.
                    if (FindPdfMonitor(index)?.HasLaunched == true) break;
                    if (!ReplaceWithJobFile(owner, path, index, file)) return;
                    break;
                default:
                {
                    index = plan.Index!.Value;
                    string question = plan.Action == JobFileOpenAction.AskMarkedUp
                        ? JobFilesService.MarkedUpQuestion(file.Filename)
                        : JobFilesService.DropboxNewerQuestion(file.Filename);
                    var answer = MessageBox.Show(DialogOwner, question, "Job Files", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Cancel) return;
                    if (answer == MessageBoxResult.No && !ReplaceWithJobFile(owner, path, index, file)) return;
                    break;
                }
            }

            // O5: open the working copy (never the shared file) through the general PDF monitor.
            var service = new PdfAttachmentService(owner);
            var monitor = FindPdfMonitor(index);
            if (monitor == null)
            {
                monitor = service.Open(index, path, PdfWorkingRoot);
                RegisterPdfMonitor(monitor);
            }
            service.PrepareForOpen(monitor);
            monitor.MarkLaunched();
            using var process = Process.Start(PdfAppLauncher.StartInfoFor(monitor.WorkingPath)); // F-017 A1: the PDF app from Settings
        }

        // O3/O4 "No" and a reference refresh: the attachment takes the job file's current bytes, ReferenceOnly true.
        private bool ReplaceWithJobFile(InspectionFile owner, string path, int index, JobFile file)
        {
            var open = FindPdfMonitor(index);
            if (open != null)
            {
                if (!open.CanLeave(out string reason))
                {
                    MessageBox.Show(DialogOwner, reason, "Job Files", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                if (open.HasLaunched && MessageBox.Show(DialogOwner,
                        $"'{file.Filename}' may still be open in your PDF app. Changes not saved there will be lost. Replace it with the job file anyway?",
                        "Job Files", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return false;
                open.Cleanup();
                _pdfSessions.RemoveAll(e => ReferenceEquals(e.Monitor, open));
                if (ReferenceEquals(_orientationPdf, open)) _orientationPdf = null;
            }
            byte[] bytes = PdfAttachmentService.ReadPdf(file.FullPath);
            var expected = PdfAttachmentService.Snapshot(owner);
            // RED safety copy: marked-up bytes about to be replaced are kept in RED's app data (never in Inspections).
            if (expected[index] is JObject current && !JobFilesService.IsReferenceOnly(current))
            {
                string folder = Path.Combine(AppIdentity.LocalAppDataPath, "Recovery", "job-files");
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(file.Filename)} (replaced {DateTime.Now:yyyyMMdd-HHmmss}).pdf"),
                    PdfAttachmentService.EmbeddedPdf(current));
            }
            var replacement = (JArray)expected.DeepClone();
            replacement[index] = JobFilesService.Refreshed((JObject)expected[index], bytes, DateTime.Now);
            if (new PdfAttachmentService(owner).Transact(expected, replacement, () => SavePdfMutation(owner, path)))
                return true;
            MessageBox.Show(DialogOwner, "The report could not be saved, so the attachment was not replaced.", "Job Files",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
