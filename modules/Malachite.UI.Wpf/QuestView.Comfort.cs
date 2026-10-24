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
    bool sidebarHidden, inspectorHidden, checksHidden;
    double checksHeight = 120;
    void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        sidebarHidden = !sidebarHidden;
        SidebarPanel.Visibility = SidebarSplitter.Visibility = sidebarHidden ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.MinWidth = sidebarHidden ? 0 : 195; SidebarColumn.Width = new GridLength(sidebarHidden ? 0 : 210);
        EditorSurface.ColumnDefinitions[1].Width = new GridLength(sidebarHidden ? 0 : 5);
        SidebarToggle.Content = sidebarHidden ? "Скрипты ›" : "‹ Скрипты";
    }
    void InspectorToggle_Click(object sender, RoutedEventArgs e)
    {
        inspectorHidden = !inspectorHidden;
        InspectorPanel.Visibility = InspectorSplitter.Visibility = inspectorHidden ? Visibility.Collapsed : Visibility.Visible;
        InspectorColumn.MinWidth = inspectorHidden ? 0 : 265; InspectorColumn.Width = new GridLength(inspectorHidden ? 0 : 325);
        EditorSurface.ColumnDefinitions[3].Width = new GridLength(inspectorHidden ? 0 : 5);
        InspectorToggle.Content = inspectorHidden ? "Объяснение ‹" : "Объяснение ›";
    }
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
