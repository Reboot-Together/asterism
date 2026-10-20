using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.UI.Core;

namespace AsterismApp;

public sealed partial class SideDocumentPane : UserControl
{
    private readonly string _workspaceRoot;
    private readonly Func<string, NoteInfo?> _resolveNote;
    private readonly Func<NoteInfo, string, string, NoteInfo> _saveNote;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly DispatcherQueueTimer _previewTimer;
    private readonly Dictionary<string, bool> _foldStates = new(StringComparer.Ordinal);
    private NoteInfo _note;
    private bool _loading;
    private bool _previewReady;
    private bool _previewInitializing;
    private bool _titlePreviewReady;
    private bool _titlePreviewInitializing;
    private WebViewHtmlUpdater? _previewHtmlUpdater;
    private WebViewHtmlUpdater? _titleHtmlUpdater;
    private PreviewEditSession? _previewEditSession;
    private double _previewScrollY;
    private double _fontScale;
    private string _accentColor;
    private string _surfaceTheme;
    private ScrollViewer? _editorScrollViewer;

    public SideDocumentPane(
        NoteInfo note,
        string workspaceRoot,
        Func<string, NoteInfo?> resolveNote,
        Func<NoteInfo, string, string, NoteInfo> saveNote,
        double fontScale,
        string accentColor,
        string surfaceTheme)
    {
        _note = note;
        _workspaceRoot = workspaceRoot;
        _resolveNote = resolveNote;
        _saveNote = saveNote;
        _fontScale = fontScale;
        _accentColor = accentColor;
        _surfaceTheme = surfaceTheme;
        InitializeComponent();
        ApplySurfacePalette();
        AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => InteractionStarted?.Invoke(this, EventArgs.Empty)),
            handledEventsToo: true);
        GotFocus += (_, _) => InteractionStarted?.Invoke(this, EventArgs.Empty);
        Preview.GotFocus += (_, _) => InteractionStarted?.Invoke(this, EventArgs.Empty);

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveNow();
        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(350);
        _previewTimer.IsRepeating = false;
        _previewTimer.Tick += (_, _) => RenderPreview();

        Preview.Loaded += Preview_Loaded;
        Preview.NavigationStarting += Preview_NavigationStarting;
        LoadNote(note, saveCurrent: false);
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? WorkspaceModeToggleRequested;
    public event EventHandler? InteractionStarted;

    public string NotePath => _note.Path;
    public NoteInfo CurrentNote => _note;

    public void FocusEditor()
    {
        InteractionStarted?.Invoke(this, EventArgs.Empty);
        if (_note.IsReadOnly) Preview.Focus(FocusState.Programmatic);
        else Editor.Focus(FocusState.Programmatic);
    }

    public bool LoadNote(NoteInfo note, bool saveCurrent = true)
    {
        if (saveCurrent && !SaveNow()) return false;
        _note = note;
        _foldStates.Clear();
        _previewScrollY = 0;
        _loading = true;
        TitleBox.Text = note.Title;
        TitleBox.IsReadOnly = note.IsReadOnly;
        TitleBox.IsHitTestVisible = !note.IsReadOnly;
        TitleBox.IsTabStop = !note.IsReadOnly;
        DocumentKindText.Text = note.IsNotebook ? "IPYNB" : note.IsReadOnly ? "GUIDE" : note.IsPlainText ? "SIDE TEXT" : "SIDE NOTE";
        StatusText.Text = note.IsNotebook ? "읽기 전용 · 저장된 셀과 출력" : note.IsReadOnly ? "읽기 전용 · 앱과 함께 자동 업데이트" : "";
        StatusText.Visibility = note.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
        Editor.Text = note.Body;
        Editor.IsReadOnly = note.IsReadOnly;
        Divider.Visibility = note.IsReadOnly ? Visibility.Collapsed : Visibility.Visible;
        EditorContainer.Visibility = note.IsReadOnly ? Visibility.Collapsed : Visibility.Visible;
        if (note.IsReadOnly)
        {
            PreviewRow.Height = new GridLength(1, GridUnitType.Star);
            DividerRow.Height = new GridLength(0);
            EditorRow.MinHeight = 0;
            EditorRow.Height = new GridLength(0);
        }
        else
        {
            PreviewRow.Height = new GridLength(2.125, GridUnitType.Star);
            DividerRow.Height = new GridLength(8);
            EditorRow.MinHeight = 110;
            EditorRow.Height = new GridLength(1, GridUnitType.Star);
        }
        _loading = false;
        ShowRenderedTitle();
        RenderPreview();
        return true;
    }

    public void RefreshAppearance(double fontScale, string accentColor, string surfaceTheme)
    {
        _fontScale = fontScale;
        _accentColor = accentColor;
        _surfaceTheme = surfaceTheme;
        if (Editor.Resources["TextControlSelectionHighlightColor"] is SolidColorBrush selection)
            selection.Color = AppearanceThemes.All.FirstOrDefault(theme =>
                theme.CssColor.Equals(accentColor, StringComparison.OrdinalIgnoreCase))?.Surface
                ?? AppearanceThemes.All[0].Surface;
        ApplySurfacePalette();
        ShowRenderedTitle();
        RenderPreview();
    }

    private void ApplySurfacePalette()
    {
        var surface = SurfaceThemes.Get(_surfaceTheme);
        RequestedTheme = surface.IsLight ? ElementTheme.Light : ElementTheme.Dark;
        Editor.Background = new SolidColorBrush(surface.DocumentBackground);
        Editor.Foreground = new SolidColorBrush(surface.PrimaryText);
        Editor.PlaceholderForeground = new SolidColorBrush(surface.PlaceholderText);
        Preview.DefaultBackgroundColor = surface.DocumentBackground;
        TitlePreview.DefaultBackgroundColor = surface.DocumentBackground;
        SetEditorBrush("TextControlBackground", surface.DocumentBackground);
        SetEditorBrush("TextControlBackgroundPointerOver", surface.DocumentBackground);
        SetEditorBrush("TextControlBackgroundFocused", surface.DocumentBackground);
        SetEditorBrush("ScrollBarThumbBackground", surface.ScrollThumb);
        SetEditorBrush("ScrollBarThumbBackgroundPointerOver", surface.ScrollThumbHover);
        SetEditorBrush("ScrollBarThumbBackgroundPressed", surface.ScrollThumbPressed);
    }

    private void SetEditorBrush(string key, Windows.UI.Color color)
    {
        if (Editor.Resources[key] is SolidColorBrush brush) brush.Color = color;
    }

    public bool SaveNow()
    {
        if (_loading || _note.IsReadOnly) return true;
        _saveTimer.Stop();
        var title = MarkdownText.NormalizeTitle(TitleBox.Text);
        var body = _note.IsPlainText ? Editor.Text : MarkdownText.NormalizeNewlines(Editor.Text).Trim();
        if (_note.Title == title && _note.Body == body) return true;

        try
        {
            _note = _saveNote(_note, title, body);
            _loading = true;
            TitleBox.Text = _note.Title;
            StatusText.Visibility = Visibility.Collapsed;
            _loading = false;
            return true;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"저장 실패 · {exception.Message}";
            StatusText.Visibility = Visibility.Visible;
            return false;
        }
    }

    private async void Preview_Loaded(object sender, RoutedEventArgs e)
    {
        if (_previewReady || _previewInitializing) return;
        _previewInitializing = true;
        try
        {
            await Preview.EnsureCoreWebView2Async();
            Preview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                MarkdownPreviewRenderer.VaultAssetHostName,
                _workspaceRoot,
                CoreWebView2HostResourceAccessKind.Allow);
            var mathAssetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "KaTeX");
            if (Directory.Exists(mathAssetsPath))
            {
                Preview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "node-assets.local",
                    mathAssetsPath,
                    CoreWebView2HostResourceAccessKind.Allow);
            }
            Preview.CoreWebView2.WebMessageReceived += Preview_WebMessageReceived;
            _previewHtmlUpdater = new WebViewHtmlUpdater(Preview);
            _previewReady = true;
            RenderPreview();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"미리보기 실패 · {exception.Message}";
            StatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            _previewInitializing = false;
        }
    }

    private void RenderPreview()
    {
        if (!_previewReady) return;
        if (_note.IsNotebook)
        {
            _previewEditSession = null;
            _previewHtmlUpdater?.Update(MarkdownPreviewRenderer.RenderNotebook(_note.Path, _workspaceRoot,
                _previewScrollY, _fontScale, _accentColor, _surfaceTheme));
            return;
        }
        _previewEditSession = _note.IsReadOnly ? null : new PreviewEditSession(Editor.Text);
        _previewHtmlUpdater?.Update(MarkdownPreviewRenderer.Render(
            Editor.Text,
            _workspaceRoot,
            title => _resolveNote(title)?.Body,
            _foldStates,
            _previewScrollY,
            _fontScale,
            _accentColor,
            _surfaceTheme,
            _previewEditSession));
    }

    private void Preview_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            var messageType = type.GetString();
            if (messageType == "inline-edit")
            {
                if (_note.IsReadOnly || _previewEditSession is null) return;
                if (_previewEditSession.TryApply(Editor.Text, root, out var updated))
                {
                    Editor.Text = updated;
                    _previewTimer.Stop();
                    SaveNow();
                }
                else
                {
                    StatusText.Text = "원문이 변경되었거나 지원하지 않는 텍스트입니다. 다시 수정해주세요.";
                    StatusText.Visibility = Visibility.Visible;
                }
                RenderPreview();
            }
            else if (messageType == "workspace-mode-toggle")
            {
                WorkspaceModeToggleRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (messageType == "fold-state"
                && root.TryGetProperty("key", out var keyElement)
                && root.TryGetProperty("open", out var openElement)
                && keyElement.GetString() is { Length: > 0 } key
                && openElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _foldStates[key] = openElement.GetBoolean();
            }
            else if (messageType == "preview-scroll"
                && root.TryGetProperty("y", out var yElement)
                && yElement.TryGetDouble(out var scrollY)
                && double.IsFinite(scrollY))
            {
                _previewScrollY = Math.Max(0, scrollY);
                if (root.TryGetProperty("maxY", out var maxElement)
                    && maxElement.TryGetDouble(out var maximum)
                    && maximum > 0)
                    SyncEditorScroll(_previewScrollY / maximum);
            }
            else if (messageType == "focus-editor")
            {
                if (_note.IsReadOnly) return;
                var offset = root.TryGetProperty("offset", out var offsetElement)
                    && offsetElement.TryGetInt32(out var requestedOffset)
                        ? MarkdownText.OriginalOffsetFromNormalized(Editor.Text, Math.Max(0, requestedOffset))
                        : Editor.SelectionStart;
                DispatcherQueue.TryEnqueue(() =>
                {
                    Editor.Focus(FocusState.Programmatic);
                    Editor.Select(Math.Clamp(offset, 0, Editor.Text.Length), 0);
                });
            }
        }
        catch { }
    }

    private void Preview_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme.Equals("node-note", StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
            var title = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (_resolveNote(title) is { } note) LoadNote(note);
        }
        else if (uri.Scheme is "http" or "https")
        {
            args.Cancel = true;
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e) => QueueSave();

    private void TitleBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_note.IsReadOnly) return;
        TitlePreview.Visibility = Visibility.Collapsed;
        TitleBox.Opacity = 1;
    }

    private void TitleBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveNow();
        ShowRenderedTitle();
    }

    private async void TitlePreview_Loaded(object sender, RoutedEventArgs e)
    {
        if (_titlePreviewReady || _titlePreviewInitializing) return;
        _titlePreviewInitializing = true;
        try
        {
            await TitlePreview.EnsureCoreWebView2Async();
            var mathAssetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "KaTeX");
            if (Directory.Exists(mathAssetsPath))
                TitlePreview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "node-assets.local", mathAssetsPath, CoreWebView2HostResourceAccessKind.Allow);
            _titleHtmlUpdater = new WebViewHtmlUpdater(TitlePreview);
            _titlePreviewReady = true;
            ShowRenderedTitle();
        }
        catch
        {
            TitlePreview.Visibility = Visibility.Collapsed;
            TitleBox.Opacity = 1;
        }
        finally
        {
            _titlePreviewInitializing = false;
        }
    }

    private void ShowRenderedTitle()
    {
        if (!_titlePreviewReady || TitleBox.FocusState != FocusState.Unfocused && !_note.IsReadOnly) return;
        TitleBox.Opacity = 0;
        TitleBox.IsHitTestVisible = !_note.IsReadOnly;
        TitleBox.IsTabStop = !_note.IsReadOnly;
        TitlePreview.Visibility = Visibility.Visible;
        _titleHtmlUpdater?.Update(MarkdownPreviewRenderer.RenderTitle(TitleBox.Text, _fontScale, _surfaceTheme));
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        QueueSave();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void QueueSave()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var controlDown = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        var altDown = e.KeyStatus.IsMenuKeyDown
            || InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                .HasFlag(CoreVirtualKeyStates.Down);
        var keyCode = (int)e.OriginalKey is 188 or 190 ? (int)e.OriginalKey : (int)e.Key;
        var headingLevelDelta = keyCode switch
        {
            188 when altDown => -1,
            190 when altDown => 1,
            _ => 0
        };
        if (headingLevelDelta != 0)
        {
            e.Handled = true;
            var edit = MarkdownHeadingLevelService.Change(Editor.Text, Editor.SelectionStart, Editor.SelectionLength, headingLevelDelta);
            if (!edit.Changed) return;
            Editor.Text = edit.Text;
            Editor.SelectionStart = edit.SelectionStart;
            Editor.SelectionLength = edit.SelectionLength;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && controlDown)
        {
            e.Handled = true;
            SaveNow();
            RenderPreview();
        }
    }

    private void Divider_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var availableHeight = Workspace.ActualHeight - Divider.ActualHeight;
        if (availableHeight <= 0) return;
        var previewHeight = Math.Clamp(
            PreviewRow.ActualHeight + e.VerticalChange,
            Math.Min(120, availableHeight * .5),
            availableHeight - Math.Min(110, availableHeight * .4));
        PreviewRow.Height = new GridLength(previewHeight / availableHeight, GridUnitType.Star);
        EditorRow.Height = new GridLength(1 - previewHeight / availableHeight, GridUnitType.Star);
    }

    private void Editor_Loaded(object sender, RoutedEventArgs e)
    {
        _editorScrollViewer = FindVisualDescendant<ScrollViewer>(Editor);
        foreach (var scrollBar in FindVisualDescendants<ScrollBar>(Editor))
        {
            if (scrollBar.Orientation != Orientation.Vertical) continue;
            scrollBar.Width = 8;
            scrollBar.MinWidth = 8;
        }
    }

    private void SyncEditorScroll(double progress)
    {
        var scrollViewer = _editorScrollViewer ??= FindVisualDescendant<ScrollViewer>(Editor);
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0) return;
        scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight * Math.Clamp(progress, 0, 1), null, true);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveNow()) return;
        _saveTimer.Stop();
        _previewTimer.Stop();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child)) yield return descendant;
        }
    }
}
