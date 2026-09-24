using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using QuestStudio;

internal static class EditorPerformance
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var results = new List<object>();
        foreach (int lines in new[] { 8000 })
        foreach (string phase in new[] { "all", "all", "no-syntax", "no-completion", "native" })
        {
            var host = new TestDocumentHost(); var view = new QuestView(host);
            var text = "WorldScript(\r\n" + string.Concat(Enumerable.Range(0, lines).Select(i => $"\tGSSetVar(0, \"key{i}\", 1)\r\n")) + ")";
            view.LoadWorkspace(new Workspace { Name = "benchmark", Text = text }, false);
            typeof(QuestView).GetMethod("Code_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);
            view.Measure(new Size(1400,850)); view.Arrange(new Rect(0,0,1400,850)); view.UpdateLayout(); view.AttachSyntax();
            var editor = (TextBox)view.FindName("CodeEditor"); editor.CaretIndex = text.IndexOf("key")+3;
            if (phase != "all")
            {
                var field = typeof(QuestView).GetField("syntax", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var layer = (System.Windows.Documents.Adorner)field.GetValue(view)!;
                System.Windows.Documents.AdornerLayer.GetAdornerLayer(editor).Remove(layer); field.SetValue(view, null);
                editor.Foreground = System.Windows.Media.Brushes.White;
            }
            if (phase is "no-completion" or "native") typeof(QuestView).GetMethod("StopCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            if (phase == "native") editor.TextChanged -= (TextChangedEventHandler)Delegate.CreateDelegate(typeof(TextChangedEventHandler), view, "Code_Changed");
            var samples = new List<double>(); long memory = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++)
            {
                var timer = Stopwatch.StartNew(); editor.SelectedText = "a"; editor.CaretIndex++; view.UpdateLayout(); timer.Stop(); samples.Add(timer.Elapsed.TotalMilliseconds);
            }
            results.Add(new { phase, lines, chars = text.Length, totalMs = samples.Sum(), maxMs = samples.Max(), samples, allocated = GC.GetAllocatedBytesForCurrentThread()-memory });
            view.Stop();
        }
        File.WriteAllText(Path.Combine(output, "editor-performance.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
