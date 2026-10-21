using System.Reflection;
using QuestStudio;

internal static class GeneratorRemovalVerification
{
    public static void Run(Action<bool, string> check)
    {
        check(!typeof(CommandRegistry).Assembly.GetTypes().Any(t =>
            t.Namespace == "QuestStudio.Generation"),
            "shipped Core assembly contains no natural-language generation implementation");
        var types = typeof(QuestView).Assembly.GetTypes();
        string[] retired = ["LocalQwenRuntime", "LocalModelSettings", "LocalModelFiles",
            "GeneratorIntent", "NpcAnimationIntent", "CompletionEventContext"];
        check(!types.Any(t => retired.Contains(t.Name) ||
            t.Name.StartsWith("Description", StringComparison.Ordinal)),
            "shipped UI assembly contains no model runtime or description parsers");
        var fields = typeof(QuestView).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        check(!fields.Any(f => f.Name.Contains("generator", StringComparison.OrdinalIgnoreCase) ||
            f.Name == "localQwen"), "module lifecycle owns no generator or model state");
        var view = TestViews.Create(new TestHost());
        try
        {
            check(view.FindName("GeneratorButton") == null,
                "compiled view exposes no script generator button");
        }
        finally { view.Stop(); }
    }
}
