namespace AsterismApp;

public sealed record NoteMetadata(string Category, DateTime Created, string Source, string Type)
{
    public static NoteMetadata Manual => new("Inbox", DateTime.Today, "Manual", "Note");
}

public sealed record NoteInfo(
    string Title,
    string Path,
    string Body,
    DateTime LastWriteTime,
    NoteMetadata Metadata,
    bool IsReadOnly = false,
    bool IsPlainText = false,
    bool IsNotebook = false);

public sealed record PdfInfo(string Title, string Path, DateTime LastWriteTime);

public sealed record SemanticSuggestion(NoteInfo Note, double Score)
{
    public string ScoreText => $"{Score:P0}";
}

public sealed record VaultItem(
    string Name,
    string Path,
    bool IsFolder,
    bool IsRoot,
    bool IsExpanded,
    int Depth,
    NoteInfo? Note,
    string OrderNumber = "")
{
    public string DisplayName => OrderNumber.Length == 0 ? Name : $"{OrderNumber}  {Name}";
    public double Indent => Depth * 14;
    public bool IsPdf => !IsFolder && Note is null && PdfReaderRules.IsPdf(Path);
    public bool IsText => !IsFolder && Note?.IsPlainText == true;
    public bool IsNotebook => !IsFolder && Note?.IsNotebook == true;
    public double CollapsedChevronOpacity => IsFolder && !IsRoot && !IsExpanded ? 1 : 0;
    public double ExpandedChevronOpacity => IsFolder && !IsRoot && IsExpanded ? 1 : 0;
    public double NoteDotOpacity => IsFolder || IsPdf || IsText || IsNotebook ? 0 : 1;
    public double FolderIconOpacity => IsFolder ? 1 : 0;
    public double PdfIconOpacity => IsPdf ? 1 : 0;
    public double TextIconOpacity => IsText ? 1 : 0;
    public double NotebookIconOpacity => IsNotebook ? 1 : 0;
    public bool IsVirtual => Path.StartsWith("asterism-guide://", StringComparison.OrdinalIgnoreCase);
}
