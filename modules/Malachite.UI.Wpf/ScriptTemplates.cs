using System.IO;
using System.Text.RegularExpressions;

namespace QuestStudio;
public enum ScriptTemplateKind { Observer, Disguise, ObserverDisguise, GiveQuest, CompleteQuest, Briefing, Once, Repeat, Launch }
public sealed record ScriptTemplateOptions(ScriptTemplateKind Kind, string Name, string Condition, string Actions, string Otherwise,
    string Npc, string Profile, string QuestKey, string BriefingKey, string Target, int Delay, bool Start);
public sealed record ScriptTemplatePlan(string Source, string Text, string Name, string Summary);
public static class ScriptTemplates
{
    public static string Literal(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c=>c is '"' or '\\' or '\r' or '\n' or '\0'))
            throw new InvalidDataException("Введите непустое значение без кавычек, обратных слешей и переводов строк.");
        return "\""+value+"\"";
    }
    public static ScriptTemplatePlan Build(string source, ScriptTemplateOptions o, CommandRegistry registry, bool includeOtherwise = false)
    {
        var a=ScriptAnalyzer.Analyze(source,registry);
        if(a.Issues.Any(i=>i.Severity=="error")) throw new InvalidDataException("Сначала исправьте ошибки текущего кода, чтобы безопасно вставить шаблон.");
        string nl=source.Contains("\r\n") ? "\r\n" : "\n";
        string Normalize(string s)=>s.Replace("\r\n","\n").Replace("\r","\n").Replace("\n",nl);
        string actions=Normalize(o.Actions.Trim()), otherwise=Normalize(o.Otherwise.Trim());
        string condition=Normalize(o.Condition.Trim());
        string name=o.Kind==ScriptTemplateKind.Briefing ? "OnBriefingComplete" : o.Name.Trim();
        if(!Regex.IsMatch(name,@"^#?[A-Za-z_][A-Za-z0-9_]*$")) throw new InvalidDataException("Имя: латинские буквы, цифры и _, в начале допустим #.");
        string original=name;int suffix=2;
        bool Used(string n)=>a.Declarations.ContainsKey(n)||a.Globals.ContainsKey(n)||registry.Commands.ContainsKey(n)||a.Blocks.Any(b=>b.Name.Equals(n,StringComparison.OrdinalIgnoreCase))||new[]{"if","then","else","Script","WorldScript","DeclareScript","GlobalVars","float","string","object","group"}.Contains(n,StringComparer.OrdinalIgnoreCase);
        while(Used(name)) name=original+"_"+suffix++;
        string Branch(string test,string yes,string no="")=>"  if"+nl+"  ("+nl+"    "+test+nl+"  )"+nl+"  then"+nl+"  ("+nl+"    "+yes.Replace(nl,nl+"    ")+nl+"  )"+(no.Length==0?"":nl+"  else"+nl+"  ("+nl+"    "+no.Replace(nl,nl+"    ")+nl+"  )");
        string view="", declaration="", script="";
        bool briefing=o.Kind==ScriptTemplateKind.Briefing;
        var edits=new List<(int Position,string Text)>();
        if(briefing)
        {
            name="#OnBriefingComplete";
            var existing=a.Blocks.SingleOrDefault(b=>b.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            var parameters=a.Declarations.GetValueOrDefault(name);
            if(parameters!=null && (parameters.Length!=2||parameters[0].Type!="float"||parameters[1].Type!="string"))
                throw new InvalidDataException("У #OnBriefingComplete несовместимое объявление: ожидаются float, string.");
            if(existing!=null && parameters==null) throw new InvalidDataException("У существующего обработчика отсутствует DeclareScript. Сначала добавьте корректное объявление.");
            if(!o.BriefingKey.StartsWith("b.",StringComparison.Ordinal)||o.BriefingKey.Split('.').Length!=3) throw new InvalidDataException("Нужен полный ключ брифинга b.персонаж.разговор.");
            if(actions.Length==0) throw new InvalidDataException("Укажите действия после брифинга.");
            string parameter=parameters?[1].Name??"szComplete";
            view=Branch("IsEqualString("+parameter+", "+Literal(o.BriefingKey)+")",actions);
            if(existing!=null) edits.Add((existing.Body.Start,nl+view+nl));
            else script="Script "+name+nl+"("+nl+Branch("","KillScript()"+nl+view)+nl+")"+nl;
            if(parameters==null) declaration="DeclareScript "+name+"(nPlayer:float, szComplete:string)"+nl;
        }
        else
        {
            if(o.Kind is ScriptTemplateKind.GiveQuest or ScriptTemplateKind.CompleteQuest)
            {
                if(!Regex.IsMatch(o.QuestKey,@"^q\.[^.\s]+\.[^.\s]+(?:\.[^.\s]+)?$")) throw new InvalidDataException("Нужен ключ q.зона.квест или q.зона.квест.подпункт.");
                actions="GSSetVarMax(0, "+Literal(o.QuestKey)+", "+(o.Kind==ScriptTemplateKind.GiveQuest?1:2)+")"+(actions.Length==0?"":nl+actions);
            }
            if(o.Kind==ScriptTemplateKind.Launch)
            {
                if(!a.Declarations.TryGetValue(o.Target,out var parameters)||parameters.Length!=0||!a.Blocks.Any(b=>b.Name.Equals(o.Target,StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Выберите существующий Script без параметров и с телом.");
                actions=o.Target+"()";
            }
            if(actions.Length==0) throw new InvalidDataException("Укажите действия для шаблона.");
            string seen="GroupHas(UnitSee(GetObjectByID("+ (o.Kind is ScriptTemplateKind.Observer or ScriptTemplateKind.ObserverDisguise ? Literal(o.Npc):"\"0\"")+")), GetUnitOfPlayer(0, 0))";
            string disguise=o.Kind is ScriptTemplateKind.Disguise or ScriptTemplateKind.ObserverDisguise ? "PartyDisguiseMatches("+Literal(o.Profile)+")" : "";
            if(o.Kind is ScriptTemplateKind.Disguise or ScriptTemplateKind.ObserverDisguise)
            {
                if(otherwise.Length==0) throw new InvalidDataException("Укажите действия при неподходящей одежде.");
                string branches=Branch(disguise,actions,otherwise);
                string trigger=o.Kind==ScriptTemplateKind.ObserverDisguise?seen:condition;
                if(o.Kind==ScriptTemplateKind.ObserverDisguise && condition.Length>0) trigger+=nl+condition;
                // Wait for party availability: false before deployment is not an outfit failure.
                trigger="IsAlive(GetUnitOfPlayer(0, 0))"+(trigger.Length==0?"":nl+trigger);
                view=Branch(trigger,"KillScript()"+nl+branches);
            }
            else
            {
                if(o.Kind==ScriptTemplateKind.Observer) condition=seen+(condition.Length==0?"":nl+condition);
                if(o.Kind==ScriptTemplateKind.Repeat)
                {
                    if(o.Delay<1 || o.Delay>1000000) throw new InvalidDataException("Задержка: от 1 до 1000000 игровых сегментов.");
                    // A failed optional condition must still schedule the next iteration.
                    view=Branch("","KillScript()"+nl+Branch(condition,actions)+nl+"Sleep("+o.Delay+")"+nl+name+"()");
                }
                else view=Branch(condition,"KillScript()"+nl+actions,includeOtherwise && otherwise.Length>0?"KillScript()"+nl+otherwise:"");
            }
            declaration="DeclareScript "+name+"()"+nl;
            script="Script "+name+nl+"("+nl+view+nl+")"+nl;
        }
        var world=a.Blocks.SingleOrDefault(b=>b.World);
        if(!briefing && o.Start)
        {
            if(world!=null) edits.Add((world.Body.End,nl+"  "+name+"()"+nl));
            else script+=nl+"WorldScript"+nl+"("+nl+"  "+name+"()"+nl+")"+nl;
        }
        if(script.Length>0) edits.Add((world?.Start??source.Length,nl+script+nl));
        if(declaration.Length>0) edits.Add((0,declaration+nl));
        string result=source;
        foreach(var e in edits.OrderByDescending(e=>e.Position)) result=result.Insert(e.Position,e.Text);
        var parsed=ScriptAnalyzer.Analyze(result,registry);
        var errors=parsed.Issues.Where(i=>i.Severity=="error"||i.Code=="UNKNOWN").ToArray();
        if(errors.Length>0) throw new InvalidDataException("Шаблон не прошёл проверку: "+errors[0].Message);
        return new(source,result,name,briefing?"Ветка события брифинга. Существующие ветки сохранены; новый запуск из WorldScript не добавляется.":"Создаётся "+name+(o.Start?" с запуском из WorldScript.":" без автоматического запуска.")+" Вызов запускает экземпляр и не ждёт его завершения.");
    }
}
