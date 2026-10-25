using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DatabaseEditor.Modules.V1;
using DatabaseEditor.Modules.Wpf.V1;
using QuestStudio;

namespace Malachite.Module;
public sealed class Entry : IWpfEditorModule
{
    private IModuleHost? host;
    private QuestView? view;
    private bool active;
    private bool disposed;
    public void Initialize(IModuleHost host) { ObjectDisposedException.ThrowIf(disposed, this); if (this.host != null) throw new InvalidOperationException("Модуль уже инициализирован."); this.host = host ?? throw new ArgumentNullException(nameof(host)); host.Workspace.Changed += Changed; }
    public FrameworkElement CreateView()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (host == null) throw new InvalidOperationException("Сначала вызовите Initialize.");
        if (view is not null) throw new InvalidOperationException("View уже создан.");
        view = new QuestView(host!);
        _ = view.SynchronizeMapsAsync();
        return view;
    }
    private void Changed(object? sender, WorkspaceEvent e) { if (disposed) return; if (view != null) _ = view.SynchronizeMapsAsync(e.Stamp); if (active && view?.IsPublishingDocument != true) { view?.DatabaseChanged(); } }
    public void Activate(bool active) { if (disposed) return; this.active = active; }
    public bool Execute(ModuleCommand command) => !disposed && active && (view?.RunCommand(command) ?? false);
    public bool CanClose() => view?.ConfirmClose() ?? true;
    public bool IsDirty => view?.HasPendingChanges ?? false;
    public ReadOnlyMemory<byte> CaptureRecovery() => view?.CaptureState() ?? [];
    public Result RestoreRecovery(ReadOnlyMemory<byte> bytes) => view?.RestoreState(bytes) ?? new(ResultStatus.Unavailable);
    public void Dispose() { if (disposed) return; disposed = true; active = false; if (host is not null) host.Workspace.Changed -= Changed; view?.Stop(); host = null; }
}
