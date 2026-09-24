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
    private readonly ListBox links = new();
    private readonly TextBox search = new() { MinWidth = 200, Margin = new(8) };
    private bool active;
    private bool disposed;
    private Border? relatedPanel;
    private readonly TextBlock contextText = new() { TextWrapping = TextWrapping.Wrap, Margin = new(8) };
    public void Initialize(IModuleHost host) { ObjectDisposedException.ThrowIf(disposed, this); if (this.host != null) throw new InvalidOperationException("Модуль уже инициализирован."); this.host = host ?? throw new ArgumentNullException(nameof(host)); host.Workspace.Changed += Changed; }
    public FrameworkElement CreateView()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (host == null) throw new InvalidOperationException("Сначала вызовите Initialize.");
        if (view is not null) throw new InvalidOperationException("View уже создан.");
        view = new QuestView(host!);
        var background = MalachiteTheme.Surface;
        var foreground = MalachiteTheme.Text;
        var root = new Grid { Background = background };
        root.Children.Add(view);
        links.Background = background; links.Foreground = foreground; links.BorderThickness = new(0);
        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        textFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        links.ItemTemplate = new DataTemplate { VisualTree = textFactory };
        links.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(links, ScrollBarVisibility.Disabled);
        search.Style = (Style)view.Resources[typeof(TextBox)];
        links.Style = (Style)view.Resources[typeof(ListBox)];
        links.ItemContainerStyle = (Style)view.Resources[typeof(ListBoxItem)];
        search.Background = background; search.Foreground = foreground; search.ToolTip = "Поиск задания, брифинга или текста в текущей БД";
        var panel = new DockPanel();
        var heading = new DockPanel { Margin = new(8) };
        var close = new Button { Content = "Закрыть", Style = (Style)view.Resources[typeof(Button)] };
        DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
        heading.Children.Add(new TextBlock { Text = "Связанные данные БД", Foreground = foreground, FontSize = 17, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        contextText.Foreground = foreground;
        DockPanel.SetDock(contextText, Dock.Top); panel.Children.Add(contextText);
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search); panel.Children.Add(links);
        relatedPanel = new Border { Child = panel, Background = background, BorderBrush = MalachiteTheme.Border, BorderThickness = new(1), Padding = new(8), Width = 520, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
        root.Children.Add(relatedPanel);
        close.Click += (_, _) => { relatedPanel.Visibility = Visibility.Collapsed; view.Focus(); };
        view.RelatedDataRequested += ShowRelated;
        view.DatabaseSelectionChanged += SelectionChanged;
        search.TextChanged += (_, _) => RefreshLinks();
        links.MouseDoubleClick += (_, _) => { if (links.SelectedItem is Link link) { var r = host!.Workspace.Navigate(link.Record.Reference); if (!r.Succeeded) host.Report(r.Message); } };
        _ = view.SynchronizeMapsAsync();
        return root;
    }
    private void ShowRelated(object? sender, EventArgs e) { if (disposed || relatedPanel == null) return; relatedPanel.Visibility = Visibility.Visible; RefreshLinks(); search.Focus(); }
    private void SelectionChanged(object? sender, EventArgs e) { if (relatedPanel?.Visibility == Visibility.Visible) RefreshLinks(); }
    private sealed record Link(RecordDto Record)
    {
        public override string ToString() => Record.Name + " · " + Record.Label + "\n" + Record.Reference.TableKey + "\n" + string.Join("\n", Record.Texts.Select(t => t.ResourceKey + "\n" + t.Title + " — " + t.Description + (t.Modified ? " [не сохранено]" : "")));
    }
    private void RefreshLinks()
    {
        if (host is null || !active || disposed || relatedPanel?.Visibility != Visibility.Visible) return;
        if (!links.Dispatcher.CheckAccess()) { links.Dispatcher.BeginInvoke(RefreshLinks); return; }
        var snapshot = host.Workspace.Snapshot();
        var context = view?.SelectedDatabaseContext(snapshot);
        contextText.Text = context == null ? "Наведите мышь на ключ QuestComplete, GS-ключ q.зона.квест.подпункт или точный ключ текстового ресурса. Здесь — поиск по заданиям и брифингам; двойной щелчок открывает запись." : context.Title + "\n" + context.Explanation.Split('\n')[0] + (context.FocusText == null ? "" : "\n" + context.FocusText);
        var records = context?.Records ?? snapshot.Records.Where(r => r.Reference.TableKey.StartsWith("quests.")).ToArray();
        links.ItemsSource = records.Where(r =>
            (search.Text.Length == 0 || r.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || r.Label.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || r.Texts.Any(t => (t.ResourceKey + t.Title + t.Description).Contains(search.Text, StringComparison.OrdinalIgnoreCase))))
            .Select(r => new Link(r)).ToArray();
    }
    private void Changed(object? sender, WorkspaceEvent e) { if (disposed) return; if (view != null) _ = view.SynchronizeMapsAsync(e.Stamp); if (active && view?.IsPublishingDocument != true) { view?.DatabaseChanged(); RefreshLinks(); } }
    public void Activate(bool active) { if (disposed) return; this.active = active; if (active) RefreshLinks(); }
    public bool Execute(ModuleCommand command) => !disposed && active && (view?.RunCommand(command) ?? false);
    public bool CanClose() => view?.ConfirmClose() ?? true;
    public bool IsDirty => view?.HasPendingChanges ?? false;
    public ReadOnlyMemory<byte> CaptureRecovery() => view?.CaptureState() ?? [];
    public Result RestoreRecovery(ReadOnlyMemory<byte> bytes) => view?.RestoreState(bytes) ?? new(ResultStatus.Unavailable);
    public void Dispose() { if (disposed) return; disposed = true; active = false; if (host is not null) host.Workspace.Changed -= Changed; if (view != null) { view.RelatedDataRequested -= ShowRelated; view.DatabaseSelectionChanged -= SelectionChanged; } view?.Stop(); host = null; }
}
