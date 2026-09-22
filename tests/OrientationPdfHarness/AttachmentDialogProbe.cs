using InspectionEditor.Services;
using Newtonsoft.Json.Linq;
using AttachmentDialog.Stubs;

namespace AttachmentDialog.Stubs
{
    public class Window { public void Close() { } }
    public class RoutedEventArgs : EventArgs { }
    public class Control { public bool IsEnabled; public string Text = ""; public object? ItemsSource; }
    public enum MessageBoxButton { YesNo }
    public enum MessageBoxImage { Warning }
    public enum MessageBoxResult { Yes, No }
    public static class MessageBox
    {
        public static MessageBoxResult Answer = MessageBoxResult.No;
        public static MessageBoxResult Default;
        public static int Calls;
        public static string Text = "";
        public static MessageBoxResult Show(object owner, string text, string title, MessageBoxButton button, MessageBoxImage image, MessageBoxResult defaultResult)
        { Calls++; Text = text; Default = defaultResult; return Answer; }
    }
    public class OpenFileDialog
    {
        public string Title = "", Filter = "";
        public bool Multiselect, CheckFileExists;
        public string[] FileNames = Array.Empty<string>();
        public bool? ShowDialog(object owner) => false;
    }
}
namespace AttachmentDialog
{
    public partial class AttachmentsWindow
    {
        private readonly Control OpenButton = new(), AddButton = new(), DeleteButton = new(), RetryButton = new(),
            ModeHint = new(), PdfList = new(), ResultText = new();
        private void InitializeComponent() { }
        public static void Run(string root, Action<bool, string> check)
        {
            string path = Path.Combine(root, "dialog-corrupt.ins");
            var raw = JObject.Parse("""
            {"UnknownRoot":{"keep":true},"Sections":[],"Attachments":[
             {"Filename":"bad.pdf","FileData":"bad!","Unknown":"preserved until confirmation"},
             {"Filename":"notes.txt","Keep":[1,2]},"opaque"]}
            """);
            File.WriteAllText(path, raw.ToString());
            var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(root,"dialog-recovery")), saveRegistryRoot: root);
            var model = saver.Load(path);
            var service = new PdfAttachmentService(model);
            int opens = 0, deletes = 0;
            bool failSave = false;
            List<PdfRow> Rows() => service.Enumerate().Select(r => new PdfRow { Index = r.Index, Filename = r.Filename,
                Status = r.Status, CanOpen = r.CanOpen, Size = r.SizeBytes < 0 ? "Unknown" : ReadableSize(r.SizeBytes) }).ToList();
            bool Delete(IReadOnlyCollection<int> indices) { deletes++; return service.Delete(indices, () => { if (failSave) return false; saver.Save(model); return true; }); }
            var dialog = new AttachmentsWindow(false, Rows, _ => opens++, (_, _) => false, Delete, () => { });
            dialog._rows.Single().IsChecked = true;
            byte[] before = File.ReadAllBytes(path);
            dialog.Open_Click(dialog, new RoutedEventArgs());
            check(opens == 0 && dialog.ResultText.Text.Contains("damaged") && dialog.ResultText.Text.Contains("repair"),
                "production dialog damaged row never invokes open callback and offers repair");
            MessageBox.Answer = MessageBoxResult.No;
            dialog.Delete_Click(dialog, new RoutedEventArgs());
            check(deletes == 0 && before.SequenceEqual(File.ReadAllBytes(path)) && model.Attachments!.Count == 3,
                "production dialog declined damaged deletion performs no transaction");
            check(MessageBox.Default == MessageBoxResult.No && MessageBox.Text.Contains("#1: bad.pdf (damaged / unreadable)"),
                "production dialog confirmation defaults No and identifies damaged row");
            MessageBox.Answer = MessageBoxResult.Yes; failSave = true;
            dialog.Delete_Click(dialog, new RoutedEventArgs());
            check(deletes == 1 && before.SequenceEqual(File.ReadAllBytes(path)) && dialog._rows.Single().IsChecked &&
                dialog.ResultText.Text.Contains("not saved"), "production dialog failed confirmed delete retains checked damaged row and original INS");
            failSave = false;
            dialog.Delete_Click(dialog, new RoutedEventArgs());
            var actual = JObject.Parse(File.ReadAllText(path));
            var expected = (JArray)raw["Attachments"]!.DeepClone(); expected.RemoveAt(0);
            check(deletes == 2 && dialog._rows.Count == 0 && JToken.DeepEquals(actual["Attachments"], expected) &&
                JToken.DeepEquals(actual["UnknownRoot"], raw["UnknownRoot"]),
                "production dialog confirmed damaged deletion saves only selected token and refreshes list");
            File.WriteAllBytes(path, before); model = saver.Load(path); service = new PdfAttachmentService(model);
            var readOnly = new AttachmentsWindow(true, Rows, _ => opens++, (_, _) => false, Delete, () => { });
            readOnly._rows.Single().IsChecked = true;
            int confirmations = MessageBox.Calls;
            readOnly.Delete_Click(readOnly, new RoutedEventArgs()); readOnly.Open_Click(readOnly, new RoutedEventArgs());
            check(deletes == 2 && opens == 0 && confirmations == MessageBox.Calls && before.SequenceEqual(File.ReadAllBytes(path)) &&
                !readOnly.DeleteButton.IsEnabled, "production dialog read-only damaged rows stay visible but cannot open or delete");
        }
    }
}
