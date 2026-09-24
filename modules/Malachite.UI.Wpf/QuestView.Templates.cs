using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.RegularExpressions;

namespace QuestStudio;
public partial class QuestView
{
    Grid? templateOverlay;
    public void ApplyTemplate(ScriptTemplatePlan plan)
    {
        if(CodeEditor.IsReadOnly || CodeEditor.Text!=plan.Source) throw new InvalidDataException("Код изменился после предпросмотра. Обновите предпросмотр шаблона.");
        EditRange(0,CodeEditor.Text.Length,plan.Text);
        RefreshAnalysis(false);
        var block=Current.Blocks.FirstOrDefault(b=>b.Name.Equals(plan.Name,StringComparison.OrdinalIgnoreCase));
        if(block!=null) GoToCode(block.Start);
    }
    void Templates_Click(object sender,RoutedEventArgs e)
    {
        if(templateOverlay!=null || CodeEditor.IsReadOnly) return;
        var overlay=new Grid {Background=new SolidColorBrush(Color.FromRgb(19,21,19)),Margin=new Thickness(12)};
        templateOverlay=overlay; Grid.SetRow(overlay,1); Panel.SetZIndex(overlay,30); Surface.Children.Add(overlay);
        overlay.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});overlay.RowDefinitions.Add(new RowDefinition());overlay.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
        var heading=new TextBlock {Text="Добавить шаблон скрипта",FontSize=21,Margin=new Thickness(8)};overlay.Children.Add(heading);
        var content=new Grid();content.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(0.45,GridUnitType.Star)});content.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(0.55,GridUnitType.Star)});Grid.SetRow(content,1);overlay.Children.Add(content);
        var form=new StackPanel {Margin=new Thickness(8)};
        content.Children.Add(new ScrollViewer {Content=form,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var preview=new TextBox {IsReadOnly=true,AcceptsReturn=true,FontFamily=new FontFamily("Consolas"),FontSize=13,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(10),Text="Заполните поля и нажмите «Предпросмотр». Справа появится итоговый документ."};Grid.SetColumn(preview,1);content.Children.Add(preview);
        var bottom=new StackPanel {Margin=new Thickness(8)};Grid.SetRow(bottom,2);overlay.Children.Add(bottom);
        var status=new TextBlock {TextWrapping=TextWrapping.Wrap,MaxHeight=80,TextTrimming=TextTrimming.CharacterEllipsis};bottom.Children.Add(status);
        var buttons=new WrapPanel {Margin=new Thickness(0,8,0,0)};bottom.Children.Add(buttons);
        var build=new Button {Content="Предпросмотр"};var insert=new Button {Content="Вставить в скрипт",IsEnabled=false};var cancel=new Button {Content="Отмена"};buttons.Children.Add(build);buttons.Children.Add(insert);buttons.Children.Add(cancel);
        ScriptTemplatePlan? plan=null;
        void Invalidate() {plan=null;insert.IsEnabled=false;status.Text="Предпросмотр требует обновления.";}
        void Close() {Surface.Children.Remove(overlay);templateOverlay=null;CodeEditor.Focus();}
        cancel.Click+=(_,_)=>Close();
        var containers=new Dictionary<FrameworkElement,StackPanel>();
        T Field<T>(string label,T control) where T:FrameworkElement
        {var group=new StackPanel {Margin=new Thickness(0,4,10,6)};group.Children.Add(new TextBlock {Text=label,Margin=new Thickness(0,0,0,4)});group.Children.Add(control);form.Children.Add(group);containers[control]=group;return control;}
        TextBox Text(string label,string value="",bool multiline=false)
        {var box=Field(label,new TextBox {Text=value,AcceptsReturn=multiline,MinHeight=multiline?85:32,MaxHeight=multiline?180:double.PositiveInfinity,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});box.TextChanged+=(_,_)=>Invalidate();return box;}
        var kinds=Enum.GetValues<ScriptTemplateKind>();
        string[] titles=["NPC заметил героя","Проверка маскировки","NPC заметил → проверить маскировку","Выдать квест / подпункт","Завершить квест / подпункт","После завершения брифинга","Выполнить один раз","Повторять с задержкой","Запустить другой скрипт по условию"];
        var kind=Field("Шаблон",new ListBox {ItemsSource=titles,Height=160});
        var note=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,10)};form.Children.Add(note);
        var name=Text("Имя нового скрипта (совпадения получат суффикс)","QuestStep");
        var npc=Text("ID наблюдающего NPC","205");var profile=Text("Имя профиля маскировки из social_stealth.lua","Поселковые");
        var npcPicker=Field("Объект из открытой MOB — выбрать по имени",new ListBox {Height=100,ItemsSource=CurrentObjects()});
        npcPicker.SelectionChanged+=(_,_)=>{if(npcPicker.SelectedItem is MobObject obj)npc.Text=obj.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);};
        var snapshot=Host.Workspace.Snapshot();
        var dbSearch=Text("Поиск задания / брифинга в БД");
        var records=Field("Запись БД — выберите по названию",new ListBox {Height=130,DisplayMemberPath="Label"});
        var zone=Text("Код зоны задания (например gz1g)");
        var sub=Text("Номер подпункта (пусто — весь квест)");
        var key=Text("Полный ключ квеста q.зона.квест[.подпункт]");
        var briefing=Text("Полный ключ брифинга b.персонаж.разговор");
        var target=Field("Запускаемый скрипт без параметров",new ListBox {Height=110,ItemsSource=Current.Declarations.Where(p=>p.Value.Length==0 && Current.Blocks.Any(b=>b.Name==p.Key)).Select(p=>p.Key).OrderBy(n=>n).ToArray()});
        var condition=Text("Дополнительные условия — код; пусто означает без условия","",true);
        var actions=Text("Действия / успех — код команд","",true);var otherwise=Text("Если маскировка не подошла — код команд","",true);
        var delay=Text("Пауза повторения в игровых сегментах (не секундах)","10");
        var start=Field("Запуск",new CheckBox {Content="Запускать из WorldScript при входе на карту",IsChecked=true,Foreground=MalachiteTheme.Text});start.Checked+=(_,_)=>Invalidate();start.Unchecked+=(_,_)=>Invalidate();
        ScriptTemplateKind SelectedKind()=>kinds[Math.Max(0,kind.SelectedIndex)];
        void Visible(FrameworkElement field,bool show)=>containers[field].Visibility=show?Visibility.Visible:Visibility.Collapsed;
        void FillRecords()
        {
            bool br=SelectedKind()==ScriptTemplateKind.Briefing;
            records.ItemsSource=snapshot.Records.Where(r=>r.Reference.TableKey==(br?"quests.briefings":"quests.quests"))
                .Select(r=>new TemplateRecord(r.Name,(r.Texts.FirstOrDefault()?.Title ?? r.Label)+" · "+r.Name,r.Texts.FirstOrDefault()?.Description??""))
                .Where(r=>(r.Name+" "+r.Label).Contains(dbSearch.Text,StringComparison.OrdinalIgnoreCase)).OrderBy(r=>r.Label).ToArray();
        }
        void QuestKey()
        {if(records.SelectedItem is TemplateRecord record && SelectedKind()!=ScriptTemplateKind.Briefing) key.Text="q."+zone.Text.Trim()+"."+record.Name+(sub.Text.Trim().Length==0?"":"."+sub.Text.Trim());}
        zone.TextChanged+=(_,_)=>QuestKey();sub.TextChanged+=(_,_)=>QuestKey();dbSearch.TextChanged+=(_,_)=>FillRecords();
        records.SelectionChanged+=(_,_)=>
        {
            if(records.SelectedItem is not TemplateRecord record)return;
            if(SelectedKind()==ScriptTemplateKind.Briefing)
            {
                var keys=Current.Flags.Select(f=>f.Key).Where(k=>k.StartsWith("b.") && k.EndsWith("."+record.Name,StringComparison.Ordinal)).Distinct().ToArray();
                briefing.Text=keys.Length==1?keys[0]:"b.ПЕРСОНАЖ."+record.Name;
                status.Text=keys.Length==1?"Найден полный ключ в коде карты.":"Укажите персонажа в полном ключе брифинга: одной записи БД недостаточно.";
            }
            else {QuestKey();status.Text=HelpService.Compact(record.Description,300);}
        };
        target.SelectionChanged+=(_,_)=>Invalidate();
        kind.SelectionChanged+=(_,_)=>
        {
            var k=SelectedKind();bool quest=k is ScriptTemplateKind.GiveQuest or ScriptTemplateKind.CompleteQuest;bool br=k==ScriptTemplateKind.Briefing;
            Visible(npcPicker,k is ScriptTemplateKind.Observer or ScriptTemplateKind.ObserverDisguise);Visible(name,!br);Visible(npc,k is ScriptTemplateKind.Observer or ScriptTemplateKind.ObserverDisguise);Visible(profile,k is ScriptTemplateKind.Disguise or ScriptTemplateKind.ObserverDisguise);
            foreach(var f in new FrameworkElement[]{dbSearch,records})Visible(f,quest||br);
            foreach(var f in new FrameworkElement[]{zone,sub,key})Visible(f,quest);
            Visible(briefing,br);Visible(target,k==ScriptTemplateKind.Launch);Visible(condition,!br);Visible(actions,k!=ScriptTemplateKind.Launch);Visible(otherwise,k is ScriptTemplateKind.Disguise or ScriptTemplateKind.ObserverDisguise);Visible(delay,k==ScriptTemplateKind.Repeat);Visible(start,!br);
            note.Text=br?"Ветка добавляется в начало существующего обработчика, до его KillScript. Остальные ветки сохраняются. Действия Sleep/KillScript внутри вашей ветки влияют на дальнейшее выполнение.":quest?"Состояние повышается до 1 (выдать) или 2 (выполнено) через GSSetVarMax. Это изменение состояния; награды и диалоги автоматически не добавляются. Зону укажите явно.":k==ScriptTemplateKind.Repeat?"Повторяется с паузой даже при ложном условии. Не добавляйте второй запуск того же цикла.":k is ScriptTemplateKind.Disguise or ScriptTemplateKind.ObserverDisguise ? "Маскировка проверяет всю живую партию. Шаблон ждёт живого первого персонажа игрока 0; при его смерти не срабатывает. Наблюдатель дополнительно ждёт, когда NPC увидит этого персонажа. Выполняется один раз." : k==ScriptTemplateKind.Observer ? "Один раз выполнить действия, когда NPC увидит первого персонажа игрока 0 и выполнятся дополнительные условия." : "Один раз выполнить действия при выполнении условий. Пустое условие срабатывает при первом обслуживании скрипта.";
            FillRecords();Invalidate();
        };
        build.Click+=(_,_)=>
        {
            try
            {
                if(SelectedKind()==ScriptTemplateKind.Briefing && briefing.Text.Contains("ПЕРСОНАЖ"))throw new InvalidDataException("Замените ПЕРСОНАЖ на точный ключ владельца брифинга.");
                int ticks=10; if(SelectedKind()==ScriptTemplateKind.Repeat && !int.TryParse(delay.Text,out ticks))throw new InvalidDataException("Пауза должна быть целым числом.");
                plan=ScriptTemplates.Build(CodeEditor.Text,new(SelectedKind(),name.Text,condition.Text,actions.Text,otherwise.Text,npc.Text,profile.Text,key.Text,briefing.Text,target.SelectedItem as string??"",ticks,start.IsChecked==true),Registry);
                preview.Text=plan.Text;status.Text=plan.Summary+" Вставка отменяется одним Ctrl+Z. Изменения сохраняются обычной кнопкой сохранения.";insert.IsEnabled=true;
            }
            catch(InvalidDataException ex){plan=null;insert.IsEnabled=false;status.Text=ex.Message;}
        };
        insert.Click+=(_,_)=>{try {if(plan==null)return;ApplyTemplate(plan);Close();}catch(InvalidDataException ex){Invalidate();status.Text=ex.Message;}};
        kind.SelectedIndex=0;
    }
    sealed record TemplateRecord(string Name,string Label,string Description);
}
