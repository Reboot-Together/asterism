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

    private sealed class PdfPageSurfaceState
    {
        public required int PageIndex { get; init; }
        public required double AspectRatio { get; init; }
        public required Grid Surface { get; init; }
        public required Image Image { get; init; }
        public required Canvas Canvas { get; init; }
        public bool IsRendering { get; set; }
        public double RenderedZoom { get; set; } = -1;
    }

    private readonly PdfAnnotationService _pdfAnnotationService = new();
    private bool _pdfMode;
    private string? _currentPdfPath;
    private PdfDocument? _openPdfDocument;
    private List<PdfAnnotation> _pdfAnnotations = [];
    private readonly List<PdfPageSurfaceState> _pdfPageSurfaces = [];
    private PdfTool _pdfTool = PdfTool.Select;
    private int _pdfPageIndex;
    private double _pdfZoom = 1;
    private bool _pdfVisibleRenderRunning;
    private bool _pdfVisibleRenderRequested;
    private int _pdfDocumentRevision;
    private Point? _pdfPointerStart;
    private Canvas? _pdfPointerCanvas;
    private int _pdfPointerPageIndex = -1;
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
        picker.FileTypeFilter.Add(".ipynb");
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            if (NotebookDocumentService.IsNotebookFile(file.Path))
            {
                var notebook = _notebookRepository.Import(file.Path, destinationFolder);
                ExpandFolder(Path.GetDirectoryName(notebook.Path)!);
                RefreshNotes();
                if (_pdfMode || _constellationMode) ShowDocumentMode();
                SelectNoteInActiveDocument(notebook);
                return;
            }
            var pdf = _pdfRepository.Import(file.Path, destinationFolder);
            ExpandFolder(Path.GetDirectoryName(pdf.Path)!);
            RefreshNotes();
            await ShowPdfModeAsync(pdf.Path);
        }
        catch (Exception exception) { await ShowMessage("자료 가져오기 실패", exception.Message); }
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
            _pdfDocumentRevision++;
            SetPdfTool(PdfTool.Select);
            BuildPdfPageSurfaces();
            await RenderVisiblePdfPagesAsync();
            PdfStatusText.Text = "주석은 PDF 원본을 바꾸지 않고 별도 저장됩니다.";
        }
        catch (Exception exception)
        {
            _openPdfDocument = null;
            PdfStatusText.Text = $"PDF를 표시하지 못했습니다: {exception.Message}";
        }
    }

    private void BuildPdfPageSurfaces()
    {
        PdfPagesHost.Children.Clear();
        _pdfPageSurfaces.Clear();
        if (_openPdfDocument is null) return;

        var pageCount = checked((int)_openPdfDocument.PageCount);
        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            using var page = _openPdfDocument.GetPage((uint)pageIndex);
            var image = new Image { Stretch = Stretch.Fill };
            var canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)), Tag = pageIndex };
            canvas.PointerPressed += PdfAnnotationCanvas_PointerPressed;
            canvas.PointerMoved += PdfAnnotationCanvas_PointerMoved;
            canvas.PointerReleased += PdfAnnotationCanvas_PointerReleased;
            canvas.PointerCanceled += PdfAnnotationCanvas_PointerCanceled;
            canvas.PointerCaptureLost += PdfAnnotationCanvas_PointerCanceled;
            var surface = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                UseLayoutRounding = true
            };
            surface.Children.Add(image);
            surface.Children.Add(canvas);
            var state = new PdfPageSurfaceState
            {
                PageIndex = pageIndex,
                AspectRatio = page.Size.Height / page.Size.Width,
                Surface = surface,
                Image = image,
                Canvas = canvas
            };
            _pdfPageSurfaces.Add(state);
            PdfPagesHost.Children.Add(surface);
        }

        ResizePdfPageSurfaces();
        PdfScroll.ChangeView(0, 0, null, true);
        PdfScroll.UpdateLayout();
        UpdatePdfCurrentPage();
        DrawPdfAnnotations();
    }

    private void ResizePdfPageSurfaces()
    {
        var viewportWidth = PdfScroll.ViewportWidth > 120
            ? PdfScroll.ViewportWidth - PdfScroll.Padding.Left - PdfScroll.Padding.Right
            : PdfScroll.ActualWidth > 120 ? PdfScroll.ActualWidth - 64 : 920;
        var displayWidth = Math.Clamp(viewportWidth, 560, 1080) * _pdfZoom;
        foreach (var state in _pdfPageSurfaces)
        {
            var displayHeight = displayWidth * state.AspectRatio;
            state.Surface.Width = displayWidth;
            state.Surface.Height = displayHeight;
            state.Image.Width = displayWidth;
            state.Image.Height = displayHeight;
            state.Canvas.Width = displayWidth;
            state.Canvas.Height = displayHeight;
        }
        PdfZoomText.Text = $"{_pdfZoom:P0}";
        DrawPdfAnnotations();
    }

    private async Task RenderVisiblePdfPagesAsync()
    {
        _pdfVisibleRenderRequested = true;
        if (_pdfVisibleRenderRunning) return;
        _pdfVisibleRenderRunning = true;
        try
        {
            while (_pdfVisibleRenderRequested)
            {
                _pdfVisibleRenderRequested = false;
                PdfScroll.UpdateLayout();
                var viewport = Math.Max(480, PdfScroll.ViewportHeight);
                var visibleTop = Math.Max(0, PdfScroll.VerticalOffset - viewport);
                var visibleBottom = PdfScroll.VerticalOffset + viewport * 2;
                var candidates = _pdfPageSurfaces.Where(state =>
                {
                    var top = PageTop(state);
                    return top + state.Surface.Height >= visibleTop && top <= visibleBottom;
                }).ToList();
                var retainTop = Math.Max(0, PdfScroll.VerticalOffset - viewport * 2.5);
                var retainBottom = PdfScroll.VerticalOffset + viewport * 3.5;
                foreach (var state in _pdfPageSurfaces.Except(candidates))
                {
                    var top = PageTop(state);
                    if (top + state.Surface.Height >= retainTop && top <= retainBottom) continue;
                    state.Image.Source = null;
                    state.RenderedZoom = -1;
                }
                foreach (var state in candidates) await RenderPdfPageSurfaceAsync(state);
            }
        }
        finally { _pdfVisibleRenderRunning = false; }
    }

    private async Task RenderPdfPageSurfaceAsync(PdfPageSurfaceState state)
    {
        var document = _openPdfDocument;
        var revision = _pdfDocumentRevision;
        if (document is null || state.IsRendering || Math.Abs(state.RenderedZoom - _pdfZoom) < .001) return;
        state.IsRendering = true;
        var requestedZoom = _pdfZoom;
        try
        {
            using var page = document.GetPage((uint)state.PageIndex);
            var destinationWidth = (uint)Math.Clamp(Math.Round(state.Surface.Width * 1.45), 1, 4096);
            var destinationHeight = (uint)Math.Clamp(Math.Round(state.Surface.Height * 1.45), 1, 5792);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                DestinationWidth = destinationWidth,
                DestinationHeight = destinationHeight
            });
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (revision != _pdfDocumentRevision || !ReferenceEquals(document, _openPdfDocument)) return;
            state.Image.Source = bitmap;
            state.RenderedZoom = requestedZoom;
        }
        catch (Exception exception)
        {
            if (revision == _pdfDocumentRevision) PdfStatusText.Text = $"{state.PageIndex + 1}페이지를 표시하지 못했습니다: {exception.Message}";
        }
        finally { state.IsRendering = false; }
    }

    private double PageTop(PdfPageSurfaceState state) =>
        state.Surface.TransformToVisual(PdfPagesHost).TransformPoint(new Point()).Y;

    private void DrawPdfAnnotations()
    {
        foreach (var state in _pdfPageSurfaces) DrawPdfAnnotations(state);
    }

    private void DrawPdfAnnotations(PdfPageSurfaceState state)
    {
        var canvas = state.Canvas;
        canvas.Children.Clear();
        var width = canvas.Width;
        var height = canvas.Height;
        if (width <= 0 || height <= 0) return;
        foreach (var annotation in _pdfAnnotations.Where(item => item.PageIndex == state.PageIndex))
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
                canvas.Children.Add(mark);
            }
            else if (annotation.Kind == PdfAnnotationKind.Text)
            {
                AddPdfTextCard(canvas, annotation, annotation.X * width, annotation.Y * height, selected);
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
                canvas.Children.Add(outline);
                canvas.Children.Add(new Line
                {
                    X1 = left + markWidth / 2,
                    Y1 = top + markHeight / 2,
                    X2 = targetX,
                    Y2 = targetY,
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 245, 166, 35)),
                    StrokeThickness = selected ? 3 : 2,
                    IsHitTestVisible = false
                });
                AddPdfTextCard(canvas, annotation, targetX, targetY, selected);
            }
        }
    }

    private void AddPdfTextCard(Canvas canvas, PdfAnnotation annotation, double left, double top, bool selected)
    {
        var card = new Border
        {
            MaxWidth = Math.Max(150, canvas.Width * .34),
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
        Canvas.SetLeft(card, Math.Clamp(left, 0, Math.Max(0, canvas.Width - 110)));
        Canvas.SetTop(card, Math.Clamp(top, 0, Math.Max(0, canvas.Height - 44)));
        canvas.Children.Add(card);
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
        if (_openPdfDocument is null || sender is not Canvas canvas || canvas.Tag is not int pageIndex) return;
        var point = e.GetCurrentPoint(canvas).Position;
        _pdfPageIndex = pageIndex;
        UpdatePdfPageText();
        if (_pdfTool == PdfTool.Select)
        {
            _selectedPdfAnnotationId = FindPdfAnnotation(canvas, pageIndex, point)?.Id;
            DrawPdfAnnotations();
            PdfStatusText.Text = _selectedPdfAnnotationId is null ? "선택된 주석이 없습니다." : "주석이 선택되었습니다.";
            return;
        }
        _pdfPointerStart = point;
        _pdfPointerCanvas = canvas;
        _pdfPointerPageIndex = pageIndex;
        canvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PdfAnnotationCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pdfPointerStart is not Point start || _pdfPointerCanvas is not Canvas canvas || !ReferenceEquals(sender, canvas) || _pdfTool == PdfTool.Text) return;
        ShowPdfDrawingPreview(canvas, start, e.GetCurrentPoint(canvas).Position);
        e.Handled = true;
    }

    private async void PdfAnnotationCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pdfPointerStart is not Point start || _pdfPointerCanvas is not Canvas canvas || _pdfPointerPageIndex < 0 || _currentPdfPath is null) return;
        var pageIndex = _pdfPointerPageIndex;
        var current = e.GetCurrentPoint(canvas).Position;
        _pdfPointerStart = null;
        _pdfPointerCanvas = null;
        _pdfPointerPageIndex = -1;
        canvas.ReleasePointerCapture(e.Pointer);
        RemovePdfDrawingPreview();
        e.Handled = true;
        var surfaceWidth = Math.Max(1, canvas.Width);
        var surfaceHeight = Math.Max(1, canvas.Height);
        if (_pdfTool == PdfTool.Text)
        {
            var text = await PromptForPdfText("PDF 텍스트 추가", "페이지에 표시할 내용을 입력하세요.");
            if (text is null) return;
            _pdfAnnotations.Add(new PdfAnnotation(Guid.NewGuid(), PdfAnnotationKind.Text, pageIndex,
                Clamp01(current.X / surfaceWidth), Clamp01(current.Y / surfaceHeight), .3, .08, 0, 0, text, DateTimeOffset.Now));
        }
        else
        {
            var bounds = NormalizedBounds(start, current, surfaceWidth, surfaceHeight);
            if (bounds.Width * surfaceWidth < 6 || bounds.Height * surfaceHeight < 6) return;
            if (_pdfTool == PdfTool.Highlight)
            {
                _pdfAnnotations.Add(new PdfAnnotation(Guid.NewGuid(), PdfAnnotationKind.Highlight, pageIndex,
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
                _pdfAnnotations.Add(new PdfAnnotation(Guid.NewGuid(), PdfAnnotationKind.Callout, pageIndex,
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
        _pdfPointerCanvas = null;
        _pdfPointerPageIndex = -1;
        RemovePdfDrawingPreview();
    }

    private void ShowPdfDrawingPreview(Canvas canvas, Point start, Point end)
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
        canvas.Children.Add(preview);
        _pdfDrawingPreview = preview;
    }

    private void RemovePdfDrawingPreview()
    {
        if (_pdfDrawingPreview?.Parent is Panel parent) parent.Children.Remove(_pdfDrawingPreview);
        _pdfDrawingPreview = null;
    }

    private PdfAnnotation? FindPdfAnnotation(Canvas canvas, int pageIndex, Point point)
    {
        var x = point.X / Math.Max(1, canvas.Width);
        var y = point.Y / Math.Max(1, canvas.Height);
        const double tolerance = .012;
        return _pdfAnnotations.Where(item => item.PageIndex == pageIndex).OrderByDescending(item => item.CreatedAt)
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
        if (_openPdfDocument is null || _pdfPageIndex <= 0) return;
        _selectedPdfAnnotationId = null;
        ScrollToPdfPage(_pdfPageIndex - 1);
        await RenderVisiblePdfPagesAsync();
    }

    private async void PdfNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_openPdfDocument is null || _pdfPageIndex + 1 >= _openPdfDocument.PageCount) return;
        _selectedPdfAnnotationId = null;
        ScrollToPdfPage(_pdfPageIndex + 1);
        await RenderVisiblePdfPagesAsync();
    }

    private async void PdfZoomOut_Click(object sender, RoutedEventArgs e)
    {
        await ApplyPdfZoomAsync(Math.Max(.5, _pdfZoom - .25), PdfViewportCenter());
    }

    private async void PdfZoomIn_Click(object sender, RoutedEventArgs e)
    {
        await ApplyPdfZoomAsync(Math.Min(4, _pdfZoom + .25), PdfViewportCenter());
    }

    private async void PdfScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_pdfMode || _openPdfDocument is null || !IsPdfControlKeyDown()) return;
        var wheelDelta = e.GetCurrentPoint(PdfScroll).Properties.MouseWheelDelta;
        if (wheelDelta == 0) return;
        e.Handled = true;
        var pointerInViewport = e.GetCurrentPoint(PdfScroll).Position;
        await ApplyPdfZoomAsync(Math.Clamp(_pdfZoom + (wheelDelta > 0 ? .25 : -.25), .5, 4), pointerInViewport);
    }

    private async void PdfScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_pdfMode) return;
        UpdatePdfCurrentPage();
        await RenderVisiblePdfPagesAsync();
    }

    private async Task ApplyPdfZoomAsync(double nextZoom, Point viewportAnchor)
    {
        if (_openPdfDocument is null || Math.Abs(nextZoom - _pdfZoom) < .001 || _pdfPageSurfaces.Count == 0) return;
        PdfScroll.UpdateLayout();
        var anchor = FindPdfZoomAnchor(viewportAnchor);
        _pdfZoom = nextZoom;
        ResizePdfPageSurfaces();
        PdfScroll.UpdateLayout();

        if (anchor.State is not null)
        {
            var pageLeft = anchor.State.Surface.TransformToVisual(PdfPagesHost).TransformPoint(new Point()).X;
            var pageTop = PageTop(anchor.State);
            var horizontal = PdfScroll.Padding.Left + pageLeft + anchor.RelativeX * anchor.State.Surface.Width - viewportAnchor.X;
            var vertical = PdfScroll.Padding.Top + pageTop + anchor.RelativeY * anchor.State.Surface.Height - viewportAnchor.Y;
            PdfScroll.ChangeView(
                Math.Clamp(horizontal, 0, PdfScroll.ScrollableWidth),
                Math.Clamp(vertical, 0, PdfScroll.ScrollableHeight),
                null,
                true);
        }

        await RenderVisiblePdfPagesAsync();
    }

    private (PdfPageSurfaceState? State, double RelativeX, double RelativeY) FindPdfZoomAnchor(Point viewportPoint)
    {
        if (_pdfPageSurfaces.Count == 0) return (null, .5, .5);
        var contentY = PdfScroll.VerticalOffset + viewportPoint.Y - PdfScroll.Padding.Top;
        var state = _pdfPageSurfaces.FirstOrDefault(candidate =>
        {
            var top = PageTop(candidate);
            return contentY >= top && contentY <= top + candidate.Surface.Height;
        }) ?? _pdfPageSurfaces.MinBy(candidate => Math.Abs(contentY - (PageTop(candidate) + candidate.Surface.Height / 2)))!;
        var pagePoint = PdfScroll.TransformToVisual(state.Surface).TransformPoint(viewportPoint);
        return (
            state,
            Clamp01(pagePoint.X / Math.Max(1, state.Surface.Width)),
            Clamp01(pagePoint.Y / Math.Max(1, state.Surface.Height)));
    }

    private Point PdfViewportCenter() => new(
        Math.Max(0, PdfScroll.ViewportWidth / 2),
        Math.Max(0, PdfScroll.ViewportHeight / 2));

    private void ScrollToPdfPage(int pageIndex)
    {
        if (_pdfPageSurfaces.Count == 0) return;
        _pdfPageIndex = Math.Clamp(pageIndex, 0, _pdfPageSurfaces.Count - 1);
        var state = _pdfPageSurfaces[_pdfPageIndex];
        PdfScroll.ChangeView(
            PdfScroll.HorizontalOffset,
            Math.Clamp(PdfScroll.Padding.Top + PageTop(state), 0, PdfScroll.ScrollableHeight),
            null,
            false);
        UpdatePdfPageText();
    }

    private void UpdatePdfCurrentPage()
    {
        if (_pdfPageSurfaces.Count == 0) return;
        var centerY = PdfScroll.VerticalOffset + PdfScroll.ViewportHeight / 2 - PdfScroll.Padding.Top;
        _pdfPageIndex = _pdfPageSurfaces
            .MinBy(state => Math.Abs(centerY - (PageTop(state) + state.Surface.Height / 2)))!
            .PageIndex;
        UpdatePdfPageText();
    }

    private void UpdatePdfPageText() =>
        PdfPageText.Text = _openPdfDocument is null ? "0 / 0" : $"{_pdfPageIndex + 1} / {_openPdfDocument.PageCount}";

    private static bool IsPdfControlKeyDown() =>
        IsKeyDown(Windows.System.VirtualKey.Control)
        || IsKeyDown(Windows.System.VirtualKey.LeftControl)
        || IsKeyDown(Windows.System.VirtualKey.RightControl);

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
        _pdfDocumentRevision++;
        foreach (var state in _pdfPageSurfaces) state.Image.Source = null;
        PdfPagesHost.Children.Clear();
        _pdfPageSurfaces.Clear();
        _pdfAnnotations = [];
        _pdfPointerStart = null;
        _pdfPointerCanvas = null;
        _pdfPointerPageIndex = -1;
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
