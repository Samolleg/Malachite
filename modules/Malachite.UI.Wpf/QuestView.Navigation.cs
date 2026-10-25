using System.IO;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

public partial class QuestView
{
    sealed record NavigationPoint(string Path,string Name,int Offset,string? Hash);
    readonly List<NavigationPoint> navigationHistory=[];
    int navigationIndex=-1;bool replayingNavigation;
    MobDocument? objectsSource;
    IReadOnlyList<MobObject> objectsCache=[];
    string? objectsError;
    IReadOnlyList<MobObject> CurrentObjects()
    {
        if(!ReferenceEquals(objectsSource,Workspace.Mob))
        {
            var source=Workspace.Mob;objectsError=null;
            try{objectsCache=source==null?[]:MobObjects.Read(source);}
            catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or OverflowException)
            {objectsCache=[];objectsError="Объекты MOB не прочитаны: "+ex.Message;}
            objectsSource=source;
        }
        return objectsCache;
    }
    HoverHelp? ObjectHelp(Analysis local,int position)
    {
        var call=local.Calls.Where(c=>c.Name.Equals("GetObjectByID",StringComparison.OrdinalIgnoreCase)&&c.Arguments.Count==1&&position>=c.Start&&position<c.End).OrderBy(c=>c.End-c.Start).FirstOrDefault();
        if(call==null)return null;
        string literal=local.Slice(call.Arguments[0]).Trim();
        if(!literal.StartsWith('"')||!literal.EndsWith('"')||!uint.TryParse(literal.Trim('"'),out var id))return null;
        try
        {
            var objects=CurrentObjects().Where(o=>o.Id==id).ToArray();
            if(objectsError!=null)return new("Объект "+id,"Ошибка чтения объектов MOB",objectsError,"Источник: открытая карта.");
            if(objects.Length==0)return new("Объект "+id,"Ссылка по ID","В исходном MOB объект не найден. Он может создаваться во время игры или загружаться из другого MOB.","Источник: объекты открытой карты.");
            return new(objects[0].Display,"Объект открытой карты",string.Join("\n\n",objects.Select(o=>o.Details)),"ID из GetObjectByID; данные исходного MOB, не состояние запущенной игры.");
        }
        catch(InvalidDataException ex){return new("Объект "+id,"Не удалось прочитать объекты MOB",ex.Message,"Источник: открытый MOB.");}
    }
    void RecordNavigation(int offset)
    {
        if(replayingNavigation||!documentOpen)return;
        var p=new NavigationPoint(Workspace.SourcePath??"",Workspace.Name,offset,Workspace.Mob?.Hash);
        if(navigationIndex>=0&&navigationHistory[navigationIndex]==p)return;
        if(navigationIndex+1<navigationHistory.Count)navigationHistory.RemoveRange(navigationIndex+1,navigationHistory.Count-navigationIndex-1);
        navigationHistory.Add(p);if(navigationHistory.Count>100)navigationHistory.RemoveAt(0);navigationIndex=navigationHistory.Count-1;
    }
    void NavigateHistory(int direction)
    {
        int target=navigationIndex+direction;if(target<0||target>=navigationHistory.Count){SetStatus("Больше переходов нет.");return;}
        var p=navigationHistory[target];
        if(!p.Path.Equals(Workspace.SourcePath??"",StringComparison.OrdinalIgnoreCase)||p.Path.Length==0&&p.Name!=Workspace.Name)
        {
            if(p.Path.Length==0)throw new InvalidDataException("Несохранённый документ из истории уже закрыт.");
            if(Host.Files is not IModuleFileBrowser browser)throw new InvalidDataException("Нет доступа к карте из истории.");
            var file=browser.Read(p.Path);var w=Workspace.FromBytes(file.DisplayPath,file.Content.ToArray());if(p.Hash!=null&&w.Mob?.Hash!=p.Hash)throw new InvalidDataException("Карта из истории изменена; откройте её заново.");
            if(documentHost==null&&!CanLeave())return;LoadOpenedFile(file);
        }
        replayingNavigation=true;try{GoToCode(p.Offset);navigationIndex=target;}finally{replayingNavigation=false;}
    }
}
