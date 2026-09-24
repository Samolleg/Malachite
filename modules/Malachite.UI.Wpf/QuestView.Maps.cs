using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public partial class QuestView
{
    WorkspaceStamp? mapsWorkspace;
    string mapsStatus = "";
    public async Task SynchronizeMapsAsync(WorkspaceStamp? expected = null)
    {
        if (!Dispatcher.CheckAccess()) { await Dispatcher.InvokeAsync(() => SynchronizeMapsAsync(expected)).Task.Unwrap(); return; }
        if (stopped || Host.Files is not IModuleFileBrowser browser || WorkspaceMaps.Connect(Host) is not { } service) return;
        var stamp = expected ?? Host.Workspace.Snapshot().Stamp;
        if (mapsWorkspace is { } old && old.SessionId == stamp.SessionId && old.Generation == stamp.Generation) return;
        var actual = Host.Workspace.Snapshot().Stamp;
        if (stamp.SessionId != actual.SessionId || stamp.Generation != actual.Generation) return;
        mapsWorkspace = stamp;
        CancelFolderIndex(); indexedMaps.Clear(); folderFiles.Clear(); atlasIndexErrors.Clear();
        int version = folderVersion;
        using var cancellation = new CancellationTokenSource(); folderCancellation = cancellation;
        bool CurrentScan() => !stopped && version == folderVersion && !cancellation.IsCancellationRequested;
        folderIndexing = true; folderProgress = "Поиск Maps рядом с БД…"; mapsStatus = "";
        FillNavigation(); RefreshQuestAtlas(); SetStatus(folderProgress);
        try
        {
            var discovery = await service.Discover(stamp, cancellation.Token);
            if (!CurrentScan()) return;
            if (!discovery.Status.Succeeded)
            {
                mapsStatus = discovery.Status.Status == ResultStatus.Unavailable ? "Maps рядом с БД не найдена · можно выбрать «Папка MOB…»" : "Maps: " + discovery.Status.Message;
                return;
            }
            var files = discovery.Paths;
            folderFiles.AddRange(files); FillNavigation();
            folderProgress = $"Проверка индекса Maps: 0 / {files.Length}"; RefreshQuestAtlas(); SetStatus(folderProgress);
            var cache = await service.Load(stamp, cancellation.Token);
            if (!CurrentScan()) return;
            var progress = new Progress<(int Done, string Name)>(p =>
            {
                if (!CurrentScan() || !folderIndexing) return;
                folderProgress = $"Проверка индекса Maps: {p.Done} / {files.Length} · {p.Name}";
                AtlasStatus.Text = folderProgress; SetStatus(folderProgress);
            });
            var result = await Task.Run(() => MapIndexCache.Build(files, cache, Registry, browser,
                (done, name) => ((IProgress<(int, string)>)progress).Report((done, name)), cancellation.Token));
            if (!CurrentScan()) return;
            indexedMaps.AddRange(result.Maps); atlasIndexErrors.AddRange(result.Errors);
            mapsStatus = $"Maps: {result.Maps.Length} карт · из кэша {result.Reused} · обработано {result.Parsed}";
            // The scan remains useful even if cache storage is full/unavailable.
            var saved = await service.Save(stamp, result.Cache, cancellation.Token);
            if (!CurrentScan()) return;
            if (!saved.Succeeded) mapsStatus += " · кэш не сохранён: " + saved.Message;
            FillNavigation();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (CurrentScan()) mapsStatus = "Не удалось обновить индекс Maps: " + ex.Message; }
        finally
        {
            if (CurrentScan())
            {
                folderIndexing = false; folderCancellation = null;
                RefreshQuestAtlas(); SetStatus(mapsStatus);
            }
        }
    }
}
