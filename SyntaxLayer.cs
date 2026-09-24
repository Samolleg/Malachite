using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace QuestStudio;

// Paint syntax above the native editor. Text input, IME, caret, selection and undo remain WPF's.
public sealed class SyntaxLayer : Adorner
{
    readonly TextBox editor;
    List<Token> tokens = [];
    static readonly Brush Base = Brush("#DBE6DF"), Keyword = Brush("#73D4AB"), String = Brush("#D5BE86"), Number = Brush("#BDA0DB"), Comment = Brush("#7C9587"), Function = Brush("#9ACBB8");
    static Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    public SyntaxLayer(TextBox editor) : base(editor)
    {
        this.editor = editor; IsHitTestVisible = false;
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => InvalidateVisual()));
        editor.SizeChanged += (_, _) => InvalidateVisual();
        editor.SelectionChanged += (_, _) => InvalidateVisual();
    }
    public void Update(Analysis analysis) { tokens = analysis.Tokens; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        if (editor.LineCount < 1 || editor.ActualWidth < 25 || editor.ActualHeight < 25) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Max(0, editor.ActualWidth - 12), Math.Max(0, editor.ActualHeight - 12))));
        int first = editor.GetFirstVisibleLineIndex(), last = editor.GetLastVisibleLineIndex();
        if (first < 0 || last < 0) { dc.Pop(); return; }
        var face = new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch);
        for (int line = first; line <= last; line++)
        {
            int start = editor.GetCharacterIndexFromLineIndex(line);
            if (start < 0) continue;
            var rect = editor.GetRectFromCharacterIndex(start, true);
            if (rect.IsEmpty) continue;
            string text = editor.GetLineText(line).TrimEnd('\r', '\n');
            if (text.Length == 0) continue;
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, editor.FontSize, Base, VisualTreeHelper.GetDpi(editor).PixelsPerDip) { MaxTextWidth = 100000 };
            foreach (var token in tokens.Where(t => t.Start < start + text.Length && t.End > start))
            {
                int begin = Math.Max(start, token.Start) - start, length = Math.Min(start + text.Length, token.End) - (start + begin);
                Brush color = token.Kind switch { "string" => String, "number" => Number, _ => Function };
                if (new[] { "Script", "DeclareScript", "WorldScript", "GlobalVars", "if", "then", "else", "object", "group", "float", "string" }.Contains(token.Value, StringComparer.OrdinalIgnoreCase)) color = Keyword;
                if (length > 0) formatted.SetForegroundBrush(color, begin, length);
            }
            int comment = text.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0 && !tokens.Any(t => t.Kind == "string" && t.Start <= start + comment && t.End > start + comment)) formatted.SetForegroundBrush(Comment, comment, text.Length - comment);
            dc.DrawText(formatted, rect.TopLeft);
        }
        dc.Pop();
    }
}
