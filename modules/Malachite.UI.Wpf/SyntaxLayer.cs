using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace QuestStudio;

// Native TextBox owns input/selection/hit-testing. Paint at its leading-edge
// coordinates, splitting tabs so a second formatter cannot move the glyphs.
public sealed class SyntaxLayer : Adorner
{
    readonly TextBox editor;
    readonly Func<List<EditorSpan>>? getTokens;
    List<EditorSpan> tokens = [];
    readonly HashSet<int> functions = [];
    readonly Dictionary<int, int> pairs = [];
    readonly Dictionary<int, List<(int Offset, FormattedText Text)>> lines = [];
    readonly Dictionary<int, FormattedText> lineNumbers = [];
    string? source;
    double dpi;
    static readonly HashSet<string> Keywords = new(new[] { "Script", "DeclareScript", "WorldScript", "GlobalVars", "if", "then", "else", "object", "group", "float", "string" }, StringComparer.OrdinalIgnoreCase);
    static readonly Brush Base = Brush("#DBE6DF"), Keyword = Brush("#66E3AA"), String = Brush("#EDCA82"), Number = Brush("#C5A3FF"), Comment = Brush("#819B88"), Function = Brush("#82CFFA"), Brackets = Brush("#5037654E"), Active = Brush("#30285A3C"), Accent = Brush("#39B86E"), Gutter = Brush("#131513");
    static Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    public SyntaxLayer(TextBox editor, Func<List<EditorSpan>>? getTokens = null) : base(editor)
    {
        this.editor = editor; this.getTokens = getTokens; IsHitTestVisible = false;
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => InvalidateVisual()));
        editor.SizeChanged += (_, _) => InvalidateVisual();
        editor.SelectionChanged += (_, _) => InvalidateVisual();
    }
    public void Update(Analysis analysis) => UpdateText(editor.Text);
    public void UpdateText(string text)
    {
        if (source == text) { InvalidateVisual(); return; }
        source = text; tokens = getTokens?.Invoke() ?? EditorLexical.Scan(text);
        functions.Clear(); pairs.Clear(); lines.Clear(); lineNumbers.Clear();
        EditorSpan? previous = null; var stack = new Stack<int>();
        foreach (var token in tokens)
        {
            if (token.Kind == "comment") continue;
            if (token.Value == "(") { stack.Push(token.Start); if (previous?.Kind == "id") functions.Add(previous.Start); }
            else if (token.Value == ")" && stack.TryPop(out int open)) { pairs[open] = token.Start; pairs[token.Start] = open; }
            previous = token;
        }
        InvalidateVisual();
    }
    FormattedText Format(string text, Brush color) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch), editor.FontSize, color,
        null, TextFormattingMode.Display, dpi);
    int FirstToken(int offset)
    {
        int lo = 0, hi = tokens.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (tokens[mid].End <= offset) lo = mid + 1; else hi = mid; }
        return lo;
    }
    List<(int Offset, FormattedText Text)> Line(int index, int start)
    {
        if (lines.TryGetValue(index, out var cached)) return cached;
        if (lines.Count > 200) lines.Clear(); // bounded to visited viewport lines
        var result = new List<(int, FormattedText)>();
        string line = editor.GetLineText(index).TrimEnd('\r', '\n');
        for (int begin = 0; begin < line.Length;)
        {
            if (line[begin] == '\t') { begin++; continue; }
            int end = line.IndexOf('\t', begin); if (end < 0) end = line.Length;
            var formatted = Format(line[begin..end], Base);
            for (int i = FirstToken(start + begin); i < tokens.Count && tokens[i].Start < start + end; i++)
            {
                var t = tokens[i]; int from = Math.Max(start + begin, t.Start), to = Math.Min(start + end, t.End);
                Brush color = t.Kind switch { "string" => String, "number" => Number, "comment" => Comment, _ => Base };
                if (functions.Contains(t.Start)) color = Function;
                if (t.Kind == "id" && Keywords.Contains(t.Value)) color = Keyword;
                if (to > from) formatted.SetForegroundBrush(color, from - start - begin, to - from);
            }
            result.Add((start + begin, formatted)); begin = end;
        }
        return lines[index] = result;
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (Visibility != Visibility.Visible || source != editor.Text) return;
        if (editor.LineCount < 1 || editor.ActualWidth < 80 || editor.ActualHeight < 25) return;
        double currentDpi = VisualTreeHelper.GetDpi(editor).PixelsPerDip;
        if (dpi != currentDpi) { dpi = currentDpi; lines.Clear(); lineNumbers.Clear(); }
        int first = editor.GetFirstVisibleLineIndex(), last = editor.GetLastVisibleLineIndex();
        if (first < 0 || last < 0) return;
        double right = Math.Max(58, editor.ActualWidth - 12), bottom = Math.Max(0, editor.ActualHeight - 12);
        dc.PushClip(new RectangleGeometry(new Rect(52, 0, right - 52, bottom)));
        var active = editor.GetRectFromCharacterIndex(editor.CaretIndex, false);
        if (!active.IsEmpty && editor.SelectionLength == 0) dc.DrawRectangle(Active, null, new Rect(52, active.Y, right - 52, active.Height));
        int selected = pairs.ContainsKey(editor.CaretIndex) ? editor.CaretIndex : editor.CaretIndex - 1;
        if (pairs.TryGetValue(selected, out int other))
            foreach (int offset in new[] { selected, other })
            {
                var rect = editor.GetRectFromCharacterIndex(offset, false);
                if (!rect.IsEmpty) dc.DrawRectangle(Brackets, null, new Rect(rect.X, rect.Y, editor.FontSize * .65, rect.Height));
            }
        for (int line = first; line <= last; line++)
        {
            int start = editor.GetCharacterIndexFromLineIndex(line); if (start < 0) continue;
            foreach (var part in Line(line, start))
            {
                var rect = editor.GetRectFromCharacterIndex(part.Offset, false);
                if (!rect.IsEmpty) dc.DrawText(part.Text, rect.TopLeft);
            }
        }
        dc.Pop();
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, 52, bottom)));
        dc.DrawRectangle(Gutter, null, new Rect(0, 0, 52, bottom));
        for (int line = first; line <= last; line++)
        {
            var rect = editor.GetRectFromCharacterIndex(editor.GetCharacterIndexFromLineIndex(line), false);
            if (rect.IsEmpty) continue;
            if (!lineNumbers.TryGetValue(line, out var number))
            {
                if (lineNumbers.Count > 200) lineNumbers.Clear();
                lineNumbers[line] = number = Format((line + 1).ToString(CultureInfo.InvariantCulture), Comment);
            }
            dc.DrawText(number, new Point(46 - number.Width, rect.Y));
        }
        if (!active.IsEmpty) dc.DrawRectangle(Accent, null, new Rect(0, active.Y, 3, active.Height));
        dc.Pop();
    }
}
