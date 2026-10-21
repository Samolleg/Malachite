using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DatabaseEditor.Modules.V1;
using QuestStudio;

internal static class TextEditingVerification
{
    sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    public static void Run(Action<bool,string> check, string output)
    {
        var view = TestViews.Create(new TestDocumentHost());
        try
        {
            void Click(string method) => typeof(QuestView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view,[view,new RoutedEventArgs()]);
            var editor = (TextBox)view.FindName("CodeEditor");
            string sample = "WorldScript\r\n(\r\n\tGSSetVar(0, \"Имя\", 1)\r\n  GSSetVar(0, \"name\", 2)\r\n)";
            view.LoadWorkspace(new Workspace { Name="editor-test", Text=sample }, false); Click("Code_Click");
            view.Measure(new Size(1400,850)); view.Arrange(new Rect(0,0,1400,850)); view.UpdateLayout(); view.AttachSyntax();
            editor.Select(sample.IndexOf("Имя"),3); editor.SelectedText="Квест";
            check(editor.Text.Contains("\"Квест\"") && editor.Text.Replace("Квест","Имя")==sample, "replace Cyrillic selection changes exactly its source range");
            view.RunCommand(ModuleCommand.Undo); check(editor.Text==sample, "undo restores exact text with tabs and CRLF");
            view.RunCommand(ModuleCommand.Redo); check(editor.Text.Contains("Квест"), "redo restores replacement");
            view.LoadWorkspace(new Workspace { Name="indent",Text="one\r\n\ttwo\r\nthree" },false);
            editor.Select(0,10); view.IndentSelection(false);
            check(editor.Text=="  one\r\n  \ttwo\r\nthree", "indent selected full lines excludes trailing line at boundary");
            view.IndentSelection(true); check(editor.Text=="one\r\n\ttwo\r\nthree", "unindent reverses two-space block indentation without changing tabs");
            view.LoadWorkspace(new Workspace { Name="enter",Text="  one\r\n" },false);
            editor.Select(5,0);
            var enter = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new TestPresentationSource(), 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            typeof(QuestView).GetMethod("TextEditingKey",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[editor,enter]);
            check(enter.Handled && editor.Text=="  one\r\n  \r\n" && editor.CaretIndex==9,"Enter preserves indentation and CRLF at the exact caret");
            view.RunCommand(ModuleCommand.Undo); check(editor.Text=="  one\r\n","autoindent is one undo operation");
            editor.Select(2,3); editor.SelectedText="first\r\nsecond";
            check(editor.Text=="  first\r\nsecond\r\n","multiline paste replacement preserves surrounding text");
            editor.Select(2,13); editor.SelectedText="";
            check(editor.Text=="  \r\n","delete selection removes exact multiline range");
            view.LoadWorkspace(new Workspace { Name="find",Text="alpha ALPHA alpha" },false);
            view.RunCommand(ModuleCommand.Find);
            var find=(TextBox)view.FindName("FindText"); find.Text="alpha";
            editor.Select(0,0); Click("FindNext_Click"); check(editor.SelectionStart==0 && editor.SelectionLength==5,"find searches code rather than stage list");
            Click("FindNext_Click"); check(editor.SelectionStart==6,"find advances case-insensitively");
            ((TextBox)view.FindName("ReplaceText")).Text="beta"; Click("ReplaceAll_Click");
            check(editor.Text=="beta beta beta","replace all uses literal matches and entire source");
            view.RunCommand(ModuleCommand.Undo); check(editor.Text=="alpha ALPHA alpha","replace all is one host undo operation");
            Click("FindClose_Click");
            view.LoadWorkspace(new Workspace {Name="glyphs",Text=sample+"\r\n"+string.Concat(Enumerable.Repeat("// spacer\r\n",60))},false); Click("Code_Click");
            view.Measure(new Size(1400,850)); view.Arrange(new Rect(0,0,1400,850)); view.UpdateLayout();
            editor.Select(editor.Text.Length,0); editor.ScrollToHome(); view.UpdateLayout();
            var syntax=(SyntaxLayer)typeof(QuestView).GetField("syntax",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
            syntax.Visibility=Visibility.Hidden; editor.Foreground=MalachiteTheme.Text; view.UpdateLayout();
            var native=Render(view,Path.Combine(output,"editor-native.png"));
            syntax.UpdateText(editor.Text); syntax.Visibility=Visibility.Visible; editor.Foreground=Brushes.Transparent; view.UpdateLayout();
            var colored=Render(view,Path.Combine(output,"editor-syntax.png"));
            for(int line=0;line<4;line++)
            {
                int offset=editor.GetCharacterIndexFromLineIndex(line);
                var rect=editor.GetRectFromCharacterIndex(offset,false); var origin=editor.TranslatePoint(new Point(),view);
                int y=(int)(origin.Y+rect.Y), x=(int)(origin.X+52);
                int a=FirstInk(native,x,y,(int)editor.ActualWidth-65,(int)rect.Height), b=FirstInk(colored,x,y,(int)editor.ActualWidth-65,(int)rect.Height);
                check(a>=0 && Math.Abs(a-b)<=1,"syntax glyph origin matches native caret geometry on line "+line);
            }
            using (var hover = new HoverController(view,editor))
            {
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var oldTip=new ToolTip { Visibility=Visibility.Hidden, Content="old", IsOpen=true };
                typeof(HoverController).GetField("tooltip",flags)!.SetValue(hover,oldTip);
                int target=editor.Text.IndexOf("GSSetVar"); var targetRect=editor.GetRectFromCharacterIndex(target,false);
                typeof(HoverController).GetMethod("MoveTo",flags)!.Invoke(hover,[new Point(targetRect.X+1,targetRect.Y+targetRect.Height/2)]);
                int pending=(int)typeof(HoverController).GetField("position",flags)!.GetValue(hover)!;
                var timer=(DispatcherTimer)typeof(HoverController).GetField("timer",flags)!.GetValue(hover)!;
                check(pending==target && timer.IsEnabled && oldTip.IsOpen,"open help does not freeze next hover target; old card remains during crossing delay");
                check(view.HelpAt(editor,pending)?.Title.Contains("GSSetVar")==true,"pending hover resolves the new function description");
                hover.Close(); check(!oldTip.IsOpen && !timer.IsEnabled,"closing hover removes old card and pending replacement");
            }
            int mark=editor.Text.IndexOf("Имя"); var r=editor.GetRectFromCharacterIndex(mark,false);
            check(editor.GetCharacterIndexFromPoint(new Point(r.X+1,r.Y+r.Height/2),true)==mark,"mouse hit testing matches Cyrillic after tab");
            editor.Select(mark,3); editor.SelectedText="Ввод";
            check(editor.Foreground!=Brushes.Transparent && syntax.Visibility!=Visibility.Visible,"typing immediately uses current native glyphs rather than stale overlay");
            Pump(250); check(syntax.Visibility==Visibility.Visible,"syntax returns after short typing pause");
            string latest=editor.Text+"\r\n// latest"; editor.Text=latest;
            typeof(QuestView).GetMethod("RefreshAnalysisInBackground",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,null);
            editor.Text=latest+"!"; view.RefreshAnalysis(false); Pump(150);
            check(view.Current.Text==editor.Text,"stale background analysis cannot overwrite newer explicit result");
            foreach (var mode in new[] { "Scenario_Click", "Mixed_Click", "Code_Click" })
            {
                Click(mode); view.Measure(new Size(1250,650));view.Arrange(new Rect(0,0,1250,650));view.UpdateLayout();
                var diagnostics=(ListBox)view.FindName("Diagnostics"); var bounds=diagnostics.TransformToAncestor(view).TransformBounds(new Rect(diagnostics.RenderSize));
                check(bounds.Bottom<=619 && editor.ActualHeight>=60,"editor and diagnostics remain inside small viewport in "+mode);
            }
            Click("Code_Click");
            var split=(GridSplitter)view.FindName("DiagnosticsSplitter");
            var row=(RowDefinition)view.FindName("DiagnosticsRow");
            check(split.ResizeDirection==GridResizeDirection.Rows && split.ResizeBehavior==GridResizeBehavior.PreviousAndNext && split.IsHitTestVisible,"checks separator resizes adjacent code and diagnostics rows");
            double previous=0;
            foreach (int height in new[] { 70, 230 })
            {
                row.Height=new GridLength(height); view.UpdateLayout();
                var diagnostics=(ListBox)view.FindName("Diagnostics");
                var bounds=diagnostics.TransformToAncestor(view).TransformBounds(new Rect(diagnostics.RenderSize));
                check(bounds.Bottom<=619 && editor.ActualHeight>=60 && row.ActualHeight>=height-1,"resized diagnostics and code stay reachable at height "+height);
                if(previous>0) check(diagnostics.ActualHeight>previous+100,"growing checks panel expands the actual issue list");
                previous=diagnostics.ActualHeight;
            }
            check(editor.ContextMenu?.Items.Count==6,"code context menu exposes undo redo cut copy paste select all");
        }
        finally { view.Stop(); }
    }
    static RenderTargetBitmap Render(FrameworkElement view,string path)
    {
        var bitmap=new RenderTargetBitmap(1400,850,96,96,PixelFormats.Pbgra32); bitmap.Render(view);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))png.Save(file);return bitmap;
    }
    static int FirstInk(BitmapSource bitmap,int x,int y,int width,int height)
    {
        var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
        for(int col=x;col<Math.Min(x+width,bitmap.PixelWidth);col++)
        for(int row=Math.Max(0,y);row<Math.Min(y+height,bitmap.PixelHeight);row++)
        { int i=(row*bitmap.PixelWidth+col)*4;if(pixels[i]+pixels[i+1]+pixels[i+2]>430)return col; }
        return -1;
    }
}
