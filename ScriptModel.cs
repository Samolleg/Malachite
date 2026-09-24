using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestStudio;

public sealed record Token(string Value, string Kind, int Start, int Length, int Line)
{ public int End => Start + Length; }
public sealed record Issue(string Severity, string Code, string Message, int Offset, int Line)
{ public string Display => $"{(Severity == "error" ? "ОШИБКА" : Severity == "warning" ? "ВНИМАНИЕ" : "СВЕДЕНИЕ")}  ·  {Line}    {Message}"; }
public sealed record Range(int Start, int Length) { public int End => Start + Length; }
public sealed record Parameter(string Name, string Type, bool Global = false);
public sealed record CommandInfo(string Name, string Signature, string ReturnType, Parameter[] Parameters, string Note, bool Native, bool Trailing = false, int[]? Arities = null, bool Variadic = false, bool DebugOnly = false, bool Internal = false, bool OverridesNative = false)
{
    public bool Accepts(int count) => Variadic ? count >= Parameters.Length : (Arities ?? [Parameters.Length]).Contains(count);
    public Parameter Argument(int index) => Parameters[Math.Min(index, Parameters.Length - 1)];
    public string ArgumentCount => Variadic ? $"не менее {Parameters.Length}" : string.Join(" или ", Arities ?? [Parameters.Length]);
}
public sealed record Call(string Name, int Start, int End, int Line, List<Range> Arguments, string Owner);
public sealed record FlagUse(string Key, string Context, bool Write, int Offset, int Line, string Owner, string Value)
{ public bool Delete { get; init; } }
public sealed class ScriptBlock
{
    public string Name { get; init; } = "";
    public int Start { get; init; }
    public int End { get; init; }
    public int Line { get; init; }
    public bool World { get; init; }
    public Range Body { get; init; } = new(0, 0);
    public Range? Conditions { get; set; }
    public Range? Actions { get; set; }
    public Range? ElseActions { get; set; }
    public Range? ElseClause { get; set; }
    public bool CardEditable { get; set; }
    public List<Call> Calls { get; } = [];
    public string Display => World ? "WorldScript · запуск карты" : Name;
    public override string ToString() => Display;
}
public sealed class CommandRegistry
{
    public Dictionary<string, CommandInfo> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int NativeCount { get; private set; }
    public int AddonReleaseCount { get; private set; }
    public string AddonSourceHash { get; private set; } = "";
    public CommandRegistry()
    {
        var asm = typeof(CommandRegistry).Assembly;
        using var input = asm.GetManifestResourceStream("QuestStudio.Data.commands.json") ?? throw new InvalidDataException("Реестр команд недоступен.");
        using var doc = JsonDocument.Parse(input);
        foreach (var c in doc.RootElement.GetProperty("commands").EnumerateArray())
        {
            var parameters = c.GetProperty("parameters").EnumerateArray().Select(p => new Parameter("arg" + p.GetProperty("position").GetInt32(), p.GetProperty("type").GetString()!, p.GetProperty("global_variable_required").GetBoolean())).ToArray();
            var name = c.GetProperty("name").GetString()!;
            Commands.Add(name, new(name, c.GetProperty("signature").GetString()!, c.GetProperty("return_type").GetString()!, parameters, c.GetProperty("note").GetString()!, true, c.GetProperty("trailing_command_body").GetBoolean()));
        }
        NativeCount = Commands.Count;
        using var addonInput = asm.GetManifestResourceStream("QuestStudio.Data.addon.json") ?? throw new InvalidDataException("Нет каталога аддона.");
        using var addon = JsonDocument.Parse(addonInput);
        AddonSourceHash = addon.RootElement.GetProperty("sourceSha256").GetString()!;
        foreach (var row in addon.RootElement.GetProperty("commands").EnumerateArray())
        {
            var name = row.GetProperty("name").GetString()!;
            var format = row.GetProperty("format").GetString()!;
            bool debug = row.GetProperty("debug").GetBoolean(), body = row.GetProperty("body").GetBoolean();
            string Type(char c) => char.ToLowerInvariant(c) switch { 'f' => "float", 's' => "string", 'o' => "object", 'g' => "group", _ => throw new InvalidDataException("Тип каталога аддона") };
            bool returns = format.Length > 0 && "FGOS".Contains(format[0]) && !body;
            var args = returns ? format[1..] : format;
            var parameters = new List<Parameter>(); var arities = new List<int>();
            foreach (var c in args)
            {
                if (c == '?') arities.Add(parameters.Count);
                else if (c != '+') parameters.Add(new("arg" + (parameters.Count + 1), Type(c), name == "ForInStr" && parameters.Count == 0));
            }
            arities.Add(parameters.Count);
            bool variadic = args.EndsWith('+');
            string signature = string.Join("\n", arities.Select(n => $"{name}({string.Join(", ", parameters.Take(n).Select(p => p.Name + ": " + p.Type))}{(variadic ? ", …" : "")}){(body ? " (действия)" : "")}"));
            Commands[name] = new(name, signature, returns ? Type(format[0]) : "void", parameters.ToArray(), "NewScriptCommands.cpp:" + row.GetProperty("line").GetInt32(), false, body, arities.ToArray(), variadic, debug, name.StartsWith("JmvBranch", StringComparison.OrdinalIgnoreCase), Commands.ContainsKey(name));
            if (!debug) AddonReleaseCount++;
        }
    }
}

public sealed class Analysis
{
    public string Text { get; init; } = "";
    public List<Token> Tokens { get; } = [];
    public List<Issue> Issues { get; } = [];
    public List<ScriptBlock> Blocks { get; } = [];
    public List<Call> Calls { get; } = [];
    public List<FlagUse> Flags { get; } = [];
    public Dictionary<string, Parameter[]> Declarations { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Range> DeclarationRanges { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, int> Pairs { get; } = [];
    public LoweredScript? Lowered { get; set; }
    public string Slice(Range r) => Text.Substring(r.Start, r.Length);
    public int LineAt(int p) { int line = 1; for (int i = 0; i < Math.Min(p, Text.Length); i++) if (Text[i] == '\n') line++; return line; }
    public ScriptBlock? BlockAt(int p) => Blocks.FirstOrDefault(b => p >= b.Start && p <= b.End);
    public void Add(string severity, string code, string message, int offset) => Issues.Add(new(severity, code, message, offset, LineAt(offset)));
}

public static class ScriptAnalyzer
{
    static readonly HashSet<string> Types = new(["float", "object", "group", "string"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Grammar = new(["GlobalVars", "DeclareScript", "Script", "WorldScript", "if", "then", "else"], StringComparer.OrdinalIgnoreCase);
    public static Analysis Analyze(string text, CommandRegistry registry)
    {
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Скрипт больше 4 МиБ символов: интерактивный анализ такой карты пока не поддержан.");
        var a = new Analysis { Text = text };
        Lex(a);
        var t = a.Tokens;
        var stack = new Stack<int>();
        for (int i = 0; i < t.Count; i++)
        {
            if (t[i].Value == "(") stack.Push(i);
            else if (t[i].Value == ")")
            {
                if (!stack.TryPop(out int open)) a.Add("error", "PAREN", "Лишняя закрывающая скобка.", t[i].Start);
                else { a.Pairs[open] = i; a.Pairs[i] = open; }
            }
        }
        foreach (int index in stack) a.Add("error", "PAREN", "Не закрыта скобка.", t[index].Start);
        var excluded = new HashSet<int>();
        for (int i = 0; i < t.Count; i++)
        {
            string word = t[i].Value;
            if (t[i].Kind != "id" || !(word.Equals("Script", StringComparison.OrdinalIgnoreCase) || word.Equals("DeclareScript", StringComparison.OrdinalIgnoreCase) || word.Equals("GlobalVars", StringComparison.OrdinalIgnoreCase) || word.Equals("WorldScript", StringComparison.OrdinalIgnoreCase))) continue;
            bool named = word.Equals("Script", StringComparison.OrdinalIgnoreCase) || word.Equals("DeclareScript", StringComparison.OrdinalIgnoreCase);
            int open = i + (named ? 2 : 1);
            if (open >= t.Count || t[open].Value != "(" || !a.Pairs.TryGetValue(open, out int close))
            { a.Add("error", "BLOCK", $"Ожидается полный блок после {word}.", t[i].Start); continue; }
            string name = named ? t[i + 1].Value : word;
            if (word.Equals("DeclareScript", StringComparison.OrdinalIgnoreCase) || word.Equals("GlobalVars", StringComparison.OrdinalIgnoreCase))
            {
                for (int x = i; x <= close; x++) excluded.Add(x);
                var parameters = new List<Parameter>();
                for (int p = open + 1; p < close;)
                {
                    if (p + 2 >= close || t[p + 1].Value != ":" || !Types.Contains(t[p + 2].Value))
                    { a.Add("error", "DECL", "Ожидается объявление «имя : object/group/float/string».", t[p].Start); break; }
                    parameters.Add(new(t[p].Value, t[p + 2].Value.ToLowerInvariant()));
                    p += 3;
                    if (p < close) { if (t[p].Value != ",") { a.Add("error", "DECL", "Между объявлениями нужна запятая.", t[p].Start); break; } p++; }
                }
                if (word.Equals("GlobalVars", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var p in parameters) if (!a.Globals.TryAdd(p.Name, p.Type)) a.Add("error", "DUP", $"Повторное объявление {p.Name}.", t[i].Start);
                }
                else if (!a.Declarations.TryAdd(name, parameters.ToArray())) a.Add("error", "DUP", $"Повторная декларация {name}.", t[i].Start);
                else a.DeclarationRanges[name] = new(t[i].Start, t[close].End - t[i].Start);
            }
            else
            {
                excluded.Add(i); if (named) excluded.Add(i + 1);
                bool world = word.Equals("WorldScript", StringComparison.OrdinalIgnoreCase);
                var b = new ScriptBlock { Name = name, Start = t[i].Start, End = t[close].End, Line = t[i].Line, World = world, Body = new(t[open].End, t[close].Start - t[open].End) };
                if (a.Blocks.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) a.Add("error", "DUP", $"Повторное определение {name}.", b.Start);
                a.Blocks.Add(b);
                int p = open + 1;
                if (!world && ReadSection(a, ref p, close, "if", out var condition) && ReadSection(a, ref p, close, "then", out var actions))
                {
                    b.Conditions = condition; b.Actions = actions;
                    int elseStart = p < close ? t[p].Start : 0;
                    if (p < close && t[p].Value.Equals("else", StringComparison.OrdinalIgnoreCase) && ReadSection(a, ref p, close, "else", out var elseActions))
                    { b.ElseActions = elseActions; b.ElseClause = new(elseStart, t[p - 1].End - elseStart); }
                    b.CardEditable = p == close;
                }
            }
            i = close;
        }
        foreach (var b in a.Blocks.Where(b => !b.World))
            if (!a.Declarations.ContainsKey(b.Name)) a.Add("warning", "NO_DECL", $"Для {b.Name} не найден DeclareScript.", b.Start);
        foreach (var name in a.Declarations.Keys)
            if (!a.Blocks.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) a.Add("warning", "NO_BODY", $"Для {name} нет тела Script в этом документе.", 0);
        for (int i = 0; i < t.Count; i++)
        {
            if (excluded.Contains(i) || t[i].Kind != "id" || !t[i].Value.Equals("if", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= t.Count || t[i + 1].Value != "(" || !a.Pairs.TryGetValue(i + 1, out int endCondition))
            { a.Add("error", "IF", "После if нужен блок условий в скобках.", t[i].Start); continue; }
            if (endCondition + 2 >= t.Count || !t[endCondition + 1].Value.Equals("then", StringComparison.OrdinalIgnoreCase) || t[endCondition + 2].Value != "(" || !a.Pairs.ContainsKey(endCondition + 2))
                a.Add("error", "THEN", "После условий if нужен then с телом в скобках.", t[i].Start);
        }
        for (int i = 0; i + 1 < t.Count; i++)
        {
            if (excluded.Contains(i) || t[i].Kind != "id" || Grammar.Contains(t[i].Value) || t[i + 1].Value != "(" || !a.Pairs.TryGetValue(i + 1, out int close)) continue;
            var args = new List<Range>();
            int start = i + 2;
            for (int p = start; p < close; p++)
            {
                if (t[p].Value == "(" && a.Pairs.TryGetValue(p, out int end)) { p = end; continue; }
                if (t[p].Value != ",") continue;
                args.Add(TokenRange(t, start, p)); start = p + 1;
            }
            if (start < close) args.Add(TokenRange(t, start, close));
            else if (start > i + 2) args.Add(new(t[close].Start, 0));
            var owner = a.BlockAt(t[i].Start);
            var call = new Call(t[i].Value, t[i].Start, t[close].End, t[i].Line, args, owner?.Name ?? "Вне блока");
            a.Calls.Add(call); owner?.Calls.Add(call);
        }
        Validate(a, registry);
        AddonValidation.Check(a, registry);
        if (!a.Issues.Any(i => i.Code is "PAREN" or "STRING" or "COMMENT"))
        {
            a.Lowered = AddonSyntax.Lower(a);
            if (a.Lowered.Error != null) a.Add("error", "ADDON_SYNTAX", a.Lowered.Error, a.Lowered.Offset);
            else if (a.Lowered.DepthExceeded) a.Add("error", "ADDON_DEPTH", "Вложенность списков действий превышает предел аддона: 64.", a.Lowered.Offset);
        }
        a.Issues.Sort((x, y) => x.Offset.CompareTo(y.Offset));
        return a;
    }
    static Range TokenRange(List<Token> t, int start, int end) => start < end ? new(t[start].Start, t[end - 1].End - t[start].Start) : new(t[end].Start, 0);
    static bool ReadSection(Analysis a, ref int p, int end, string name, out Range? range)
    {
        range = null; var t = a.Tokens;
        if (p + 1 >= end || !t[p].Value.Equals(name, StringComparison.OrdinalIgnoreCase) || t[p + 1].Value != "(" || !a.Pairs.TryGetValue(p + 1, out int close)) return false;
        range = new(t[p + 1].End, t[close].Start - t[p + 1].End); p = close + 1; return true;
    }
    static void Lex(Analysis a)
    {
        string s = a.Text; int line = 1;
        for (int p = 0; p < s.Length;)
        {
            char c = s[p];
            if (char.IsWhiteSpace(c)) { if (c == '\n') line++; p++; continue; }
            if (c == '/' && p + 1 < s.Length && s[p + 1] == '/') { while (p < s.Length && s[p] != '\n') p++; continue; }
            if (c == '/' && p + 1 < s.Length && s[p + 1] == '*')
            {
                int start = p; p += 2;
                while (p + 1 < s.Length && !(s[p] == '*' && s[p + 1] == '/')) { if (s[p] == '\n') line++; p++; }
                if (p + 1 >= s.Length) { a.Add("error", "COMMENT", "Не закрыт комментарий.", start); break; }
                p += 2; continue;
            }
            int at = p, atLine = line;
            if (c == '"')
            {
                p++; while (p < s.Length && s[p] != '"') { if (s[p] == '\n') line++; p++; }
                if (p == s.Length) a.Add("error", "STRING", "Не закрыта строка.", at); else p++;
                a.Tokens.Add(new(s[at..p], "string", at, p - at, atLine)); continue;
            }
            if ("(),:=".Contains(c)) { a.Tokens.Add(new(c.ToString(), "symbol", p++, 1, line)); continue; }
            while (p < s.Length && !char.IsWhiteSpace(s[p]) && !"(),:=\"".Contains(s[p]))
            { if (s[p] == '/' && p + 1 < s.Length && (s[p + 1] == '/' || s[p + 1] == '*')) break; p++; }
            if (p == at) p++;
            var value = s[at..p];
            a.Tokens.Add(new(value, double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? "number" : "id", at, p - at, atLine));
        }
    }
    static void Validate(Analysis a, CommandRegistry registry)
    {
        foreach (var token in a.Tokens.Where(t => t.Kind == "string"))
        {
            try { if (MobDocument.GameEncoding.GetByteCount(token.Value.Trim('"')) > 253) a.Add("warning", "STRING_SIZE", "Строка длиннее 253 байт CP1251.", token.Start); }
            catch (System.Text.EncoderFallbackException) { a.Add("error", "ENCODING", "Символ нельзя сохранить в Windows-1251.", token.Start); }
        }
        foreach (var call in a.Calls)
        {
            CommandInfo? command = registry.Commands.GetValueOrDefault(call.Name);
            if (command == null && a.Declarations.TryGetValue(call.Name, out var declaration)) command = new(call.Name, "", "void", declaration, "", true);
            if (command == null)
            { a.Add("warning", "UNKNOWN", $"{call.Name}: нет в штатном реестре или декларациях; возможна команда аддона.", call.Start); continue; }
            if (command.DebugOnly) a.Add("error", "DEBUG_ONLY", $"{call.Name}: команда только Debug-сборки; в установленном Release-аддоне отсутствует.", call.Start);
            if (!command.Accepts(call.Arguments.Count))
            { a.Add("error", "ARITY", $"{call.Name}: ожидается аргументов {command.ArgumentCount}, передано {call.Arguments.Count}.", call.Start); continue; }
            for (int n = 0; n < call.Arguments.Count; n++)
            {
                var range = call.Arguments[n]; var arg = a.Slice(range).Trim(); var expected = command.Argument(n);
                if (string.IsNullOrWhiteSpace(arg)) { a.Add("error", "EMPTY_ARG", "Пустой аргумент.", range.Start); continue; }
                string? type = InferType(a, registry, range, call.Owner);
                if (type != null && !expected.Type.Equals(type, StringComparison.OrdinalIgnoreCase)) a.Add("error", "TYPE", $"{call.Name}, аргумент {n + 1}: ожидается {expected.Type}, получен {type}.", range.Start);
                if (expected.Global && (!a.Globals.TryGetValue(arg, out var global) || global != expected.Type)) a.Add("error", "GLOBAL", $"{call.Name}: аргумент {n + 1} должен быть глобальной переменной {expected.Type}.", range.Start);
                if (type == null)
                {
                    var expression = a.Tokens.Where(t => t.Start >= range.Start && t.End <= range.End).ToArray();
                    var nestedCall = a.Calls.FirstOrDefault(c => c.Start == range.Start && c.End == range.End);
                    if (expression.Length == 1 && expression[0].Kind == "id")
                    {
                        string value = expression[0].Value;
                        bool numeric = char.IsDigit(value[0]) || (value.Length > 1 && (value[0] is '+' or '-') && char.IsDigit(value[1]));
                        a.Add(numeric ? "error" : "warning", numeric ? "INVALID_NUMBER" : "UNDEFINED", numeric ? $"«{value}» — некорректное число в аргументе {n + 1} команды {call.Name}." : $"«{value}» не объявлено в открытом документе ({call.Name}, аргумент {n + 1}). Проверьте имя или объявление в подключаемом сценарии.", range.Start);
                    }
                    else if (nestedCall == null) a.Add("error", "EXPRESSION", $"{call.Name}, аргумент {n + 1}: некорректное выражение; проверьте запятые и вложенный вызов.", range.Start);
                    else a.Add("info", "TYPE_UNKNOWN", $"{call.Name}, аргумент {n + 1}: тип результата неизвестной функции не определён.", range.Start);
                }
            }
            if (call.Name.Equals("CreateVoiceFX", StringComparison.OrdinalIgnoreCase) || call.Name.Equals("CreateVoiceFXWait", StringComparison.OrdinalIgnoreCase))
            {
                bool Number(int index, out double value) => double.TryParse(a.Slice(call.Arguments[index]).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                if (Number(2, out var volume) && (!double.IsFinite(volume) || volume < 0 || volume > 100))
                    a.Add("error", "VOICE_VOLUME", "Громкость речи должна быть от 0 до 100.", call.Arguments[2].Start);
                if (call.Arguments.Count == 5)
                {
                    bool hasNear = Number(3, out var near), hasFar = Number(4, out var far);
                    if ((hasNear && (!double.IsFinite(near) || near < 0)) || (hasFar && (!double.IsFinite(far) || far <= 0)) || (hasNear && hasFar && near >= far))
                        a.Add("error", "VOICE_DISTANCE", "Границы слышимости должны удовлетворять 0 <= ближняя < дальняя.", call.Arguments[3].Start);
                }
            }
            if (command.Trailing)
            {
                var next = a.Tokens.FirstOrDefault(t => t.Start >= call.End);
                if (next?.Value != "(") a.Add("error", "BODY", $"После {call.Name} ожидается тело команд в скобках.", call.End);
            }
            if ((call.Name.Equals("GSGetVar", StringComparison.OrdinalIgnoreCase) || call.Name.Equals("GSSetVar", StringComparison.OrdinalIgnoreCase) || call.Name.Equals("GSSetVarMax", StringComparison.OrdinalIgnoreCase) || call.Name.Equals("GSDelVar", StringComparison.OrdinalIgnoreCase)) && call.Arguments.Count >= 2)
            {
                var key = a.Slice(call.Arguments[1]).Trim();
                if (key.StartsWith('"') && key.EndsWith('"')) a.Flags.Add(new(key[1..^1], a.Slice(call.Arguments[0]).Trim(), !call.Name.Equals("GSGetVar", StringComparison.OrdinalIgnoreCase), call.Start, call.Line, call.Owner, call.Arguments.Count == 3 ? a.Slice(call.Arguments[2]) : "") { Delete = call.Name.Equals("GSDelVar", StringComparison.OrdinalIgnoreCase) });
            }
        }
        foreach (var b in a.Blocks)
        {
            if (b.Calls.Any(c => c.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)) && !b.Calls.Any(c => c.Name.Equals("Sleep", StringComparison.OrdinalIgnoreCase)))
                a.Add("warning", "CYCLE", $"{b.Name} запускает себя без обнаруженного Sleep; проверьте частоту и число экземпляров.", b.Start);
            if (!b.World && b.CardEditable && b.Conditions is { } condition && !a.Tokens.Any(t => t.Start >= condition.Start && t.End <= condition.End) && !b.Calls.Any(c => c.Name.Equals("KillScript", StringComparison.OrdinalIgnoreCase)))
                a.Add("warning", "REPEAT", "Безусловный этап без KillScript: действия могут повторяться.", b.Start);
        }
        if (a.Blocks.Count(b => b.World) == 0) a.Add("info", "WORLD", "WorldScript не найден: документ может быть фрагментом.", 0);
    }
    public static string? InferType(Analysis a, CommandRegistry registry, Range r, string owner)
    {
        var tokens = a.Tokens.Where(t => t.Start >= r.Start && t.End <= r.End).ToArray();
        if (tokens.Length == 1)
        {
            var t = tokens[0];
            if (t.Kind == "string") return "string";
            if (t.Kind == "number") return "float";
            if (a.Declarations.TryGetValue(owner, out var parameters))
            { var p = parameters.FirstOrDefault(p => p.Name.Equals(t.Value, StringComparison.OrdinalIgnoreCase)); if (p != null) return p.Type; }
            return a.Globals.GetValueOrDefault(t.Value);
        }
        var call = a.Calls.FirstOrDefault(c => c.Start == r.Start && c.End == r.End);
        if (call != null)
        {
            if (registry.Commands.TryGetValue(call.Name, out var c)) return c.ReturnType;
            if (a.Declarations.ContainsKey(call.Name)) return "void";
        }
        return null;
    }
}
