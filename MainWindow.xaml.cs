using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI.Core;

namespace AsterismApp;

public sealed partial class MainWindow : Window
{
    private readonly WorkspaceService _workspace = new();
    private readonly UiLayoutSettingsService _uiLayoutSettingsService = new();
    private readonly NoteRepository _repository;
    private readonly TextDocumentService _textRepository;
    private readonly PdfDocumentService _pdfRepository;
    private readonly NoteLinkService _linkService = new();
    private readonly NoteImageService _imageService = new();
    private readonly VaultTreeService _vaultTreeService = new();
    private readonly FolderExpansionService _folderExpansionService = new();
    private readonly BuiltInGuideService _guideService = new();
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly DispatcherQueueTimer _previewTimer;
    private List<NoteInfo> _notes = [];
    private List<PdfInfo> _pdfDocuments = [];
    private IReadOnlyList<string> _folders = [];
    private IReadOnlyList<string> ExplorerFolders => [.. _folders, BuiltInGuideService.FolderPath];
    private IReadOnlyList<VaultItem> _vaultItems = [];
    private IReadOnlyDictionary<string, List<string>> _noteLinks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);
    private bool _folderExpansionInitialized;
    private readonly Dictionary<string, Dictionary<string, bool>> _noteFoldStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _notePreviewScrollPositions = new(StringComparer.OrdinalIgnoreCase);
    private NoteInfo? _selected;
    private bool _loading;
    private bool _previewReady;
    private bool _previewInitializing;
    private bool _titlePreviewReady;
    private bool _titlePreviewInitializing;
    private WebViewHtmlUpdater? _previewHtmlUpdater;
    private WebViewHtmlUpdater? _titleHtmlUpdater;
    private PreviewEditSession? _previewEditSession;
    private UiLayoutSettings _uiLayoutSettings = UiLayoutSettings.Default;
    private ScrollViewer? _editorScrollViewer;
    private bool _previewHoverSelectionActive;
    private int _previewHoverOriginalStart;
    private int _previewHoverOriginalLength;
    private int _previewHoverSelectionRevision;
    private bool _suppressNoteListSelectionChanged;

    public MainWindow()
    {
        _repository = new NoteRepository(_workspace.RootPath);
        _textRepository = new TextDocumentService(_workspace.RootPath);
        _pdfRepository = new PdfDocumentService(_workspace.RootPath);
        InitializeComponent();
        GraphCanvas.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(GraphCanvas_PointerWheelChanged),
            handledEventsToo: true);
        PdfScroll.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(PdfScroll_PointerWheelChanged),
            handledEventsToo: true);
        ConfigureTitleBar();
        _uiLayoutSettings = _uiLayoutSettingsService.Load();
        ApplyAppearanceSettings(refreshContent: false);
        ApplyDocumentSplit(_uiLayoutSettings.PreviewRatio);
        ApplyExplorerState(_uiLayoutSettings.ExplorerCollapsed);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Asterism.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        AppWindow.Resize(new SizeInt32(1440, 920));
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveCurrent();
        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(350);
        _previewTimer.IsRepeating = false;
        _previewTimer.Tick += (_, _) => UpdateMarkdownPreview();
        MarkdownPreview.Loaded += MarkdownPreview_Loaded;
        DocumentPanel.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => ActivatePrimaryDocument()),
            handledEventsToo: true);
        DocumentPanel.GotFocus += (_, _) => ActivatePrimaryDocument();
        MarkdownPreview.GotFocus += (_, _) => ActivatePrimaryDocument();
        RefreshNotes();
        if (_notes.Count == 0) NewNote(); else Select(_notes[0]);
        AppWindow.Closing += AppWindow_Closing;
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                ResetGraphDirectionalCursor();
        };
        Closed += (_, _) =>
        {
            ResetGraphDirectionalCursor();
            SaveSideDocuments();
            SaveCurrent();
            _uiLayoutSettingsService.Save(_uiLayoutSettings);
            StopSemanticIndexing();
        };
    }

    private void RefreshNotes()
    {
        // Markdown and TXT are both editable knowledge resources.  Keeping them
        // in one index means wiki links, backlinks, semantic suggestions and the
        // constellation use the same source of truth regardless of extension.
        _notes = [.. _repository.Load(), .. _textRepository.Load()];
        _pdfDocuments = _pdfRepository.Load();
        _folders = _vaultTreeService.LoadFolders(_workspace.RootPath);
        if (!_folderExpansionInitialized)
        {
            _folderExpansionService.InitializeDefaults(_workspace.RootPath, ExplorerFolders, _expandedFolders);
            _folderExpansionService.ExpandExclusive(
                _workspace.RootPath,
                ExplorerFolders,
                _expandedFolders,
                BuiltInGuideService.FolderPath);
            _folderExpansionInitialized = true;
        }
        RefreshLinkIndex();
        ApplySearch();
        QueueSemanticRefresh();
    }

    private void RefreshLinkIndex() => _noteLinks = _linkService.Build(_notes);

    private void RefreshFolders()
    {
        _folders = _vaultTreeService.LoadFolders(_workspace.RootPath);
        ApplySearch();
    }

    private void ApplySearch()
    {
        var query = SearchBox?.Text.Trim() ?? "";
        var activeNote = ActiveDocumentNote;
        var activePath = _pdfMode ? _currentPdfPath : activeNote?.Path;
        var preferredFolders = activePath is null
            ? []
            : activeNote?.IsReadOnly == true
                ? [BuiltInGuideService.FolderPath]
                : _vaultTreeService.AncestorFolders(_workspace.RootPath, activePath);
        _folderExpansionService.EnforceExclusive(
            _workspace.RootPath,
            ExplorerFolders,
            _expandedFolders,
            preferredFolders);
        var items = _vaultTreeService.Build(_workspace.RootPath, _notes, _folders, _expandedFolders, query, _pdfDocuments).ToList();
        items.InsertRange(0, _guideService.BuildItems(_expandedFolders, query));
        _suppressNoteListSelectionChanged = true;
        try
        {
            _vaultItems = items;
            NoteList.ItemsSource = _vaultItems;
            if (activePath is not null)
                NoteList.SelectedItem = _vaultItems.FirstOrDefault(item => item.Path.Equals(activePath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressNoteListSelectionChanged = false;
        }
    }

    private void NewNote(string? parentFolder = null)
    {
        SaveCurrent();
        var note = parentFolder is null
            ? _repository.Create()
            : _repository.CreateInFolder(parentFolder);
        if (parentFolder is not null) ExpandFolder(parentFolder);
        RefreshNotes();
        Select(note, focusEditor: true);
    }

    private void NewTextDocument(string? parentFolder = null)
    {
        SaveCurrent();
        var folder = parentFolder ?? _workspace.RootPath;
        var text = _textRepository.Create(folder, "새 텍스트");
        ExpandFolder(folder);
        RefreshNotes();
        Select(text, focusEditor: true);
    }

    private void Select(NoteInfo note, bool focusEditor = false)
    {
        var requestedPath = note.Path;
        SaveCurrent();
        note = _notes.FirstOrDefault(candidate =>
            candidate.Path.Equals(requestedPath, StringComparison.OrdinalIgnoreCase)) ?? note;

        CaptureCurrentGraphViewport();
        _previewHoverSelectionActive = false;
        _previewHoverSelectionRevision++;
        _loading = true;
        _selected = note;
        TitleBox.Text = note.Title;
        ConstellationTitleText.Text = note.Title;
        Editor.Text = note.Body;
        RevealNoteInTree(note);
        _loading = false;
        ShowRenderedTitle();
        ShowEditorAndPreview();
        UpdateBacklinks();
        UpdateSemanticSuggestions();
        var restoreGraphViewport = PrepareGraphViewport(note);
        DrawGraph(centerCurrentNode: !restoreGraphViewport);
        if (restoreGraphViewport) RestoreGraphViewport(note);
        if (focusEditor) DispatcherQueue.TryEnqueue(() => Editor.Focus(FocusState.Programmatic));
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try
        {
            _saveTimer.Stop();
            _previewTimer.Stop();
            if (!SaveSideDocuments())
            {
                args.Cancel = true;
                _ = ShowMessage("앱을 닫을 수 없음", "저장하지 못한 옆 문서가 있습니다. 문서에 표시된 저장 오류를 확인해 주세요.");
                return;
            }

            SaveCurrent();
        }
        catch (Exception exception)
        {
            args.Cancel = true;
            _ = ShowMessage("앱을 닫을 수 없음", $"현재 노트를 저장하지 못했습니다.\n\n{exception.Message}");
        }
    }

    private void RevealNoteInTree(NoteInfo note)
    {
        RevealVaultItemInTree(note.Path, note.IsReadOnly);
    }

    private void RevealVaultItemInTree(string path, bool isReadOnly = false)
    {
        var treeChanged = false;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            SearchBox.Text = "";
            treeChanged = true;
        }
        if (isReadOnly)
        {
            var before = _expandedFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _folderExpansionService.ExpandExclusive(
                _workspace.RootPath,
                ExplorerFolders,
                _expandedFolders,
                BuiltInGuideService.FolderPath);
            treeChanged |= !before.SetEquals(_expandedFolders);
        }
        else
            foreach (var folder in _vaultTreeService.AncestorFolders(_workspace.RootPath, path).Reverse())
            {
                var before = _expandedFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
                _folderExpansionService.ExpandExclusive(_workspace.RootPath, ExplorerFolders, _expandedFolders, folder);
                treeChanged |= !before.SetEquals(_expandedFolders);
            }

        var item = treeChanged
            ? null
            : _vaultItems.FirstOrDefault(candidate => candidate.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            ApplySearch();
            item = _vaultItems.FirstOrDefault(candidate => candidate.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
        if (item is null) return;

        _suppressNoteListSelectionChanged = true;
        try
        {
            NoteList.SelectedItem = item;
        }
        finally
        {
            _suppressNoteListSelectionChanged = false;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (NoteList.SelectedItem is VaultItem selectedItem && selectedItem.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                NoteList.ScrollIntoView(item);
        });
    }

    private NoteMetadata MetadataFromEditor() => _selected?.Metadata ?? NoteMetadata.Manual;

    private void SaveCurrent()
    {
        if (_loading || _selected is null || _selected.IsReadOnly) return;
        _saveTimer.Stop();
        var previous = _selected;
        if (previous.IsPlainText)
        {
            SaveTextDocument(previous);
            return;
        }

        var metadata = MetadataFromEditor();
        var title = MarkdownText.NormalizeTitle(TitleBox.Text);
        var body = MarkdownText.NormalizeNewlines(Editor.Text).Trim();
        if (TitleBox.Text != title)
        {
            _loading = true;
            TitleBox.Text = title;
            _loading = false;
        }
        if (previous.Title == title && previous.Body == body && previous.Metadata == metadata) return;

        var linksChanged = !_linkService.ExtractTargets(previous.Body).SetEquals(_linkService.ExtractTargets(body));
        _selected = _repository.Save(previous.Path, title, body, metadata, previous.Title);
        var noteIndex = _notes.FindIndex(note => note.Path.Equals(previous.Path, StringComparison.OrdinalIgnoreCase));
        if (noteIndex >= 0) _notes[noteIndex] = _selected;
        else _notes.Add(_selected);

        if (TitleBox.Text != _selected.Title)
        {
            _loading = true;
            TitleBox.Text = _selected.Title;
            _loading = false;
        }

        var titleChanged = !previous.Title.Equals(_selected.Title, StringComparison.Ordinal);
        var bodyChanged = previous.Body != _selected.Body;
        var metadataChanged = previous.Metadata != _selected.Metadata;
        if (titleChanged || metadataChanged) ApplySearch();
        if (titleChanged || linksChanged)
        {
            RefreshLinkIndex();
            UpdateBacklinks();
        }
        if (titleChanged || linksChanged || metadataChanged) DrawGraph();
        if (titleChanged || bodyChanged) QueueSemanticRefresh();
    }

    private NoteInfo SaveTextDocument(NoteInfo previous)
    {
        var title = MarkdownText.NormalizeTitle(TitleBox.Text);
        var body = Editor.Text;
        if (TitleBox.Text != title)
        {
            _loading = true;
            TitleBox.Text = title;
            _loading = false;
        }
        if (previous.Title == title && previous.Body == body) return previous;

        var linksChanged = !_linkService.ExtractTargets(previous.Body).SetEquals(_linkService.ExtractTargets(body));
        var saved = previous;
        if (!previous.Title.Equals(title, StringComparison.Ordinal))
        {
            saved = _textRepository.Rename(previous.Path, title);
            _vaultTreeService.RemapOrderPath(_workspace.RootPath, previous.Path, saved.Path);
        }
        saved = _textRepository.Save(saved.Path, body);
        _selected = saved;
        ReplaceIndexedNote(previous.Path, saved);

        var titleChanged = !previous.Title.Equals(saved.Title, StringComparison.Ordinal);
        var bodyChanged = previous.Body != saved.Body;
        if (titleChanged) ApplySearch();
        if (titleChanged || linksChanged)
        {
            RefreshLinkIndex();
            UpdateBacklinks();
            DrawGraph();
        }
        if (titleChanged || bodyChanged) QueueSemanticRefresh();
        return saved;
    }

    private void ReplaceIndexedNote(string previousPath, NoteInfo saved)
    {
        var noteIndex = _notes.FindIndex(note => note.Path.Equals(previousPath, StringComparison.OrdinalIgnoreCase));
        if (noteIndex >= 0) _notes[noteIndex] = saved;
        else _notes.Add(saved);
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (_selected is { IsReadOnly: false })
        {
            _saveTimer.Stop();
            _saveTimer.Start();
            _previewTimer.Stop();
            _previewTimer.Start();
        }
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && _selected is { IsReadOnly: false })
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    private void TitleBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_selected?.IsReadOnly == true) return;
        TitlePreview.Visibility = Visibility.Collapsed;
        TitleBox.Opacity = 1;
    }

    private void TitleBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveCurrent();
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
        if (!_titlePreviewReady || TitleBox.FocusState != FocusState.Unfocused && _selected?.IsReadOnly != true) return;
        TitleBox.Opacity = 0;
        TitleBox.IsHitTestVisible = _selected?.IsReadOnly != true;
        TitleBox.IsTabStop = _selected?.IsReadOnly != true;
        TitlePreview.Visibility = Visibility.Visible;
        _titleHtmlUpdater?.Update(MarkdownPreviewRenderer.RenderTitle(
            TitleBox.Text, _uiLayoutSettings.FontScale, CurrentSurface.Key));
    }

    private async void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var controlDown = IsKeyDown(Windows.System.VirtualKey.Control)
            || IsKeyDown(Windows.System.VirtualKey.LeftControl)
            || IsKeyDown(Windows.System.VirtualKey.RightControl);
        var altDown = e.KeyStatus.IsMenuKeyDown
            || IsKeyDown(Windows.System.VirtualKey.Menu)
            || IsKeyDown(Windows.System.VirtualKey.LeftMenu)
            || IsKeyDown(Windows.System.VirtualKey.RightMenu);
        var originalKeyCode = (int)e.OriginalKey;
        var keyCode = originalKeyCode is 188 or 190 ? originalKeyCode : (int)e.Key;

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
            if (edit.Changed)
            {
                Editor.Text = edit.Text;
                Editor.SelectionStart = edit.SelectionStart;
                Editor.SelectionLength = edit.SelectionLength;
            }
            return;
        }

        if (e.Key == Windows.System.VirtualKey.V && controlDown)
        {
            DataPackageView? clipboard = null;
            try { clipboard = Clipboard.GetContent(); }
            catch { }
            if (clipboard?.Contains(StandardDataFormats.Bitmap) == true)
            {
                e.Handled = true;
                try
                {
                    var bitmap = await clipboard.GetBitmapAsync();
                    var relativePath = await _imageService.SavePngAsync(_workspace.RootPath, bitmap);
                    InsertAtEditorSelection($"![[{relativePath}]]");
                    Editor.Focus(FocusState.Programmatic);
                }
                catch (Exception exception)
                {
                    await ShowMessage("이미지를 붙여넣을 수 없음", $"클립보드 이미지를 저장하지 못했습니다.\n\n{exception.Message}");
                }
                return;
            }
        }

        if (e.Key != Windows.System.VirtualKey.Enter || !controlDown) return;

        e.Handled = true;
        SaveEditor();
    }

    private void InsertAtEditorSelection(string markdown)
    {
        var start = Editor.SelectionStart;
        var length = Editor.SelectionLength;
        Editor.Text = Editor.Text.Remove(start, length).Insert(start, markdown);
        Editor.SelectionStart = start + markdown.Length;
        Editor.SelectionLength = 0;
    }

    private void Editor_Save_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SaveEditor();
    }

    private void Editor_LostFocus(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
            if (IsInside(focused, DocumentWorkspace) || MarkdownPreview.FocusState != FocusState.Unfocused) return;
            SaveEditor();
        });
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, container)) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private static bool IsKeyDown(Windows.System.VirtualKey key) => InputKeyboardSource
        .GetKeyStateForCurrentThread(key)
        .HasFlag(CoreVirtualKeyStates.Down);

    private void SaveEditor()
    {
        _previewTimer.Stop();
        _saveTimer.Stop();
        SaveCurrent();
        UpdateMarkdownPreview();
    }

    private void ShowEditorAndPreview()
    {
        var readOnly = _selected?.IsReadOnly == true;
        DocumentKindText.Text = readOnly ? "GUIDE" : _selected?.IsPlainText == true ? "TEXT" : "NOTE";
        ReadOnlyBadge.Visibility = readOnly ? Visibility.Visible : Visibility.Collapsed;
        TitleBox.IsReadOnly = readOnly;
        TitleBox.IsHitTestVisible = !readOnly;
        TitleBox.IsTabStop = !readOnly;
        Editor.IsReadOnly = readOnly;
        EditorContainer.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        EditorPreviewDivider.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        MarkdownPreview.Visibility = Visibility.Visible;
        if (readOnly)
        {
            PreviewRow.Height = new GridLength(1, GridUnitType.Star);
            EditorDividerRow.Height = new GridLength(0);
            EditorRow.MinHeight = 0;
            EditorRow.Height = new GridLength(0);
        }
        else
        {
            EditorDividerRow.Height = new GridLength(8);
            EditorRow.MinHeight = 130;
            ApplyDocumentSplit(_uiLayoutSettings.PreviewRatio);
        }
        UpdateMarkdownPreview();
    }

    private void EditorPreviewDivider_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var availableHeight = DocumentWorkspace.ActualHeight - EditorPreviewDivider.ActualHeight;
        if (availableHeight <= 0) return;
        var minimumPreviewHeight = Math.Min(180, availableHeight * .5);
        var minimumEditorHeight = Math.Min(150, availableHeight * .4);
        var previewHeight = Math.Clamp(
            PreviewRow.ActualHeight + e.VerticalChange,
            minimumPreviewHeight,
            availableHeight - minimumEditorHeight);
        ApplyDocumentSplit(previewHeight / availableHeight);
    }

    private void EditorPreviewDivider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _uiLayoutSettingsService.Save(_uiLayoutSettings);
    }

    private void ApplyDocumentSplit(double previewRatio)
    {
        previewRatio = Math.Clamp(previewRatio, .3, .85);
        PreviewRow.Height = new GridLength(previewRatio, GridUnitType.Star);
        EditorRow.Height = new GridLength(1 - previewRatio, GridUnitType.Star);
        _uiLayoutSettings = _uiLayoutSettings with { PreviewRatio = previewRatio };
    }

    private void ExplorerCollapse_Click(object sender, RoutedEventArgs e) => SetExplorerCollapsed(true);

    private void ExplorerOpen_Click(object sender, RoutedEventArgs e) => SetExplorerCollapsed(false);

    private void ExplorerToggle_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetExplorerCollapsed(!_uiLayoutSettings.ExplorerCollapsed);
    }

    private void ExplorerDivider_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_uiLayoutSettings.ExplorerCollapsed) return;
        var availableMaximum = Root.ActualWidth > 0
            ? Math.Max(180, Root.ActualWidth - 480)
            : 520;
        var maximum = Math.Min(520, availableMaximum);
        var width = Math.Clamp(ExplorerColumn.ActualWidth + e.HorizontalChange, 180, maximum);
        ExplorerColumn.Width = new GridLength(width);
        _uiLayoutSettings = _uiLayoutSettings with { ExplorerWidth = width };
    }

    private void ExplorerDivider_DragCompleted(object sender, DragCompletedEventArgs e) =>
        _uiLayoutSettingsService.Save(_uiLayoutSettings);

    private void SetExplorerCollapsed(bool collapsed)
    {
        ApplyExplorerState(collapsed);
        _uiLayoutSettings = _uiLayoutSettings with { ExplorerCollapsed = collapsed };
        _uiLayoutSettingsService.Save(_uiLayoutSettings);
    }

    private void ApplyExplorerState(bool collapsed)
    {
        ExplorerPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ExplorerResizeDivider.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ExplorerColumn.Width = collapsed ? new GridLength(0) : new GridLength(_uiLayoutSettings.ExplorerWidth);
        ExplorerDividerColumn.Width = collapsed ? new GridLength(0) : new GridLength(8);
        ExplorerOpenButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        DocumentPanel.Padding = new Thickness(28, 18, 28, 12);
    }

    private async void MarkdownPreview_Loaded(object sender, RoutedEventArgs e)
    {
        if (_previewReady || _previewInitializing) return;
        _previewInitializing = true;
        try
        {
            await MarkdownPreview.EnsureCoreWebView2Async();
            MarkdownPreview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                MarkdownPreviewRenderer.VaultAssetHostName,
                _workspace.RootPath,
                CoreWebView2HostResourceAccessKind.Allow);
            var mathAssetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "KaTeX");
            if (Directory.Exists(mathAssetsPath))
            {
                MarkdownPreview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "node-assets.local",
                    mathAssetsPath,
                    CoreWebView2HostResourceAccessKind.Allow);
            }
            MarkdownPreview.CoreWebView2.WebMessageReceived += MarkdownPreview_WebMessageReceived;
            _previewHtmlUpdater = new WebViewHtmlUpdater(MarkdownPreview);
            _previewReady = true;
            UpdateMarkdownPreview();
        }
        catch (Exception exception)
        {
            await ShowMessage("미리보기를 열 수 없음", $"WebView2 마크다운 미리보기를 초기화하지 못했습니다.\n\n{exception.Message}");
        }
        finally
        {
            _previewInitializing = false;
        }
    }

    private void UpdateMarkdownPreview()
    {
        if (!_previewReady) return;
        ClearPreviewHoverSelection();
        _previewEditSession = _selected is { IsReadOnly: false } ? new PreviewEditSession(Editor.Text) : null;
        _previewHtmlUpdater?.Update(MarkdownPreviewRenderer.Render(
            Editor.Text,
            _workspace.RootPath,
            ResolveNoteBody,
            CurrentFoldStates(),
            CurrentPreviewScrollY(),
            _uiLayoutSettings.FontScale,
            CurrentAccent.CssColor,
            CurrentSurface.Key,
            _previewEditSession));
    }

    private Dictionary<string, bool> CurrentFoldStates()
    {
        if (_selected is null) return [];
        if (!_noteFoldStates.TryGetValue(_selected.Path, out var states))
        {
            states = new Dictionary<string, bool>(StringComparer.Ordinal);
            _noteFoldStates[_selected.Path] = states;
        }
        return states;
    }

    private double CurrentPreviewScrollY()
        => _selected is not null && _notePreviewScrollPositions.TryGetValue(_selected.Path, out var scrollY)
            ? scrollY
            : 0;

    private void MarkdownPreview_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            var messageType = type.GetString();
            if (messageType == "inline-edit")
            {
                if (_selected is not { IsReadOnly: false } || _previewEditSession is null) return;
                ClearPreviewHoverSelection();
                if (_previewEditSession.TryApply(Editor.Text, root, out var updated))
                {
                    Editor.Text = updated;
                    SaveEditor();
                }
                else
                {
                    UpdateMarkdownPreview();
                    _ = ShowMessage("텍스트 수정 미반영", "원문이 변경되었거나 지원하지 않는 텍스트입니다. 최신 미리보기에서 다시 수정해주세요.");
                }
            }
            else if (messageType is "workspace-mode-toggle" or "workspace-mode-document")
            {
                ActivatePrimaryDocument();
                HandleWorkspaceModeMessage(messageType);
            }
            else if (messageType == "fold-state"
                && root.TryGetProperty("key", out var keyElement)
                && root.TryGetProperty("open", out var openElement)
                && keyElement.GetString() is { Length: > 0 } key
                && openElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                CurrentFoldStates()[key] = openElement.GetBoolean();
            }
            else if (messageType == "preview-scroll"
                && _selected is not null
                && root.TryGetProperty("y", out var yElement)
                && yElement.TryGetDouble(out var scrollY)
                && double.IsFinite(scrollY)
                && scrollY >= 0)
            {
                _notePreviewScrollPositions[_selected.Path] = scrollY;
                if (root.TryGetProperty("maxY", out var maxYElement)
                    && maxYElement.TryGetDouble(out var maximumScrollY)
                    && double.IsFinite(maximumScrollY)
                    && maximumScrollY > 0)
                {
                    SyncEditorToPreview(scrollY / maximumScrollY);
                }
            }
            else if (messageType == "focus-editor")
            {
                if (_selected?.IsReadOnly == true) return;
                ClearPreviewHoverSelection(restoreOriginalSelection: false);
                var offset = root.TryGetProperty("offset", out var offsetElement) && offsetElement.TryGetInt32(out var requestedOffset)
                    ? MarkdownText.OriginalOffsetFromNormalized(Editor.Text, Math.Max(0, requestedOffset))
                    : Editor.SelectionStart;
                DispatcherQueue.TryEnqueue(() =>
                {
                    Editor.Focus(FocusState.Programmatic);
                    Editor.Select(offset, 0);
                    DispatcherQueue.TryEnqueue(() => CenterEditorOnCharacter(offset));
                });
            }
            else if (messageType == "hover-editor"
                && root.TryGetProperty("offset", out var hoverOffsetElement)
                && hoverOffsetElement.TryGetInt32(out var hoverOffset))
            {
                var hoverEndOffset = root.TryGetProperty("endOffset", out var hoverEndElement)
                    && hoverEndElement.TryGetInt32(out var requestedEndOffset)
                        ? requestedEndOffset
                        : -1;
                ShowPreviewHoverSelection(hoverOffset, hoverEndOffset);
            }
            else if (messageType == "hover-editor-clear")
            {
                ClearPreviewHoverSelection();
            }
        }
        catch { }
    }

    private void ShowPreviewHoverSelection(int normalizedStart, int normalizedEnd)
    {
        if (!_previewHoverSelectionActive)
        {
            _previewHoverOriginalStart = Editor.SelectionStart;
            _previewHoverOriginalLength = Editor.SelectionLength;
            _previewHoverSelectionActive = true;
        }

        var start = MarkdownText.OriginalOffsetFromNormalized(Editor.Text, Math.Max(0, normalizedStart));
        var end = normalizedEnd > normalizedStart
            ? MarkdownText.OriginalOffsetFromNormalized(Editor.Text, normalizedEnd)
            : Editor.Text.Length;
        start = Math.Clamp(start, 0, Editor.Text.Length);
        end = Math.Clamp(end, start, Editor.Text.Length);
        while (end > start && Editor.Text[end - 1] is '\r' or '\n') end--;
        SelectEditorRangeWithoutScrolling(start, Math.Max(0, end - start));
    }

    private void ClearPreviewHoverSelection(bool restoreOriginalSelection = true)
    {
        if (!_previewHoverSelectionActive) return;
        _previewHoverSelectionActive = false;
        if (restoreOriginalSelection)
        {
            var start = Math.Clamp(_previewHoverOriginalStart, 0, Editor.Text.Length);
            var length = Math.Clamp(_previewHoverOriginalLength, 0, Editor.Text.Length - start);
            SelectEditorRangeWithoutScrolling(start, length);
        }
        else
        {
            _previewHoverSelectionRevision++;
        }
    }

    private void SelectEditorRangeWithoutScrolling(int start, int length)
    {
        var scrollViewer = EditorScrollViewer();
        var verticalOffset = scrollViewer?.VerticalOffset;
        Editor.Select(start, length);
        if (scrollViewer is null || verticalOffset is null) return;

        var revision = ++_previewHoverSelectionRevision;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (revision == _previewHoverSelectionRevision)
                scrollViewer.ChangeView(null, verticalOffset.Value, null, true);
        });
    }

    private void CenterEditorOnCharacter(int offset)
    {
        var scrollViewer = EditorScrollViewer();
        if (scrollViewer is null || scrollViewer.ViewportHeight <= 0) return;

        var characterBounds = Editor.GetRectFromCharacterIndex(Math.Clamp(offset, 0, Editor.Text.Length), false);
        var centeredOffset = scrollViewer.VerticalOffset
            + characterBounds.Y
            + characterBounds.Height / 2
            - scrollViewer.ViewportHeight / 2;
        scrollViewer.ChangeView(
            null,
            Math.Clamp(centeredOffset, 0, scrollViewer.ScrollableHeight),
            null,
            true);
    }

    private void SyncEditorToPreview(double progress)
    {
        var scrollViewer = EditorScrollViewer();
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0) return;

        var targetOffset = scrollViewer.ScrollableHeight * Math.Clamp(progress, 0, 1);
        if (Math.Abs(scrollViewer.VerticalOffset - targetOffset) < 1) return;
        scrollViewer.ChangeView(null, targetOffset, null, true);
    }

    private ScrollViewer? EditorScrollViewer()
        => _editorScrollViewer ??= FindVisualDescendant<ScrollViewer>(Editor);

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

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child)) yield return descendant;
        }
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

    private NoteInfo? FindNoteByTitle(string title) =>
        _notes.FirstOrDefault(note => note.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
        ?? _guideService.FindByTitle(title);

    private string? ResolveNoteBody(string title) => FindNoteByTitle(title)?.Body;

    private void MarkdownPreview_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme.Equals("node-note", StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
            var title = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            var note = FindNoteByTitle(title);
            if (note is not null) Select(note);
        }
        else if (uri.Scheme is "http" or "https")
        {
            args.Cancel = true;
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
    }
    private void Search_TextChanged(object sender, TextChangedEventArgs e) { if (!_loading) ApplySearch(); }
    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var pdfPath = _pdfMode ? _currentPdfPath : null;
        var selectedPath = _selected?.Path;
        SaveCurrent();
        RefreshNotes();
        if (pdfPath is not null && _pdfDocuments.Any(pdf => pdf.Path.Equals(pdfPath, StringComparison.OrdinalIgnoreCase)))
        {
            _ = ShowPdfModeAsync(pdfPath);
            return;
        }
        var selected = _notes.FirstOrDefault(note => note.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) Select(selected);
        else if (_notes.Count > 0) Select(_notes[0]);
    }
    private async void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _suppressNoteListSelectionChanged) return;
        if (NoteList.SelectedItem is not VaultItem item) return;
        if (item.Note is NoteInfo note) SelectNoteInActiveDocument(note);
        else if (item.IsPdf) await ShowPdfModeAsync(item.Path);
    }
    private void BacklinkList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BacklinkList.SelectedItem is NoteInfo note
            && note.Path != GraphSelectedNote?.Path)
            SelectGraphNote(note);
    }
    private void UpdateBacklinks(NoteInfo? selectedNote = null)
    {
        var selected = selectedNote ?? _selected;
        if (selected is null) { BacklinkTitle.Text = "이 노트를 언급한 노트"; BacklinkList.ItemsSource = Array.Empty<NoteInfo>(); return; }
        var backlinks = _notes.Where(note => _noteLinks.TryGetValue(note.Title, out var targets) && targets.Contains(selected.Title, StringComparer.OrdinalIgnoreCase)).ToList();
        BacklinkTitle.Text = $"이 노트를 언급한 노트 ({backlinks.Count})";
        BacklinkList.ItemsSource = backlinks;
    }

    private async Task ShowMessage(string title, string content) => await new ContentDialog { Title = title, Content = content, CloseButtonText = "확인", XamlRoot = Root.XamlRoot }.ShowAsync();
}
