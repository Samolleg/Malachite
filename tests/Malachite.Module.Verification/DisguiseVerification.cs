using QuestStudio;
internal static class DisguiseVerification
{
    public static void Run(Action<bool,string> check)
    {
        var registry=new CommandRegistry(); var help=new HelpService(registry);
        var command=registry.Commands["PartyDisguiseMatches"];
        check(!command.Native && !command.DebugOnly && command.ReturnType=="float" && command.Parameters.Length==1 && command.Parameters[0].Type=="string","disguise registry: Release numeric predicate with one profile string");
        const string script="DeclareScript Test() Script Test(if(PartyDisguiseMatches(\"Поселковые\"))then(KillScript())else(Sleep(1)))";
        var analysis=ScriptAnalyzer.Analyze(script,registry);
        check(!analysis.Issues.Any(i=>i.Code is "UNKNOWN" or "ARITY" or "TYPE" or "DEBUG_ONLY"),"disguise valid Cyrillic profile condition accepted");
        foreach(var args in new[]{"", "\"p\",0"})
            check(ScriptAnalyzer.Analyze("WorldScript(PartyDisguiseMatches("+args+"))",registry).Issues.Any(i=>i.Code=="ARITY"),"disguise rejects wrong arity: "+args);
        check(ScriptAnalyzer.Analyze("WorldScript(PartyDisguiseMatches(123))",registry).Issues.Any(i=>i.Code=="TYPE"),"disguise rejects numeric profile");
        var tip=help.Command("PartyDisguiseMatches",analysis,analysis.Calls.First());
        check(tip.Body.Contains("Поселковые") && tip.Body.Contains("иначе 0") && tip.Body.Contains("не меняет отношения") && tip.Body.Contains("minimum"),"disguise help shows argument, outcome and behavior limits");
        check(ReadableScenario.Describe(analysis,analysis.Blocks.Single(),help).Any(l=>l.Text.Contains("Одежда всех живых") && l.Text.Contains("Поселковые")),"disguise readable scenario explains profile condition");
        check(help.Script(analysis,analysis.Blocks.Single()).Body.Contains("соответствует профилю"),"disguise script tooltip decodes nested condition");
        var view=new QuestView(new TestDocumentHost());
        try
        {
            view.LoadWorkspace(new Workspace {Name="disguise",Text="WorldScript(PartyDisg)"},false);
            var editor=(System.Windows.Controls.TextBox)view.FindName("CodeEditor"); editor.CaretIndex=20;
            check(view.CompletionCandidates(true).Any(c=>c.Name=="PartyDisguiseMatches"),"disguise available in completion");
        }
        finally { view.Stop(); }
    }
}
