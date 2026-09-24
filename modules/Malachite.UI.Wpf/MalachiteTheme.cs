using System.Windows.Media;

namespace QuestStudio;

// Same semantic palette as the Database Editor; shared by dynamically created controls.
public static class MalachiteTheme
{
    static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
    public static SolidColorBrush Surface { get; } = Brush("#1B1E1C");
    public static SolidColorBrush Popup { get; } = Brush("#171A18");
    public static SolidColorBrush Text { get; } = Brush("#EEF1EC");
    public static SolidColorBrush Muted { get; } = Brush("#AAB2AA");
    public static SolidColorBrush Border { get; } = Brush("#303632");
    public static SolidColorBrush StrongBorder { get; } = Brush("#465048");
    public static SolidColorBrush Warning { get; } = Brush("#F2C96D");
    public static SolidColorBrush Accent { get; } = Brush("#39B86E");
}
