using System.Collections.Immutable;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public partial class QuestView
{
    IModuleDocumentHost? documentHost;
    DocumentBlobs? documentBlobs;
    Guid? documentId;
    bool suppressPublish, publishing;
    long acceptedRevision;
    WorkspaceStamp? documentStamp;
    void DocumentWorkspaceChanged(object? sender, WorkspaceEvent e) => documentStamp = e.Stamp;
    bool publishConflict;
    string saveArtifactKind = "malachite-project";
    public string SaveArtifactKind => saveArtifactKind;
    public bool IsPublishingDocument => publishing;
    static string ArtifactForPath(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mob" => "mob", ".txt" or ".scr" => "script-text", _ => "malachite-project"
    };
    public Guid? DocumentId => documentId;

    void InitializeDocuments()
    {
        documentHost = Host as IModuleDocumentHost;
        documentBlobs = DocumentBlobs.Connect(Host);
        if (documentHost == null) return;
        var result = documentHost.Documents.RegisterCodec("malachite", "malachite-script", 2, new DocumentCodec(Host));
        if (!result.Succeeded) throw new InvalidOperationException(result.Message);
        documentHost.Documents.Changed += DocumentChanged;
        documentStamp = Host.Workspace.Snapshot().Stamp;
        Host.Workspace.Changed += DocumentWorkspaceChanged;
        RegisterCurrentDocument(null, false);
    }
    void RegisterCurrentDocument(Guid? source, bool modified)
    {
        if (documentHost == null || suppressPublish || !documentOpen) return;
        var document = documentHost.Documents.Open(new("malachite", "malachite-script", 2,
            Workspace.Name, CodeEditor.Text, ImmutableArray.CreateRange(CaptureState(false)), source, modified) { SaveArtifactKind = saveArtifactKind });
        documentId = document.DocumentId;
        acceptedRevision = document.Revision; publishConflict = false;
        CodeEditor.IsUndoEnabled = false;
    }
    void CloseCurrentDocument()
    {
        if (documentHost == null || documentId == null || suppressPublish) return;
        // Host recovery can replace the temporary document before module reattachment.
        if (documentHost.Documents.Get(documentId.Value) == null) { documentId = null; return; }
        var result = documentHost.Documents.Close(documentId.Value, acceptedRevision, true);
        if (!result.Succeeded) throw new InvalidOperationException(result.Message);
        documentId = null;
    }
    void DetachDocumentHost()
    {
        if (documentHost != null) { documentHost.Documents.Changed -= DocumentChanged; Host.Workspace.Changed -= DocumentWorkspaceChanged; }
    }
    void ReportResult(Result result) { if (!result.Succeeded) { SetStatus(result.Message); Host.Report(result.Message); } }
    void PublishDocument(string description)
    {
        if (suppressPublish || publishing || stopped || documentHost == null || documentId == null) return;
        var previous = documentHost.Documents.Get(documentId.Value);
        if (previous == null) { SetStatus("Сеанс сценария недоступен. Сохраните recovery перед повторным открытием."); return; }
        publishing = true;
        try
        {
            var result = documentHost.Transactions.Apply(new(Guid.NewGuid(), documentStamp ??= Host.Workspace.Snapshot().Stamp,
                description, [new UpdateDocument(previous.DocumentId, acceptedRevision, CodeEditor.Text, ImmutableArray.CreateRange(CaptureState(false)))]));
            publishConflict = !result.Succeeded;
            if (result.Succeeded) acceptedRevision = documentHost.Documents.Get(previous.DocumentId)!.Revision;
            ReportResult(result);
            // Keep rejected input in the view/recovery. Never silently replace it with a stale host snapshot.
        }
        finally { publishing = false; }
    }
    void DocumentChanged(object? sender, DocumentEvent e)
    {
        if (stopped || e.DocumentId != documentId || publishing) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => DocumentChanged(sender, e)); return; }
        var snapshot = documentHost?.Documents.Get(e.DocumentId);
        if (snapshot == null) return;
        if (publishConflict) { Host.Report("Локальный ввод конфликтует с сеансом; он сохранён в recovery и не заменён автоматически."); return; }
        try
        {
            var (state, workspace) = DocumentCodec.Decode(snapshot.State.AsSpan(), snapshot.Text, documentBlobs);
            if (snapshot.Text != CodeEditor.Text || state.CardDirty || cardDirty)
            {
                string before = CodeEditor.Text, after = snapshot.Text;
                int prefix = 0, suffix = 0;
                while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
                while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
                double vertical = CodeEditor.VerticalOffset, horizontal = CodeEditor.HorizontalOffset;
                bool home = HomePanel.Visibility == System.Windows.Visibility.Visible;
                int caret = before == after ? CodeEditor.CaretIndex : after.Length - suffix;
                if (!state.CardDirty && !cardDirty)
                {
                    // Text undo/redo must not synchronously reparse and rebuild the entire UI.
                    internalUpdate = true;
                    try { Workspace = workspace; CodeEditor.Text = after; CodeEditor.Select(Math.Clamp(caret, 0, after.Length), 0); }
                    finally { internalUpdate = false; }
                    savedText = state.SavedText; saveArtifactKind = state.SaveArtifactKind;
                    suppressPublish = true;
                    try { Code_Changed(CodeEditor, new System.Windows.Controls.TextChangedEventArgs(System.Windows.Controls.TextBox.TextChangedEvent, System.Windows.Controls.UndoAction.None)); }
                    finally { suppressPublish = false; }
                }
                else ApplyRecovered(state with { Caret = caret }, workspace);
                CodeEditor.ScrollToVerticalOffset(vertical); CodeEditor.ScrollToHorizontalOffset(horizontal);
                if (home && HomePanel.Visibility != System.Windows.Visibility.Visible) ShowHome(true);
            }
            if (!snapshot.IsDirty) savedText = CodeEditor.Text;
            dirty = snapshot.IsDirty;
            acceptedRevision = snapshot.Revision;
        }
        catch (Exception ex) { Host.Report("Не удалось принять изменение сценария: " + ex.Message); }
    }
    public void LoadOpenedFile(OpenedFile file)
    {
        var workspace = Workspace.FromBytes(file.DisplayPath, file.Content.ToArray());
        CloseCurrentDocument();
        suppressPublish = true;
        try { LoadWorkspace(workspace, false); }
        finally { suppressPublish = false; }
        saveArtifactKind = ArtifactForPath(file.DisplayPath);
        RegisterCurrentDocument(file.SourceToken == Guid.Empty ? null : file.SourceToken, false);
    }
}
