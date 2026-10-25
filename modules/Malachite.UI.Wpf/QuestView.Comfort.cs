using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuestStudio;

public partial class QuestView
{
    static string CountLabel(int count, string one, string few, string many) => count + " " +
        (count % 100 is >= 11 and <= 14 ? many : count % 10 == 1 ? one : count % 10 is >= 2 and <= 4 ? few : many);
    Analysis? explainedAnalysis;
    int explainedLine = -1, explainedFocus = -1;
    bool explanationWaiting;
    bool checksHidden;
    double checksHeight = 120;
    void ChecksToggle_Click(object sender, RoutedEventArgs e)
    {
        checksHidden = !checksHidden;
        if (checksHidden) checksHeight = DiagnosticsRow.ActualHeight;
        Diagnostics.Visibility = DiagnosticsSplitter.Visibility = checksHidden ? Visibility.Collapsed : Visibility.Visible;
        EditorArea.RowDefinitions[2].Height = new GridLength(checksHidden ? 0 : 8);
        DiagnosticsRow.MinHeight = checksHidden ? 36 : 70; DiagnosticsRow.Height = new GridLength(checksHidden ? 36 : Math.Max(70, checksHeight));
        ChecksToggle.Content = checksHidden ? "Проверки ▸" : "Проверки ▾";
        UpdateEditorBounds();
    }
}
