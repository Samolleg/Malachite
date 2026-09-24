using System.Globalization;
using System.IO;
namespace QuestStudio;

public sealed record PreviewStep(int Offset,string Text,bool Unknown=false)
{public override string ToString()=>Text;}
// One pass through one Script. Never executes game commands or predicts later ticks.
public sealed class ScenarioPreview
{
    readonly Analysis a;
    readonly Dictionary<string,double> values;
    readonly List<PreviewStep> steps=[];
    bool stopped, killed;
    ScenarioPreview(Analysis a,IReadOnlyDictionary<string,double> input){this.a=a;values=new(input,StringComparer.Ordinal);}
    public static IReadOnlyList<PreviewStep> Run(Analysis analysis,ScriptBlock block,IReadOnlyDictionary<string,double> input)
    {
        if(analysis.Issues.Any(i=>i.Severity=="error"))throw new InvalidDataException("Исправьте ошибки перед проверкой веток.");
        if(input.Values.Any(v=>!double.IsFinite(v)))throw new InvalidDataException("Входные значения должны быть конечными числами.");
        var p=new ScenarioPreview(analysis,input);
        p.steps.Add(new(block.Start,"Один проход: "+block.Display+". Значения заданы вручную; игровое время и другие скрипты не исполняются."));
        int start=analysis.Tokens.FindIndex(t=>t.Start>=block.Body.Start),end=analysis.Tokens.FindIndex(t=>t.Start>=block.Body.End);
        if(start>=0)p.Sequence(start,end<0?analysis.Tokens.Count:end,0);
        if(!p.stopped)p.steps.Add(new(block.End,p.killed?"Экземпляр снят KillScript. Следующие проверки не выполняются.":"Конец одного прохода. Повторные проверки игрового цикла не моделируются."));
        return p.steps;
    }
    public static Dictionary<string,double> ParseInputs(string text)
    {
        var result=new Dictionary<string,double>(StringComparer.Ordinal);
        foreach(var line in text.Split('\n').Select(l=>l.Trim()).Where(l=>l.Length>0))
        {
            int equal=line.LastIndexOf('=');
            if(equal<1||!double.TryParse(line[(equal+1)..].Trim(),NumberStyles.Float,CultureInfo.InvariantCulture,out double value)||!double.IsFinite(value))throw new InvalidDataException("Значение задаётся как выражение = число: "+line);
            result[line[..equal].Trim()]=value;
        }
        return result;
    }
    void Stop(int offset,string message){steps.Add(new(offset,message,true));stopped=true;}
    // Script arguments and return values are float in the engine. Round at each operation,
    // not only at the final comparison, or Add(0.1,0.2) takes the wrong branch.
    static double? FloatValue(double value){float rounded=(float)value;return float.IsFinite(rounded)?rounded:null;}
    double? Eval(Range range,int depth=0)
    {
        if(depth>32)return null;
        var text=a.Slice(range).Trim();if(double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var number))return FloatValue(number);
        var call=a.Calls.FirstOrDefault(c=>c.Start>=range.Start&&c.End<=range.End&&a.Text[range.Start..c.Start].Trim().Length==0&&a.Text[c.End..range.End].Trim().Length==0);
        if(call==null)return values.TryGetValue(text,out var given)?FloatValue(given):null;
        var args=call.Arguments;double? V(int i)=>i<args.Count?Eval(args[i],depth+1):null;
        string name=call.Name.ToLowerInvariant();
        if(name=="gsgetvar"&&args.Count==2)
        {
            var context=V(0);string literal=a.Slice(args[1]).Trim();
            if(context==null||!literal.StartsWith('"')||!literal.EndsWith('"'))return null;
            string key="GS:"+context.Value.ToString(CultureInfo.InvariantCulture)+":"+literal.Trim('"');
            if(values.TryGetValue(key,out var value))return FloatValue(value);
            if(values.TryGetValue(text,out value)){values[key]=value;return FloatValue(value);}
            return null;
        }
        if(name is not ("not" or "isequal" or "isgreater" or "isless" or "add" or "sub" or "mul" or "div"))return values.TryGetValue(text,out var external)?FloatValue(external):null;
        if(name is "add" or "mul")
        {
            if(args.Count<2)return null;
            double? total=name=="add"?0:1;
            for(int i=0;i<args.Count;i++){var part=V(i);if(!part.HasValue)return null;total=FloatValue(name=="add"?total!.Value+part.Value:total!.Value*part.Value);if(!total.HasValue)return null;}
            return total;
        }
        double? x=V(0),y=args.Count>1?V(1):null;
        if(name=="not")return x.HasValue?(x==0?1:0):null;
        if(!x.HasValue||!y.HasValue)return null;
        return name switch {"isequal"=>x==y?1:0,"isgreater"=>x>y?1:0,"isless"=>x<y?1:0,"add"=>FloatValue(x.Value+y.Value),"sub"=>FloatValue(x.Value-y.Value),"mul"=>FloatValue(x.Value*y.Value),"div" when y!=0=>FloatValue(x.Value/y.Value),_=>null};
    }
    double? Conditions(int start,int end)
    {
        bool unknown=false;
        for(int i=start;i<end;)
        {
            var c=a.Calls.FirstOrDefault(c=>c.Start==a.Tokens[i].Start);
            int last=c?.End??a.Tokens[i].End;
            var r=new Range(a.Tokens[i].Start,last-a.Tokens[i].Start);var v=Eval(r);
            steps.Add(new(r.Start,(v==null?"? ":v==0?"НЕТ: ":"ДА: ")+a.Slice(r),v==null));
            if(v==0)return 0; if(v==null)unknown=true;
            do{i++;}while(i<end&&a.Tokens[i].Start<last);
        }
        return unknown?null:1;
    }
    void Sequence(int start,int end,int depth)
    {
        if(depth>32){Stop(a.Tokens[start].Start,"Превышена глубина 32.");return;}
        var t=a.Tokens;
        for(int i=start;i<end&&!stopped;)
        {
            if(steps.Count>=500){Stop(t[i].Start,"Лимит 500 шагов.");return;}
            if(t[i].Value.Equals("if",StringComparison.OrdinalIgnoreCase)&&i+1<end&&a.Pairs.TryGetValue(i+1,out int conditionEnd)&&conditionEnd+2<end&&t[conditionEnd+1].Value.Equals("then",StringComparison.OrdinalIgnoreCase)&&a.Pairs.TryGetValue(conditionEnd+2,out int yesEnd))
            {
                int next=yesEnd+1,noStart=-1,noEnd=-1;
                if(next+1<end&&t[next].Value.Equals("else",StringComparison.OrdinalIgnoreCase)&&a.Pairs.TryGetValue(next+1,out noEnd)){noStart=next+2;next=noEnd+1;}
                var decision=Conditions(i+2,conditionEnd);
                if(decision==null){Stop(t[i].Start,"Ветка неизвестна. Задайте выражение из строки «?» или GS:контекст:ключ = число и повторите.");return;}
                steps.Add(new(t[i].Start,decision!=0?"Выбрано ТОГДА":"Выбрано ИНАЧЕ"));
                if(decision!=0)Sequence(conditionEnd+3,yesEnd,depth+1);else if(noStart>=0)Sequence(noStart,noEnd,depth+1);
                if(depth==0&&killed)break;
                i=next;continue;
            }
            var call=a.Calls.FirstOrDefault(c=>c.Start==t[i].Start);
            if(call==null){Stop(t[i].Start,"Не поддерживается: "+t[i].Value);return;}
            string name=call.Name.ToLowerInvariant();
            if(name=="killscript"){killed=true;steps.Add(new(call.Start,"KillScript: дальнейшее обслуживание снято; текущие действия продолжаются."));}
            else if((name=="gssetvar"||name=="gssetvarmax")&&call.Arguments.Count==3)
            {
                var args=call.Arguments;var value=Eval(args[2]);var context=Eval(args[0]);string literal=a.Slice(args[1]).Trim();
                if(!context.HasValue||!literal.StartsWith('"')||!literal.EndsWith('"')){Stop(call.Start,"Не определён контекст или ключ GS.");return;}
                string key="GS:"+context.Value.ToString(CultureInfo.InvariantCulture)+":"+literal.Trim('"');
                if(name=="gssetvarmax"){
                    if(!values.TryGetValue(key,out var current)){Stop(call.Start,"Для максимума нужно исходное значение "+key);return;}
                    var roundedCurrent=FloatValue(current);if(roundedCurrent==null){Stop(call.Start,"Исходное состояние не представимо как float.");return;}
                    if(value!=null)value=Math.Max(roundedCurrent.Value,value.Value);
                }
                if(!value.HasValue||!double.IsFinite(value.Value)){Stop(call.Start,"Неизвестное значение записи GS.");return;}
                var rounded=FloatValue(value.Value);if(rounded==null){Stop(call.Start,"Результат не представим как float в движке.");return;}
                values[key]=rounded.Value;steps.Add(new(call.Start,key+" ← "+rounded.Value.ToString(CultureInfo.InvariantCulture)));
            }
            else {Stop(call.Start,(name=="sleep"?"Пауза: после неё внешнее состояние может измениться. ":"Игровое действие требует движка. ")+a.Text[call.Start..call.End]+" — остановка прогноза.");return;}
            do{i++;}while(i<end&&t[i].Start<call.End);
        }
    }
}
