using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DatabaseEditor.Modules.V1;
using QuestStudio;

internal static class TestViews
{
    // Explicit fixture: the product no longer loads a tutorial at startup.
    static readonly string CatalogPath = Path.Combine(Path.GetTempPath(), "malachite-verification-" + Guid.NewGuid().ToString("N"), "map-catalog.json");
    public static QuestView Create(IModuleHost host) => new QuestView(host, CatalogPath);
    public static QuestView Demo(IModuleHost host) { var view = Create(host); view.LoadWorkspace(new Workspace(), false); return view; }
}
internal static class HomeVerification
{
    public static void Run(Action<bool, string> check, string output)
    {
        var host = new TestDocumentHost(); var view = TestViews.Create(host);
        check(((Grid)view.FindName("HomePanel")).Visibility == Visibility.Visible && ((Grid)view.FindName("EditorSurface")).Visibility == Visibility.Collapsed,
            "startup displays map picker instead of tutorial editor");
        check(view.DocumentId == null && host.List("malachite").Length == 0 && view.Current.Blocks.Count == 0 && !view.HasPendingChanges,
            "startup has no tutorial code or phantom host document");
        check(!((Button)view.FindName("CopiesButton")).IsEnabled && !((Button)view.FindName("SaveChangesButton")).IsEnabled,
            "document actions disabled before a map opens");
        byte[] state = view.CaptureState(); var restored = TestViews.Create(new TestDocumentHost());
        check(restored.RestoreState(state).Succeeded && restored.DocumentId == null && !restored.HasPendingChanges,
            "start screen recovery does not invent an editable document"); restored.Stop();
        var files = (List<string>)typeof(QuestView).GetField("folderFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var fixture = Path.GetFullPath(Path.Combine(output, "map-metadata")); Directory.CreateDirectory(fixture);
        var modified = new DateTime(2026, 9, 18, 7, 15, 0, DateTimeKind.Utc);
        foreach (int i in Enumerable.Range(1, 180))
        {
            var path = Path.Combine(fixture, $"zone{i:000}.mob"); File.WriteAllBytes(path, []); File.SetLastWriteTimeUtc(path, modified); files.Add(path);
        }
        typeof(QuestView).GetMethod("FillNavigation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
        var list = (ListBox)view.FindName("HomeMaps");
        check(((QuestView.HomeMap)list.Items[0]).Modified == modified.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), "map date uses actual filesystem last-write in local time");
        check(QuestView.HomeMap.ReadMetadata(Path.Combine(fixture, "missing.mob")).Modified == "Недоступно", "missing map date does not invent epoch timestamp");
        check(list.Items.Count == 180 && ((ListBox)view.FindName("Navigation")).Items.Count == 0, "all maps reside in picker and never appear as script stages");
        void Click(string method) => typeof(QuestView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);
        var homeButton = (Button)view.FindName("HomeButton");
        check(homeButton.BorderThickness.Left == 2 && ((SolidColorBrush)homeButton.BorderBrush).Color == Color.FromRgb(57, 184, 110), "Maps has persistent active outline at startup");
        File.SetLastWriteTimeUtc(files[1], modified.AddDays(-40));
        File.SetLastWriteTimeUtc(files[2], modified.AddDays(40));
        var missing = Path.Combine(fixture, "missing.mob"); files.Add(missing);
        Click("Home_Click");
        list.SelectedItem = list.Items.Cast<QuestView.HomeMap>().Single(m => m.Name == "zone050.mob");
        Click("HomeSortDate_Click");
        check(((QuestView.HomeMap)list.Items[0]).Name == "zone003.mob" && ((QuestView.HomeMap)list.Items[^1]).ModifiedUtc == null, "date sort starts newest and puts missing metadata last");
        Click("HomeSortDate_Click");
        check(((QuestView.HomeMap)list.Items[0]).Name == "zone002.mob" && ((QuestView.HomeMap)list.Items[^1]).ModifiedUtc == null, "date ascending compares timestamps across months and keeps unknown last");
        Click("HomeSortName_Click"); Click("HomeSortName_Click");
        check(((QuestView.HomeMap)list.Items[0]).Name == "zone180.mob" && ((QuestView.HomeMap)list.SelectedItem).Name == "zone050.mob", "descending name sort preserves selected map");
        ((TextBox)view.FindName("HomeSearch")).Text = "zone00";
        check(list.Items.Count == 9 && ((QuestView.HomeMap)list.Items[0]).Name == "zone009.mob", "search retains selected sort order");
        ((TextBox)view.FindName("HomeSearch")).Text = "";
        Click("HomeSortName_Click"); files.Remove(missing); Click("Home_Click");
        ((TextBox)view.FindName("HomeSearch")).Text = "zone180";
        check(list.Items.Count == 1 && ((QuestView.HomeMap)list.Items[0]).Name == "zone180.mob", "map picker search finds final map");
        ((TextBox)view.FindName("HomeSearch")).Text = "";
        view.Measure(new Size(1250, 650)); view.Arrange(new Rect(0, 0, 1250, 650)); view.UpdateLayout();
        list.ScrollIntoView(list.Items[^1]); view.UpdateLayout();
        var last = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(179);
        var bounds = last.TransformToAncestor(view).TransformBounds(new Rect(last.RenderSize));
        check(last.ActualHeight == 88 && last.ActualWidth == 240, "compact map tile keeps name and modification date");
        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var second = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
        var firstPoint = first.TranslatePoint(new Point(), list); var secondPoint = second.TranslatePoint(new Point(), list);
        check(secondPoint.X > firstPoint.X && secondPoint.Y == firstPoint.Y, "maps arranged as multiple columns rather than a vertical list");
        check(bounds.Top >= 0 && bounds.Bottom < 610, "last of 180 maps fully reachable above controls in small window");
        Directory.CreateDirectory(output); Render(view, Path.Combine(output, "start-maps.png"), 1250, 650);
        view.Measure(new Size(1000, 650)); view.Arrange(new Rect(0, 0, 1000, 650)); view.UpdateLayout();
        list.ScrollIntoView(list.Items[^1]); view.UpdateLayout();
        bounds = last.TransformToAncestor(view).TransformBounds(new Rect(last.RenderSize));
        check(bounds.Left >= 0 && bounds.Right < 1000 && bounds.Top >= 0 && bounds.Bottom < 610, "tiles reflow and final map stays fully reachable in narrower viewport");
        Render(view, Path.Combine(output, "start-maps-narrow.png"), 1000, 650);
        view.LoadWorkspace(new Workspace { Name = "actual.mob", Text = "WorldScript()" }, false);
        check(((Grid)view.FindName("HomePanel")).Visibility == Visibility.Collapsed && view.DocumentId != null && ((ListBox)view.FindName("Navigation")).Items.Count == 1,
            "opening document displays only its actual stages");
        check(homeButton.BorderThickness.Left == 1 && ((SolidColorBrush)homeButton.BorderBrush).Color != Color.FromRgb(57, 184, 110), "Maps active outline clears when document opens");
        var code = (TextBox)view.FindName("CodeEditor"); code.AppendText("\n// unsaved");
        typeof(QuestView).GetMethod("Home_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);
        check(homeButton.BorderThickness.Left == 2, "Maps outline returns when picker reopens");
        var selectedPath = ((QuestView.HomeMap)list.SelectedItem).Path;
        File.SetLastWriteTimeUtc(selectedPath, modified.AddHours(1));
        typeof(QuestView).GetMethod("Home_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);
        check(((QuestView.HomeMap)list.SelectedItem).Path == selectedPath && ((QuestView.HomeMap)list.SelectedItem).ModifiedUtc == modified.AddHours(1), "return to maps refreshes changed date while preserving selected map");
        check(view.HasPendingChanges && code.Text.EndsWith("// unsaved"), "returning to maps preserves unsaved code");
        state = view.CaptureState(); var pending = TestViews.Create(new TestDocumentHost());
        check(pending.RestoreState(state).Succeeded && pending.HasPendingChanges && ((Grid)pending.FindName("HomePanel")).Visibility == Visibility.Visible,
            "recovery preserves pending document while map picker is shown"); pending.Stop();
        var menu = ((Button)view.FindName("CopiesButton")).ContextMenu;
        menu.ApplyTemplate(); menu.Measure(new Size(430, 150)); menu.Arrange(new Rect(0, 0, 430, 150)); menu.UpdateLayout();
        check(menu.Template != null && ((SolidColorBrush)menu.Background).Color == Color.FromRgb(27, 30, 28), "copies menu explicitly uses editor dark surface");
        foreach (MenuItem item in menu.Items)
        {
            item.ApplyTemplate();
            check(item.Template.FindName("MenuChrome", item) is Border && ((SolidColorBrush)item.Foreground).Color == Color.FromRgb(238, 241, 236), "copies item custom template replaces system selection chrome");
        }
        Render(menu, Path.Combine(output, "copies-menu.png"), 430, 150);
        var highlighted = new HighlightedItem { Header = "Сохранить копию MOB / текста…", Style = ((MenuItem)menu.Items[0]).Style };
        highlighted.Highlight(); highlighted.Measure(new Size(430, 50)); highlighted.Arrange(new Rect(0, 0, 430, 50)); highlighted.UpdateLayout();
        check(((SolidColorBrush)((Border)highlighted.Template.FindName("MenuChrome", highlighted)).Background).Color == Color.FromRgb(35, 58, 44), "copies hovered or keyboard-highlighted item uses malachite selection without system blue");
        Render(highlighted, Path.Combine(output, "copies-highlight.png"), 430, 50);
        view.Stop();
    }
    static void Render(FrameworkElement view, string path, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream);
    }
    sealed class HighlightedItem : MenuItem { public void Highlight() => IsHighlighted = true; }
}
