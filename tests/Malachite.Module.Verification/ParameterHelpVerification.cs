using QuestStudio;
internal static class ParameterHelpVerification
{
    public static void Run(Action<bool,string> check)
    {
        var registry=new CommandRegistry(); var help=new HelpService(registry);
        const string text="GlobalVars(szComplete:float) DeclareScript #OnBriefingComplete(nPlayer:float,szComplete:string) DeclareScript Other(szComplete:object) Script #OnBriefingComplete(if()then(ConsoleString(szComplete) GSGetVar(nPlayer,szComplete))) Script Other(if()then(GetX(szComplete))) WorldScript(#OnBriefingComplete(0,\"b.npc.dialog\"))";
        var a=ScriptAnalyzer.Analyze(text,registry);
        int declaration=text.IndexOf("szComplete:string");
        var p=help.Symbol(a,"szComplete",a,declaration);
        check(p.Kind.Contains("Параметр Script") && p.Body.Contains("string") && p.Body.Contains("полный ключ"),"declared callback parameter has type and briefing-key explanation");
        p=help.Symbol(a,"szComplete",a,text.IndexOf("szComplete) GS"));
        check(p.Kind.Contains("#OnBriefingComplete") && p.Body.Contains("ConsoleString") && p.Body.Contains("b.npc.dialog"),"body parameter shadows global and includes usage and caller expression");
        p=help.Symbol(a,"szComplete",a,text.LastIndexOf("szComplete)))"));
        check(p.Kind.Contains("Other") && p.Body.Contains("object") && !p.Body.Contains("полный ключ"),"same parameter name in another script has independent scope and no borrowed meaning");
        p=help.Symbol(a,"szComplete",a,text.IndexOf("szComplete:float"));
        check(p.Kind=="Глобальная переменная","global declaration remains global outside script scope");
        var fragment=ScriptAnalyzer.Analyze("ConsoleString(szComplete)",registry);
        p=help.Symbol(a,"szComplete",fragment,14,"#OnBriefingComplete");
        check(p.Kind.Contains("Параметр Script"),"inspector fragment resolves selected script parameters");
        p=help.Symbol(a,"#OnBriefingComplete",a,text.LastIndexOf("#OnBriefingComplete"));
        check(p.Body.Contains("szComplete (текст) ← \"b.npc.dialog\""),"custom script call maps actual arguments to declared parameter names");
        var declarationOnly=ScriptAnalyzer.Analyze("DeclareScript #OnBriefingComplete(nPlayer:float,szComplete:string)",registry);
        check(help.Symbol(declarationOnly,"#OnBriefingComplete").Body.Contains("завершения брифинга"),"event declaration is described even without body");
        var wrong=ScriptAnalyzer.Analyze("DeclareScript #OnBriefingComplete(nPlayer:object,szComplete:float)",registry);
        check(!help.Symbol(wrong,"#OnBriefingComplete").Body.Contains("движок передаёт"),"incompatible callback signature is not labelled with known argument semantics");
        check(help.Entries.Count>=registry.Commands.Count,"registered engine and addon functions retain full help coverage");
    }
}
