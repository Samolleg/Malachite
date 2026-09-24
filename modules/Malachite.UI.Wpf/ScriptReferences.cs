namespace QuestStudio;
public enum ScriptSearchKind { Symbol, State, Text }
public sealed record ScriptReference(int Offset,int Line,string Owner,string Kind,string Excerpt);
public static class ScriptReferences
{
    public static IReadOnlyList<ScriptReference> Find(Analysis a,string query,ScriptSearchKind kind,int limit=1000)
    {
        if(string.IsNullOrWhiteSpace(query))return [];
        var result=new List<ScriptReference>();
        void Add(int offset,string owner,string category)
        {if(result.Count>=limit)return;int end=a.Text.IndexOf('\n',offset);if(end<0)end=a.Text.Length;result.Add(new(offset,a.LineAt(offset),owner,category,a.Text[offset..Math.Min(end,offset+120)].Trim()));}
        if(kind==ScriptSearchKind.State)
        {
            foreach(var f in a.Flags.Where(f=>f.Key.Equals(query,StringComparison.Ordinal)))Add(f.Offset,f.Owner,f.Delete?"Удаление GS":f.Write?"Запись GS":"Чтение GS");
        }
        else if(kind==ScriptSearchKind.Symbol)
        {
            if(a.DeclarationRanges.TryGetValue(query,out var range))Add(range.Start,query,"Объявление");
            foreach(var b in a.Blocks.Where(b=>b.Name.Equals(query,StringComparison.OrdinalIgnoreCase)))Add(b.Start,b.Name,"Тело Script");
            foreach(var c in a.Calls.Where(c=>c.Name.Equals(query,StringComparison.OrdinalIgnoreCase)))Add(c.Start,c.Owner,"Вызов → "+c.Name);
        }
        else
        {
            for(int offset=0;result.Count<limit;offset+=query.Length)
            {offset=a.Text.IndexOf(query,offset,StringComparison.OrdinalIgnoreCase);if(offset<0)break;Add(offset,a.BlockAt(offset)?.Name??"документ","Текст");}
        }
        return result.OrderBy(r=>r.Offset).ToArray();
    }
}
