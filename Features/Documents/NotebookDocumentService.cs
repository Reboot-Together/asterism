using System.Text;
using System.Text.Json;
using FileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using RecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using UIOption = Microsoft.VisualBasic.FileIO.UIOption;

namespace AsterismApp;

public sealed record NotebookCell(string Kind, string Source, string ExecutionCount, IReadOnlyList<NotebookOutput> Outputs, IReadOnlyDictionary<string, string> Attachments);
public sealed record NotebookOutput(string Kind, string Text, IReadOnlyDictionary<string, string> Data);
public sealed record NotebookDocument(string Language, IReadOnlyList<NotebookCell> Cells, string? Error = null);

/// <summary>Reads notebooks without rewriting their JSON, metadata or saved outputs.</summary>
public sealed class NotebookDocumentService
{
    private const long MaximumFileBytes = 64 * 1024 * 1024;
    public string RootPath { get; private set; } = "";
    public NotebookDocumentService(string rootPath) => SetRootPath(rootPath);
    public void SetRootPath(string rootPath) => RootPath = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
    public static bool IsNotebookFile(string path) => Path.GetExtension(path).Equals(".ipynb", StringComparison.OrdinalIgnoreCase);

    public List<NoteInfo> Load() => Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories)
        .Where(IsNotebookFile)
        .Where(path => !Path.GetRelativePath(RootPath, path).Split(Path.DirectorySeparatorChar).Any(part => part.Equals(".trash", StringComparison.OrdinalIgnoreCase) || part.Equals(".ipynb_checkpoints", StringComparison.OrdinalIgnoreCase)))
        .Select(Read).ToList();

    public static NotebookDocument ReadDocument(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
                return new("", [], "64MB를 넘는 노트북입니다. 출력 크기를 줄인 파일을 열어 주세요.");
            using var json = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 128 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("nbformat", out var version) || !version.TryGetInt32(out var number) || number != 4
                || !root.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
                return new("", [], "Jupyter Notebook v4 형식의 파일이 아닙니다.");
            var language = "python";
            if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                if (metadata.TryGetProperty("language_info", out var info)) language = Property(info, "name", language);
                else if (metadata.TryGetProperty("kernelspec", out var kernel)) language = Property(kernel, "language", language);
            }
            var parsed = new List<NotebookCell>();
            foreach (var cell in cells.EnumerateArray())
            {
                if (cell.ValueKind != JsonValueKind.Object) continue;
                var outputs = new List<NotebookOutput>();
                if (cell.TryGetProperty("outputs", out var outputArray) && outputArray.ValueKind == JsonValueKind.Array)
                    foreach (var output in outputArray.EnumerateArray())
                    {
                        var kind = Property(output, "output_type");
                        var text = Property(output, "text");
                        if (kind == "error") text = Property(output, "ename") + ": " + Property(output, "evalue") + "\n" + Property(output, "traceback");
                        var data = new Dictionary<string, string>();
                        if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("data", out var bundle) && bundle.ValueKind == JsonValueKind.Object)
                            foreach (var value in bundle.EnumerateObject()) data[value.Name] = Text(value.Value);
                        outputs.Add(new(kind, text, data));
                    }
                var attachments = new Dictionary<string, string>();
                if (cell.TryGetProperty("attachments", out var files) && files.ValueKind == JsonValueKind.Object)
                    foreach (var file in files.EnumerateObject())
                        foreach (var mime in new[] { "image/png", "image/jpeg", "image/gif" })
                            if (file.Value.ValueKind == JsonValueKind.Object && file.Value.TryGetProperty(mime, out var image))
                            { attachments[file.Name] = $"data:{mime};base64,{Text(image).Replace("\n", "").Replace("\r", "")}"; break; }
                parsed.Add(new(Property(cell, "cell_type"), Property(cell, "source"), Property(cell, "execution_count"), outputs, attachments));
            }
            return new(language, parsed);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new("", [], "노트북을 읽을 수 없습니다. " + exception.Message); }
    }

    public NoteInfo Read(string path)
    {
        var document = ReadDocument(path);
        var text = new StringBuilder();
        foreach (var cell in document.Cells)
        {
            text.AppendLine(cell.Source).AppendLine();
            foreach (var output in cell.Outputs)
            {
                if (output.Text.Length > 0) text.AppendLine(output.Text);
                if (output.Data.TryGetValue("text/plain", out var plain)) text.AppendLine(plain);
            }
        }
        return new(Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path), document.Error ?? text.ToString(), File.GetLastWriteTime(path),
            new NoteMetadata("Notebook", File.GetCreationTime(path), "File", "Notebook"), IsReadOnly: true, IsNotebook: true);
    }

    public NoteInfo Import(string sourcePath, string directory)
    {
        if (!IsNotebookFile(sourcePath) || !File.Exists(sourcePath)) throw new FileNotFoundException("IPYNB 파일을 찾을 수 없습니다.", sourcePath);
        if (InsideRoot(sourcePath)) return Read(sourcePath);
        var destination = Unique(ValidateDirectory(directory), Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destination);
        return Read(destination);
    }

    public NoteInfo Rename(string path, string name)
    {
        var source = ValidateFile(path);
        name = name.Trim();
        if (IsNotebookFile(name)) name = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("IPYNB 이름에 사용할 수 없는 문자가 있습니다.");
        var destination = Path.Combine(Path.GetDirectoryName(source)!, name.TrimEnd('.') + ".ipynb");
        if (!destination.Equals(source, StringComparison.OrdinalIgnoreCase)) File.Move(source, destination);
        return Read(destination);
    }

    public NoteInfo Move(string path, string directory)
    {
        var source = ValidateFile(path);
        var target = ValidateDirectory(directory);
        if (Path.GetDirectoryName(source)!.Equals(target, StringComparison.OrdinalIgnoreCase)) return Read(source);
        var destination = Unique(target, Path.GetFileName(source));
        File.Move(source, destination);
        return Read(destination);
    }

    public NoteInfo Duplicate(string path)
    {
        var source = ValidateFile(path);
        var destination = Unique(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + " 복사본.ipynb");
        File.Copy(source, destination);
        return Read(destination);
    }

    public void MoveToTrash(string path)
    {
        var source = ValidateFile(path);
        try { FileSystem.DeleteFile(source, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); }
        catch
        {
            var directory = Path.Combine(RootPath, ".trash");
            Directory.CreateDirectory(directory);
            File.Move(source, Unique(directory, Path.GetFileName(source)));
        }
    }

    private bool InsideRoot(string path) => Path.GetFullPath(path).Equals(RootPath, StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private string ValidateFile(string path) => IsNotebookFile(path) && InsideRoot(path) ? Path.GetFullPath(path) : throw new InvalidOperationException("현재 저장소의 IPYNB 파일만 변경할 수 있습니다.");
    private string ValidateDirectory(string path) => InsideRoot(path) && Directory.Exists(path) ? Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) : throw new InvalidOperationException("현재 저장소 안의 폴더를 선택해 주세요.");
    private static string Unique(string directory, string name)
    {
        var destination = Path.Combine(directory, name);
        for (var i = 2; File.Exists(destination); i++) destination = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)} {i}{Path.GetExtension(name)}");
        return destination;
    }
    private static string Property(JsonElement element, string name, string fallback = "") => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? Text(value) : fallback;
    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Array => string.Concat(value.EnumerateArray().Select(Text)),
        JsonValueKind.Number => value.GetRawText(),
        _ => ""
    };
}
