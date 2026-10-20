using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AsterismApp;

public sealed partial class MainWindow
{
    private readonly SemanticLinkService _semanticLinkService = new();
    private IReadOnlyDictionary<string, List<SemanticSuggestion>> _semanticSuggestions =
        new Dictionary<string, List<SemanticSuggestion>>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, List<string>> _semanticLinks =
        new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _semanticIndexCancellation;
    private int _semanticIndexGeneration;
    private bool _semanticIndexBusy;
    private string? _semanticIndexError;

    private void QueueSemanticRefresh()
    {
        if (SemanticStatusText is null) return;
        _semanticIndexCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _semanticIndexCancellation = cancellation;
        var generation = ++_semanticIndexGeneration;
        var notes = _notes.ToList();
        var workspace = _workspace.RootPath;
        _semanticIndexBusy = true;
        _semanticIndexError = null;
        UpdateSemanticSuggestions();
        _ = RefreshSemanticLinksAsync(generation, workspace, notes, cancellation);
    }

    private async Task RefreshSemanticLinksAsync(
        int generation,
        string workspace,
        IReadOnlyList<NoteInfo> notes,
        CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);
            var result = await _semanticLinkService.BuildAsync(workspace, notes, cancellationToken);
            if (generation != _semanticIndexGeneration || cancellationToken.IsCancellationRequested) return;
            _semanticSuggestions = result.SuggestionsByPath;
            _semanticLinks = result.GraphLinks;
            _semanticIndexBusy = false;
            _semanticIndexError = null;
            UpdateSemanticSuggestions();
            DrawGraph();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (generation != _semanticIndexGeneration) return;
            _semanticIndexBusy = false;
            _semanticIndexError = exception.Message;
            _semanticSuggestions = new Dictionary<string, List<SemanticSuggestion>>(StringComparer.OrdinalIgnoreCase);
            _semanticLinks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            UpdateSemanticSuggestions();
            DrawGraph();
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_semanticIndexCancellation, cancellation))
                _semanticIndexCancellation = null;
        }
    }

    private void UpdateSemanticSuggestions()
    {
        if (SemanticSuggestionList is null || SemanticStatusText is null) return;
        if (_semanticIndexBusy)
        {
            SemanticStatusText.Text = "분석 중";
            return;
        }
        if (_semanticIndexError is not null)
        {
            SemanticStatusText.Text = "사용 불가";
            ToolTipService.SetToolTip(SemanticStatusText, _semanticIndexError);
            SemanticSuggestionList.ItemsSource = Array.Empty<SemanticSuggestion>();
            return;
        }

        var suggestions = _selected is not null && _semanticSuggestions.TryGetValue(_selected.Path, out var matches)
            ? matches.Where(match => !_linkService.ExtractTargets(Editor.Text).Contains(match.Note.Title)).ToList()
            : [];
        SemanticSuggestionList.ItemsSource = suggestions;
        SemanticStatusText.Text = suggestions.Count == 0 ? "추천 없음" : $"{suggestions.Count}개";
        ToolTipService.SetToolTip(SemanticStatusText, "인터넷과 API 없이 이 PC에서 계산합니다.");
    }

    private void SemanticSuggestionOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        var note = _notes.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (note is null || note.Path == _selected?.Path) return;
        SaveCurrent();
        Select(note);
    }

    private void SemanticSuggestionLink_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selected.IsReadOnly || sender is not Button { Tag: string path }) return;
        var target = _notes.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.Path == _selected.Path || _linkService.ExtractTargets(Editor.Text).Contains(target.Title)) return;

        Editor.Text = Editor.Text.TrimEnd() + $"\n\n[[{target.Title}]]\n";
        Editor.SelectionStart = Editor.Text.Length;
        Editor.SelectionLength = 0;
        SaveCurrent();
        UpdateSemanticSuggestions();
    }

    private void StopSemanticIndexing()
    {
        _semanticIndexCancellation?.Cancel();
        _semanticIndexCancellation = null;
    }
}
