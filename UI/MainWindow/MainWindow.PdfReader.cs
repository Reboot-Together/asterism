using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Line = Microsoft.UI.Xaml.Shapes.Line;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace AsterismApp;

public sealed partial class MainWindow
{
    private enum PdfTool { Select, Text, Callout, Highlight }

    private readonly PdfAnnotationService _pdfAnnotationService = new();
    private bool _pdfMode;
    private string? _currentPdfPath;
    private PdfDocument? _openPdfDocument;
    private List<PdfAnnotation> _pdfAnnotations = [];
    private PdfTool _pdfTool = PdfTool.Select;
    private int _pdfPageIndex;
    private double _pdfZoom = 1;
    private bool _pdfPageRendering;
    private Point? _pdfPointerStart;
    private Guid? _selectedPdfAnnotationId;
    private FrameworkElement? _pdfDrawingPreview;

    private async void ImportPdfToRoot_Click(object sender, RoutedEventArgs e) => await PickAndImportPdfAsync(_workspace.RootPath);

    private async void ChangePdf_Click(object sender, RoutedEventArgs e) =>
        await PickAndImportPdfAsync(_currentPdfPath is null ? _workspace.RootPath : Path.GetDirectoryName(_currentPdfPath)!);

    private async void ImportPdf_Click(object sender, RoutedEventArgs e) => await PickAndImportPdfAsync(_contextFolder ?? _workspace.RootPath);

    private async void OpenContextPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_contextPdfPath is not null) await ShowPdfModeAsync(_contextPdfPath);
    }

    private async void RenamePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_contextPdfPath is not string path) return;
        var name = await PromptForName("PDF 이름 변경", "새 PDF 이름", Path.GetFileNameWithoutExtension(path));
        if (name is null || name.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal)) return;
        try
        {
            var renamed = _pdfRepository.Rename(path, name);
            _vaultTreeService.RemapOrderPath(_workspace.RootPath, path, renamed.Path);
            _contextPdfPath = renamed.Path;
            RefreshNotes();
            await ShowPdfModeAsync(renamed.Path);
        }
        catch (Exception exception) { await ShowMessage("이름 변경 실패", exception.Message); }
    }

    private async void MovePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_contextPdfPath is not string path) return;
        var picker = CreateFolderPicker();
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        try
        {
            var moved = _pdfRepository.Move(path, folder.Path);
            _vaultTreeService.RemapOrderPath(_workspace.RootPath, path, moved.Path);
            _contextPdfPath = moved.Path;
            RefreshNotes();
            await ShowPdfModeAsync(moved.Path);
        }
        catch (Exception exception)
        {
            await ShowMessage("이동 실패", $"PDF는 현재 저장소 내부의 폴더로만 이동할 수 있습니다.\n\n{exception.Message}");
        }
    }

    private async void ContextDeletePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_contextPdfPath is not string path) return;
        var confirmation = new ContentDialog
        {
            Title = "PDF 삭제",
            Content = $"'{Path.GetFileNameWithoutExtension(path)}' PDF를 삭제할까요?\n\n가능하면 Windows 휴지통으로 이동합니다. 주석도 함께 이동합니다.",
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (_currentPdfPath?.Equals(path, StringComparison.OrdinalIgnoreCase) == true) ShowDocumentMode();
            _pdfRepository.MoveToTrash(path);
            _vaultTreeService.RemoveOrderPath(_workspace.RootPath, path);
            _contextPdfPath = null;
            RefreshNotes();
        }
        catch (Exception exception) { await ShowMessage("삭제 실패", $"PDF를 삭제하지 못했습니다.\n\n{exception.Message}"); }
    }

    private async Task PickAndImportPdfAsync(string destinationFolder)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".pdf");
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var pdf = _pdfRepository.Import(file.Path, destinationFolder);
            ExpandFolder(Path.GetDirectoryName(pdf.Path)!);
            RefreshNotes();
            await ShowPdfModeAsync(pdf.Path);
        }
        catch (Exception exception) { await ShowMessage("PDF 가져오기 실패", exception.Message); }
    }

    private async Task ShowPdfModeAsync(string path)
    {
        if (!PdfReaderRules.IsPdf(path) || !File.Exists(path))
        {
            await ShowMessage("PDF를 열 수 없음", "선택한 PDF 파일을 찾을 수 없습니다.");
            return;
        }
        if (_constellationMode) ShowDocumentMode();
        SaveCurrent();
        if (!SaveSideDocuments())
        {
            await ShowMessage("PDF를 열 수 없음", "저장하지 못한 옆 문서가 있습니다. 문서의 저장 오류를 확인해 주세요.");
            return;
        }

        _pdfMode = true;
        _currentPdfPath = Path.GetFullPath(path);
        _pdfPageIndex = 0;
        _pdfZoom = 1;
        _selectedPdfAnnotationId = null;
        PdfTitleText.Text = Path.GetFileName(path);
        PdfStatusText.Text = "PDF 불러오는 중…";
        DocumentGroupsHost.Visibility = Visibility.Collapsed;
        ConstellationPanel.Visibility = Visibility.Collapsed;
        PdfPanel.Visibility = Visibility.Visible;
        DocumentModeIndicator.Visibility = Visibility.Collapsed;
        ConstellationModeIndicator.Visibility = Visibility.Collapsed;
        RevealVaultItemInTree(_currentPdfPath);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_currentPdfPath);
            _openPdfDocument = await PdfDocument.LoadFromFileAsync(file);
            _pdfAnnotations = _pdfAnnotationService.Load(_currentPdfPath).Annotations;
            SetPdfTool(PdfTool.Select);
            await RenderPdfPageAsync();
            PdfStatusText.Text = "주석은 PDF 원본을 바꾸지 않고 별도 저장됩니다.";
        }
        catch (Exception exception)
        {
            _openPdfDocument = null;
            PdfStatusText.Text = $"PDF를 표시하지 못했습니다: {exception.Message}";
        }
    }

    private async Task RenderPdfPageAsync()
    {
        if (_openPdfDocument is null || _openPdfDocument.PageCount == 0 || _pdfPageRendering) return;
        _pdfPageRendering = true;
        try
        {
            _pdfPageIndex = Math.Clamp(_pdfPageIndex, 0, checked((int)_openPdfDocument.PageCount) - 1);
            using var page = _openPdfDocument.GetPage((uint)_pdfPageIndex);
            var viewportWidth = PdfScroll.ActualWidth > 120 ? PdfScroll.ActualWidth - 64 : 920;
            var baseWidth = Math.Clamp(viewportWidth, 560, 1080);
            var displayWidth = baseWidth * _pdfZoom;
            var displayHeight = displayWidth * page.Size.Height / page.Size.Width;
            PdfPageSurface.Width = displayWidth;
            PdfPageSurface.Height = displayHeight;
            PdfPageImage.Width = displayWidth;
            PdfPageImage.Height = displayHeight;
            PdfAnnotationCanvas.Width = displayWidth;
            PdfAnnotationCanvas.Height = displayHeight;

            var destinationWidth = (uint)Math.Clamp(Math.Round(displayWidth * 1.45), 1, 4096);
            var destinationHeight = (uint)Math.Clamp(Math.Round(displayHeight * 1.45), 1, 5792);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                DestinationWidth = destinationWidth,
                DestinationHeight = destinationHeight
            });
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            PdfPageImage.Source = bitmap;
            PdfPageText.Text = $"{_pdfPageIndex + 1} / {_openPdfDocument.PageCount}";
            PdfZoomText.Text = $"{_pdfZoom:P0}";
            DrawPdfAnnotations();
        }
        finally { _pdfPageRendering = false; }
    }

    private void DrawPdfAnnotations()
    {
        PdfAnnotationCanvas.Children.Clear();
        var width = PdfAnnotationCanvas.Width;
        var height = PdfAnnotationCanvas.Height;
        if (width <= 0 || height <= 0) return;
        foreach (var annotation in _pdfAnnotations.Where(item => item.PageIndex == _pdfPageIndex))
        {
            var selected = annotation.Id == _selectedPdfAnnotationId;
            if (annotation.Kind == PdfAnnotationKind.Highlight)
            {
                var mark = new Rectangle
                {
                    Width = annotation.Width * width,
                    Height = annotation.Height * height,
                    Fill = new SolidColorBrush(Color.FromArgb(92, 255, 213, 59)),
                    Stroke = selected ? new SolidColorBrush(Color.FromArgb(255, 219, 153, 0)) : null,
                    StrokeThickness = selected ? 2 : 0,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(mark, annotation.X * width);
                Canvas.SetTop(mark, annotation.Y * height);
                PdfAnnotationCanvas.Children.Add(mark);
            }
            else if (annotation.Kind == PdfAnnotationKind.Text)
            {
                AddPdfTextCard(annotation, annotation.X * width, annotation.Y * height, selected);
            }
            else
            {
                var left = annotation.X * width;
                var top = annotation.Y * height;
                var markWidth = annotation.Width * width;
                var markHeight = annotation.Height * height;
                var targetX = annotation.TargetX * width;
                var targetY = annotation.TargetY * height;
                var outline = new Rectangle
                {
                    Width = markWidth,
                    Height = markHeight,
                    Fill = new SolidColorBrush(Color.FromArgb(30, 255, 184, 77)),
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 245, 166, 35)),
                    StrokeThickness = selected ? 3 : 2,
                    StrokeDashArray = [3, 2],
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(outline, left);
                Canvas.SetTop(outline, top);
                PdfAnnotationCanvas.Children.Add(outline);
                PdfAnnotationCanvas.Children.Add(new Line
                {
                    X1 = left + markWidth / 2,
                    Y1 = top + markHeight / 2,
                    X2 = targetX,
                    Y2 = targetY,
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 245, 166, 35)),
                    StrokeThickness = selected ? 3 : 2,
                    IsHitTestVisible = false
                });
                AddPdfTextCard(annotation, targetX, targetY, selected);
            }
        }
    }

    private void AddPdfTextCard(PdfAnnotation annotation, double left, double top, bool selected)
    {
        var card = new Border
        {
            MaxWidth = Math.Max(150, PdfAnnotationCanvas.Width * .34),
            MinWidth = 96,
            Background = new SolidColorBrush(Color.FromArgb(235, 255, 250, 218)),
            BorderBrush = new SolidColorBrush(selected ? Color.FromArgb(255, 219, 124, 0) : Color.FromArgb(220, 222, 164, 52)),
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = annotation.Text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 35, 35, 35)),
                FontSize = Math.Clamp(13 * _pdfZoom, 11, 18),
                FontWeight = FontWeights.SemiBold
            }
        };
        Canvas.SetLeft(card, Math.Clamp(left, 0, Math.Max(0, PdfAnnotationCanvas.Width - 110)));
        Canvas.SetTop(card, Math.Clamp(top, 0, Math.Max(0, PdfAnnotationCanvas.Height - 44)));
        PdfAnnotationCanvas.Children.Add(card);
    }

    private void PdfSelectTool_Click(object sender, RoutedEventArgs e) => SetPdfTool(PdfTool.Select);
    private void PdfTextTool_Click(object sender, RoutedEventArgs e) => SetPdfTool(PdfTool.Text);
    private void PdfCalloutTool_Click(object sender, RoutedEventArgs e) => SetPdfTool(PdfTool.Callout);
    private void PdfHighlightTool_Click(object sender, RoutedEventArgs e) => SetPdfTool(PdfTool.Highlight);

    private void SetPdfTool(PdfTool tool)
    {
        _pdfTool = tool;
        PdfSelectToolButton.Opacity = tool == PdfTool.Select ? 1 : .66;
        PdfTextToolButton.Opacity = tool == PdfTool.Text ? 1 : .66;
        PdfCalloutToolButton.Opacity = tool == PdfTool.Callout ? 1 : .66;
        PdfHighlightToolButton.Opacity = tool == PdfTool.Highlight ? 1 : .66;
        PdfStatusText.Text = tool switch
        {
            PdfTool.Text => "페이지를 클릭하면 텍스트를 추가합니다.",
            PdfTool.Callout => "강조할 영역을 드래그하면 사각형·지시선·설명이 함께 추가됩니다.",
            PdfTool.Highlight => "강조할 영역을 드래그하세요.",
            _ => "주석을 클릭해 선택합니다. 실행 취소로 마지막 주석을 지울 수 있습니다."
        };
    }

    private void PdfAnnotationCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_openPdfDocument is null) return;
        var point = e.GetCurrentPoint(PdfAnnotationCanvas).Position;
        if (_pdfTool == PdfTool.Select)
        {
            _selectedPdfAnnotationId = FindPdfAnnotation(point)?.Id;
            DrawPdfAnnotations();
            PdfStatusText.Text = _selectedPdfAnnotationId is null ? "선택된 주석이 없습니다." : "주석이 선택되었습니다.";
            return;
        }
        _pdfPointerStart = point;
        PdfAnnotationCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PdfAnnotationCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pdfPointerStart is not Point start || _pdfTool == PdfTool.Text) return;
        ShowPdfDrawingPreview(start, e.GetCurrentPoint(PdfAnnotationCanvas).Position);
        e.Handled = true;
    }

    private async void PdfAnnotationCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pdfPointerStart is not Point start || _currentPdfPath is null) return;
        var current = e.GetCurrentPoint(PdfAnnotationCanvas).Position;
        _pdfPointerStart = null;
        PdfAnnotationCanvas.ReleasePointerCapture(e.Pointer);
        RemovePdfDrawingPreview();
        e.Handled = true;
        var surfaceWidth = Math.Max(1, PdfAnnotationCanvas.Width);
        var surfaceHeight = Math.Max(1, PdfAnnotationCanvas.Height);
        if (_pdfTool == PdfTool.Text)
        {
            var text = await PromptForPdfText("PDF 텍스트 추가", "페이지에 표시할 내용을 입력하세요.");
            if (text is null) return;
            _pdfAnnotations.Add(new PdfAnnotation(Guid.NewGuid(), PdfAnnotationKind.Text, _pdfPageIndex,
                Clamp01(current.X / surfaceWidth), Clamp01(current.Y / surfaceHeight), .3, .08, 0, 0, text, DateTimeOffset.Now));
        }
        else
        {
            var bounds = NormalizedBounds(start, current, surfaceWidth, surfaceHeight);
            if (bounds.Width * surfaceWidth < 6 || bounds.Height * surfaceHeight < 6) return;
            if (_pdfTool == PdfTool.Highlight)
            {
                _pdfAnnotations.Add(new PdfAnnotation(Guid.NewGuid(), PdfAnnotationKind.Highlight, _pdfPageIndex,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, 0, "", DateTimeOffset.Now));
            }
            else if (_pdfTool == PdfTool.Callout)
            {
                var text = await PromptForPdfText("PDF 설명 추가", "지시선에 표시할 내용을 입력하세요.");
                if (text is null) return;
                var targetX = bounds.X + bounds.Width < .68
                    ? Math.Min(.82, bounds.X + bounds.Width + .045)
                    : Math.Max(.03, bounds.X - .29);
                var targetY = Math.Clamp(bounds.Y + bounds.Height / 2, .02, .94);
                _pdfAnnotations.Add(new PdfAnnotation(Guid.NewGuid(), PdfAnnotationKind.Callout, _pdfPageIndex,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height, targetX, targetY, text, DateTimeOffset.Now));
            }
        }
        _selectedPdfAnnotationId = _pdfAnnotations.LastOrDefault()?.Id;
        SavePdfAnnotations();
        DrawPdfAnnotations();
    }

    private void PdfAnnotationCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _pdfPointerStart = null;
        RemovePdfDrawingPreview();
    }

    private void ShowPdfDrawingPreview(Point start, Point end)
    {
        RemovePdfDrawingPreview();
        var preview = new Rectangle
        {
            Width = Math.Abs(start.X - end.X),
            Height = Math.Abs(start.Y - end.Y),
            Fill = new SolidColorBrush(_pdfTool == PdfTool.Highlight ? Color.FromArgb(80, 255, 213, 59) : Color.FromArgb(24, 255, 184, 77)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 245, 166, 35)),
            StrokeThickness = 2,
            StrokeDashArray = _pdfTool == PdfTool.Callout ? [3, 2] : null,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(preview, Math.Min(start.X, end.X));
        Canvas.SetTop(preview, Math.Min(start.Y, end.Y));
        PdfAnnotationCanvas.Children.Add(preview);
        _pdfDrawingPreview = preview;
    }

    private void RemovePdfDrawingPreview()
    {
        if (_pdfDrawingPreview is not null) PdfAnnotationCanvas.Children.Remove(_pdfDrawingPreview);
        _pdfDrawingPreview = null;
    }

    private PdfAnnotation? FindPdfAnnotation(Point point)
    {
        var x = point.X / Math.Max(1, PdfAnnotationCanvas.Width);
        var y = point.Y / Math.Max(1, PdfAnnotationCanvas.Height);
        const double tolerance = .012;
        return _pdfAnnotations.Where(item => item.PageIndex == _pdfPageIndex).OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault(item => item.Kind switch
            {
                PdfAnnotationKind.Text => x >= item.X - tolerance && x <= item.X + .31 && y >= item.Y - tolerance && y <= item.Y + .1,
                PdfAnnotationKind.Callout =>
                    (x >= item.X - tolerance && x <= item.X + item.Width + tolerance && y >= item.Y - tolerance && y <= item.Y + item.Height + tolerance) ||
                    (x >= item.TargetX - tolerance && x <= item.TargetX + .31 && y >= item.TargetY - tolerance && y <= item.TargetY + .1),
                _ => x >= item.X - tolerance && x <= item.X + item.Width + tolerance && y >= item.Y - tolerance && y <= item.Y + item.Height + tolerance
            });
    }

    private void SavePdfAnnotations()
    {
        if (_currentPdfPath is null) return;
        try
        {
            _pdfAnnotationService.Save(_currentPdfPath, _pdfAnnotations);
            PdfStatusText.Text = "주석을 저장했습니다.";
        }
        catch (Exception exception) { PdfStatusText.Text = $"주석을 저장하지 못했습니다: {exception.Message}"; }
    }

    private void PdfUndo_Click(object sender, RoutedEventArgs e)
    {
        var annotation = _pdfAnnotations.Where(item => item.PageIndex == _pdfPageIndex).OrderByDescending(item => item.CreatedAt).FirstOrDefault();
        if (annotation is null) return;
        _pdfAnnotations.Remove(annotation);
        if (_selectedPdfAnnotationId == annotation.Id) _selectedPdfAnnotationId = null;
        SavePdfAnnotations();
        DrawPdfAnnotations();
    }

    private async Task<string?> PromptForPdfText(string title, string description)
    {
        var input = new TextBox
        {
            Header = description,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 360,
            MinHeight = 110,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = "추가",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        var result = await dialog.ShowAsync();
        var value = input.Text.Trim();
        return result == ContentDialogResult.Primary && value.Length > 0 ? value : null;
    }

    private async void PdfPreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_openPdfDocument is null || _pdfPageRendering || _pdfPageIndex <= 0) return;
        _pdfPageIndex--;
        _selectedPdfAnnotationId = null;
        await RenderPdfPageAsync();
    }

    private async void PdfNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_openPdfDocument is null || _pdfPageRendering || _pdfPageIndex + 1 >= _openPdfDocument.PageCount) return;
        _pdfPageIndex++;
        _selectedPdfAnnotationId = null;
        await RenderPdfPageAsync();
    }

    private async void PdfZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfPageRendering) return;
        _pdfZoom = Math.Max(.5, _pdfZoom - .25);
        await RenderPdfPageAsync();
    }

    private async void PdfZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfPageRendering) return;
        _pdfZoom = Math.Min(4, _pdfZoom + .25);
        await RenderPdfPageAsync();
    }

    private void OpenPdfExternally_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPdfPath is not null && File.Exists(_currentPdfPath))
            Process.Start(new ProcessStartInfo(_currentPdfPath) { UseShellExecute = true });
    }

    private void OpenPdfLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPdfPath is null || !File.Exists(_currentPdfPath))
        {
            PdfStatusText.Text = "PDF 파일을 찾을 수 없습니다.";
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentPdfPath}\"") { UseShellExecute = true }); }
        catch (Exception exception) { PdfStatusText.Text = $"파일 위치를 열지 못했습니다: {exception.Message}"; }
    }

    private void ClosePdf_Click(object sender, RoutedEventArgs e) => ShowDocumentMode();

    private void HidePdfMode()
    {
        if (!_pdfMode && PdfPanel.Visibility != Visibility.Visible) return;
        _pdfMode = false;
        PdfPanel.Visibility = Visibility.Collapsed;
        _currentPdfPath = null;
        PdfTitleText.Text = "PDF";
        PdfStatusText.Text = "";
        PdfPageImage.Source = null;
        PdfAnnotationCanvas.Children.Clear();
        _pdfAnnotations = [];
        _openPdfDocument = null;
    }

    private static (double X, double Y, double Width, double Height) NormalizedBounds(Point start, Point end, double width, double height)
    {
        var left = Clamp01(Math.Min(start.X, end.X) / width);
        var top = Clamp01(Math.Min(start.Y, end.Y) / height);
        var right = Clamp01(Math.Max(start.X, end.X) / width);
        var bottom = Clamp01(Math.Max(start.Y, end.Y) / height);
        return (left, top, right - left, bottom - top);
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
}
