using System.Text.Json;
using System.Windows.Controls;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;
public partial class QuestView
{
    public event EventHandler? RelatedDataRequested;
    void RelatedData_Click(object sender, System.Windows.RoutedEventArgs e) => RelatedDataRequested?.Invoke(this, EventArgs.Empty);
    public bool HasPendingChanges => dirty || cardDirty || CodeEditor.Text != savedText || AllTabsPending;
    public bool ConfirmClose() => ConfirmAllDocuments();
    bool stopped;
    bool SaveChanges()
    {
        if (stopped) return false;
        if (publishConflict) { Host.Report("Сохранение остановлено: конфликт сценария. Локальный ввод сохранён в recovery."); return false; }
        if (documentHost == null) { SetStatus("Хост не поддерживает сохранение исходного документа. Используйте «Копия MOB» или «Копия проекта»."); return false; }
        if (!ResolveAllCards()) return false;
        debounce.Stop(); RefreshAnalysis(false);
        if (publishConflict) return false;
        var report = documentHost.Save.SaveAll();
        string message = report.Complete ? "Сохранено: " + Workspace.Name + (openTabs.Count > 1 ? " и другие открытые файлы" : "") + "." : "Сохранение не завершено: " + string.Join("; ", report.Files.Select(f => f.Message));
        SetStatus(message); Host.Report(message);
        return report.Complete && !HasPendingChanges;
    }
    public void Stop() { if (stopped) return; stopped = true; CancelWorkbenchSearch(); QuestAtlasPanel.LayoutUpdated -= Atlas_LayoutUpdated; StopCompletion(); debounce.Stop(); CancelFolderIndex(); foreach (var hover in hovers) hover.Dispose(); DetachDocumentHost(); }
    public bool RunCommand(ModuleCommand command)
    {
        if (stopped) return false;
        if (publishConflict && command is ModuleCommand.Save or ModuleCommand.Undo or ModuleCommand.Redo)
        { Host.Report("Команда остановлена: конфликт сценария. Локальный ввод сохранён в recovery."); return true; }
        switch (command)
        {
            case ModuleCommand.Save:
                SaveChanges(); return true;
            case ModuleCommand.Undo: RunHistory(false); return true;
            case ModuleCommand.Redo: RunHistory(true); return true;
            case ModuleCommand.Find:
                ShowFind(false); return true;
            case ModuleCommand.Validate: Check_Click(this, new()); return true;
            default: return false;
        }
    }
    void RunHistory(bool redo)
    {
        historyCommand = true;
        try
        {
            if (documentHost != null)
            {
                var before = documentHost.Documents.List("malachite").ToDictionary(d => d.DocumentId, d => d.Revision);
                var result = redo ? documentHost.Transactions.Redo() : documentHost.Transactions.Undo(); ReportResult(result);
                if (result.Succeeded && documentHost.Documents.List("malachite").FirstOrDefault(d => before.TryGetValue(d.DocumentId, out var revision) && d.Revision != revision) is { } changed && changed.DocumentId != documentId)
                    SwitchDocument(changed.DocumentId);
            }
            else if (redo) CodeEditor.Redo(); else CodeEditor.Undo();
        }
        finally { historyCommand = false; }
    }
    MobDocument? unavailableBlob;
    public byte[] CaptureState() => CaptureState(true);
    byte[] CaptureState(bool includeAtlas)
    {
        // Include input before the debounce timer and before card publication.
        Workspace.Text = CodeEditor.Text;
        WorkspaceReference? source = null;
        if (!includeAtlas && documentBlobs != null && Workspace.Mob is { } mob && mob != unavailableBlob)
        {
            try { source = new(Workspace.Name, Workspace.SourcePath, Workspace.TextEncoding.CodePage, Workspace.TextPreamble, documentBlobs.Put(mob)); }
            catch (Exception ex)
            {
                unavailableBlob = mob;
                Host.Report("Компактная история недоступна; используется полный снимок MOB. " + ex.Message);
            }
        }
        // Transaction text already carries the code. Plain scripts need metadata only;
        // recovery exported out of the host still contains the complete document.
        bool plainTransaction = !includeAtlas && Workspace.Mob == null;
        byte[] project = source != null ? [] : plainTransaction
            ? new Workspace { Name = Workspace.Name, SourcePath = Workspace.SourcePath, Text = "", TextEncoding = Workspace.TextEncoding, TextPreamble = Workspace.TextPreamble }.SaveProject()
            : Workspace.SaveProject();
        return JsonSerializer.SerializeToUtf8Bytes(new RecoveryState(source == null ? 2 : 3, project, source == null && !plainTransaction ? CodeEditor.Text : "",
            cardDirty, ConditionBox.Text, ActionBox.Text, ElseBox.Text, Selected?.Name, savedText, UseElse.IsChecked == true, CodeEditor.CaretIndex, documentId, saveArtifactKind, includeAtlas
                ? new AtlasRecovery(indexedMaps.ToArray(), folderFiles.ToArray(), atlasIndexErrors.ToArray(),
                    false, AtlasIndirect.IsChecked == true, atlasQuest, QuestAtlasPanel.Visibility == System.Windows.Visibility.Visible)
                : null, source, documentOpen, HomePanel.Visibility == System.Windows.Visibility.Visible,
                includeAtlas ? CaptureOtherTabs() : null, includeAtlas ? CaptureDocumentView() : null));
    }
    public Result RestoreState(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var (r, workspace) = DocumentCodec.Decode(bytes.Span);
            foreach (var tab in r.Tabs ?? [])
            {
                DocumentCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(tab.State));
                if (tab.State.DocumentId is { } tabId && documentHost?.Documents.Get(tabId) is { } existingTab)
                {
                    if (existingTab.OwnerId != "malachite" || existingTab.FormatId != "malachite-script" || existingTab.StateVersion != 2)
                        return new(ResultStatus.Unsupported, "Сохранённая вкладка имеет несовместимый формат.");
                    DocumentCodec.Decode(existingTab.State.AsSpan(), existingTab.Text, documentBlobs);
                }
            }
            if (!r.HasDocument)
            {
                CloseCurrentDocument(); documentId = null;
                suppressPublish = true;
                try { LoadWorkspace(new Workspace { Name = "", Text = "" }, false); }
                finally { suppressPublish = false; }
                documentOpen = false; ShowHome(true); EncodingText.Text = ""; SetStatus("Выберите карту, откройте файл или создайте новую цепочку."); RestoreAtlas(r.Atlas);
                return Result.Ok;
            }
            if (documentHost != null && r.DocumentId is { } recoveredId && documentHost.Documents.Get(recoveredId) is { } existing)
            {
                if (existing.OwnerId != "malachite" || existing.FormatId != "malachite-script" || existing.StateVersion != 2)
                    return new(ResultStatus.Unsupported, "Сохранённый документ имеет несовместимый формат.");
                // Validate before switching ownership; preserve failed local input from module recovery.
                var (official, officialWorkspace) = DocumentCodec.Decode(existing.State.AsSpan(), existing.Text, documentBlobs);
                if (documentId != recoveredId) CloseCurrentDocument();
                documentId = recoveredId; acceptedRevision = existing.Revision;
                publishConflict = r.Code != existing.Text || r.CardDirty != official.CardDirty ||
                    (r.CardDirty && (r.Conditions != official.Conditions || r.Actions != official.Actions || r.Otherwise != official.Otherwise || r.UseElse != official.UseElse));
                ApplyRecovered(publishConflict ? r : official with { Caret = r.Caret }, publishConflict ? workspace : officialWorkspace);
                if (!existing.IsDirty && !publishConflict) savedText = CodeEditor.Text;
                dirty = existing.IsDirty || publishConflict;
                RestoreAtlas(r.Atlas);
                RestoreOtherTabs(r);
                ShowHome(r.Home);
                return Result.Ok;
            }
            CloseCurrentDocument();
            ApplyRecovered(r, workspace);
            RegisterCurrentDocument(null, true);
            RestoreAtlas(r.Atlas);
            RestoreOtherTabs(r);
            ShowHome(r.Home);
            return Result.Ok;
        }
        catch (Exception ex) { return new(ResultStatus.Failed, ex.Message); }
    }
    void RestoreAtlas(AtlasRecovery? state)
    {
        CancelFolderIndex();
        indexedMaps.Clear(); folderFiles.Clear(); atlasIndexErrors.Clear();
        atlasUpdating = true;
        try
        {
            if (state != null)
            {
                indexedMaps.AddRange(state.Maps ?? []); folderFiles.AddRange(state.Files ?? []);
                atlasIndexErrors.AddRange(state.Errors ?? []);
            }
            // Legacy AllMaps recovery is ignored: inventory always belongs to the current map.
            AtlasIndirect.IsChecked = state?.Indirect == true;
            atlasQuest = state?.Quest;
            QuestAtlasPanel.Visibility = state?.Visible == true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }
        finally { atlasUpdating = false; }
        // Cached models are snapshots only. Navigation still uses host-authorized reads and hash checks.
        FillNavigation(); RefreshQuestAtlas();
        // Recovery can cancel an in-flight discovery. Revalidate the restored snapshot
        // against the current DB rather than treating recovered map hashes as current.
        mapsWorkspace = null;
        _ = SynchronizeMapsAsync();
    }
    void ApplyRecovered(RecoveryState r, Workspace workspace)
    {
            suppressPublish = true;
            try {
            LoadWorkspace(workspace, true); savedText = r.SavedText;
            saveArtifactKind = r.SaveArtifactKind;
            Selected = Current.Blocks.FirstOrDefault(b => b.Name == r.SelectedName);
            if (Selected != null) FillCard(Selected);
            if (r.CardDirty)
            {
                internalUpdate = true;
                ConditionBox.Text = r.Conditions; ActionBox.Text = r.Actions; ElseBox.Text = r.Otherwise;
                UseElse.IsChecked = r.UseElse; ElseBox.IsEnabled = r.UseElse;
                internalUpdate = false; cardDirty = true; CodeEditor.IsReadOnly = true;
            }
            CodeEditor.CaretIndex = Math.Clamp(r.Caret, 0, CodeEditor.Text.Length);
            dirty = cardDirty || CodeEditor.Text != savedText;
            } finally { internalUpdate = false; suppressPublish = false; }
    }
}
