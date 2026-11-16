using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DatabaseEditor.Modules.V1;
using QuestStudio;

internal static class PakWorkspaceVerification
{
    internal static void Run(Action<bool,string> check, string output)
    {
        var stamp = new WorkspaceStamp(Guid.NewGuid(), 1, 1);
        RecordDto Row(string name, string title, string table = "quests.quests") => new(new(stamp.SessionId,1,table,Guid.NewGuid()), name, title,
            [new(3,0,[],new("Int32","1","zone",false))], [new("quest " + name,title,"description",false)]);
        var row = Row("q0g", "Старое название");
        var snapshot = new WorkspaceSnapshot(stamp,"database.pak",false,
            Enumerable.Range(0,3786).Select(i=>Row("other"+i,"Other","items.materials")).Append(row).ToImmutableArray());
        check(QuestAtlas.QuestRecord("q.gz1g.q0g", snapshot) == row, "PAK snapshot exact quest and zone");
        check(QuestAtlas.QuestRecord("q.gz2g.q0g", snapshot) == null && QuestAtlas.QuestRecord("Q0G", snapshot) == null, "zone and case remain exact");
        var watch = Stopwatch.StartNew();
        for (int i=0;i<10000;i++) if (QuestAtlas.QuestRecord("q.gz1g.q0g",snapshot) != row) throw new Exception("Lost quest");
        double lookupMs=watch.Elapsed.TotalMilliseconds;
        var duplicate = snapshot with { Records = snapshot.Records.Add(Row("q0g","Duplicate")) };
        check(QuestAtlas.QuestRecord("q0g",duplicate) == null,"duplicate quest remains ambiguous");
        var updated = snapshot with { Stamp = stamp with { Revision = 2 }, Modified = true,
            Records = snapshot.Records.SetItem(snapshot.Records.Length-1,row with { Texts = [new("quest q0g","Новое название","edited",true)] }) };
        check(MapQuestInventory.Title("q.gz1g.q0g",updated)=="Новое название" && MapQuestInventory.Title("q.gz1g.q0g",snapshot)=="Старое название", "edited snapshot index cannot reuse stale texts");
        var registry=new CommandRegistry(); var text="Script Test ( if () then ( ConsoleString(\"quest q0g\") ) )";
        var analysis=ScriptAnalyzer.Analyze(text,registry); int position=text.IndexOf("quest q0g",StringComparison.Ordinal);
        check(DatabaseReferences.Resolve(analysis,position,snapshot)?.Records.Single()==row,"exact text resource lookup");
        File.WriteAllText(Path.Combine(output,"workspace-performance.json"),JsonSerializer.Serialize(new { records=snapshot.Records.Length, lookups=10000, lookupMs }));
    }
}
