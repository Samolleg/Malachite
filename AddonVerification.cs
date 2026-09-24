using System.IO;
using System.Text.Json;

namespace QuestStudio;

public static class AddonVerification
{
    public static void WriteParityFixture(string output, string name, Analysis a)
    {
        var dir = Path.Combine(output, "parity"); Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".input"), MobDocument.GameEncoding.GetBytes(a.Text));
        var lower = a.Lowered ?? AddonSyntax.Lower(a);
        File.WriteAllBytes(Path.Combine(dir, name + ".managed"), MobDocument.GameEncoding.GetBytes(lower.Text));
        File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(new { lower.Changed, HasError = lower.Error != null }));
    }
    public static void Run(CommandRegistry registry, HelpService help, Action<bool, string> require, string output)
    {
        require(registry.AddonReleaseCount == 94, "addon inventory: all 94 Release registrations");
        require(registry.Commands.Count(c => c.Value.DebugOnly) == 10, "addon inventory: ten Debug-only registrations explicitly unavailable");
        string Value(Parameter p) => p.Type switch { "float" => "1", "string" => "\"text\"", "object" => "unit", "group" => "units", _ => throw new InvalidDataException() };
        foreach (var c in registry.Commands.Values.Where(c => !c.Native))
        {
            foreach (var count in (c.Arities ?? [c.Parameters.Length]).Concat(c.Variadic ? [c.Parameters.Length + 3] : Array.Empty<int>()))
            {
                string args = string.Join(",", Enumerable.Range(0, count).Select(n => c.Name == "ForInStr" && n == 0 ? "text" : Value(c.Argument(n))));
                var parsed = ScriptAnalyzer.Analyze($"GlobalVars(unit:object,units:group,text:string) WorldScript({c.Name}({args}){(c.Trailing ? "(ConsoleString(\"body\"))" : "")})", registry);
                require(!parsed.Issues.Any(i => i.Code is "UNKNOWN" or "ARITY" or "TYPE" or "GLOBAL" or "BODY" or "ADDON_SYNTAX"), "addon signature: " + c.Name + "/" + count);
                require(parsed.Issues.Any(i => i.Code == "DEBUG_ONLY") == c.DebugOnly, "addon availability: " + c.Name + "/" + count);
                require(help.Command(c.Name, parsed, parsed.Calls.First()).Body.Contains("Результат:"), "addon call help: " + c.Name + "/" + count);
            }
            int min = (c.Arities ?? [c.Parameters.Length]).Min();
            require(!c.Accepts(-1) && (min == 0 || !c.Accepts(min - 1)), "addon rejects below minimum: " + c.Name);
            if (!c.Variadic) require(!c.Accepts(c.Parameters.Length + 1), "addon rejects above maximum: " + c.Name);
        }
        var scenarios = new Dictionary<string, string>
        {
            ["empty-else"] = "DeclareScript A() Script A(if()then()else()) WorldScript(A())",
            ["nested"] = "GlobalVars(x:float) WorldScript(if(IsEqual(x,1) IsLess(x,5))then(x=Add(x,1,2) if()then(Sleep(2))else(Sleep(3)))else(Sleep(4)))",
            ["sleep-once"] = "DeclareScript A() Script A(if(IsEqual(GSGetVar(0,\"Said1\"),0))then(GSSetVar(0,\"Said1\",1) Sleep(905))else(ConsoleString(\"other\")))",
            ["multiple"] = "DeclareScript A() Script A(if()then(Sleep(1)) if(IsEqual(1,2))then()else(Sleep(3)))",
            ["world-if"] = "WorldScript(if(IsEqual(1,2))then(ConsoleString(\"Да\")) ConsoleString(\"Нет\"))",
            ["loop-body"] = "GlobalVars(i:object,g:group) WorldScript(For(i,g)(if()then(Sleep(1))else(Sleep(2))))",
            ["bad-orphan"] = "WorldScript(else(Sleep(1)))",
            ["bad-else-if"] = "WorldScript(if()then()else if()then())",
            ["bad-double"] = "WorldScript(if()then()else()else())",
            ["bad-then"] = "WorldScript(if()else())",
            ["legacy"] = "// no edit\nWorldScript(ConsoleString(\"else // if\"))",
            ["prefix"] = "DeclareScript if_task#1() Script if_task#1(if()then(KillScript())) WorldScript(if_task#1())",
            ["depth64"] = "WorldScript(" + string.Concat(Enumerable.Repeat("if()then(", 63)) + "ConsoleString(\"ok\")" + new string(')',64),
            ["depth65"] = "WorldScript(" + string.Concat(Enumerable.Repeat("if()then(", 64)) + "ConsoleString(\"bad\")" + new string(')',65)
        };
        foreach (var pair in scenarios)
        {
            var a = ScriptAnalyzer.Analyze(pair.Value, registry); WriteParityFixture(output, "fixture-" + pair.Key, a);
            if (pair.Key.StartsWith("bad-")) require(a.Issues.Any(i => i.Code == "ADDON_SYNTAX"), "addon structural rejection: " + pair.Key);
            else if (pair.Key == "depth65") require(a.Issues.Any(i => i.Code is "ADDON_DEPTH" or "ADDON_SYNTAX"), "addon depth65 rejected");
            else require(a.Issues.All(i => i.Severity != "error"), "addon structure accepted: " + pair.Key);
        }
        require(ScriptAnalyzer.Analyze("GlobalVars(i:object,g:group) WorldScript(For(i,g)(CreateVoiceFXWait(i,\"x.wav\",100)))", registry).Issues.Any(i => i.Code == "VOICE_SYNC_BODY"), "voice waiting forbidden in synchronous loop");
        require(ScriptAnalyzer.Analyze("WorldScript(ConsoleString(Add(1,2,3)))", registry).Issues.Any(i => i.Code == "TYPE"), "variadic nested return type checked");
        require(ScriptAnalyzer.Analyze("WorldScript(ConsoleString(\"a\",1))", registry).Issues.Any(i => i.Code == "TYPE"), "variadic repeated type checked");
        require(ScriptAnalyzer.Analyze("WorldScript(if(ConsoleString(\"a\"))then())", registry).Issues.Any(i => i.Code == "CONDITION_TYPE"), "void action rejected as condition");
        require(ScriptAnalyzer.Analyze("GlobalVars(x:float) WorldScript(x=StrCat(\"a\",\"b\"))", registry).Issues.Any(i => i.Code == "ASSIGN_TYPE"), "assignment result type checked");
        require(ScriptAnalyzer.Analyze("WorldScript(ConsoleString(\"a\")())", registry).Issues.Any(i => i.Code == "UNEXPECTED_BODY"), "body must belong to body-taking command");
        require(ScriptAnalyzer.Analyze("WorldScript(JmvBranchSkip(-1))", registry).Issues.Any(i => i.Code == "BRANCH_OFFSET"), "forward-only internal offsets checked");
    }
}
