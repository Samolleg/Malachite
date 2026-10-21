using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DatabaseEditor.Modules.V1;
using QuestStudio;
using TextChange = QuestStudio.TextChange;
internal static class WorkbenchVerification
{
    public static void Run(Action<bool,string> check,string output)
    {
        var registry=new CommandRegistry();
        const string source="// Task must stay\r\nDeclareScript Task(value:float)\r\nDeclareScript Other(value:float)\r\nScript Task\r\n(\r\nif ( IsEqual(value,1) ) then ( ConsoleString(\"Task value\") )\r\n)\r\nScript Other(if()then(Task(value)))\r\nWorldScript(Task(1))\r\n";
        var a=ScriptAnalyzer.Analyze(source,registry);
        var renamed=EditorOperations.Rename(a,registry,"Task","QuestStep");
        check(renamed.Result.Contains("DeclareScript QuestStep(")&&renamed.Result.Contains("Script QuestStep\r\n")&&renamed.Result.Contains("QuestStep(value)")&&renamed.Result.Contains("QuestStep(1)"),"rename definitions and calls");
        check(renamed.Result.Contains("// Task must stay")&&renamed.Result.Contains("\"Task value\""),"rename preserves comments and strings");
        var parameter=EditorOperations.Rename(a,registry,"value","state","Task");
        check(parameter.Result.Contains("Task(state:float)")&&parameter.Result.Contains("IsEqual(state,1)")&&parameter.Result.Contains("Other(value:float)")&&parameter.Result.Contains("Task(value)"),"parameter rename is scoped to declaration and body");
        void Reject(Action action,string name){try{action();check(false,name);}catch(InvalidDataException){check(true,name);}}
        Reject(()=>EditorOperations.Rename(a,registry,"Task","Other"),"reject collision");
        Reject(()=>EditorOperations.Rename(a,registry,"Task","GSSetVar"),"reject builtin name");
        Reject(()=>EditorOperations.Rename(a,registry,"Task","X) then("),"reject identifier injection");
        var eventSource=ScriptAnalyzer.Analyze("DeclareScript #OnBriefingComplete(player:float,key:string) Script #OnBriefingComplete(if()then(KillScript()))",registry);
        Reject(()=>EditorOperations.Rename(eventSource,registry,"#OnBriefingComplete","Renamed"),"engine briefing callback cannot be renamed locally");
        check(EditorOperations.Rename(eventSource,registry,"key","briefingKey","#OnBriefingComplete").Result.Contains("briefingKey:string"),"callback parameter may be renamed without changing callback name");
        var formatted=EditorOperations.Format(a);
        check(formatted.Contains("    if (")&&!formatted.Replace("\r\n","").Contains('\n'),"formatter preserves CRLF and indents blocks");
        check(ScriptAnalyzer.Analyze(formatted,registry).Tokens.Select(t=>(t.Kind,t.Value)).SequenceEqual(a.Tokens.Select(t=>(t.Kind,t.Value))),"formatter preserves exact lexical tokens");
        int bracket=source.IndexOf('(');int match=EditorOperations.MatchingBracket(a,bracket);check(match>bracket&&source[match]==')',"matching bracket from cursor");
        var comment=EditorOperations.Comment("one\r\n  two\r\nthree",0,12);
        check(comment.Result=="//one\r\n  //two\r\nthree","comment selected full lines without next line");
        check(EditorOperations.Comment(comment.Result,0,16).Result=="one\r\n  two\r\nthree","uncomment reverses selected lines");
        string old="a\r\nb\r\nc\r\nd\r\n",now="A\r\nb\r\nc\r\nD\r\n";
        var differences=EditorOperations.Diff(old,now);check(differences.Count==2,"diff separates independent hunks");
        check(EditorOperations.Apply(now,[new(differences[0].Start,differences[0].Length,differences[0].Before)])=="a\r\nb\r\nc\r\nD\r\n","selective restore preserves other change");
        foreach(var pair in new[]{("", "x\n"),("x", ""),("a\nb", "a\nc\nb"),("a\nb\nc", "a\nc"),("a\r\n", "a\n"),("same","same")})
        {var d=EditorOperations.Diff(pair.Item1,pair.Item2);check(EditorOperations.Apply(pair.Item2,d.Select(h=>new TextChange(h.Start,h.Length,h.Before)))==pair.Item1,"diff roundtrip "+pair);}
        var random=new Random(40);for(int i=0;i<40;i++){string x=string.Concat(Enumerable.Range(0,random.Next(20)).Select(_=>random.Next(5)+"\n")),y=string.Concat(Enumerable.Range(0,random.Next(20)).Select(_=>random.Next(5)+"\n"));var d=EditorOperations.Diff(x,y);check(EditorOperations.Apply(y,d.Select(h=>new TextChange(h.Start,h.Length,h.Before)))==x,"random diff roundtrip "+i);}
        string library=Path.Combine(output,"test-user-library.json");var template=new UserScriptTemplate("Выдать","{{id:command}}(0, {{text:key}}, {{number:state}})","sample");UserTemplateLibrary.Save(library,[template]);check(UserTemplateLibrary.Load(library).Single()==template,"user library persists exact template");
        var expanded=UserTemplateLibrary.Expand(template.Body,new Dictionary<string,string>{{"command","GSSetVarMax"},{"key","q.gz1g.q0g.2"},{"state","1"}});check(expanded=="GSSetVarMax(0, \"q.gz1g.q0g.2\", 1)","typed template expansion");
        Reject(()=>UserTemplateLibrary.Expand(template.Body,new Dictionary<string,string>()),"missing template parameter rejected");
        Reject(()=>UserTemplateLibrary.Expand("{{text:x}}",new Dictionary<string,string>{{"x","\") KillScript("}}),"template literal injection rejected");
        const string sim="DeclareScript Test() Script Test(if(IsEqual(GSGetVar(0,\"q.a.b\"),1))then(GSSetVar(0,\"q.a.b\",2))else(GSSetVar(0,\"q.a.b\",3)) if(IsEqual(GSGetVar(0,\"q.a.b\"),2))then(KillScript()))";
        var sa=ScriptAnalyzer.Analyze(sim,registry);var sb=sa.Blocks.Single();
        var trace=ScenarioPreview.Run(sa,sb,ScenarioPreview.ParseInputs("GS:0:q.a.b = 1"));
        check(trace.Any(s=>s.Text.Contains("← 2"))&&!trace.Any(s=>s.Unknown)&&trace.Any(s=>s.Text.Contains("обслуживание")),"scenario follows then and observes written state");
        var no=ScenarioPreview.Run(sa,sb,ScenarioPreview.ParseInputs("GS:0:q.a.b = 0"));check(no.Any(s=>s.Text.Contains("← 3"))&&!no.Any(s=>s.Text.Contains("обслуживание")),"scenario chooses else");
        var unknown=ScenarioPreview.Run(sa,sb,new Dictionary<string,double>());check(unknown.Any(s=>s.Unknown)&&!unknown.Any(s=>s.Text.Contains("←")),"scenario stops before uncertain branch actions");
        var sleep=ScriptAnalyzer.Analyze("Script X(if()then(Sleep(1) GSSetVar(0,\"k\",1)))",registry);check(!ScenarioPreview.Run(sleep,sleep.Blocks.Single(),new Dictionary<string,double>()).Any(s=>s.Text.Contains("←")),"scenario does not predict after wait");
        var killed=ScriptAnalyzer.Analyze("Script X(if()then(KillScript() GSSetVar(0,\"sameBranch\",1)) if()then(GSSetVar(0,\"laterRule\",1)))",registry);
        var killedTrace=ScenarioPreview.Run(killed,killed.Blocks.Single(),new Dictionary<string,double>());
        check(killedTrace.Any(s=>s.Text.Contains("sameBranch ← 1"))&&!killedTrace.Any(s=>s.Text.Contains("laterRule ←"))&&killedTrace.Last().Text.Contains("снят"),"KillScript keeps active branch and skips later top-level checks");
        var nested=ScriptAnalyzer.Analyze("Script X(if()then(if()then(KillScript() GSSetVar(0,\"nested\",1)) GSSetVar(0,\"current\",1)) if()then(GSSetVar(0,\"later\",1)))",registry);
        var nestedTrace=ScenarioPreview.Run(nested,nested.Blocks.Single(),new Dictionary<string,double>());
        check(nestedTrace.Any(s=>s.Text.Contains("nested ← 1"))&&nestedTrace.Any(s=>s.Text.Contains("current ← 1"))&&!nestedTrace.Any(s=>s.Text.Contains("later ←")),"nested KillScript completes current branch only");
        var floatCase=ScriptAnalyzer.Analyze("Script X(if(IsEqual(Add(0.1,0.2),0.3))then(GSSetVar(0,\"branch\",1))else(GSSetVar(0,\"branch\",2)))",registry);
        check(ScenarioPreview.Run(floatCase,floatCase.Blocks.Single(),new Dictionary<string,double>()).Any(s=>s.Text.Contains("branch ← 1")),"preview rounds function results to engine float before IsEqual");
        var variadic=ScriptAnalyzer.Analyze("Script X(if(IsEqual(Add(0.1,0.2,0.3),0.6))then(GSSetVar(0,\"sum\",1))else(GSSetVar(0,\"sum\",2)))",registry);
        check(ScenarioPreview.Run(variadic,variadic.Blocks.Single(),new Dictionary<string,double>()).Any(s=>s.Text.Contains("sum ← 1")),"preview evaluates addon variadic Add in float order");
        byte[] Chunk(uint id,byte[] payload){using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);writer.Write(id);writer.Write(payload.Length+8);writer.Write(payload);return stream.ToArray();}
        byte[] Join(params byte[][] parts)=>parts.SelectMany(p=>p).ToArray();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var cp=Encoding.GetEncoding(1251);
        byte[] objectChunk=Chunk(0xBBBB0000,Join(Chunk(0xB002,BitConverter.GetBytes(205u)),Chunk(0xB004,cp.GetBytes("Стражник")),Chunk(0xBBBB0002,cp.GetBytes("guard")),Chunk(0xB013,[1]),Chunk(0xB009,Join(BitConverter.GetBytes(1f),BitConverter.GetBytes(2f),BitConverter.GetBytes(3f)))));
        var mob=new MobDocument(Chunk(MobDocument.RootId,Join(Chunk(MobDocument.LegacyId,cp.GetBytes("WorldScript()")),Chunk(0xB000,objectChunk))));
        var obj=MobObjects.Read(mob).Single();check(obj.Id==205&&obj.Name=="Стражник"&&obj.Prototype=="guard"&&obj.ScriptEnabled&&obj.Position=="1; 2; 3","MOB objects typed chunk and CP1251 decoding");
        byte[] leverChunk=Chunk(0xBBAC0000,Join(Chunk(0xB002,BitConverter.GetBytes(1063u)),Chunk(0xB004,cp.GetBytes("Рычаг")),Chunk(0xB006,cp.GetBytes("nmoloot300")),Chunk(0xB013,[0])));
        var leverMob=new MobDocument(Chunk(MobDocument.RootId,Join(Chunk(MobDocument.LegacyId,cp.GetBytes("WorldScript()")),Chunk(0xB000,leverChunk))));
        var leverObjects=MobObjects.Read(leverMob);
        check(leverObjects.Count==1&&leverObjects[0].Id==1063&&leverObjects[0].Kind=="Рычаг"&&leverObjects[0].Prototype=="nmoloot300",
            "MOB lever chunk is available as a typed object for script binding");
        check(mob.Encode(mob.Text).SequenceEqual(mob.Original),"object read leaves original MOB exact");
        check(ScriptReferences.Find(a,"Task",ScriptSearchKind.Symbol).Count==4,"symbol search includes declaration body and calls only");
        check(ScriptReferences.Find(a,"Task",ScriptSearchKind.Text).Count>4,"text search explicitly includes strings and comments");
        check(ScriptReferences.Find(sa,"q.a.b",ScriptSearchKind.State).Count==4,"GS search includes reads and writes with exact key");
        string protectedText="Script X(\r\n/* title\r\n   literal space\r\n*/\r\nif()then(ConsoleString(\"a\r\n  b\"))\r\n)";
        var formattedProtected=EditorOperations.Format(ScriptAnalyzer.Analyze(protectedText,registry));check(formattedProtected.Contains("/* title\r\n   literal space\r\n*/")&&formattedProtected.Contains("\"a\r\n  b\""),"format preserves multiline comments and string content");
        foreach(int x in new[]{0,1,-1})foreach(int y in new[]{0,1,-1})
        {
            // Build with the same expression through the validated template generator.
            var generated=ScriptTemplates.Build("",new(ScriptTemplateKind.Once,"Any",$"Not(Mul(Not({x}),Not({y})))","GSSetVar(0,\"seen\",1)","","","","","","",10,false),registry);
            var ga=ScriptAnalyzer.Analyze(generated.Text,registry);var gt=ScenarioPreview.Run(ga,ga.Blocks.Single(),new Dictionary<string,double>());
            check(gt.Any(s=>s.Text.Contains("← 1"))==(x!=0||y!=0),$"ANY uses supported commands and normalizes truth {x},{y}");
        }
        var withElse=ScriptTemplates.Build("",new(ScriptTemplateKind.Once,"Either","0","GSSetVar(0,\"branch\",1)","GSSetVar(0,\"branch\",2)","","","","","",10,false),registry,includeOtherwise:true);
        var elseAnalysis=ScriptAnalyzer.Analyze(withElse.Text,registry);check(ScenarioPreview.Run(elseAnalysis,elseAnalysis.Blocks.Single(),new Dictionary<string,double>()).Any(s=>s.Text.Contains("← 2")),"composer ELSE executes false branch");
        string? liveMaps=Environment.GetEnvironmentVariable("MALACHITE_TEST_MAPS");
        if(liveMaps!=null)
        {
            int readable=0,objects=0,scriptUnavailable=0;var issues=new List<string>();
            foreach(var path in Directory.EnumerateFiles(liveMaps,"*.mob",SearchOption.AllDirectories))
            {
                MobDocument live;try{live=new MobDocument(File.ReadAllBytes(path));}catch(InvalidDataException){scriptUnavailable++;continue;}
                try{objects+=MobObjects.Read(live).Count;readable++;}catch(Exception ex){issues.Add(Path.GetFileName(path)+": "+ex.Message);}
            }
            check(readable>0&&objects>0&&issues.Count==0,"live MOB object catalogue: "+readable+" maps, "+objects+" objects, "+scriptUnavailable+" script unavailable");
            File.WriteAllText(Path.Combine(output,"live-object-report.json"),System.Text.Json.JsonSerializer.Serialize(new{readable,objects,scriptUnavailable,issues}));
        }
        var workbenchHost=new TestDocumentHost{CompleteSave=true};
        var dbReader=(TestReader)workbenchHost.Workspace;
        var dbStamp=new WorkspaceStamp(Guid.NewGuid(),1,0);
        dbReader.Current=new WorkspaceSnapshot(dbStamp,"disposable-db",false,
            [new RecordDto(new RecordRef(dbStamp.SessionId,dbStamp.Generation,"quests.quests",Guid.NewGuid()),
                "qTest","Проверочный квест",[],[new TextDto("quest qTest","Проверочный квест","Найти свидетеля",false)])]);
        var view=TestViews.Create(workbenchHost);
        try
        {
            view.LoadWorkspace(new Workspace{Name="workbench",Text=source,Mob=mob},false);
            view.ApplyEditorPlan(renamed);var editor=(TextBox)view.FindName("CodeEditor");check(editor.Text==renamed.Result,"preview applies exact result");
            view.RunCommand(ModuleCommand.Undo);check(editor.Text==source,"workbench operation undone once");view.RunCommand(ModuleCommand.Redo);check(editor.Text==renamed.Result,"workbench redo restores exact text");
            Reject(()=>view.ApplyEditorPlan(renamed),"workbench rejects stale source");
            typeof(QuestView).GetField("documentOpen",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(view,true);
            typeof(QuestView).GetMethod("Workbench_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(view,[view,new RoutedEventArgs()]);
            var overlay=(Grid)typeof(QuestView).GetField("workbench",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
            var content=(Grid)overlay.Children[1];var nav=(ListBox)content.Children[0];
            check(nav.Items.Count==8&&!nav.Items.Cast<string>().Any(p=>p.Contains("Генератор")),"workbench retains eight manual tools without generator page");
            IEnumerable<T> Desc<T>(DependencyObject parent) where T:DependencyObject
            {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T t)yield return t;foreach(var nested in Desc<T>(child))yield return nested;}}
            void Click(string name)=>Desc<Button>(overlay).Single(b=>b.Content as string==name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TextBox Field(string name)=>Desc<TextBox>(overlay).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)==name);
            for(int page=0;page<8;page++)
            {
                nav.SelectedIndex=page;view.Measure(new Size(1180,680));view.Arrange(new Rect(0,0,1180,680));view.UpdateLayout();
                check(overlay.ActualHeight>400&&overlay.ActualHeight<600,"workbench page viewport "+page);
                var status=overlay.Children.OfType<DockPanel>().Last().Children.OfType<TextBlock>().Single();check(!status.Text.Contains("Exception"),"workbench page opens "+page);
                var preview=content.Children.OfType<TextBox>().Single();
                if(page==0)
                {
                    Field("Имя нового этапа").Text="DraftStep";
                    Field("Ключ квеста для условия").Text="q.a.b";Click("Добавить условие");
                    Field("Число состояния (1 — выдано; 2 — выполнено; 3 — провал)").Text="2";Click("Добавить условие");Click("Выдать");
                    check(status.Text.Contains("выдаваемый квест")&&!preview.Text.Contains("GSSetVarMax"),"quest action requires its own target");
                    Field("Ключ выдаваемого квеста").Text="q.a.b";Click("Выдать");
                    var conditionKind=Desc<ComboBox>(overlay).Single(c=>c.Items.Count==5&&c.Items[0] as string=="Состояние квеста");
                    conditionKind.SelectedIndex=3;Field("ID объекта для условия").Text="205";Click("Добавить условие");Click("Выдать");
                    Desc<ComboBox>(overlay).First().SelectedIndex=1;Click("Предпросмотр нового этапа");
                    check(preview.Text.Contains("Not( Mul(")&&preview.Text.Contains("GSSetVarMax"),"composer controls generate supported ANY and quest action");
                    check(preview.Text.Contains("GetObjectByID( \"205\" )")&&preview.Text.Contains("GSSetVarMax( 0, \"q.a.b\", 1 )")&&!preview.Text.Contains("GSSetVarMax( 0, \"205\""),"object condition cannot redirect quest action");
                    check(!ScriptAnalyzer.Analyze(preview.Text,registry).Issues.Any(i=>i.Severity=="error"||i.Code=="UNKNOWN"),"composer UI produces valid complete script");
                }
                if(page==1){var list=Desc<ListBox>(content.Children[1]).Single();list.SelectedIndex=0;check(preview.Text.Contains("Стражник"),"object picker displays actual MOB name");Click("Предпросмотр вставки GetObjectByID");check(preview.Text.Contains("GetObjectByID( \"205\" )"),"object selection inserts its ID");nav.SelectedIndex=0;check(Field("Имя нового этапа").Text=="DraftStep"&&Field("Ключ выдаваемого квеста").Text=="q.a.b","composer draft survives section switch");nav.SelectedIndex=1;}
                if(page==2){Click("Проверить ветки");check(preview.Text.Contains("Один проход"),"scenario UI runs selected script preview");}
                if(page==4){Desc<ComboBox>(overlay).First().SelectedItem="QuestStep";Field("Новое имя").Text="RenamedStep";Click("Предпросмотр переименования");check(preview.Text.Contains("DeclareScript RenamedStep"),"rename form creates preview");}
                if(page==5){Click("Форматировать · предпросмотр");check(preview.Text.Contains("    if"),"format button presents changed code");var tree=Desc<TreeView>(overlay).Single();((TreeViewItem)tree.Items[0]).IsExpanded=true;view.UpdateLayout();check(((TreeViewItem)tree.Items[0]).Items[0] is TextBox,"folded code lazily expands source");}
                if(page==6){Field("key · text").Text="q.a.b";Field("state · number").Text="1";Click("Предпросмотр вставки");check(preview.Text.Contains("GSSetVarMax( 0, \"q.a.b\", 1 )"),"user template form substitutes typed parameters");}
                if(page==7){var list=Desc<ListBox>(content.Children[1]).Single();list.SelectedIndex=0;Click("Предпросмотр восстановления выбранных");check(preview.Text.Length>0&&!preview.Text.StartsWith("СОХРАНЕНО"),"change history creates selective restore preview");}
                if(page==0||page==5||page==7){view.UpdateLayout();var image=new RenderTargetBitmap(1180,680,96,96,PixelFormats.Pbgra32);image.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var f=File.Create(Path.Combine(output,"workbench-"+page+".png"));png.Save(f);}
            }
            nav.SelectedIndex=0;
            Click("Предпросмотр нового этапа");
            string manualResult=content.Children.OfType<TextBox>().Single().Text;
            Click("Применить изменения");
            check(editor.Text==manualResult&&editor.Text.Contains("Script DraftStep"),"manual composer applies its preview as an editor operation");
            view.RunCommand(ModuleCommand.Undo);check(editor.Text==renamed.Result,"manual composer apply undoes once");
            view.RunCommand(ModuleCommand.Redo);check(editor.Text==manualResult,"manual composer redo restores exact text");
            view.RunCommand(ModuleCommand.Save);check(workbenchHost.SaveAllCalls==1&&!view.HasPendingChanges,"manual composer result saves through host document command");
            var reopened=new MobDocument(mob.Encode(editor.Text));
            check(reopened.Text==editor.Text&&MobObjects.Read(reopened).Single().Id==205&&mob.Text=="WorldScript()","manual composer MOB roundtrip preserves objects and original bytes");
        }
        finally{view.Stop();}
        var jumpView=TestViews.Create(new TestDocumentHost());
        try
        {
            jumpView.LoadWorkspace(new Workspace{Name="jump",Text=source},false);
            var jumpEditor=(TextBox)jumpView.FindName("CodeEditor");jumpEditor.CaretIndex=source.LastIndexOf("Task(1)",StringComparison.Ordinal)+2;
            var atlas=(FrameworkElement)jumpView.FindName("QuestAtlasPanel");atlas.Visibility=Visibility.Visible;
            typeof(QuestView).GetMethod("Workbench_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(jumpView,[jumpView,new RoutedEventArgs()]);
            var jumpOverlay=(Grid)typeof(QuestView).GetField("workbench",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(jumpView)!;
            ((ListBox)((Grid)jumpOverlay.Children[1]).Children[0]).SelectedIndex=5;
            var jumpBody=(StackPanel)((ScrollViewer)((Grid)jumpOverlay.Children[1]).Children[1]).Content;
            jumpBody.Children.OfType<Button>().Single(b=>b.Content as string=="К объявлению имени под курсором").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(atlas.Visibility==Visibility.Collapsed&&jumpEditor.CaretIndex==source.IndexOf("DeclareScript Task",StringComparison.Ordinal)&&typeof(QuestView).GetField("workbench",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(jumpView)==null,"workbench declaration jump reveals code and exact location [atlas="+atlas.Visibility+", caret="+jumpEditor.CaretIndex+", expected="+source.IndexOf("DeclareScript Task",StringComparison.Ordinal)+", overlay="+(typeof(QuestView).GetField("workbench",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(jumpView)!=null)+"]");
        }
        finally{jumpView.Stop();}
        var draftView=TestViews.Create(new TestDocumentHost());
        try
        {
            void OpenTools()=>typeof(QuestView).GetMethod("Workbench_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(draftView,[draftView,new RoutedEventArgs()]);
            TextBox DraftName(){var overlay=(Grid)typeof(QuestView).GetField("workbench",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(draftView)!;var body=(StackPanel)((ScrollViewer)((Grid)overlay.Children[1]).Children[1]).Content;return body.Children.OfType<TextBox>().Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Имя нового этапа");}
            draftView.LoadWorkspace(new Workspace{Name="first.mob",SourcePath="first.mob",Text=source},false);OpenTools();DraftName().Text="FirstDraft";
            var firstOverlay=(Grid)typeof(QuestView).GetField("workbench",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(draftView)!;
            ((DockPanel)firstOverlay.Children[0]).Children.OfType<Button>().Single(b=>b.Content as string=="Закрыть").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            OpenTools();bool survivesClose=DraftName().Text=="FirstDraft";
            draftView.LoadWorkspace(new Workspace{Name="second.mob",SourcePath="second.mob",Text=source},false);OpenTools();
            bool separate=DraftName().Text=="QuestStep";
            draftView.LoadWorkspace(new Workspace{Name="first.mob",SourcePath="first.mob",Text=source},false);OpenTools();
            check(survivesClose&&separate&&DraftName().Text=="FirstDraft","composer draft survives tools close and stays with its document across map switches");
            var resetOverlay=(Grid)typeof(QuestView).GetField("workbench",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(draftView)!;
            var resetBody=(StackPanel)((ScrollViewer)((Grid)resetOverlay.Children[1]).Children[1]).Content;
            resetBody.Children.OfType<Button>().Single(b=>b.Content as string=="Сбросить черновик").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(DraftName().Text=="QuestStep","composer draft has explicit reset without source edit");
        }
        finally{draftView.Stop();}
        var dbHost=new TestDocumentHost();
        var questRef=new RecordRef(Guid.NewGuid(),1,"quests.quests",Guid.NewGuid());
        ((TestReader)dbHost.Workspace).Current=new WorkspaceSnapshot(new(questRef.SessionId,1,0),"quests",false,
            [new(questRef,"q0g","Квест",[],[new("quest q0g","Найти письмо","Начало\n#subobj 2\nВернуть письмо старосте\n",false)])]);
        var dbView=TestViews.Create(dbHost);
        try
        {
            dbView.LoadWorkspace(new Workspace{Name="db",Text=source},false);
            typeof(QuestView).GetMethod("Workbench_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(dbView,[dbView,new RoutedEventArgs()]);
            var overlay=(Grid)typeof(QuestView).GetField("workbench",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(dbView)!;
            var body=(StackPanel)((ScrollViewer)((Grid)overlay.Children[1]).Children[1]).Content;
            var quests=body.Children.OfType<ListBox>().Where(l=>l.DisplayMemberPath=="Label").ToArray();quests[0].SelectedIndex=0;quests[1].SelectedIndex=0;
            var subs=body.Children.OfType<ComboBox>().Where(c=>c.DisplayMemberPath=="Label").ToArray();subs[0].SelectedIndex=0;subs[1].SelectedIndex=0;
            TextBox Field(string n)=>body.Children.OfType<TextBox>().Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)==n);
            check(subs[0].Items.Count==1&&subs[0].Items[0]!.ToString()!.Contains("Вернуть письмо")&&Field("Ключ квеста для условия").Text=="q.gz1g.q0g.2"&&Field("Ключ выдаваемого квеста").Text=="q.gz1g.q0g.2","composer resolves DB quest and subobject titles to exact keys");
        }
        finally{dbView.Stop();}
        var cancelHost=new CancelHost();var cancelView=TestViews.Create(cancelHost);
        try
        {
            cancelView.LoadWorkspace(new Workspace{Name="cancel",Text="DeclareScript Target() Script Target(if()then(KillScript()))"},false);
            var cancelMaps=(List<QuestView.IndexedMap>)typeof(QuestView).GetField("indexedMaps",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(cancelView)!;
            cancelMaps.Add(new("one.mob","hash",[]));cancelMaps.Add(new("two.mob","hash",[]));
            typeof(QuestView).GetMethod("Workbench_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(cancelView,[cancelView,new RoutedEventArgs()]);
            var cancelOverlay=(Grid)typeof(QuestView).GetField("workbench",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(cancelView)!;
            ((ListBox)((Grid)cancelOverlay.Children[1]).Children[0]).SelectedIndex=3;
            var cancelBody=(StackPanel)((ScrollViewer)((Grid)cancelOverlay.Children[1]).Children[1]).Content;
            cancelBody.Children.OfType<TextBox>().Single().Text="Target";
            var search=cancelBody.Children.OfType<Button>().Single(b=>b.Content as string=="Найти");
            var previous=System.Threading.SynchronizationContext.Current;System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(search.Dispatcher));
            try{search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}finally{System.Threading.SynchronizationContext.SetSynchronizationContext(previous);}
            cancelView.Stop();
            var frame=new System.Windows.Threading.DispatcherFrame();var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(150)};
            timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();System.Windows.Threading.Dispatcher.PushFrame(frame);
            check(cancelHost.Browser.Reads==0,"stopped workbench search starts no further map reads");
        }
        finally{cancelView.Stop();}
        var searchHost=new SearchHost();
        byte[] firstBytes=mob.Encode("DeclareScript Target() Script Target(if()then(KillScript())) WorldScript(Target())"),secondBytes=mob.Encode("DeclareScript Target() Script Target(if()then(KillScript())) WorldScript(Target()) // second");
        searchHost.Browser.Files["first.mob"]=firstBytes;searchHost.Browser.Files["second.mob"]=secondBytes;
        var searchView=TestViews.Create(searchHost);
        try
        {
            searchView.LoadWorkspace(Workspace.FromBytes("first.mob",firstBytes),false);
            var maps=(List<QuestView.IndexedMap>)typeof(QuestView).GetField("indexedMaps",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(searchView)!;
            maps.Add(new("second.mob",new MobDocument(secondBytes).Hash,[]));
            typeof(QuestView).GetMethod("Workbench_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(searchView,[searchView,new RoutedEventArgs()]);
            var overlay=(Grid)typeof(QuestView).GetField("workbench",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(searchView)!;
            var grid=(Grid)overlay.Children[1];var nav=(ListBox)grid.Children[0];nav.SelectedIndex=3;
            var body=(StackPanel)((ScrollViewer)grid.Children[1]).Content;
            body.Children.OfType<TextBox>().Single().Text="Target";
            var search=body.Children.OfType<Button>().Single(b=>b.Content as string=="Найти");
            var previousContext=System.Threading.SynchronizationContext.Current;System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(search.Dispatcher));
            try{search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}finally{System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);}
            var deadline=DateTime.UtcNow.AddSeconds(5);
            while(!search.IsEnabled&&DateTime.UtcNow<deadline)
            {
                var frame=new System.Windows.Threading.DispatcherFrame();var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};
                timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            var results=body.Children.OfType<ListBox>().Single();var rows=results.Items.Cast<QuestView.UsageRow>().ToArray();
            check(search.IsEnabled&&rows.Any(r=>r.Path=="second.mob")&&rows.Any(r=>r.Path==null),"async search includes current and second indexed MOB");
            results.SelectedItem=rows.First(r=>r.Path=="second.mob");
            results.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent});
            check(searchView.Workspace.SourcePath=="second.mob","cross-map result opens verified target");
            var history=typeof(QuestView).GetMethod("NavigateHistory",BindingFlags.NonPublic|BindingFlags.Instance)!;
            // A cross-map jump may include the target's initial caret as an intermediate point.
            for(int i=0;i<3&&searchView.Workspace.SourcePath!="first.mob";i++)history.Invoke(searchView,[-1]);
            check(searchView.Workspace.SourcePath=="first.mob","navigation history returns to previous MOB");
            for(int i=0;i<3&&searchView.Workspace.SourcePath!="second.mob";i++)history.Invoke(searchView,[1]);
            check(searchView.Workspace.SourcePath=="second.mob","navigation history forward restores target MOB");
        }
        finally{searchView.Stop();}
    }
    sealed class SearchHost : IModuleHost
    {
        public MapCacheVerification.MemoryBrowser Browser {get;}=new();
        public IWorkspaceReader Workspace {get;}=new TestReader();
        public IModuleFiles Files=>Browser;
        public System.Collections.Immutable.ImmutableArray<string> Capabilities=>[];
        public void Report(string message){}
    }
    sealed class CancelHost : IModuleHost
    {
        public CountingBrowser Browser {get;}=new();
        public IWorkspaceReader Workspace {get;}=new TestReader();
        public IModuleFiles Files=>Browser;
        public System.Collections.Immutable.ImmutableArray<string> Capabilities=>[];
        public void Report(string message){}
    }
    sealed class CountingBrowser : IModuleFileBrowser
    {
        public int Reads;
        public OpenedFile Read(string path){System.Threading.Interlocked.Increment(ref Reads);throw new IOException("unexpected read after Stop");}
        public System.Collections.Immutable.ImmutableArray<string> SelectFolder(string extension)=>[];
        public OpenedFile? Open(string filter)=>null;
        public Result Export(string name,string filter,ReadOnlyMemory<byte> bytes)=>new(ResultStatus.Cancelled);
    }
}
