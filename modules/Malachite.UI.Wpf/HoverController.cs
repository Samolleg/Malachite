using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace QuestStudio;

// Hit-testing uses the native TextBox layout: scrolling, font metrics and DPI stay consistent.
public sealed class HoverController : IDisposable
{
    readonly QuestView window;
    readonly TextBox editor;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer leave = new() { Interval = TimeSpan.FromMilliseconds(180) };
    ToolTip? tooltip;
    Point point;
    int position = -1;
    readonly List<Action> detach = [];
    bool disposed;
    public HoverController(QuestView window, TextBox editor)
    {
        this.window = window; this.editor = editor;
        MouseEventHandler move = (_, e) => MoveTo(e.GetPosition(editor));
        MouseEventHandler left = (_, _) => leave.Start();
        KeyEventHandler key = (_, _) => Close();
        TextChangedEventHandler changed = (_, _) => Close();
        ScrollChangedEventHandler scroll = (_, e) => { if (e.VerticalChange != 0 || e.HorizontalChange != 0) Close(); };
        editor.MouseMove += move; editor.MouseLeave += left; editor.PreviewKeyDown += key; editor.TextChanged += changed;
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, scroll);
        detach.Add(() => { editor.MouseMove -= move; editor.MouseLeave -= left; editor.PreviewKeyDown -= key; editor.TextChanged -= changed; editor.RemoveHandler(ScrollViewer.ScrollChangedEvent, scroll); });
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!editor.IsMouseOver || tooltip?.IsMouseOver == true) return;
            ShowPendingHelp();
        };
        leave.Tick += (_, _) => { leave.Stop(); if (tooltip?.IsMouseOver != true && !editor.IsMouseOver) Close(); };
        RoutedEventHandler unload = (_, _) => { timer.Stop(); leave.Stop(); Close(); };
        window.Unloaded += unload; detach.Add(() => window.Unloaded -= unload);
    }
    void ShowPendingHelp()
    {
        var help = window.HelpAt(editor, position);
        if (tooltip != null) tooltip.IsOpen = false;
        tooltip = null;
        if (help == null) return;
        tooltip = window.MakeTip(help);
        tooltip.PlacementTarget = editor; tooltip.Placement = PlacementMode.Relative;
        tooltip.HorizontalOffset = point.X + 12; tooltip.VerticalOffset = point.Y + 22;
        tooltip.MouseEnter += (_, _) => { timer.Stop(); leave.Stop(); position = -1; }; tooltip.MouseLeave += (_, _) => leave.Start();
        tooltip.IsOpen = true;
    }
    void MoveTo(Point next)
    {
        leave.Stop();
        int index = editor.GetCharacterIndexFromPoint(next, false);
        if (index == position) return;
        timer.Stop(); point = next; position = index;
        // Keep the old card available only during the short crossing/dwell delay.
        // Entering its interactive surface cancels the pending replacement.
        if (index >= 0 || tooltip?.IsOpen == true) timer.Start();
        else Close();
    }
    public void Close() { timer.Stop(); if (tooltip != null) tooltip.IsOpen = false; tooltip = null; position = -1; }
    public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); leave.Stop(); Close(); foreach (var action in detach) action(); detach.Clear(); }
}
