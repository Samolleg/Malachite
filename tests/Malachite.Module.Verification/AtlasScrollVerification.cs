using System.Reflection;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuestStudio;

internal static class AtlasScrollVerification
{
    static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    public static void Run(Action<bool, string> check, string output)
    {
        Directory.CreateDirectory(output);
        var view = TestViews.Demo(new TestHost());
        try
        {
            var text = "WorldScript(" + string.Join("\n", Enumerable.Range(0, 200).Select(i => $"GSSetVar(0, \"q.gz1g.quest{i:D3}\", 1)")) + ")";
            var analysis = ScriptAnalyzer.Analyze(text, view.Registry);
            var map = QuestAtlas.Read(analysis, "indexed.mob", "test");
            var index = (List<QuestView.IndexedMap>)typeof(QuestView).GetField("indexedMaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            index.Add(new("indexed.mob", "test", analysis.Flags) { Quests = map });
            ((Border)view.FindName("QuestAtlasPanel")).Visibility = Visibility.Visible;
            view.LoadWorkspace(new Workspace { SourcePath = "indexed.mob", Text = text }, false);
            ((Border)view.FindName("QuestAtlasPanel")).Visibility = Visibility.Visible;
            view.RefreshQuestAtlas();
            var list = (ListBox)view.FindName("AtlasQuests");
            foreach (var size in new[] { new Size(1510, 922), new Size(1250, 650) })
            {
                view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
                var scroll = Children(list).OfType<ScrollViewer>().First();
                check(list.Items.Count >= 200 && scroll.ScrollableHeight > 0, "grouped quest inventory scrolls at " + size);
                scroll.ScrollToTop(); view.UpdateLayout();
                scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
                view.UpdateLayout();
                check(scroll.VerticalOffset > 0, "mouse wheel moves grouped quest inventory at " + size);
                scroll.ScrollToBottom(); view.UpdateLayout();
                var last = list.Items[list.Items.Count - 1];
                var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(last);
                bool visible = container != null && new Rect(new Point(), list.RenderSize).Contains(container.TransformToAncestor(list).TransformBounds(new Rect(container.RenderSize))) && new Rect(0, 0, size.Width, size.Height - 32).Contains(container.TransformToAncestor(view).TransformBounds(new Rect(container.RenderSize)));
                check(visible && scroll.VerticalOffset > 0, "final indexed quest is reachable at " + size);
                var source = list.ItemsSource;
                list.SelectedItem = last; view.UpdateLayout();
                check(ReferenceEquals(source, list.ItemsSource) && scroll.VerticalOffset > 0,
                    "selecting bottom quest preserves inventory and scroll at " + size);
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(output, "quest-list-bottom-" + (int)size.Height + ".png"))) png.Save(file);
                scroll.ScrollToTop(); view.UpdateLayout();
                check(scroll.VerticalOffset == 0, "quest inventory returns to top at " + size);
            }
            view.LoadWorkspace(new Workspace { Name = "Empty map", SourcePath = "empty.mob", Text = "WorldScript ( KillScript() )" }, false);
            void Click(string name) => typeof(QuestView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);
            var atlas = (Border)view.FindName("QuestAtlasPanel");
            foreach (var size in new[] { new Size(1510, 922), new Size(1250, 650) })
            foreach (var previous in new[] { "Scenario_Click", "Mixed_Click", "Code_Click", "Scenario_Click" })
            {
                Click(previous); Click("Atlas_Click");
                view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
                var area = (Grid)atlas.Parent;
                var surface = (Grid)view.FindName("Surface");
                double expected = surface.RowDefinitions[1].ActualHeight - area.Margin.Top - area.Margin.Bottom - area.RowDefinitions[0].ActualHeight;
                check(Math.Abs(atlas.ActualHeight - expected) < 1, "empty atlas covers code/scenario after " + previous + " at " + size);
                check(Children(atlas).OfType<TextBlock>().Any(t => t.Text.Contains("На этой карте квесты не найдены")), "empty atlas has explicit explanation after " + previous);
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(output, "empty-atlas-" + (int)size.Height + ".png"))) png.Save(file);
                Click("AtlasClose_Click");
                check(atlas.Visibility == Visibility.Collapsed, "empty atlas returns to previous view after " + previous);
            }
        }
        finally { view.Stop(); }
    }
}
