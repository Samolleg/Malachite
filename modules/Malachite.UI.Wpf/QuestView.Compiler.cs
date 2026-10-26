using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace QuestStudio;

public partial class QuestView
{
    CancellationTokenSource? compilerCancellation;
    long compilerRevision;
    int compilerLine;

    void InvalidateCompiler()
    {
        compilerRevision++;
        compilerCancellation?.Cancel(); compilerCancellation = null;
        compilerLine = 0;
        if (CompilerResult != null)
        {
            CompilerResult.Content = "Компилятор EIRebuild: нажмите F5 для проверки текущего текста";
            CompilerResult.IsEnabled = false;
        }
    }

    async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (stopped || !documentOpen || !ResolveCard()) return;
        InvalidateCompiler();
        RefreshAnalysis(false);
        if (checksHidden) ChecksToggle_Click(sender, e);
        string text = CodeEditor.Text;
        var workspace = Workspace;
        long revision = compilerRevision;
        using var cancellation = new CancellationTokenSource();
        compilerCancellation = cancellation;
        CompilerResult.Content = "Компилятор EIRebuild: проверка…";
        try
        {
            var result = await Task.Run(() => ScriptCheckClient.CheckAsync(text, cancellation.Token));
            if (stopped || revision != compilerRevision || !ReferenceEquals(workspace, Workspace) || text != CodeEditor.Text) return;
            compilerLine = result.Ok ? 0 : result.Line;
            CompilerResult.Content = result.Ok ? $"Компилятор {result.Compiler}: ошибок нет" : $"Строка {result.Line}: {result.Error}";
            CompilerResult.ToolTip = $"Компилятор {result.Compiler}" + (compilerLine > 0 ? " · Нажмите для перехода к строке" : "");
            CompilerResult.IsEnabled = compilerLine > 0;
            SetStatus(result.Ok ? $"Компилятор {result.Compiler}: ошибок нет" : $"Строка {result.Line}: {result.Error}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (stopped || revision != compilerRevision || !ReferenceEquals(workspace, Workspace) || text != CodeEditor.Text) return;
            CompilerResult.Content = "Компилятор не запустился: " + ex.Message;
            CompilerResult.ToolTip = ex.Message;
            SetStatus("Компилятор не запустился: " + ex.Message);
        }
        finally { if (ReferenceEquals(compilerCancellation, cancellation)) compilerCancellation = null; }
    }

    void CompilerResult_Click(object sender, RoutedEventArgs e)
    {
        if (compilerLine <= 0 || compilerLine > CodeEditor.LineCount) return;
        int start = CodeEditor.GetCharacterIndexFromLineIndex(compilerLine - 1);
        GoToCode(start);
        CodeEditor.Select(start, CodeEditor.GetLineLength(compilerLine - 1));
    }
}
