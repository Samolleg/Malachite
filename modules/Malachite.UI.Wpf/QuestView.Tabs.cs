using System.Collections.Immutable;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

internal sealed record DocumentViewState(int Caret, int SelectionLength, double Vertical, double Horizontal,
    string Mode, string Search, string Layout);
internal sealed record TabRecovery(RecoveryState State, DocumentViewState View);

public partial class QuestView
{
    sealed class OpenTab(Guid id, Workspace workspace, RecoveryState state, DocumentViewState view)
    {
        public Guid Id = id;
        public Workspace Workspace = workspace;
        public RecoveryState State = state;
        public DocumentViewState View = view;
        public bool Conflict, Dirty;
        public StackPanel? Panel;
        public Button? Button;
    }
    readonly List<OpenTab> openTabs = [];
    OpenTab? activeTab;
    bool historyCommand;
    DocumentViewState CaptureDocumentView() => new(CodeEditor.SelectionStart, CodeEditor.SelectionLength,
        CodeEditor.VerticalOffset, CodeEditor.HorizontalOffset, mode, SearchBox.Text,
        QuestAtlasPanel.Visibility == Visibility.Visible ? "quests" : codeOnly ? "code" : !readable ? "links" : scenarioOnly ? "scenario" : "together");
    RecoveryState CaptureTabState() => new(2, [], CodeEditor.Text, cardDirty, ConditionBox.Text, ActionBox.Text,
        ElseBox.Text, Selected?.Name, savedText, UseElse.IsChecked == true, CodeEditor.CaretIndex, documentId, saveArtifactKind);
    void CaptureActiveTab()
    {
        if (activeTab == null) return;
        Workspace.Text = CodeEditor.Text;
        activeTab.Workspace = Workspace; activeTab.State = CaptureTabState(); activeTab.View = CaptureDocumentView();
        activeTab.Conflict = publishConflict; activeTab.Dirty = dirty || cardDirty || CodeEditor.Text != savedText;
    }
    void AddActiveTab()
    {
        if (documentId == null) return;
        activeTab = new(documentId.Value, Workspace, CaptureTabState(), CaptureDocumentView()) { Dirty = dirty };
        openTabs.Add(activeTab); RefreshDocumentTabs();
    }
    void SuspendCurrentDocument()
    {
        CaptureActiveTab(); activeTab = null; documentId = null;
    }
    bool UseExistingTab(string path)
    {
        var tab = openTabs.FirstOrDefault(t => !string.IsNullOrEmpty(path) && t.Workspace.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (tab == null) return false;
        SwitchDocument(tab.Id); return true;
    }
    void EnsureIndexedTarget(string path)
    {
        var tab = openTabs.FirstOrDefault(t => t.Workspace.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (tab == null || tab.Workspace.Mob == null) return;
        string code = tab == activeTab ? CodeEditor.Text : tab.Conflict ? tab.State.Code : documentHost?.Documents.Get(tab.Id)?.Text ?? tab.State.Code;
        if (code != tab.Workspace.Mob.Text) throw new System.IO.InvalidDataException("Во вкладке «" + tab.Workspace.Name + "» изменён код. Откройте её и обновите цепочку / поиск: позиции из индекса больше не соответствуют тексту.");
    }
    public bool SwitchDocument(Guid id)
    {
        var tab = openTabs.FirstOrDefault(t => t.Id == id);
        if (tab == null || stopped) return false;
        if (tab == activeTab) { ShowHome(false); return true; }
        CaptureActiveTab();
        var state = tab.State; var workspace = tab.Workspace;
        var snapshot = documentHost?.Documents.Get(id);
        // Rejected local input belongs to this tab and must never be replaced by a host snapshot.
        if (snapshot != null && !tab.Conflict)
        {
            (state, workspace) = DocumentCodec.Decode(snapshot.State.AsSpan(), snapshot.Text, documentBlobs);
            state = state with { Caret = tab.View.Caret, SelectedName = tab.State.SelectedName,
                SavedText = snapshot.IsDirty ? state.SavedText : snapshot.Text };
        }
        activeTab = tab; documentId = id; acceptedRevision = snapshot?.Revision ?? acceptedRevision;
        publishConflict = tab.Conflict;
        ApplyRecovered(state, workspace);
        dirty = tab.Conflict || state.CardDirty || (snapshot?.IsDirty ?? tab.Dirty);
        RestoreDocumentView(tab.View); ShowHome(false); CaptureActiveTab(); RefreshDocumentTabs();
        return true;
    }
    void RestoreDocumentView(DocumentViewState view)
    {
        mode = view.Mode is "scripts" or "functions" or "flags" ? view.Mode : "scripts";
        SearchBox.Text = view.Search; FillNavigation();
        switch (view.Layout)
        {
            case "code": Code_Click(this, new()); break;
            case "scenario": ReadableOnly_Click(this, new()); break;
            case "links": Mixed_Click(this, new()); break;
            case "quests": Atlas_Click(this, new()); break;
            default: Scenario_Click(this, new()); break;
        }
        internalUpdate = true;
        try { CodeEditor.Select(Math.Clamp(view.Caret, 0, CodeEditor.Text.Length), Math.Clamp(view.SelectionLength, 0, CodeEditor.Text.Length - Math.Clamp(view.Caret, 0, CodeEditor.Text.Length))); }
        finally { internalUpdate = false; }
        CodeEditor.UpdateLayout(); CodeEditor.ScrollToVerticalOffset(view.Vertical); CodeEditor.ScrollToHorizontalOffset(view.Horizontal);
        ExplainAt(CodeEditor.CaretIndex);
    }
    void RefreshDocumentTabs()
    {
        if (DocumentTabs == null) return;
        if (activeTab != null) activeTab.Dirty = dirty || cardDirty || CodeEditor.Text != savedText;
        foreach (var tab in openTabs)
        {
            if (tab.Panel == null)
            {
                tab.Panel = new StackPanel { Orientation = Orientation.Horizontal };
                tab.Button = new Button { ToolTip = tab.Workspace.SourcePath, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0) };
                tab.Button.Click += (_, _) => SwitchDocument(tab.Id);
                var close = new Button { Content = "×", ToolTip = "Закрыть файл", Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(2, 0, 8, 0) };
                close.Click += (_, _) => CloseDocumentTab(tab.Id);
                tab.Panel.Children.Add(tab.Button); tab.Panel.Children.Add(close);
            }
            string caption = (tab.Dirty || tab.Conflict ? "● " : "") + tab.Workspace.Name;
            if (!Equals(tab.Button!.Content, caption)) tab.Button.Content = caption;
            tab.Button.BorderBrush = tab == activeTab ? MalachiteTheme.Accent : MalachiteTheme.Border;
        }
        if (!DocumentTabs.Children.Cast<object>().SequenceEqual(openTabs.Select(t => t.Panel)))
        { DocumentTabs.Children.Clear(); foreach (var tab in openTabs) DocumentTabs.Children.Add(tab.Panel!); }
    }
    public bool CloseDocumentTab(Guid id)
    {
        if (!SwitchDocument(id) || !CanLeave()) return false;
        CloseCurrentDocument();
        if (openTabs.LastOrDefault() is { } next) SwitchDocument(next.Id);
        else
        {
            suppressPublish = true;
            try { LoadWorkspace(new() { Name = "", Text = "" }, false); }
            finally { suppressPublish = false; }
            documentOpen = false; ShowHome(true); EncodingText.Text = "";
        }
        RefreshDocumentTabs(); return true;
    }
    bool AllTabsPending => openTabs.Any(t => t != activeTab && (t.Dirty || t.Conflict || t.State.CardDirty));
    bool ResolveAllCards()
    {
        if (!ResolveCard()) return false;
        CaptureActiveTab(); var original = activeTab;
        foreach (var tab in openTabs.ToArray())
        {
            if (!tab.Conflict && !tab.State.CardDirty) continue;
            SwitchDocument(tab.Id);
            if (tab.Conflict) { Host.Report("Сохранение остановлено: конфликт в " + tab.Workspace.Name + ". Локальный ввод сохранён в recovery."); return false; }
            if (!ResolveCard()) return false;
            CaptureActiveTab();
        }
        if (original != null) SwitchDocument(original.Id);
        return true;
    }
    bool ConfirmAllDocuments()
    {
        if (!AllTabsPending) return CanLeave();
        if (!ResolveAllCards()) return false;
        if (!HasPendingChanges) return true;
        var answer = MessageBox.Show(Window.GetWindow(this), "Сохранить изменения во всех открытых файлах Малахита перед закрытием?", "Открытые сценарии", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && SaveChanges();
    }
    TabRecovery[] CaptureOtherTabs()
    {
        CaptureActiveTab();
        return openTabs.Where(t => t != activeTab).Select(t => new TabRecovery(t.State with {
            Version = 2, Source = null, Project = t.Workspace.SaveProject(), Code = t.Workspace.Text, Tabs = null, View = null }, t.View)).ToArray();
    }
    void RestoreOtherTabs(RecoveryState state)
    {
        // Decode every fallback first; an invalid session must not discard existing open input.
        var others = (state.Tabs ?? []).Select(t => (Recovery: t, Decoded: DocumentCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(t.State)))).ToArray();
        if (activeTab == null && documentId != null) AddActiveTab();
        foreach (var item in others)
        {
            var (local, workspace) = item.Decoded;
            if (local.DocumentId == documentId) continue;
            var existing = local.DocumentId is { } id ? documentHost?.Documents.Get(id) : null;
            bool conflict = false;
            if (existing != null)
            {
                if (existing.OwnerId != "malachite" || existing.FormatId != "malachite-script" || existing.StateVersion != 2) throw new InvalidOperationException("Несовместимая вкладка recovery.");
                var (official, _) = DocumentCodec.Decode(existing.State.AsSpan(), existing.Text, documentBlobs);
                conflict = local.Code != existing.Text || local.CardDirty != official.CardDirty || local.CardDirty &&
                    (local.Actions != official.Actions || local.Conditions != official.Conditions || local.Otherwise != official.Otherwise || local.UseElse != official.UseElse);
                if (!existing.IsDirty && !conflict) local = local with { SavedText = existing.Text };
            }
            else if (documentHost != null)
            {
                existing = documentHost.Documents.Open(new("malachite", "malachite-script", 2, workspace.Name, workspace.Text,
                    ImmutableArray.CreateRange(JsonSerializer.SerializeToUtf8Bytes(local)), null, true) { SaveArtifactKind = local.SaveArtifactKind });
            }
            if (existing == null) continue;
            openTabs.RemoveAll(t => t.Id == existing.DocumentId);
            openTabs.Add(new(existing.DocumentId, workspace, local with { DocumentId = existing.DocumentId }, item.Recovery.View)
                { Conflict = conflict, Dirty = existing.IsDirty || local.CardDirty || conflict });
        }
        if (state.View != null) RestoreDocumentView(state.View);
        CaptureActiveTab(); RefreshDocumentTabs();
    }
}
