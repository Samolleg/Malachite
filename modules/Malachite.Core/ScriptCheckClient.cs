using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestStudio;

public sealed record ScriptCheckResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("compiler")] string Compiler);

// Compiles a snapshot only; never saves a document or executes its script.
public static class ScriptCheckClient
{
    public static string ExecutablePath => Path.Combine(
        Path.GetDirectoryName(typeof(ScriptCheckClient).Assembly.Location)!, "tools", "ScriptCheck", "ScriptCheck.exe");

    public static async Task<ScriptCheckResult> CheckAsync(string text, CancellationToken cancellation = default)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] input;
        try { input = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(text); }
        catch (EncoderFallbackException ex)
        { throw new InvalidDataException($"Символ в позиции {ex.Index + 1} не представим в Windows-1251. Исправьте текст перед компиляцией.", ex); }
        if (!File.Exists(ExecutablePath)) throw new FileNotFoundException("Не найден ScriptCheck.exe в комплекте Малахита.", ExecutablePath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        var token = linked.Token;
        using var process = new Process { StartInfo = new ProcessStartInfo(ExecutablePath, "--stdin")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!
        } };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Не удалось создать процесс ScriptCheck.");
        using var terminate = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        // Drain both pipes while writing stdin, including on cancellation/failure.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(input, token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode is not (0 or 1 or 2))
                throw new IOException($"ScriptCheck завершился аварийно (код {process.ExitCode}). {error}".Trim());
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            // Require all protocol fields: an incomplete response cannot mean success.
            var result = new ScriptCheckResult(root.GetProperty("ok").GetBoolean(), root.GetProperty("line").GetInt32(),
                root.GetProperty("code").GetInt32(), root.GetProperty("error").GetString()!, root.GetProperty("compiler").GetString()!);
            if (string.IsNullOrWhiteSpace(result.Compiler) || result.Error == null || result.Line < 0 ||
                (process.ExitCode == 0 && (!result.Ok || result.Line != 0 || result.Code != 0)) ||
                (process.ExitCode != 0 && result.Ok) || (process.ExitCode == 1 && (result.Line < 1 || result.Code <= 0)))
                throw new InvalidDataException("Некорректный ответ ScriptCheck.");
            if (process.ExitCode == 2) throw new IOException("Ошибка ScriptCheck: " + result.Error);
            return result;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException("Компиляция не завершилась за 30 секунд.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false); }
            catch (IOException) { }
            catch (DecoderFallbackException) { }
        }
    }
}
