using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text;
using DatabaseEditor.Modules.V1;

namespace QuestStudio;

internal sealed record RecoveryState(int Version, byte[] Project, string Code, bool CardDirty,
    string Conditions, string Actions, string Otherwise, string? SelectedName, string SavedText,
    bool UseElse = false, int Caret = 0, Guid? DocumentId = null, string SaveArtifactKind = "malachite-project", AtlasRecovery? Atlas = null, WorkspaceReference? Source = null, bool HasDocument = true, bool Home = false);

internal sealed record WorkspaceReference(string Name, string Path, int CodePage, byte[] Preamble, string MobHash);

internal sealed record AtlasRecovery(QuestView.IndexedMap[] Maps, string[] Files, string[] Errors,
    bool AllMaps, bool Indirect, string? Quest, bool Visible);

public sealed class DocumentCodec : IDocumentCodec
{
    readonly DocumentBlobs? blobs;
    public DocumentCodec() { }
    public DocumentCodec(IModuleHost host) { blobs = DocumentBlobs.Connect(host); }
    public static byte[] PrepareMob(Workspace workspace)
    {
        var analysis = ScriptAnalyzer.Analyze(workspace.Text, new CommandRegistry());
        var errors = analysis.Issues.Where(i => i.Severity == "error").ToArray();
        if (errors.Length > 0) throw new InvalidDataException("MOB не сохранён: исправьте ошибки скрипта. " + errors[0].Display);
        var bytes = (workspace.Mob ?? throw new InvalidDataException("Нет исходного MOB-контейнера.")).Encode(workspace.Text);
        if (new MobDocument(bytes).Text != workspace.Text) throw new InvalidDataException("Повторный разбор MOB не совпал с исходным текстом.");
        return bytes;
    }
    internal static (RecoveryState State, Workspace Workspace) Decode(ReadOnlySpan<byte> bytes, string? text = null, DocumentBlobs? blobs = null)
    {
        if (bytes.Length > 300 * 1024 * 1024) throw new InvalidDataException("Снимок слишком большой.");
        var state = JsonSerializer.Deserialize<RecoveryState>(bytes) ?? throw new InvalidDataException("Пустой снимок.");
        if (state.Version is not (1 or 2 or 3)) throw new InvalidDataException("Неизвестная версия recovery Малахита.");
        if (state.Project == null || state.Code == null || state.SavedText == null || state.Conditions == null || state.Actions == null || state.Otherwise == null)
            throw new InvalidDataException("Неполный снимок Малахита.");
        if (state.SaveArtifactKind is not ("mob" or "malachite-project" or "script-text"))
            throw new InvalidDataException("Неизвестный вид сохраняемого документа.");
        Workspace workspace;
        if (state.Version == 3)
        {
            if (state.Source is not { } source || blobs == null || text == null)
                throw new InvalidDataException("Для компактного снимка требуется исходный MOB из хранилища редактора и текст документа.");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            workspace = new() { Name = source.Name, SourcePath = source.Path, Mob = blobs.Get(source.MobHash),
                TextEncoding = Encoding.GetEncoding(source.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), TextPreamble = source.Preamble };
        }
        else workspace = Workspace.FromBytes("recovery.malachite", state.Project);
        workspace.Text = text ?? state.Code;
        if (state.Version == 1)
        {
            var block = ScriptAnalyzer.Analyze(workspace.Text, new CommandRegistry()).Blocks.FirstOrDefault(b => b.Name == state.SelectedName);
            state = state with { UseElse = block?.ElseActions != null };
        }
        return (state, workspace);
    }
    public Result Validate(DocumentSnapshot snapshot)
    {
        try { Decode(snapshot.State.AsSpan(), snapshot.Text, blobs); return Result.Ok; }
        catch (Exception ex) { return new(ResultStatus.Failed, ex.Message); }
    }
    public PreparedDocument Prepare(DocumentSnapshot snapshot, string artifactKind)
    {
        try
        {
            var (state, workspace) = Decode(snapshot.State.AsSpan(), snapshot.Text, blobs);
            // Pending cards must be applied or explicitly discarded by the view before game export.
            if (state.CardDirty && artifactKind != "malachite-project")
                return new(new(ResultStatus.Conflict, "Сначала примените изменения карточки."), []);
            if (state.CardDirty)
                return new(new(ResultStatus.Conflict, "Проект не должен терять неприменённую карточку. Сначала примените её; recovery сохраняет черновик."), []);
            byte[] bytes = artifactKind switch
            {
                "malachite-project" => workspace.SaveProject(),
                "mob" => PrepareMob(workspace),
                "script-text" => workspace.ExportText(),
                _ => throw new NotSupportedException("Неизвестный формат экспорта.")
            };
            return new(Result.Ok, ImmutableArray.CreateRange(bytes));
        }
        catch (NotSupportedException ex) { return new(new(ResultStatus.Unsupported, ex.Message), []); }
        catch (Exception ex) { return new(new(ResultStatus.Failed, ex.Message), []); }
    }
}
