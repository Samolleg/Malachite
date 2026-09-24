using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace QuestStudio;

// Hit-testing uses the native TextBox layout: scrolling, font metrics and DPI stay consistent.
public sealed class HoverController
{
    readonly MainWindow window;
    readonly TextBox editor;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer leave = new() { Interval = TimeSpan.FromMilliseconds(180) };
    ToolTip? tooltip;
    Point point;
    int position = -1;
    public HoverController(MainWindow window, TextBox editor)
    {
        this.window = window; this.editor = editor;
        editor.MouseMove += (_, e) =>
        {
            leave.Stop(); point = e.GetPosition(editor);
            int index = editor.GetCharacterIndexFromPoint(point, false);
            if (index == position) return;
            Close(); position = index;
            if (index >= 0) timer.Start();
        };
        editor.MouseLeave += (_, _) => leave.Start();
        editor.PreviewKeyDown += (_, _) => Close();
        editor.TextChanged += (_, _) => Close();
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => { if (e.VerticalChange != 0 || e.HorizontalChange != 0) Close(); }));
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var help = window.HelpAt(editor, position);
            if (help == null || !editor.IsMouseOver) return;
            tooltip = window.MakeTip(help);
            tooltip.PlacementTarget = editor; tooltip.Placement = PlacementMode.Relative;
            tooltip.HorizontalOffset = point.X + 12; tooltip.VerticalOffset = point.Y + 22;
            tooltip.MouseEnter += (_, _) => leave.Stop(); tooltip.MouseLeave += (_, _) => leave.Start();
            tooltip.IsOpen = true;
        };
        leave.Tick += (_, _) => { leave.Stop(); if (tooltip?.IsMouseOver != true && !editor.IsMouseOver) Close(); };
        window.Deactivated += (_, _) => Close(); window.Closed += (_, _) => { timer.Stop(); leave.Stop(); Close(); };
    }
    public void Close() { timer.Stop(); if (tooltip != null) tooltip.IsOpen = false; tooltip = null; position = -1; }
}
