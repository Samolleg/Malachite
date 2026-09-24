using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public partial class QuestView
{
    public event EventHandler? DatabaseSelectionChanged;
    void NotifyDatabaseSelection(object sender, RoutedEventArgs e) { if (!internalUpdate && Current?.Text == CodeEditor.Text) DatabaseSelectionChanged?.Invoke(this, EventArgs.Empty); }
    public void NavigateSelectedDatabaseReference()
    {
        var context = SelectedDatabaseContext();
        if (context?.Records.Length == 1)
        {
            foreach (var hover in hovers) hover.Close();
            var result = Host.Workspace.Navigate(context.Records[0].Reference);
            if (!result.Succeeded) Host.Report(result.Message);
        }
        else
        {
            RelatedDataRequested?.Invoke(this, EventArgs.Empty);
            Host.Report(context?.Explanation ?? "Поставьте курсор на ключ QuestComplete или точный ключ текстового ресурса.");
        }
    }
    readonly ConditionalWeakTable<HoverHelp, DatabaseContext> databaseHelp = new();
    public DatabaseContext? SelectedDatabaseContext(WorkspaceSnapshot? snapshot = null)
    {
        try
        {
            if (Current.Text != CodeEditor.Text) return null;
            var analysis = Current;
            return DatabaseReferences.Resolve(analysis, CodeEditor.CaretIndex, snapshot ?? Host.Workspace.Snapshot());
        }
        catch (System.IO.InvalidDataException) { return null; }
    }

    HoverHelp? EnrichDatabaseHelp(Analysis analysis, int position, HoverHelp? original)
    {
        if (!analysis.Tokens.Any(t => t.Start <= position && t.End > position &&
            (t.Kind == "string" || t.Kind == "number" || QuestStateReference.Supports(t.Value) || t.Value.Equals("QuestComplete", StringComparison.OrdinalIgnoreCase)))) return original;
        var context = DatabaseReferences.Resolve(analysis, position, Host.Workspace.Snapshot());
        if (context == null) return original;
        var help = new HoverHelp(original?.Title ?? context.Title, original?.Kind ?? "Данные текущей БД",
            (original == null ? "" : original.Body + "\n\n") + context.Title + "\n" + context.Description,
            "Ctrl+Enter — открыть запись под курсором. «Данные БД» — постоянная панель. Источник: текущий снимок Редактора БД.");
        databaseHelp.Add(help, context);
        return help;
    }

    void AddDatabaseNavigation(StackPanel panel, HoverHelp help)
    {
        if (!databaseHelp.TryGetValue(help, out var context)) return;
        foreach (var record in context.Records)
        {
            var button = new Button { Content = "Открыть в БД: " + record.Name, Margin = new Thickness(0, 8, 0, 0) };
            button.Click += (_, _) =>
            {
                var result = Host.Workspace.Navigate(record.Reference);
                if (!result.Succeeded) Host.Report(result.Message);
            };
            panel.Children.Add(button);
        }
    }

    public void DatabaseChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(DatabaseChanged); return; }
        foreach (var hover in hovers) hover.Close();
        RenderScenario();
        RefreshQuestAtlas();
    }
}
