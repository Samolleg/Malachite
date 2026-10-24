using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuestStudio;

public partial class QuestView
{
    sealed record ActionTarget(string Name, bool Otherwise, string Label) { public override string ToString() => Label; }
    void AddAction_Click(object sender, RoutedEventArgs e)
    {
        if (!documentOpen || workbench != null || !ResolveCard()) return;
        RefreshAnalysis(false);
        var source = Current; var objects = CurrentObjects();
        var targets = source.Blocks.SelectMany(b => b.World ? new[] { new ActionTarget(b.Name, false, b.Display) } : !b.CardEditable ? [] :
            b.ElseActions == null ? [new ActionTarget(b.Name, false, b.Display + " · ТОГДА")] : new[] { new ActionTarget(b.Name, false, b.Display + " · ТОГДА"), new ActionTarget(b.Name, true, b.Display + " · ИНАЧЕ") }).ToArray();
        if (targets.Length == 0) { SetStatus("Нет однозначной ветки для добавления. Откройте код или добавьте новый скрипт / шаблон."); return; }
        var overlay = new Grid { Name = "AddActionOverlay", Background = (Brush)FindResource("Panel"), Margin = new Thickness(12) };
        overlay.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Malachite.UI.Wpf;component/WorkbenchTheme.xaml", UriKind.Relative) });
        overlay.RowDefinitions.Add(new() { Height = GridLength.Auto }); overlay.RowDefinitions.Add(new()); overlay.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(overlay, 1); Panel.SetZIndex(overlay, 40); Surface.Children.Add(overlay); workbench = overlay;
        var header = new DockPanel { Margin = new Thickness(12) }; overlay.Children.Add(header);
        var close = new Button { Content = "Закрыть" }; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(ToolNote("Добавить действие · " + Workspace.Name));
        void Close() { Surface.Children.Remove(overlay); workbench = null; CodeEditor.Focus(); }
        close.Click += (_, _) => Close();
        var grid = new Grid(); Grid.SetRow(grid, 1); overlay.Children.Add(grid);
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        var form = new StackPanel { Margin = new Thickness(12) }; var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; grid.Children.Add(scroll);
        var preview = new TextBox { Name = "ActionPreview", IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12) }; Grid.SetColumn(preview, 1); grid.Children.Add(preview);
        form.Children.Add(ToolNote("Куда добавить")); var target = new ComboBox { Name = "ActionTarget", ItemsSource = targets, Margin = new Thickness(4) }; form.Children.Add(target);
        var search = ToolField(form, "Найти действие: имя или смысл"); search.Name = "ActionSearch";
        var commands = Registry.Commands.Values.Where(c => c.ReturnType == "void" && !c.Internal && !c.DebugOnly).OrderBy(c => c.Name).ToArray();
        var picker = new ListBox { Name = "ActionCommand", ItemsSource = commands, DisplayMemberPath = "Name", Margin = new Thickness(4), Height = 160, BorderBrush = MalachiteTheme.Border, BorderThickness = new Thickness(1) }; form.Children.Add(picker);
        var purpose = ToolNote(""); form.Children.Add(purpose);
        var count = new ComboBox { Name = "ActionArity", Margin = new Thickness(4) }; form.Children.Add(count);
        var fieldsPanel = new StackPanel(); form.Children.Add(fieldsPanel); var fields = new List<TextBox>();
        var footer = new DockPanel { Margin = new Thickness(12) }; Grid.SetRow(footer, 2); overlay.Children.Add(footer);
        var apply = new Button { Name = "ActionApply", Content = "Добавить в скрипт", Background = MalachiteTheme.Accent, IsEnabled = false }; DockPanel.SetDock(apply, Dock.Right); footer.Children.Add(apply);
        var check = new Button { Name = "ActionPreviewButton", Content = "Посмотреть код" }; DockPanel.SetDock(check, Dock.Right); footer.Children.Add(check);
        var status = ToolNote("Заполните параметры. Предпросмотр показывает точное изменение, которое можно отменить."); footer.Children.Add(status);
        EditorPlan? plan = null;
        void Invalidate() { plan = null; apply.IsEnabled = false; preview.Text = ""; }
        void MakeFields()
        {
            Invalidate(); fields.Clear(); fieldsPanel.Children.Clear();
            if (picker.SelectedItem is not CommandInfo command || count.SelectedItem is not int number) return;
            for (int i = 0; i < number; i++)
            {
                var parameter = command.Argument(i);
                string label = Help.Entries[command.Name].Arguments[Math.Min(i, command.Parameters.Length - 1)] + " · " + HelpService.TypeName(parameter.Type);
                if (parameter.Type == "object" && !parameter.Global) label += " (ID из MOB или объявленная переменная)";
                var box = ToolField(fieldsPanel, label); box.Name = "ActionArgument" + i;
                fields.Add(box); box.TextChanged += (_, _) => Invalidate();
                if (parameter.Type == "object" && !parameter.Global && objects.Count > 0)
                {
                    var objectPicker = new ComboBox { ItemsSource = objects, Margin = new Thickness(4), MaxDropDownHeight = 220, ToolTip = "Объекты текущей MOB" };
                    objectPicker.SelectionChanged += (_, _) => { if (objectPicker.SelectedItem is MobObject obj) box.Text = obj.Id.ToString(CultureInfo.InvariantCulture); };
                    fieldsPanel.Children.Add(objectPicker);
                }
            }
        }
        picker.SelectionChanged += (_, _) =>
        {
            Invalidate();
            if (picker.SelectedItem is not CommandInfo c) { fieldsPanel.Children.Clear(); purpose.Text = ""; return; }
            // The first selection precedes layout; reveal it after native
            // virtualization has its viewport rather than scrolling at size 0.
            picker.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (ReferenceEquals(picker.SelectedItem, c)) picker.ScrollIntoView(c);
            }));
            purpose.Text = Help.Entries[c.Name].Purpose;
            count.ItemsSource = c.Variadic ? Enumerable.Range(c.Parameters.Length, 17 - c.Parameters.Length).ToArray() : c.Arities ?? [c.Parameters.Length];
            count.Visibility = c.Variadic || c.Arities?.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            count.SelectedIndex = 0; MakeFields();
        };
        count.SelectionChanged += (_, _) => MakeFields(); target.SelectionChanged += (_, _) => Invalidate();
        search.TextChanged += (_, _) => { var selected = picker.SelectedItem; picker.ItemsSource = commands.Where(c => (c.Name + " " + Help.Entries[c.Name].Purpose).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray(); picker.SelectedItem = selected; };
        check.Click += (_, _) =>
        {
            Invalidate();
            try
            {
                if (CodeEditor.Text != source.Text) throw new InvalidDataException("Код изменился. Откройте добавление действия заново.");
                if (target.SelectedItem is not ActionTarget destination || picker.SelectedItem is not CommandInfo command || fields.Count != (int?)count.SelectedItem) throw new InvalidDataException("Выберите действие и ветку.");
                string Argument(int i)
                {
                    string value = fields[i].Text.Trim(); var p = command.Argument(i);
                    if (p.Type == "string" && !p.Global) return ScriptTemplates.Literal(fields[i].Text);
                    if (p.Type == "float" && !p.Global && double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)) return n.ToString("R", CultureInfo.InvariantCulture);
                    if (p.Type == "object" && !p.Global && uint.TryParse(value, out var id))
                    {
                        if (!objects.Any(o => o.Id == id)) throw new InvalidDataException("ID " + id + " не найден в открытой MOB.");
                        return "GetObjectByID(" + ScriptTemplates.Literal(value) + ")";
                    }
                    EditorOperations.Identifier(value);
                    var type = source.Globals.GetValueOrDefault(value) ?? source.Declarations.GetValueOrDefault(destination.Name)?.FirstOrDefault(par => par.Name.Equals(value, StringComparison.OrdinalIgnoreCase))?.Type;
                    if (type != p.Type || p.Global && !source.Globals.ContainsKey(value)) throw new InvalidDataException("Параметр «" + value + "» должен иметь тип «" + HelpService.TypeName(p.Type) + "».");
                    return value;
                }
                plan = ContextAction.Build(source, destination.Name, destination.Otherwise, command, Enumerable.Range(0, fields.Count).Select(Argument).ToArray(), Registry);
                preview.Text = string.Join("\n\n", EditorOperations.Diff(plan.Source, plan.Result).Select(d => "+ Добавляется:\n" + d.After));
                status.Text = plan.Description; apply.IsEnabled = true;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        apply.Click += (_, _) =>
        {
            try { if (plan == null) return; ApplyEditorPlan(plan); var name = ((ActionTarget)target.SelectedItem).Name; Close(); SelectBlock(name, false); Code_Click(this, new()); SetStatus(plan.Description + " · Ctrl+S — сохранить."); }
            catch (Exception ex) { Invalidate(); status.Text = ex.Message; }
        };
        target.SelectedItem = Selected == null ? targets.First() : targets.FirstOrDefault(t => t.Name == Selected.Name);
        picker.SelectedItem = commands.FirstOrDefault(c => c.Name == Current.Calls.FirstOrDefault(c => c.Start == explanationOffset)?.Name) ?? commands.FirstOrDefault(c => c.Name == "ConsoleString") ?? commands.FirstOrDefault();
    }
}
