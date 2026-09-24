using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DatabaseEditor.Modules.V1;
using QuestStudio;
internal static class TemplateVerification
{
    public static void Run(Action<bool,string> check,string output)
    {
        var registry=new CommandRegistry();
        ScriptTemplateOptions Options(ScriptTemplateKind k)=>new(k,"Task","IsEqual(1,1)","ConsoleString(\"yes\")","ConsoleString(\"no\")","205","Поселковые","q.gz1g.q0g.2","b.npc.dialog","Target",10,true);
        const string source="// preserved\r\nDeclareScript Target()\r\nScript Target(if()then(KillScript()))\r\nWorldScript(Target())\r\n";
        foreach(var kind in Enum.GetValues<ScriptTemplateKind>())
        {
            var plan=ScriptTemplates.Build(source,Options(kind),registry);
            var a=ScriptAnalyzer.Analyze(plan.Text,registry);
            check(!a.Issues.Any(i=>i.Severity=="error"||i.Code is "UNKNOWN" or "ARITY" or "TYPE"),"template valid typed script: "+kind);
            check(plan.Text.Contains("// preserved\r\n") && plan.Text.Contains("Script Target(if()then(KillScript()))") && !plan.Text.Replace("\r\n","").Contains('\n'),"template preserves existing source and CRLF: "+kind);
            if(kind!=ScriptTemplateKind.Briefing)check(a.Blocks.Single(b=>b.World).Calls.Count(c=>c.Name==plan.Name)==1,"template starts once in existing WorldScript: "+kind);
        }
        var standalone=ScriptTemplates.Build("",Options(ScriptTemplateKind.Once),registry);
        check(ScriptAnalyzer.Analyze(standalone.Text,registry).Blocks.Count(b=>b.World)==1,"template creates absent WorldScript");
        var twice=ScriptTemplates.Build(standalone.Text,Options(ScriptTemplateKind.Once),registry);
        check(twice.Name=="Task_2","template avoids colliding names");
        var manual=ScriptTemplates.Build("",Options(ScriptTemplateKind.Once) with {Start=false},registry);
        check(!manual.Text.Contains("WorldScript"),"template supports manual start without hidden invocation");
        const string callback="DeclareScript #OnBriefingComplete(player:float,key:string) Script #OnBriefingComplete(if()then(KillScript() ConsoleString(\"old\"))) WorldScript()";
        var extended=ScriptTemplates.Build(callback,Options(ScriptTemplateKind.Briefing),registry);
        var ea=ScriptAnalyzer.Analyze(extended.Text,registry);
        check(ea.Blocks.Count(b=>b.Name=="#OnBriefingComplete")==1 && extended.Text.Contains("IsEqualString(key, \"b.npc.dialog\")"),"briefing extends existing handler and uses actual declared parameter");
        check(extended.Text.IndexOf("IsEqualString")<extended.Text.IndexOf("KillScript") && extended.Text.Contains("KillScript() ConsoleString(\"old\")"),"briefing inserts before terminal checks and preserves existing actions");
        check(!ea.Blocks.Single(b=>b.World).Calls.Any(c=>c.Name=="#OnBriefingComplete"),"briefing event not started from WorldScript");
        void Reject(ScriptTemplateOptions o,string title){try{ScriptTemplates.Build(source,o,registry);check(false,title);}catch(InvalidDataException){check(true,title);}}
        Reject(Options(ScriptTemplateKind.Repeat) with {Delay=0},"reject busy repeat");
        Reject(Options(ScriptTemplateKind.Observer) with {Npc="205\") bad("},"reject quoted literal injection");
        Reject(Options(ScriptTemplateKind.Launch) with {Target="Missing"},"reject absent launch target");
        Reject(Options(ScriptTemplateKind.Once) with {Actions="UnknownCommand()"},"reject unrecognized action");
        var repeat=ScriptTemplates.Build(source,Options(ScriptTemplateKind.Repeat),registry);
        check(repeat.Text.Contains("Sleep(10)") && repeat.Text.IndexOf("Sleep(10)")<repeat.Text.LastIndexOf("Task()"),"repeat schedules next instance after bounded pause");
        var view=new QuestView(new TestDocumentHost());
        try
        {
            view.LoadWorkspace(new Workspace{Name="templates",Text=source},false);
            view.ApplyTemplate(ScriptTemplates.Build(source,Options(ScriptTemplateKind.ObserverDisguise),registry));
            var editor=(TextBox)view.FindName("CodeEditor");string changed=editor.Text;
            view.RunCommand(ModuleCommand.Undo);check(editor.Text==source,"entire template insertion including startup undone once");
            view.RunCommand(ModuleCommand.Redo);check(editor.Text==changed,"template redo restores exact document");
            try{view.ApplyTemplate(standalone);check(false,"stale preview rejected");}catch(InvalidDataException){check(true,"stale preview rejected");}
            typeof(QuestView).GetMethod("Templates_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(view,[view,new RoutedEventArgs()]);
            view.Measure(new Size(1180,680));view.Arrange(new Rect(0,0,1180,680));view.UpdateLayout();
            var overlay=(Grid)typeof(QuestView).GetField("templateOverlay",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(1180,680,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(view);
            var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,"templates.png")))png.Save(file);
            check(overlay.ActualHeight>400 && overlay.ActualHeight<600,"template form bounded in minimum viewport");
        }
        finally{view.Stop();}
    }
}
