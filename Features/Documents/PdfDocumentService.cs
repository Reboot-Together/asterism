using FileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using RecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using UIOption = Microsoft.VisualBasic.FileIO.UIOption;

namespace AsterismApp;

public sealed class PdfDocumentService
{
    private readonly PdfAnnotationService _annotationService = new();

    public PdfDocumentService(string rootPath) => SetRootPath(rootPath);

    public string RootPath { get; private set; } = "";

    public void SetRootPath(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(RootPath);
    }

    public List<PdfInfo> Load()
    {
        try
        {
            return Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories)
                .Where(PdfReaderRules.IsPdf)
                .Where(path => !IsFallbackTrashPath(path))
                .Select(path => new PdfInfo(
                    Path.GetFileNameWithoutExtension(path),
                    Path.GetFullPath(path),
                    File.GetLastWriteTime(path)))
                .OrderByDescending(pdf => pdf.LastWriteTime)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public PdfInfo Import(string sourcePath, string destinationDirectory)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!PdfReaderRules.IsPdf(source) || !File.Exists(source))
            throw new FileNotFoundException("가져올 PDF 파일을 찾을 수 없습니다.", source);

        if (IsInsideRoot(source)) return Read(source);

        var destination = UniqueDestination(ValidateDirectoryPath(destinationDirectory), Path.GetFileName(source));
        File.Copy(source, destination);
        return Read(destination);
    }

    public PdfInfo Move(string path, string destinationDirectory)
    {
        var source = ValidatePdfPath(path);
        if (!File.Exists(source)) throw new FileNotFoundException("PDF 파일을 찾을 수 없습니다.", source);
        var destinationFolder = ValidateDirectoryPath(destinationDirectory);
        if (Path.GetDirectoryName(source)?.Equals(destinationFolder, StringComparison.OrdinalIgnoreCase) == true)
            return Read(source);

        var destination = UniqueDestination(destinationFolder, Path.GetFileName(source));
        MovePdfWithSidecar(source, destination);
        return Read(destination);
    }

    public PdfInfo Rename(string path, string requestedName)
    {
        var source = ValidatePdfPath(path);
        if (!File.Exists(source)) throw new FileNotFoundException("PDF 파일을 찾을 수 없습니다.", source);
        var name = ValidateFileName(requestedName);
        var destination = Path.Combine(Path.GetDirectoryName(source)!, name + ".pdf");
        if (destination.Equals(source, StringComparison.OrdinalIgnoreCase)) return Read(source);
        if (File.Exists(destination)) throw new IOException("같은 이름의 PDF 파일이 이미 있습니다.");
        MovePdfWithSidecar(source, destination);
        return Read(destination);
    }

    public void MoveToTrash(string path)
    {
        var source = ValidatePdfPath(path);
        if (!File.Exists(source)) return;
        var sidecar = PdfAnnotationService.SidecarPath(source);
        try
        {
            FileSystem.DeleteFile(source, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch
        {
            var trashDirectory = Path.Combine(RootPath, ".trash");
            Directory.CreateDirectory(trashDirectory);
            var destination = UniqueDestination(
                trashDirectory,
                $"{Path.GetFileNameWithoutExtension(source)} {DateTime.Now:yyyyMMdd-HHmmssfff}.pdf");
            File.Move(source, destination);
            _annotationService.MoveSidecar(source, destination);
            return;
        }

        if (!File.Exists(sidecar)) return;
        try
        {
            FileSystem.DeleteFile(sidecar, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch
        {
            _annotationService.DeleteSidecar(source);
        }
    }

    private PdfInfo Read(string path) => new(
        Path.GetFileNameWithoutExtension(path),
        Path.GetFullPath(path),
        File.GetLastWriteTime(path));

    private void MovePdfWithSidecar(string source, string destination)
    {
        File.Move(source, destination);
        try
        {
            _annotationService.MoveSidecar(source, destination);
        }
        catch
        {
            if (!File.Exists(source) && File.Exists(destination)) File.Move(destination, source);
            throw;
        }
    }

    private string ValidatePdfPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!PdfReaderRules.IsPdf(fullPath) || !IsInsideRoot(fullPath))
            throw new InvalidOperationException("현재 저장소 안의 PDF 파일만 변경할 수 있습니다.");
        return fullPath;
    }

    private string ValidateDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!IsInsideRoot(fullPath)) throw new InvalidOperationException("현재 저장소 밖으로 PDF를 이동할 수 없습니다.");
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("대상 폴더를 찾을 수 없습니다.");
        return fullPath;
    }

    private bool IsInsideRoot(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.Equals(RootPath, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateFileName(string requestedName)
    {
        var name = Path.GetFileNameWithoutExtension(requestedName.Trim());
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("PDF 이름에 사용할 수 없는 문자가 있습니다.");
        return name.TrimEnd('.');
    }

    private static string UniqueDestination(string directory, string fileName)
    {
        var destination = Path.Combine(directory, fileName);
        if (!File.Exists(destination)) return destination;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            destination = Path.Combine(directory, $"{stem} {index}{extension}");
            if (!File.Exists(destination)) return destination;
        }
    }

    private bool IsFallbackTrashPath(string path)
    {
        var relative = Path.GetRelativePath(RootPath, path);
        return relative.Equals(".trash", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith($".trash{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }
}
