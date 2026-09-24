using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuestStudio;

public static class EditingVerification
{
    public static int Run(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var results = new List<object>(); int failures = 0;
        void Check(bool pass, string name) { results.Add(new { name, pass }); if (!pass) failures++; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var w = new MainWindow();
        var path = args[1]; var workspace = Workspace.Open(path);
        w.LoadWorkspace(workspace, false);
        w.WindowSurface.Measure(new Size(1510, 922)); w.WindowSurface.Arrange(new Rect(0, 0, 1510, 922)); w.WindowSurface.UpdateLayout();
        var call = w.Current.Calls.First(c => c.Name == "CreateVoiceFX" && c.Line == 912);
        var diagnostic = w.Current.Issues.First(i => i.Offset >= call.Start && i.Offset <= call.End);
        w.Diagnostics.SelectedItem = diagnostic;
        Check(w.Selected?.Name == call.Owner && w.InspectorTitle.Text == call.Owner, "video: clicking line912 diagnostic selects actual script instead of Starter");
        Check(w.CodeEditor.CaretIndex == diagnostic.Offset, "diagnostic keeps exact error caret");
        w.CodeEditor.Select(call.Arguments[3].Start, call.Arguments[4].End - call.Arguments[3].Start);
        w.CodeEditor.SelectedText = "232312сц, ауц";
        int caret = w.CodeEditor.CaretIndex;
        Check(w.CardPanel.IsEnabled && w.LinkTargets.IsEnabled, "typing does not disable themed panel/list");
        w.RefreshAnalysis(false);
        Check(w.CodeEditor.CaretIndex == caret, "analysis does not move typing caret");
        Check(w.Selected?.Name == call.Owner, "analysis keeps editing script in inspector");
        Check(w.Current.Issues.Any(i => i.Severity == "error" && i.Line == 912 && i.Code is "INVALID_NUMBER" or "UNDEFINED" or "EXPRESSION"), "malformed parameter from video is a blocking error");
        Check(w.Current.Issues.Any(i => i.Line == 912 && i.Code == "UNDEFINED" && i.Severity == "warning"), "unresolved identifier is visible warning, without claiming external globals impossible");
        w.CodeEditor.Undo(); w.RefreshAnalysis(false);
        Check(w.CodeEditor.Text == workspace.Mob!.Text, "single undo restores original map text");
        var beforeText = w.CodeEditor.Text;
        var beforeLine = w.Current.Calls.First(c => c.Name == "CreateVoiceFX" && c.Line == 912);
        w.CodeEditor.CaretIndex = beforeLine.Start;
        Check(w.Selected?.Name == beforeLine.Owner, "direct caret movement synchronizes inspector");
        w.CodeEditor.Select(beforeLine.Arguments[3].Start, beforeLine.Arguments[4].End - beforeLine.Arguments[3].Start);
        w.CodeEditor.SelectedText = "20, 30"; w.RefreshAnalysis(false);
        Check(!w.Current.Issues.Any(i => i.Line == 912 && i.Severity == "error"), "valid replacement clears line912 diagnostic");
        Check(w.InspectorTitle.Text == beforeLine.Owner, "valid edit does not revert inspector to Starter");
        Check(w.StatusText.Text.StartsWith("Проверка обновлена"), "completed analysis clears pending status");
        foreach (var example in new[] { "20", "-20", "2.5", "Add(10,20)", "radius" })
        {
            var parsed = ScriptAnalyzer.Analyze($"GlobalVars(radius:float) WorldScript(Sleep({example}))", w.Registry);
            Check(!parsed.Issues.Any(i => i.Severity == "error" || i.Code == "UNDEFINED"), "valid expression retained: " + example);
        }
        Check(ScriptAnalyzer.Analyze("WorldScript(Sleep(FutureAddon(1)))", w.Registry).Issues.All(i => i.Severity != "error"), "unknown extension result remains uncertainty");
        w.WindowSurface.UpdateLayout();
        var bmp = new RenderTargetBitmap(1510, 922, 96, 96, PixelFormats.Pbgra32); bmp.Render(w.WindowSurface);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using (var stream = File.Create(Path.Combine(output, "editing.png"))) png.Save(stream);
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { status = failures == 0 ? "PASS" : "FAIL", failures, results }, new JsonSerializerOptions { WriteIndented = true }));
        app.Shutdown(); return failures == 0 ? 0 : 1;
    }
}
