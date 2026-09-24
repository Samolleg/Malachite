using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuestStudio;

public sealed record TextChange(int Start, int Length, string Replacement);
public sealed record EditorPlan(string Source, string Result, string Description);
public sealed record LineChange(int Start, int Length, string Before, string After)
{
    public override string ToString() => $"Позиция {Start}: −{Before.Length} / +{After.Length} символов";
}
public static class EditorOperations
{
    // WorldServer constructs this call by name after completing a briefing.
    static readonly HashSet<string> EngineCallbacks = new(StringComparer.OrdinalIgnoreCase) { "#OnBriefingComplete" };
    public static string Apply(string source, IEnumerable<TextChange> changes)
    {
        int boundary=source.Length; var result=new StringBuilder(source);
        foreach(var c in changes.OrderByDescending(c=>c.Start))
        {
            if(c.Start<0 || c.Length<0 || c.Start+c.Length>boundary) throw new InvalidDataException("Пересекающиеся изменения.");
            result.Remove(c.Start,c.Length).Insert(c.Start,c.Replacement); boundary=c.Start;
        }
        return result.ToString();
    }
    public static void Identifier(string value)
    {
        if(!Regex.IsMatch(value,@"^#?[A-Za-z_][A-Za-z_0-9#]*$")) throw new InvalidDataException("Имя: латинские буквы, цифры, _, #; первая буква или _. ");
    }
    public static EditorPlan Rename(Analysis a, CommandRegistry registry, string oldName, string newName, string? parameterOwner=null)
    {
        Identifier(newName);
        if(a.Issues.Any(i=>i.Severity=="error")) throw new InvalidDataException("Сначала устраните ошибки структуры скрипта.");
        if(oldName.Equals(newName,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Укажите другое имя.");
        if(parameterOwner==null && EngineCallbacks.Contains(oldName))
            throw new InvalidDataException(oldName+" вызывается движком по точному имени после брифинга. Обычное переименование отключит этот обработчик.");
        if(registry.Commands.ContainsKey(newName) || a.Globals.ContainsKey(newName) || a.Declarations.ContainsKey(newName) || a.Blocks.Any(b=>b.Name.Equals(newName,StringComparison.OrdinalIgnoreCase)) || new[]{"if","then","else","Script","WorldScript","DeclareScript","GlobalVars","float","string","object","group"}.Contains(newName,StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Имя уже занято или зарезервировано.");
        var ranges=new List<Range>();
        if(parameterOwner!=null)
        {
            if(!a.Declarations.TryGetValue(parameterOwner,out var pars) || !pars.Any(p=>p.Name.Equals(oldName,StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Параметр не найден.");
            if(pars.Any(p=>p.Name.Equals(newName,StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Такой параметр уже есть.");
            if(a.DeclarationRanges.TryGetValue(parameterOwner,out var dr)) ranges.Add(dr);
            ranges.AddRange(a.Blocks.Where(b=>b.Name.Equals(parameterOwner,StringComparison.OrdinalIgnoreCase)).Select(b=>b.Body));
        }
        else
        {
            if(!a.Declarations.ContainsKey(oldName)) throw new InvalidDataException("Выберите объявленный Script.");
            // A script's name can also be a parameter in another scope. Rename only definitions and actual calls.
            var offsets=a.Calls.Where(c=>c.Name.Equals(oldName,StringComparison.OrdinalIgnoreCase)).Select(c=>c.Start).ToHashSet();
            for(int i=1;i<a.Tokens.Count;i++) if(a.Tokens[i].Value.Equals(oldName,StringComparison.OrdinalIgnoreCase) && (a.Tokens[i-1].Value.Equals("Script",StringComparison.OrdinalIgnoreCase)||a.Tokens[i-1].Value.Equals("DeclareScript",StringComparison.OrdinalIgnoreCase))) offsets.Add(a.Tokens[i].Start);
            ranges.AddRange(a.Tokens.Where(t=>offsets.Contains(t.Start)).Select(t=>new Range(t.Start,t.Length)));
        }
        var edits=a.Tokens.Where(t=>t.Kind!="string" && t.Value.Equals(oldName,StringComparison.OrdinalIgnoreCase) && ranges.Any(r=>t.Start>=r.Start&&t.End<=r.End)).Select(t=>new TextChange(t.Start,t.Length,newName)).ToArray();
        var result=Apply(a.Text,edits); var checkedResult=ScriptAnalyzer.Analyze(result,registry);
        if(checkedResult.Issues.Any(i=>i.Severity=="error")) throw new InvalidDataException("Переименование создаёт ошибки; изменения не применены.");
        return new(a.Text,result,$"{oldName} → {newName}: {edits.Length} мест в текущем документе. Другие карты не изменяются.");
    }
    public static string Format(Analysis a)
    {
        if(a.Issues.Any(i=>i.Code=="PAREN")) throw new InvalidDataException("Сначала исправьте скобки.");
        var protectedSpans=EditorLexical.Scan(a.Text).Where(t=>t.Kind is "string" or "comment").ToArray();
        var edits=new List<TextChange>(); int depth=0,tokenIndex=0,protectedIndex=0;
        foreach(Match line in Regex.Matches(a.Text,@"[^\r\n]*(?:\r\n|\r|\n|$)"))
        {
            if(line.Length==0) continue;
            var text=line.Value.TrimEnd('\r','\n'); int whitespace=text.Length-text.TrimStart(' ','\t').Length;
            int firstToken=tokenIndex;
            while(tokenIndex<a.Tokens.Count&&a.Tokens[tokenIndex].Start<line.Index+text.Length)tokenIndex++;
            var tokens=a.Tokens.Skip(firstToken).Take(tokenIndex-firstToken).ToArray();
            // Never indent continuation lines inside a string literal.
            while(protectedIndex<protectedSpans.Length&&protectedSpans[protectedIndex].End<=line.Index)protectedIndex++;
            bool inString=protectedIndex<protectedSpans.Length&&protectedSpans[protectedIndex].Start<line.Index&&protectedSpans[protectedIndex].End>line.Index;
            int leading=tokens.TakeWhile(t=>t.Value==")").Count();
            if(text.Length>whitespace && !inString) edits.Add(new(line.Index,whitespace,new string(' ',Math.Min(64,Math.Max(0,depth-leading))*4)));
            depth=Math.Max(0,depth+tokens.Count(t=>t.Value=="(")-tokens.Count(t=>t.Value==")"));
        }
        return Apply(a.Text,edits);
    }
    public static EditorPlan Comment(string source,int start,int length)
    {
        if(source.Length==0)return new(source,source,"Нет строк для комментария.");
        int first=start==0?-1:source.LastIndexOf('\n',Math.Min(source.Length-1,start-1)); first=first<0?0:first+1;
        int end=Math.Min(source.Length,start+length); if(length>0 && end>first && source[end-1]=='\n') end--;
        var offsets=new List<int>(); for(int p=first;p<=end && p<source.Length;){ offsets.Add(p);int next=source.IndexOf('\n',p);if(next<0)break;p=next+1; }
        bool remove=offsets.Count>0 && offsets.All(p=>source[p..].TrimStart(' ','\t').StartsWith("//"));
        var changes=offsets.Select(p=>{int q=p;while(q<source.Length && (source[q]==' '||source[q]=='\t'))q++;return new TextChange(q,remove?2:0,remove?"":"//");});
        return new(source,Apply(source,changes),remove?"Убрать комментарий со строк":"Закомментировать строки");
    }
    public static int MatchingBracket(Analysis a,int offset)
    {
        int index=a.Tokens.FindIndex(t=>t.Start==offset && (t.Value=="("||t.Value==")"));
        if(index<0) index=a.Tokens.FindIndex(t=>t.End==offset && (t.Value=="("||t.Value==")"));
        return index>=0 && a.Pairs.TryGetValue(index,out int mate)?a.Tokens[mate].Start:-1;
    }
    public static List<LineChange> Diff(string before,string after)
    {
        // Bounded LCS with common prefix/suffix removed. A large replacement remains one honest hunk.
        string[] Lines(string s)=>Regex.Matches(s,@"[^\r\n]+(?:\r\n|\r|\n|$)|\r\n|\r|\n").Select(m=>m.Value).ToArray();
        var x=Lines(before);var y=Lines(after);int p=0,xe=x.Length,ye=y.Length;
        while(p<xe&&p<ye&&x[p]==y[p])p++;
        while(xe>p&&ye>p&&x[xe-1]==y[ye-1]){xe--;ye--;}
        int position=y.Take(p).Sum(s=>s.Length), n=xe-p,m=ye-p;
        if(n==0&&m==0)return [];
        if((long)n*m>2_000_000) return [new(position,y.Skip(p).Take(m).Sum(s=>s.Length),string.Concat(x.Skip(p).Take(n)),string.Concat(y.Skip(p).Take(m)))];
        var lcs=new int[n+1,m+1];for(int i=n-1;i>=0;i--)for(int j=m-1;j>=0;j--)lcs[i,j]=x[p+i]==y[p+j]?lcs[i+1,j+1]+1:Math.Max(lcs[i+1,j],lcs[i,j+1]);
        var result=new List<LineChange>();int a=0,b=0;var old=new StringBuilder();var current=new StringBuilder();int at=position;
        void Flush(){if(old.Length+current.Length>0){result.Add(new(at,current.Length,old.ToString(),current.ToString()));old.Clear();current.Clear();}at=position;}
        while(a<n||b<m)
        {
            if(a<n&&b<m&&x[p+a]==y[p+b]){Flush();position+=y[p+b].Length;a++;b++;at=position;}
            else if(a<n&&(b==m||lcs[a+1,b]>=lcs[a,b+1]))old.Append(x[p+a++]);
            else {current.Append(y[p+b]);position+=y[p+b++].Length;}
        }
        Flush();return result;
    }
}

public sealed record UserScriptTemplate(string Name,string Body,string Description);
public static class UserTemplateLibrary
{
    public static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Malachite","script-templates.json");
    public static UserScriptTemplate[] Load(string path)
    {
        if(!File.Exists(path))return [];
        if(new FileInfo(path).Length>2_000_000)throw new InvalidDataException("Библиотека шаблонов больше 2 МБ.");
        return JsonSerializer.Deserialize<UserScriptTemplate[]>(File.ReadAllText(path))??[];
    }
    public static void Save(string path,IEnumerable<UserScriptTemplate> templates)
    {
        var array=templates.ToArray();if(array.Length>200||array.Any(t=>string.IsNullOrWhiteSpace(t.Name)||t.Body.Length>200_000))throw new InvalidDataException("Не более 200 шаблонов, до 200000 символов каждый.");
        var data=JsonSerializer.Serialize(array,new JsonSerializerOptions{WriteIndented=true});if(data.Length>1_000_000)throw new InvalidDataException("Библиотека слишком велика.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,data,new UTF8Encoding(false));File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
    // Placeholders have explicit types; strings are supplied without surrounding quotes.
    public static string Expand(string body,IReadOnlyDictionary<string,string> values)
    {
        var result=Regex.Replace(body,@"\{\{(id|text|number):([A-Za-z_][A-Za-z_0-9]*)\}\}",m=>{
            if(!values.TryGetValue(m.Groups[2].Value,out var v))throw new InvalidDataException("Заполните параметр "+m.Groups[2].Value);
            switch(m.Groups[1].Value){case "id":EditorOperations.Identifier(v);return v;case "text":return ScriptTemplates.Literal(v);default:if(!double.TryParse(v,NumberStyles.Float,CultureInfo.InvariantCulture,out var d)||!double.IsFinite(d))throw new InvalidDataException("Ожидается число: "+m.Groups[2].Value);return d.ToString(CultureInfo.InvariantCulture);}
        });
        if(result.Contains("{{")||result.Contains("}}"))throw new InvalidDataException("Нераспознанный параметр. Используйте {{id:name}}, {{text:key}} или {{number:state}}.");
        return result;
    }
}
