using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DatabaseEditor.Modules.V1;
namespace QuestStudio;

public partial class QuestView
{
    Grid? workbench;
    CancellationTokenSource? workbenchSearch;
    void CancelWorkbenchSearch()=>workbenchSearch?.Cancel();
    Action? captureComposerDraft;
    sealed class ComposerDraft
    {
        public string Name="QuestStep",Zone="gz1g",Sub="",QuestKey="",GsKey="",Profile="",ObjectId="",Value="1",Command="";
        public string ActionZone="gz1g",ActionSub="",ActionQuestKey="",FreeGsKey="",ActionValue="1";
        public string? QuestName,ActionQuestName;
        public int Logic,Kind,Comparison,Branch;public bool Start;
        public readonly List<string> Conditions=[],Then=[],Else=[];
        public readonly Dictionary<string,string> Arguments=new(StringComparer.OrdinalIgnoreCase);
    }
    readonly Dictionary<string,ComposerDraft> composerDrafts=new(StringComparer.OrdinalIgnoreCase);
    readonly ConditionalWeakTable<Workspace,ComposerDraft> unsavedComposerDrafts=new();
    ComposerDraft CurrentComposerDraft()
    {
        if(Workspace.SourcePath is not { Length: > 0 } path)return unsavedComposerDrafts.GetValue(Workspace,_=>new());
        string key=Path.GetFullPath(path);if(!composerDrafts.TryGetValue(key,out var draft))composerDrafts[key]=draft=new();return draft;
    }
    void ResetComposerDraft()
    {if(Workspace.SourcePath is { Length: > 0 } path)composerDrafts.Remove(Path.GetFullPath(path));else unsavedComposerDrafts.Remove(Workspace);}
    public void ApplyEditorPlan(EditorPlan plan)
    {
        if(CodeEditor.IsReadOnly || CodeEditor.Text!=plan.Source)throw new InvalidDataException("Документ изменился после предпросмотра. Откройте инструменты заново.");
        int caret=CodeEditor.CaretIndex;EditRange(0,CodeEditor.Text.Length,plan.Result);RefreshAnalysis(false);CodeEditor.CaretIndex=Math.Min(caret,CodeEditor.Text.Length);
    }
    static TextBlock ToolNote(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,6,4,8),Foreground=MalachiteTheme.Text};
    static Button ToolButton(Panel panel,string text,Action action)
    {var b=new Button{Content=text,Margin=new Thickness(3),HorizontalAlignment=HorizontalAlignment.Left};b.Click+=(_,_)=>action();panel.Children.Add(b);return b;}
    static TextBox ToolField(Panel parent,string label,string value="",bool multiline=false)
    {
        parent.Children.Add(ToolNote(label));var box=new TextBox{Text=value,AcceptsReturn=multiline,MinHeight=32,Height=multiline?110:double.NaN,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(4)};System.Windows.Automation.AutomationProperties.SetName(box,label);parent.Children.Add(box);return box;
    }
    void Workbench_Click(object sender,RoutedEventArgs e)
    {
        if(workbench!=null||!documentOpen||!ResolveCard())return;
        RefreshAnalysis(false);
        string source=CodeEditor.Text;var analysis=Current;int insertion=CodeEditor.SelectionStart,selectionLength=CodeEditor.SelectionLength;
        var overlay=new Grid{Background=new SolidColorBrush(Color.FromRgb(19,21,19)),Margin=new Thickness(8)};
        overlay.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/Malachite.UI.Wpf;component/WorkbenchTheme.xaml",UriKind.Relative)});
        workbench=overlay;Grid.SetRow(overlay,1);Panel.SetZIndex(overlay,40);Surface.Children.Add(overlay);
        overlay.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});overlay.RowDefinitions.Add(new RowDefinition());overlay.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var header=new DockPanel();overlay.Children.Add(header);var close=new Button{Content="Закрыть",HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);header.Children.Add(ToolNote("Инструменты скрипта · "+Workspace.Name));
        void Close(){captureComposerDraft?.Invoke();captureComposerDraft=null;CancelWorkbenchSearch();Surface.Children.Remove(overlay);workbench=null;CodeEditor.Focus();}
        close.Click+=(_,_)=>Close();
        var grid=new Grid();Grid.SetRow(grid,1);overlay.Children.Add(grid);grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(178)});grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(0.72,GridUnitType.Star)});
        string[] pages=["1 · Создать Script","2 · Объекты карты","3 · Прогноз веток","4 · Связи и поиск","5 · Переименовать","6 · Просмотр кода","7 · Мои шаблоны","8 · Изменения"];
        var navigation=new ListBox{ItemsSource=pages,Margin=new Thickness(4)};grid.Children.Add(navigation);
        var body=new StackPanel{Margin=new Thickness(8)};var scroll=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetColumn(scroll,1);grid.Children.Add(scroll);
        var preview=new TextBox{IsReadOnly=true,AcceptsReturn=true,FontFamily=new FontFamily("Consolas"),FontSize=13,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(8)};Grid.SetColumn(preview,2);grid.Children.Add(preview);
        var footer=new DockPanel{Margin=new Thickness(6)};Grid.SetRow(footer,2);overlay.Children.Add(footer);
        var apply=new Button{Content="Применить изменения",IsEnabled=false};DockPanel.SetDock(apply,Dock.Right);footer.Children.Add(apply);var status=ToolNote("Изменения кода сначала показываются справа и отменяются общей кнопкой отмены.");status.MaxHeight=65;footer.Children.Add(status);
        EditorPlan? plan=null;
        var comparisonBar=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(comparisonBar,Dock.Right);header.Children.Insert(1,comparisonBar);
        var compare=ToolButton(comparisonBar,"Различия",()=>{if(plan==null)return;preview.TextWrapping=TextWrapping.NoWrap;preview.Text=string.Join("\n\n",EditorOperations.Diff(plan.Source,plan.Result).Select(d=>"ПОЗИЦИЯ "+d.Start+"\n− Было:\n"+d.Before+"\n+ Стало:\n"+d.After));});
        var complete=ToolButton(comparisonBar,"Итоговый код",()=>{if(plan!=null){preview.TextWrapping=TextWrapping.NoWrap;preview.Text=plan.Result;}});compare.IsEnabled=complete.IsEnabled=false;
        void Clear(){plan=null;apply.IsEnabled=false;compare.IsEnabled=complete.IsEnabled=false;}
        void Try(Action action){try{action();}catch(Exception ex){Clear();status.Text=ex.Message;}}
        void Present(EditorPlan p)
        {
            plan=p;preview.Text=p.Result;status.Text=p.Description;compare.IsEnabled=complete.IsEnabled=true;
            apply.IsEnabled=p.Result!=p.Source&&!CodeEditor.IsReadOnly;
        }
        void Insert(string text,string description)
        {
            var result=EditorOperations.Apply(source,[new(insertion,selectionLength,text)]);
            var after=ScriptAnalyzer.Analyze(result,Registry);
            int errors=after.Issues.Count(i=>i.Severity=="error");
            Present(new(source,result,description+(errors>0?$" · После вставки {errors} ошибок: проверьте место вставки.":"")));
            // Fragment insertion can leave a partially written document, just like typing. The preview makes this explicit.
        }
        apply.Click+=(_,_)=>Try(()=>{
            if(plan==null)return;
            string? created=navigation.SelectedIndex==0?CurrentComposerDraft().Name:null;
            captureComposerDraft?.Invoke();if(created!=null)created=CurrentComposerDraft().Name;
            ApplyEditorPlan(plan);
            if(created!=null){captureComposerDraft=null;ResetComposerDraft();}
            Close();
            if(created!=null && Current.Blocks.FirstOrDefault(b=>b.Name.Equals(created,StringComparison.OrdinalIgnoreCase)) is { } block)GoToCode(block.Start);
        });
        void Watch(TextBox box){box.TextChanged+=(_,_)=>Clear();}
        void Jump(int offset){Close();GoToCode(offset);}
        navigation.SelectionChanged+=(_,_)=>Try(()=>
        {
            captureComposerDraft?.Invoke();captureComposerDraft=null;
            CancelWorkbenchSearch();
            body.Children.Clear();Clear();preview.Clear();status.Text="Выберите действие.";
            preview.TextWrapping=TextWrapping.NoWrap;
            apply.Content="Применить изменения";compare.Content="Различия";complete.Content="Итоговый код";
            switch(navigation.SelectedIndex)
            {
                case 0: Composer();break;
                case 1: Objects();break;
                case 2: Simulation();break;
                case 3: Usages();break;
                case 4: Rename();break;
                case 5: CodeTools();break;
                case 6: Library();break;
                case 7: Changes();break;
            }
        });
        void Composer()
        {
            var draft=CurrentComposerDraft();
            body.Children.Add(ToolNote("Создание нового Script. Существующий код не меняется до нажатия «Применить изменения». Черновик сохраняется при переключении инструментов и карт."));
            var name=ToolField(body,"Имя нового этапа",draft.Name);Watch(name);
            var previewButton=ToolButton(body,"Предпросмотр нового этапа",()=>{});
            var conditionRows=draft.Conditions;var yesRows=draft.Then;var noRows=draft.Else;
            var actionBranch=new ComboBox{ItemsSource=new[]{"ТОГДА — условия выполнены","ИНАЧЕ — условия не выполнены"},SelectedIndex=draft.Branch,Margin=new Thickness(4)};
            List<string> Actions()=>actionBranch.SelectedIndex==1?noRows:yesRows;
            var conditions=new ListBox{Height=100};var actions=new ListBox{Height=100};
            var logic=new ComboBox{ItemsSource=new[]{"Все условия (И)","Хотя бы одно (ИЛИ)"},SelectedIndex=draft.Logic,Margin=new Thickness(4)};body.Children.Add(logic);logic.SelectionChanged+=(_,_)=>Clear();
            body.Children.Add(ToolNote("Условия"));body.Children.Add(conditions);
            var conditionButtons=new WrapPanel();body.Children.Add(conditionButtons);
            var snapshot=Host.Workspace.Snapshot();
            string QuestLabel(string key)
            {
                string? code=QuestAtlas.QuestKey(key);if(code==null)return key;
                var record=snapshot.Records.FirstOrDefault(r=>r.Reference.TableKey=="quests.quests"&&r.Name==code);
                string title=record?.Texts.FirstOrDefault()?.Title??record?.Label??code;
                string[] parts=key.Split('.');return "«"+title+"»"+(parts.Length==4?", подпункт "+parts[3]:"")+" · "+key;
            }
            string Explain(string code)
            {
                var gs=Regex.Match(code,@"GSSetVarMax\(\s*0\s*,\s*""([^""]+)""\s*,\s*([12])\s*\)");
                if(gs.Success)return (gs.Groups[2].Value=="1"?"Выдать":"Завершить")+" квест · "+QuestLabel(gs.Groups[1].Value)+"  ["+code+"]";
                var check=Regex.Match(code,@"GSGetVar\(\s*0\s*,\s*""([^""]+)""");
                if(check.Success)return "Проверить состояние · "+QuestLabel(check.Groups[1].Value)+"  ["+code+"]";
                if(code.StartsWith("IsAlive(",StringComparison.OrdinalIgnoreCase))return "Объект жив · "+code;
                if(code.StartsWith("PartyDisguiseMatches(",StringComparison.OrdinalIgnoreCase))return "Маскировка партии совпадает · "+code;
                if(code.StartsWith("GSSetVar(",StringComparison.OrdinalIgnoreCase))return "Записать GS · "+code;
                return code;
            }
            void Update(){conditions.ItemsSource=conditionRows.Select(Explain).ToArray();actions.ItemsSource=Actions().Select(Explain).ToArray();Clear();}
            ToolButton(conditionButtons,"НЕ",()=>{if(conditions.SelectedIndex>=0){int n=conditions.SelectedIndex;conditionRows[n]="Not( "+conditionRows[n]+" )";Update();}});
            ToolButton(conditionButtons,"Удалить",()=>{if(conditions.SelectedIndex>=0){conditionRows.RemoveAt(conditions.SelectedIndex);Update();}});
            var kind=new ComboBox{ItemsSource=new[]{"Состояние квеста","Переменная GS","Маскировка партии","Объект жив","Команда из справочника"},SelectedIndex=draft.Kind};body.Children.Add(kind);
            var questRecords=snapshot.Records.Where(r=>r.Reference.TableKey=="quests.quests").Select(r=>new TemplateRecord(r.Name,(r.Texts.FirstOrDefault()?.Title??r.Label)+" · "+r.Name,r.Texts.FirstOrDefault()?.Description??"")).OrderBy(r=>r.Label).ToArray();
            TemplateRecord[] SubChoices(TemplateRecord? quest)
            {
                if(quest==null)return [];
                var record=snapshot.Records.FirstOrDefault(r=>r.Reference.TableKey=="quests.quests"&&r.Name==quest.Name);
                string description=record?.Texts.FirstOrDefault()?.Description??"";
                return Regex.Matches(description,@"(?im)^[\t ]*#subobj[\t ]+(\S+)[\t ]*\r?$").Cast<Match>().Select(m=>{
                    string tail=description[(m.Index+m.Length)..].TrimStart('\r','\n');
                    string title=tail.Split('\n')[0].Trim();if(title.StartsWith('#')||title.Length==0)title="Подпункт";
                    return new TemplateRecord(m.Groups[1].Value,title+" · "+m.Groups[1].Value,"");
                }).ToArray();
            }
            var questList=new ListBox{Height=90,DisplayMemberPath="Label",ItemsSource=questRecords};body.Children.Add(questList);
            var subChoices=new ComboBox{DisplayMemberPath="Label",Margin=new Thickness(4)};body.Children.Add(subChoices);
            var zone=ToolField(body,"Зона квеста",draft.Zone);var sub=ToolField(body,"Подпункт квеста (пусто — весь квест)",draft.Sub);
            var questKey=ToolField(body,"Ключ квеста для условия",draft.QuestKey);
            var gsKey=ToolField(body,"Ключ обычной переменной GS для условия",draft.GsKey);
            var profile=ToolField(body,"Профиль маскировки для условия",draft.Profile);
            var objectId=ToolField(body,"ID объекта для условия",draft.ObjectId);
            void MakeKey(){if(questList.SelectedItem is TemplateRecord r)questKey.Text="q."+zone.Text.Trim()+"."+r.Name+(sub.Text.Trim().Length>0?"."+sub.Text.Trim():"");}
            questList.SelectionChanged+=(_,_)=>{subChoices.ItemsSource=SubChoices(questList.SelectedItem as TemplateRecord);MakeKey();};
            subChoices.SelectionChanged+=(_,_)=>{if(subChoices.SelectedItem is TemplateRecord r)sub.Text=r.Name;};
            zone.TextChanged+=(_,_)=>MakeKey();sub.TextChanged+=(_,_)=>MakeKey();
            var objectPicker=new ComboBox{ItemsSource=CurrentObjects(),MaxDropDownHeight=220,Margin=new Thickness(4)};body.Children.Add(objectPicker);
            objectPicker.SelectionChanged+=(_,_)=>{if(objectPicker.SelectedItem is MobObject o)objectId.Text=o.Id.ToString(CultureInfo.InvariantCulture);};
            void DisplayField(FrameworkElement control,bool visible){control.Visibility=visible?Visibility.Visible:Visibility.Collapsed;int at=body.Children.IndexOf(control);if(at>0&&body.Children[at-1] is TextBlock label)label.Visibility=control.Visibility;}
            kind.SelectionChanged+=(_,_)=>{bool quest=kind.SelectedIndex==0;questList.Visibility=subChoices.Visibility=quest?Visibility.Visible:Visibility.Collapsed;DisplayField(zone,quest);DisplayField(sub,quest);DisplayField(questKey,quest);DisplayField(gsKey,kind.SelectedIndex==1);DisplayField(profile,kind.SelectedIndex==2);DisplayField(objectId,kind.SelectedIndex==3);objectPicker.Visibility=kind.SelectedIndex==3?Visibility.Visible:Visibility.Collapsed;};
            kind.SelectedIndex=-1;kind.SelectedIndex=draft.Kind;
            var comparison=new ComboBox{ItemsSource=new[]{"Равно","Больше","Меньше"},SelectedIndex=draft.Comparison};body.Children.Add(comparison);
            var value=ToolField(body,"Число состояния (1 — выдано; 2 — выполнено; 3 — провал)",draft.Value);
            var commands=new ComboBox{ItemsSource=Registry.Commands.Values.Where(c=>!c.DebugOnly&&!c.Internal).OrderBy(c=>c.Name).Select(c=>c.Name).ToArray(),IsEditable=false,MaxDropDownHeight=220,SelectedItem=draft.Command};body.Children.Add(commands);
            var args=new StackPanel();body.Children.Add(args);var argumentFields=new List<TextBox>();
            void CaptureArguments(){if(commands.SelectedItem is string current)for(int i=0;i<argumentFields.Count;i++)draft.Arguments[current+":"+i]=argumentFields[i].Text;}
            string? shownCommand=null;
            commands.SelectionChanged+=(_,_)=>{if(shownCommand!=null)for(int i=0;i<argumentFields.Count;i++)draft.Arguments[shownCommand+":"+i]=argumentFields[i].Text;args.Children.Clear();argumentFields.Clear();shownCommand=commands.SelectedItem as string;if(shownCommand==null)return;var c=Registry.Commands[shownCommand];args.Children.Add(ToolNote(c.Signature));for(int i=0;i<c.Parameters.Length;i++){var p=c.Parameters[i];argumentFields.Add(ToolField(args,p.Name+" : "+p.Type+(p.Type=="string"?" · текст без кавычек, =имя для переменной":p.Type=="object"?" · ID объекта или выражение":""),draft.Arguments.GetValueOrDefault(shownCommand+":"+i,"")));}Clear();};
            if(commands.SelectedItem is string selectedCommand){commands.SelectedIndex=-1;commands.SelectedItem=selectedCommand;}
            string Command(){if(commands.SelectedItem is not string n)throw new InvalidDataException("Выберите команду.");var c=Registry.Commands[n];return n+"( "+string.Join(", ",argumentFields.Select((b,i)=>{string v=b.Text.Trim();if(c.Parameters[i].Type=="string"){if(v.StartsWith('='))return v[1..].Trim();return v.Length==0?"\"\"":ScriptTemplates.Literal(v.Trim('"'));}if(c.Parameters[i].Type=="object"&&uint.TryParse(v,out _))return "GetObjectByID( "+ScriptTemplates.Literal(v)+" )";return v;}))+" )";}
            string Number(){if(!double.TryParse(value.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var d)||!double.IsFinite(d))throw new InvalidDataException("Введите число.");return d.ToString(CultureInfo.InvariantCulture);}
            ToolButton(body,"Добавить условие",()=>Try(()=>{
                if(kind.SelectedIndex==4 && commands.SelectedItem is string command && Registry.Commands[command].ReturnType!="float")throw new InvalidDataException("Для условия нужна команда с числовым результатом (float).");
                string conditionKey=kind.SelectedIndex==0?questKey.Text.Trim():gsKey.Text.Trim();
                if(kind.SelectedIndex==0&&QuestAtlas.QuestKey(conditionKey)==null)throw new InvalidDataException("Выберите квест из БД или укажите полный ключ вида q.зона.квест[.подпункт].");
                if(kind.SelectedIndex==1&&conditionKey.Length==0)throw new InvalidDataException("Укажите ключ GS для условия.");
                if(kind.SelectedIndex==2&&profile.Text.Trim().Length==0)throw new InvalidDataException("Укажите профиль маскировки.");
                if(kind.SelectedIndex==3&&!uint.TryParse(objectId.Text.Trim(),out _))throw new InvalidDataException("Укажите числовой ID объекта.");
                string expr=kind.SelectedIndex switch{2=>"PartyDisguiseMatches( "+ScriptTemplates.Literal(profile.Text.Trim())+" )",3=>"IsAlive( GetObjectByID( "+ScriptTemplates.Literal(objectId.Text.Trim())+" ) )",4=>Command(),_=>new[]{"IsEqual","IsGreater","IsLess"}[comparison.SelectedIndex]+"( GSGetVar( 0, "+ScriptTemplates.Literal(conditionKey)+" ), "+Number()+" )"};
                conditionRows.Add(expr);Update();
            }));
            body.Children.Add(ToolNote("Действия — в указанном порядке. Если заполнено ИНАЧЕ, этап выбирает одну ветку при первом проходе и завершает обслуживание."));body.Children.Add(actionBranch);actionBranch.SelectionChanged+=(_,_)=>Update();body.Children.Add(actions);
            body.Children.Add(ToolNote("Цель действий «Выдать» и «Завершить» выбирается отдельно от проверяемого объекта или профиля."));
            var actionQuestList=new ListBox{Height=80,DisplayMemberPath="Label",ItemsSource=questRecords};body.Children.Add(actionQuestList);
            var actionSubChoices=new ComboBox{DisplayMemberPath="Label",Margin=new Thickness(4)};body.Children.Add(actionSubChoices);
            var actionZone=ToolField(body,"Зона выдаваемого квеста",draft.ActionZone);
            var actionSub=ToolField(body,"Подпункт выдаваемого квеста (необязательно)",draft.ActionSub);
            var actionQuestKey=ToolField(body,"Ключ выдаваемого квеста",draft.ActionQuestKey);
            void MakeActionKey(){if(actionQuestList.SelectedItem is TemplateRecord r)actionQuestKey.Text="q."+actionZone.Text.Trim()+"."+r.Name+(actionSub.Text.Trim().Length>0?"."+actionSub.Text.Trim():"");}
            actionQuestList.SelectionChanged+=(_,_)=>{actionSubChoices.ItemsSource=SubChoices(actionQuestList.SelectedItem as TemplateRecord);MakeActionKey();};
            actionSubChoices.SelectionChanged+=(_,_)=>{if(actionSubChoices.SelectedItem is TemplateRecord r)actionSub.Text=r.Name;};
            actionZone.TextChanged+=(_,_)=>MakeActionKey();actionSub.TextChanged+=(_,_)=>MakeActionKey();
            var freeGsKey=ToolField(body,"Ключ свободной записи GS",draft.FreeGsKey);
            var actionValue=ToolField(body,"Значение свободной записи GS",draft.ActionValue);
            string ActionQuest(){string key=actionQuestKey.Text.Trim();if(QuestAtlas.QuestKey(key)==null)throw new InvalidDataException("Выберите выдаваемый квест из БД или укажите полный ключ q.зона.квест[.подпункт].");return ScriptTemplates.Literal(key);}
            var actionButtons=new WrapPanel();body.Children.Add(actionButtons);
            ToolButton(actionButtons,"Выдать",()=>Try(()=>{Actions().Add("GSSetVarMax( 0, "+ActionQuest()+", 1 )");Update();}));
            ToolButton(actionButtons,"Завершить",()=>Try(()=>{Actions().Add("GSSetVarMax( 0, "+ActionQuest()+", 2 )");Update();}));
            ToolButton(actionButtons,"Записать GS",()=>Try(()=>{if(freeGsKey.Text.Trim().Length==0)throw new InvalidDataException("Укажите ключ свободной записи GS.");if(!double.TryParse(actionValue.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var v)||!double.IsFinite(v))throw new InvalidDataException("Введите числовое значение GS.");Actions().Add("GSSetVar( 0, "+ScriptTemplates.Literal(freeGsKey.Text.Trim())+", "+v.ToString(CultureInfo.InvariantCulture)+" )");Update();}));
            ToolButton(actionButtons,"Команда",()=>Try(()=>{Actions().Add(Command());Update();}));
            ToolButton(actionButtons,"↑",()=>{int n=actions.SelectedIndex;if(n>0){(Actions()[n-1],Actions()[n])=(Actions()[n],Actions()[n-1]);Update();actions.SelectedIndex=n-1;}});
            ToolButton(actionButtons,"Удалить",()=>{if(actions.SelectedIndex>=0){Actions().RemoveAt(actions.SelectedIndex);Update();}});
            var start=new CheckBox{Content="Запускать из WorldScript",IsChecked=draft.Start,Margin=new Thickness(4)};body.Children.Add(start);start.Checked+=(_,_)=>Clear();start.Unchecked+=(_,_)=>Clear();
            previewButton.Click+=(_,_)=>Try(()=>{
                string condition=string.Join(Workspace.NewLine,conditionRows);
                if(logic.SelectedIndex==1&&conditionRows.Count>0)condition=conditionRows.Aggregate((left,right)=>"Not( Mul( Not( "+left+" ), Not( "+right+" ) ) )");
                var p=ScriptTemplates.Build(source,new(ScriptTemplateKind.Once,name.Text,condition,string.Join(Workspace.NewLine,yesRows),string.Join(Workspace.NewLine,noRows),"","","","","",10,start.IsChecked==true),Registry,includeOtherwise:true);
                Present(new(p.Source,p.Text,p.Summary));
            });
            questList.SelectedItem=questRecords.FirstOrDefault(r=>r.Name==draft.QuestName);
            actionQuestList.SelectedItem=questRecords.FirstOrDefault(r=>r.Name==draft.ActionQuestName);
            // Selection handlers may regenerate keys; preserve a manually entered full key.
            questKey.Text=draft.QuestKey;actionQuestKey.Text=draft.ActionQuestKey;
            Update();
            captureComposerDraft=()=>{
                draft.Name=name.Text;draft.Logic=logic.SelectedIndex;draft.Kind=kind.SelectedIndex;
                draft.QuestName=(questList.SelectedItem as TemplateRecord)?.Name;draft.Zone=zone.Text;draft.Sub=sub.Text;draft.QuestKey=questKey.Text;
                draft.GsKey=gsKey.Text;draft.Profile=profile.Text;draft.ObjectId=objectId.Text;
                draft.Comparison=comparison.SelectedIndex;draft.Value=value.Text;draft.Command=commands.SelectedItem as string??"";
                draft.Branch=actionBranch.SelectedIndex;draft.Start=start.IsChecked==true;
                draft.ActionQuestName=(actionQuestList.SelectedItem as TemplateRecord)?.Name;
                draft.ActionZone=actionZone.Text;draft.ActionSub=actionSub.Text;draft.ActionQuestKey=actionQuestKey.Text;
                draft.FreeGsKey=freeGsKey.Text;draft.ActionValue=actionValue.Text;CaptureArguments();
            };
            ToolButton(body,"Сбросить черновик",()=>{
                captureComposerDraft=null;ResetComposerDraft();
                navigation.SelectedIndex=-1;navigation.SelectedIndex=0;
                status.Text="Черновик конструктора сброшен. Исходный код не менялся.";
            });
        }
        void Objects()
        {
            body.Children.Add(ToolNote("Имена из MOB. Вставка — в позицию курсора, сохранённую при открытии инструментов."));
            var rows=CurrentObjects().ToArray();
            var query=ToolField(body,"Поиск по имени, ID или прототипу");var list=new ListBox{Height=300};body.Children.Add(list);
            void Fill()=>list.ItemsSource=rows.Where(o=>(o.Display+" "+o.Prototype).Contains(query.Text,StringComparison.OrdinalIgnoreCase)).ToArray();query.TextChanged+=(_,_)=>Fill();Fill();
            list.SelectionChanged+=(_,_)=>{Clear();if(list.SelectedItem is MobObject o){var record=Host.Workspace.Snapshot().Records.FirstOrDefault(r=>r.Reference.TableKey.StartsWith("units.",StringComparison.OrdinalIgnoreCase)&&r.Name.Equals(o.Prototype,StringComparison.OrdinalIgnoreCase));preview.Text=o.Details+(record==null?"":"\n\nБД: "+record.Label+"\n"+string.Join("\n",record.Texts.Select(t=>t.Title+"\n"+t.Description)));}};
            ToolButton(body,"Предпросмотр вставки GetObjectByID",()=>Try(()=>{if(list.SelectedItem is MobObject o){Insert("GetObjectByID( "+ScriptTemplates.Literal(o.Id.ToString(CultureInfo.InvariantCulture))+" )","Ссылка на "+o.Display+(o.ScriptEnabled?"":" · У объекта не установлен флаг использования в скриптах; доступность ID проверьте в игре."));}}));
            ToolButton(body,"Найти ссылки в коде",()=>{if(list.SelectedItem is MobObject o){var uses=analysis.Calls.Where(c=>c.Name.Equals("GetObjectByID",StringComparison.OrdinalIgnoreCase)&&c.Arguments.Count==1&&analysis.Slice(c.Arguments[0]).Trim().Trim('"')==o.Id.ToString(CultureInfo.InvariantCulture)).ToArray();preview.Text=o.Details+"\n\n"+string.Join("\n",uses.Select(c=>$"Строка {c.Line} · {c.Owner}"));if(uses.Length>0)ToolButton(body,"Перейти к первой ссылке",()=>Jump(uses[0].Start));}});
            status.Text=(objectsError??"")+ $" {rows.Length} объектов и юнитов; свет, звук и частицы имеют другой формат и в этот список не входят.";
        }
        void Simulation()
        {
            body.Children.Add(ToolNote("Проверка одного прохода выбранного Script. Неизвестные действия, запуск другого Script и Sleep останавливают прогноз. Игра не запускается."));
            var blocks=new ComboBox{ItemsSource=analysis.Blocks,SelectedItem=Selected??analysis.Blocks.FirstOrDefault()};body.Children.Add(blocks);
            var input=ToolField(body,"Входные значения: GS:0:q.gz1g.q0g.2 = 1\nИли точное выражение из кода: PartyDisguiseMatches( \"Поселковые\" ) = 1","",true);input.Height=180;
            ToolButton(body,"Подготовить список значений GS",()=>{if(blocks.SelectedItem is ScriptBlock selected)input.Text=string.Join("\n",analysis.Flags.Where(f=>f.Owner.Equals(selected.Name,StringComparison.OrdinalIgnoreCase)).Select(f=>"GS:"+f.Context+":"+f.Key+" = ").Distinct());status.Text="Впишите числа после =. Пустые значения не заменяются нулём автоматически.";});
            var trace=new ListBox{Height=230};body.Children.Add(trace);trace.MouseDoubleClick+=(_,_)=>{if(trace.SelectedItem is PreviewStep s)Jump(s.Offset);};
            ToolButton(body,"Проверить ветки",()=>Try(()=>{Clear();if(blocks.SelectedItem is not ScriptBlock b)return;var result=ScenarioPreview.Run(analysis,b,ScenarioPreview.ParseInputs(input.Text));trace.ItemsSource=result;preview.Text=string.Join("\n\n",result.Select(s=>$"Строка {analysis.LineAt(s.Offset)} · {s.Text}"));status.Text="Двойной щелчок по шагу — перейти к коду. Входные значения действуют только в этой проверке.";}));
        }
        void Rename()
        {
            body.Children.Add(ToolNote("Переименовывает текущий документ: объявления, тела и вызовы Script либо параметр внутри его области. Строки и комментарии сохраняются."));
            var scripts=new ComboBox{ItemsSource=analysis.Declarations.Keys.OrderBy(n=>n).ToArray(),SelectedItem=Selected?.Name};body.Children.Add(scripts);
            var parameter=new ComboBox{Margin=new Thickness(4)};body.Children.Add(parameter);
            void Fill(){parameter.ItemsSource=new[]{"Script целиком"}.Concat(scripts.SelectedItem is string n?analysis.Declarations[n].Select(p=>p.Name):[]).ToArray();parameter.SelectedIndex=0;Clear();}
            scripts.SelectionChanged+=(_,_)=>Fill();Fill();
            var name=ToolField(body,"Новое имя");Watch(name);parameter.SelectionChanged+=(_,_)=>Clear();
            ToolButton(body,"Предпросмотр переименования",()=>Try(()=>{if(scripts.SelectedItem is not string script)throw new InvalidDataException("Выберите Script.");Present(EditorOperations.Rename(analysis,Registry,parameter.SelectedIndex>0?(string)parameter.SelectedItem:script,name.Text.Trim(),parameter.SelectedIndex>0?script:null));}));
        }
        void CodeTools()
        {
            body.Children.Add(ToolNote("Сверните этапы в дереве, раскройте нужный и перейдите к нему двойным щелчком. Форматирование меняет только отступы."));
            var tree=new TreeView{Height=230,Margin=new Thickness(4)};body.Children.Add(tree);
            foreach(var b in analysis.Blocks)
            {
                var node=new TreeViewItem{Header=b.Display+" · строка "+b.Line,Tag=b.Start};tree.Items.Add(node);
                node.Items.Add("Развернуть код");node.Expanded+=(_,_)=>{if(node.Items[0] is not string)return;node.Items.Clear();
                var snippet=new TextBox{Text=analysis.Slice(b.Body),IsReadOnly=true,AcceptsReturn=true,MaxHeight=200,MaxWidth=420,FontFamily=new FontFamily("Consolas"),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};
                node.Items.Add(snippet);};
            }
            tree.MouseDoubleClick+=(_,_)=>{if(Keyboard.FocusedElement is TextBox)return;if(tree.SelectedItem is TreeViewItem item&&item.Tag is int offset)Jump(offset);};
            ToolButton(body,"Форматировать · предпросмотр",()=>Try(()=>Present(new(source,EditorOperations.Format(analysis),"Изменены только начальные отступы; строки и окончания строк сохранены."))));
            ToolButton(body,"Комментарий выделенных строк · предпросмотр",()=>Try(()=>Present(EditorOperations.Comment(source,insertion,selectionLength))));
            ToolButton(body,"К парной скобке",()=>{int at=EditorOperations.MatchingBracket(analysis,insertion);if(at>=0)Jump(at);else status.Text="Поставьте курсор перед скобкой или сразу после неё.";});
            ToolButton(body,"К объявлению имени под курсором",()=>{
                var token=analysis.Tokens.FirstOrDefault(t=>insertion>=t.Start&&insertion<=t.End);
                if(token!=null&&analysis.DeclarationRanges.TryGetValue(token.Value,out var r))Jump(r.Start);
                else status.Text="Под курсором нет имени объявленного Script.";
            });
            ToolButton(body,"← Назад по переходам",()=>Try(()=>{Close();NavigateHistory(-1);}));
            ToolButton(body,"Вперёд по переходам →",()=>Try(()=>{Close();NavigateHistory(1);}));
        }
        void Library()
        {
            body.Children.Add(ToolNote("Личная библиотека сохраняется между запусками. Параметры: {{id:name}}, {{text:key}}, {{number:state}}. Названия параметров — латиницей; text заменяется строкой с кавычками."));
            var items=UserTemplateLibrary.Load(UserTemplateLibrary.DefaultPath).ToList();
            var list=new ListBox{ItemsSource=items,DisplayMemberPath="Name",Height=100};body.Children.Add(list);
            var name=ToolField(body,"Название");var description=ToolField(body,"Описание");var text=ToolField(body,"Код шаблона",selectionLength>0?source.Substring(insertion,selectionLength):"GSSetVarMax( 0, {{text:key}}, {{number:state}} )",true);text.Height=150;
            var values=new StackPanel();body.Children.Add(values);var fields=new Dictionary<string,TextBox>();
            void Parameters(){values.Children.Clear();fields.Clear();foreach(Match m in Regex.Matches(text.Text,@"\{\{(id|text|number):([A-Za-z_][A-Za-z_0-9]*)\}\}")){string n=m.Groups[2].Value;if(!fields.ContainsKey(n)){fields[n]=ToolField(values,n+" · "+m.Groups[1].Value);Watch(fields[n]);}}Clear();}
            text.TextChanged+=(_,_)=>Parameters();Parameters();
            list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is UserScriptTemplate t){name.Text=t.Name;description.Text=t.Description;text.Text=t.Body;Clear();}};
            ToolButton(body,"Сохранить в моей библиотеке",()=>Try(()=>{if(string.IsNullOrWhiteSpace(name.Text))throw new InvalidDataException("Укажите название.");items.RemoveAll(t=>t.Name.Equals(name.Text.Trim(),StringComparison.OrdinalIgnoreCase));items.Add(new(name.Text.Trim(),text.Text,description.Text));UserTemplateLibrary.Save(UserTemplateLibrary.DefaultPath,items);list.ItemsSource=items.ToArray();status.Text="Шаблон сохранён в личной библиотеке.";}));
            ToolButton(body,"Удалить выбранный шаблон",()=>Try(()=>{if(list.SelectedItem is UserScriptTemplate t){items.Remove(t);UserTemplateLibrary.Save(UserTemplateLibrary.DefaultPath,items);list.ItemsSource=items.ToArray();status.Text="Шаблон удалён из библиотеки.";}}));
            ToolButton(body,"Предпросмотр вставки",()=>Try(()=>Insert(UserTemplateLibrary.Expand(text.Text,fields.ToDictionary(k=>k.Key,k=>k.Value.Text)),"Вставка личного шаблона в выбранную позицию.")));
        }
        void Changes()
        {
            body.Children.Add(ToolNote("Сравнение с последним сохранённым текстом. Выберите фрагменты, которые хотите вернуть. Другие изменения останутся."));
            var diffs=EditorOperations.Diff(savedText,source);var list=new ListBox{ItemsSource=diffs,SelectionMode=SelectionMode.Extended,Height=290};body.Children.Add(list);
            list.SelectionChanged+=(_,_)=>{Clear();if(list.SelectedItem is LineChange d)preview.Text="СОХРАНЕНО\n"+d.Before+"\n\nСЕЙЧАС\n"+d.After;};
            ToolButton(body,"Предпросмотр восстановления выбранных",()=>Try(()=>{
                var selected=list.SelectedItems.Cast<LineChange>().ToArray();if(selected.Length==0)throw new InvalidDataException("Выберите изменения (Ctrl — несколько).");
                Present(new(source,EditorOperations.Apply(source,selected.Select(d=>new TextChange(d.Start,d.Length,d.Before))),$"Будут восстановлены {selected.Length} фрагментов. Можно отменить общей отменой."));
            }));
            status.Text=$"{diffs.Count} изменённых фрагментов. Для большого полностью заменённого участка может показываться один общий фрагмент.";
        }
        void Usages()
        {
            body.Children.Add(ToolNote("Поиск по текущему несохранённому коду и проиндексированным MOB. Двойной щелчок по результату — перейти. Для Script ищите имя, для состояния — полный GS-ключ."));
            var query=ToolField(body,"Имя Script, GS-ключ или текст",CodeEditor.SelectedText);
            var searchKind=new ComboBox{ItemsSource=new[]{"Имя Script / команды (точно)","GS-ключ (точно)","Любой текст, включая комментарии"},SelectedIndex=0,Margin=new Thickness(4)};body.Children.Add(searchKind);
            var results=new ListBox{Height=300};body.Children.Add(results);
            var scope=new CheckBox{Content="Все проиндексированные карты",IsChecked=true,Margin=new Thickness(4)};body.Children.Add(scope);
            ToolButton(body,"Что вызывает выбранный этап",()=>{Clear();results.ItemsSource=Selected?.Calls.Select(c=>new UsageRow(Selected.Name+" → "+c.Name+" · строка "+c.Line,c.Start)).ToArray();});
            var search=ToolButton(body,"Найти",()=>{});
            ToolButton(body,"Остановить поиск",CancelWorkbenchSearch);
            search.Click+=async (_,_)=>{
                Clear();if(query.Text.Trim().Length==0){status.Text="Введите запрос.";return;}
                CancelWorkbenchSearch();var request=new CancellationTokenSource();workbenchSearch=request;
                bool Active()=>!request.IsCancellationRequested&&!stopped&&workbench==overlay&&ReferenceEquals(workbenchSearch,request);
                string q=query.Text.Trim();var kind=(ScriptSearchKind)searchKind.SelectedIndex;searchKind.IsEnabled=false;search.IsEnabled=false;var found=new List<UsageRow>();var errors=new List<string>();
                void Add(Analysis a,string? path,string? hash)
                {
                    foreach(var r in ScriptReferences.Find(a,q,kind,1000-found.Count))found.Add(new((path==null?Workspace.Name:Path.GetFileName(path))+" · "+r.Owner+" · "+r.Line+" · "+r.Kind+" · "+r.Excerpt,r.Offset,path,hash));
                }
                try
                {
                    Add(analysis,null,null);
                    var maps=scope.IsChecked==true?indexedMaps.Where(m=>!m.Path.Equals(Workspace.SourcePath,StringComparison.OrdinalIgnoreCase)).ToArray():[];
                    for(int i=0;i<maps.Length&&found.Count<1000;i++)
                    {
                        if(!Active())return;
                        status.Text=$"Поиск: {i+1}/{maps.Length} · найдено {found.Count}";
                        await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.Background);
                        if(!Active())return;
                        var map=maps[i];
                        try{
                            if(Host.Files is not IModuleFileBrowser browser)throw new InvalidDataException("Хост не предоставляет чтение карт.");
                            var parsed=await Task.Run(()=>{
                                request.Token.ThrowIfCancellationRequested();var f=browser.Read(map.Path);
                                request.Token.ThrowIfCancellationRequested();var w=Workspace.FromBytes(f.DisplayPath,f.Content.ToArray());
                                if(w.Mob?.Hash!=map.Hash)throw new InvalidDataException("карта изменилась — обновите индекс");
                                return ScriptAnalyzer.Analyze(w.Text,Registry);
                            },request.Token);
                            if(!Active())return;Add(parsed,map.Path,map.Hash);
                        }
                        catch(OperationCanceledException) when(request.IsCancellationRequested){return;}
                        catch(Exception ex){if(!Active())return;errors.Add(Path.GetFileName(map.Path)+": "+ex.Message);}
                    }
                    if(!Active())return;
                    results.ItemsSource=found;status.Text=$"Найдено {found.Count}"+(found.Count>=1000?" · предел 1000":"")+$" · пропущено {errors.Count}";preview.Text=string.Join("\n",errors);
                }
                catch(Exception ex){if(Active())status.Text=ex.Message;}
                finally{if(workbench==overlay&&!stopped){search.IsEnabled=true;searchKind.IsEnabled=true;if(request.IsCancellationRequested)status.Text="Поиск остановлен.";}if(ReferenceEquals(workbenchSearch,request))workbenchSearch=null;request.Dispose();}
            };
            results.MouseDoubleClick+=(_,_)=>Try(()=>{if(results.SelectedItem is not UsageRow row)return;RecordNavigation(CodeEditor.CaretIndex);if(row.Path!=null){if(Host.Files is not IModuleFileBrowser browser)throw new InvalidDataException("Нет доступа к картам.");var file=browser.Read(row.Path);var w=Workspace.FromBytes(file.DisplayPath,file.Content.ToArray());if(w.Mob?.Hash!=row.Hash)throw new InvalidDataException("Карта изменилась. Повторите индексацию.");if(!CanLeave())return;LoadOpenedFile(file);}Close();GoToCode(row.Offset);});
        }
        navigation.SelectedIndex=0;
    }
    sealed record NavigationPoint(string Path,string Name,int Offset,string? Hash);
    readonly List<NavigationPoint> navigationHistory=[];
    int navigationIndex=-1;bool replayingNavigation;
    MobDocument? objectsSource;
    IReadOnlyList<MobObject> objectsCache=[];
    string? objectsError;
    IReadOnlyList<MobObject> CurrentObjects()
    {
        if(!ReferenceEquals(objectsSource,Workspace.Mob))
        {
            var source=Workspace.Mob;objectsError=null;
            try{objectsCache=source==null?[]:MobObjects.Read(source);}
            catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or OverflowException)
            {objectsCache=[];objectsError="Объекты MOB не прочитаны: "+ex.Message;}
            objectsSource=source;
        }
        return objectsCache;
    }
    HoverHelp? ObjectHelp(Analysis local,int position)
    {
        var call=local.Calls.Where(c=>c.Name.Equals("GetObjectByID",StringComparison.OrdinalIgnoreCase)&&c.Arguments.Count==1&&position>=c.Start&&position<c.End).OrderBy(c=>c.End-c.Start).FirstOrDefault();
        if(call==null)return null;
        string literal=local.Slice(call.Arguments[0]).Trim();
        if(!literal.StartsWith('"')||!literal.EndsWith('"')||!uint.TryParse(literal.Trim('"'),out var id))return null;
        try
        {
            var objects=CurrentObjects().Where(o=>o.Id==id).ToArray();
            if(objectsError!=null)return new("Объект "+id,"Ошибка чтения объектов MOB",objectsError,"Источник: открытая карта.");
            if(objects.Length==0)return new("Объект "+id,"Ссылка по ID","В исходном MOB объект не найден. Он может создаваться во время игры или загружаться из другого MOB.","Источник: объекты открытой карты.");
            return new(objects[0].Display,"Объект открытой карты",string.Join("\n\n",objects.Select(o=>o.Details)),"ID из GetObjectByID; данные исходного MOB, не состояние запущенной игры.");
        }
        catch(InvalidDataException ex){return new("Объект "+id,"Не удалось прочитать объекты MOB",ex.Message,"Источник: открытый MOB.");}
    }
    void RecordNavigation(int offset)
    {
        if(replayingNavigation||!documentOpen)return;
        var p=new NavigationPoint(Workspace.SourcePath??"",Workspace.Name,offset,Workspace.Mob?.Hash);
        if(navigationIndex>=0&&navigationHistory[navigationIndex]==p)return;
        if(navigationIndex+1<navigationHistory.Count)navigationHistory.RemoveRange(navigationIndex+1,navigationHistory.Count-navigationIndex-1);
        navigationHistory.Add(p);if(navigationHistory.Count>100)navigationHistory.RemoveAt(0);navigationIndex=navigationHistory.Count-1;
    }
    void NavigateHistory(int direction)
    {
        int target=navigationIndex+direction;if(target<0||target>=navigationHistory.Count){SetStatus("Больше переходов нет.");return;}
        var p=navigationHistory[target];
        if(!p.Path.Equals(Workspace.SourcePath??"",StringComparison.OrdinalIgnoreCase)||p.Path.Length==0&&p.Name!=Workspace.Name)
        {
            if(p.Path.Length==0)throw new InvalidDataException("Несохранённый документ из истории уже закрыт.");
            if(Host.Files is not IModuleFileBrowser browser)throw new InvalidDataException("Нет доступа к карте из истории.");
            var file=browser.Read(p.Path);var w=Workspace.FromBytes(file.DisplayPath,file.Content.ToArray());if(p.Hash!=null&&w.Mob?.Hash!=p.Hash)throw new InvalidDataException("Карта из истории изменена; откройте её заново.");
            if(!CanLeave())return;LoadOpenedFile(file);
        }
        replayingNavigation=true;try{GoToCode(p.Offset);navigationIndex=target;}finally{replayingNavigation=false;}
    }
}
