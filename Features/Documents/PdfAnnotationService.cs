using System.Text;
using System.Text.Json;

namespace AsterismApp;

public enum PdfAnnotationKind
{
    Text,
    Callout,
    Highlight
}

public sealed record PdfAnnotation(
    Guid Id,
    PdfAnnotationKind Kind,
    int PageIndex,
    double X,
    double Y,
    double Width,
    double Height,
    double TargetX,
    double TargetY,
    string Text,
    DateTimeOffset CreatedAt);

public sealed record PdfAnnotationDocument(int Version, List<PdfAnnotation> Annotations)
{
    public static PdfAnnotationDocument Empty => new(1, []);
}

/// <summary>
/// Persists non-destructive PDF annotations beside the source PDF.  The sidecar
/// follows the PDF when it is renamed or moved, while the original bytes remain
/// untouched.
/// </summary>
public sealed class PdfAnnotationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string SidecarPath(string pdfPath) =>
        Path.GetFullPath(pdfPath) + ".asterism-annotations.json";

    public PdfAnnotationDocument Load(string pdfPath)
    {
        var path = SidecarPath(pdfPath);
        if (!File.Exists(path)) return PdfAnnotationDocument.Empty;

        try
        {
            var document = JsonSerializer.Deserialize<PdfAnnotationDocument>(File.ReadAllText(path), JsonOptions);
            return document is null
                ? PdfAnnotationDocument.Empty
                : document with { Annotations = document.Annotations ?? [] };
        }
        catch
        {
            return PdfAnnotationDocument.Empty;
        }
    }

    public void Save(string pdfPath, IEnumerable<PdfAnnotation> annotations)
    {
        var path = SidecarPath(pdfPath);
        var document = new PdfAnnotationDocument(1, annotations.OrderBy(item => item.CreatedAt).ToList());
        var json = JsonSerializer.Serialize(document, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void MoveSidecar(string sourcePdfPath, string destinationPdfPath)
    {
        var source = SidecarPath(sourcePdfPath);
        if (!File.Exists(source)) return;
        var destination = SidecarPath(destinationPdfPath);
        if (File.Exists(destination)) throw new IOException("대상 PDF의 Asterism 주석 파일이 이미 있습니다.");
        File.Move(source, destination);
    }

    public void DeleteSidecar(string pdfPath)
    {
        var path = SidecarPath(pdfPath);
        if (File.Exists(path)) File.Delete(path);
    }
}
