using System.IO;
using System.Text;
using System.Text.Json;

namespace QuestStudio;

public sealed class Workspace
{
    public string Name { get; set; } = "Потерянное письмо · пример";
    public string SourcePath { get; set; } = "";
    public string Text { get; set; } = Demo;
    public MobDocument? Mob { get; set; }
    public Encoding TextEncoding { get; set; } = new UTF8Encoding(false, true);
    public byte[] TextPreamble { get; set; } = [];
    public string NewLine => Text.Contains("\r\n") ? "\r\n" : "\n";
    public static Workspace Open(string path)
    {
        if (new FileInfo(path).Length > 128 * 1024 * 1024) throw new InvalidDataException("Файл больше 128 МиБ.");
        return FromBytes(path, File.ReadAllBytes(path));
    }
    public static Workspace FromBytes(string path, byte[] data)
    {
        if (data.Length > 128 * 1024 * 1024) throw new InvalidDataException("Файл больше 128 МиБ.");
        if (Path.GetExtension(path).Equals(".mob", StringComparison.OrdinalIgnoreCase))
        {
            var mob = new MobDocument(data);
            return new() { SourcePath = path, Name = Path.GetFileName(path), Mob = mob, Text = mob.Text, TextEncoding = MobDocument.GameEncoding };
        }
        if (Path.GetExtension(path).Equals(".malachite", StringComparison.OrdinalIgnoreCase))
        {
            var p = JsonSerializer.Deserialize<ProjectFile>(data) ?? throw new InvalidDataException("Пустой проект.");
            if (p.Format != "malachite-quest-1" || p.Text == null) throw new InvalidDataException("Неподдерживаемый формат проекта.");
            var mob = p.MobBase == null ? null : new MobDocument(Convert.FromBase64String(p.MobBase));
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return new() { Name = p.Name, SourcePath = p.SourcePath, Text = p.Text, Mob = mob, TextEncoding = Encoding.GetEncoding(p.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), TextPreamble = p.Preamble ?? [] };
        }
        Encoding encoding = new UTF8Encoding(false, true); byte[] preamble = [];
        int offset = 0; string text;
        if (data.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { preamble = [0xEF, 0xBB, 0xBF]; offset = 3; }
        try { text = encoding.GetString(data, offset, data.Length - offset); }
        catch (DecoderFallbackException) { encoding = MobDocument.GameEncoding; text = encoding.GetString(data); preamble = []; }
        return new() { Name = Path.GetFileName(path), SourcePath = path, Text = text, TextEncoding = encoding, TextPreamble = preamble };
    }
    public byte[] SaveProject() => JsonSerializer.SerializeToUtf8Bytes(new ProjectFile("malachite-quest-1", Name, SourcePath, Text, Mob == null ? null : Convert.ToBase64String(Mob.Original), TextEncoding.CodePage, TextPreamble), new JsonSerializerOptions { WriteIndented = true });
    public byte[] ExportText() => TextPreamble.Concat(TextEncoding.GetBytes(Text)).ToArray();
    public sealed record ProjectFile(string Format, string Name, string SourcePath, string Text, string? MobBase, int CodePage, byte[]? Preamble);

    public string AddStage(Analysis analysis)
    {
        int n = 1; while (analysis.Blocks.Any(b => b.Name.Equals("Stage" + n, StringComparison.OrdinalIgnoreCase)) || analysis.Declarations.ContainsKey("Stage" + n)) n++;
        string name = "Stage" + n, nl = NewLine;
        var firstBody = analysis.Blocks.FirstOrDefault();
        int insert = firstBody?.Start ?? Text.Length;
        Text = Text.Insert(insert, $"DeclareScript {name}(){nl}{nl}");
        Text += $"{nl}{nl}// Новый этап: задайте условия и свяжите запуск.{nl}Script {name}{nl}({nl}  if ( ){nl}  then{nl}  ({nl}    KillScript(){nl}    GSSetVar(0, \"q.custom.{name.ToLowerInvariant()}\", 2){nl}  ){nl}){nl}";
        return name;
    }
    public void ApplyCard(ScriptBlock block, string conditions, string actions, string otherwise, bool? includeElse = null)
    {
        if (!block.CardEditable || block.Conditions == null || block.Actions == null) throw new InvalidOperationException("Этот Script редактируется в коде.");
        var edits = new List<(Range Range, string Text)> { (block.Conditions, conditions), (block.Actions, actions) };
        bool useElse = includeElse ?? block.ElseActions != null;
        if (block.ElseActions != null && useElse) edits.Add((block.ElseActions, otherwise));
        else if (block.ElseClause != null && !useElse) edits.Add((block.ElseClause, ""));
        else if (useElse) edits.Add((new(block.Actions.End + 1, 0), "\n  else\n  (" + otherwise + ")"));
        foreach (var edit in edits.OrderByDescending(e => e.Range.Start)) Text = Text.Remove(edit.Range.Start, edit.Range.Length).Insert(edit.Range.Start, edit.Text);
    }
    public void Link(Analysis a, ScriptBlock from, string target)
    {
        if (!a.Declarations.TryGetValue(target, out var p) || p.Length != 0) throw new InvalidOperationException("Через карточку можно запускать только Script без параметров.");
        if (from.Calls.Any(c => c.Name.Equals(target, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Такой вызов уже есть в выбранном Script.");
        var range = from.World ? from.Body : from.CardEditable ? from.Actions : null;
        if (range == null) throw new InvalidOperationException("Добавьте вызов в нужную ветку кода вручную.");
        Text = Text.Insert(range.End, $"{NewLine}    {target}(){NewLine}  ");
    }
    public const string Demo = """
// Учебная цепочка. Флаги разговора и письма задаёт ваш сценарий.
// Связи показывают запуск Script, а не ожидание его завершения.
GlobalVars
(
  Heroes : group
)

DeclareScript MeetKeeper()
DeclareScript FindLetter()
DeclareScript ReturnLetter()

WorldScript
(
  MeetKeeper()
)

// 01 · Разговор с хранителем
Script MeetKeeper
(
  if
  (
    IsEqual(GSGetVar(0, "q.letter.talked"), 1)
  )
  then
  (
    KillScript()
    GSSetVar(0, "q.letter", 1)
    FindLetter()
  )
)

// 02 · Найти потерянное письмо
Script FindLetter
(
  if
  (
    IsEqual(GSGetVar(0, "q.letter.found"), 1)
  )
  then
  (
    KillScript()
    GSSetVar(0, "q.letter.return", 1)
    ReturnLetter()
  )
)

// 03 · Вернуться к хранителю
Script ReturnLetter
(
  if
  (
    IsEqual(GSGetVar(0, "q.letter.delivered"), 1)
  )
  then
  (
    KillScript()
    GSSetVar(0, "q.letter", 2)
  )
)
""";
}
