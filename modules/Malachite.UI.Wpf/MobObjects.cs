using System.Buffers.Binary;
using System.IO;
namespace QuestStudio;

public sealed record MobObject(uint Id,string Name,string Kind,string Prototype,string Position,bool ScriptEnabled)
{
    public string Display=>$"{(Name.Length==0?"Без имени":Name)} · ID {Id} · {Kind}";
    public string Details=>$"{Display}\nПрототип: {Prototype}\nПоложение: {Position}\nИспользуется в скриптах: {(ScriptEnabled?"да":"нет")}";
    public override string ToString()=>Display;
}
public static class MobObjects
{
    // MapEditObject::WriteData and ChunkFile::WriteString: CP1251 payload, no length prefix.
    public static IReadOnlyList<MobObject> Read(MobDocument mob)
    {
        var bytes=mob.Original;var root=MobDocument.Chunks(bytes,0,bytes.Length).Single(c=>c.Id==MobDocument.RootId);
        var result=new List<MobObject>();
        foreach(var section in MobDocument.Chunks(bytes,root.Offset+8,root.Length-8).Where(c=>c.Id==0xB000))
        foreach(var item in MobDocument.Chunks(bytes,section.Offset+8,section.Length-8))
        {
            // Units, objects and levers share the CMEObject identity fields.
            if(item.Id!=0xB001 && item.Id!=0xBBBB0000 && item.Id!=0xBBAC0000)continue;
            var fields=MobDocument.Chunks(bytes,item.Offset+8,item.Length-8);
            var id=fields.SingleOrDefault(c=>c.Id==0xB002);if(id==null)continue;
            if(id.Length!=12)throw new InvalidDataException("Неверный размер ID объекта MOB.");
            uint number=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(id.Offset+8));
            string Text(uint field){var c=fields.SingleOrDefault(c=>c.Id==field);return c==null?"":MobDocument.GameEncoding.GetString(bytes,c.Offset+8,c.Length-8).TrimEnd('\0');}
            var use=fields.SingleOrDefault(c=>c.Id==0xB013);if(use!=null&&use.Length!=9)throw new InvalidDataException("Неверный флаг объекта MOB.");
            var pos=fields.SingleOrDefault(c=>c.Id==0xB009);string position="не указано";
            if(pos!=null){if(pos.Length!=20)throw new InvalidDataException("Неверные координаты MOB.");position=string.Join("; ",Enumerable.Range(0,3).Select(i=>BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(pos.Offset+8+i*4)).ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)));}
            string kind=item.Id==0xBBBB0000?"Юнит":item.Id==0xBBAC0000?"Рычаг":"Объект";
            result.Add(new(number,Text(0xB004),kind,Text(item.Id==0xBBBB0000?0xBBBB0002u:0xB006u),position,use!=null&&bytes[use.Offset+8]==1));
        }
        return result.OrderBy(o=>o.Name,StringComparer.OrdinalIgnoreCase).ThenBy(o=>o.Id).ToArray();
    }
}
