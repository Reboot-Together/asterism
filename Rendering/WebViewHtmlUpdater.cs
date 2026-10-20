using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace AsterismApp;

/// <summary>
/// Loads the first document normally, then replaces it in the existing WebView document.
/// This keeps the compositor surface alive so preview refreshes do not flash between pages.
/// </summary>
internal sealed class WebViewHtmlUpdater
{
    private readonly WebView2 _view;
    private string? _pendingHtml;
    private bool _documentReady;
    private bool _navigationPending;
    private bool _replacementRunning;

    public WebViewHtmlUpdater(WebView2 view)
    {
        _view = view;
        _view.NavigationCompleted += NavigationCompleted;
    }

    public void Update(string html)
    {
        _pendingHtml = html;
        if (_documentReady)
        {
            _ = FlushReplacementAsync();
            return;
        }

        if (_navigationPending) return;
        NavigatePendingDocument();
    }

    private void NavigatePendingDocument()
    {
        if (_pendingHtml is null) return;
        var html = _pendingHtml;
        // NavigateToString has a 2 MB limit; notebook plots can exceed it.
        // Initialize a small document, then write the complete page through JS.
        _pendingHtml = html.Length > 1_000_000 ? html : null;
        _navigationPending = true;
        _view.NavigateToString(_pendingHtml is null ? html : "<!doctype html><html><head><meta charset=\"utf-8\"></head><body></body></html>");
    }

    private void NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        // Link clicks are handled and cancelled by the host; they must not invalidate
        // the already loaded preview document.
        if (!_navigationPending) return;
        _navigationPending = false;
        _documentReady = args.IsSuccess;
        if (_documentReady)
            _ = FlushReplacementAsync();
        else
            NavigatePendingDocument();
    }

    private async Task FlushReplacementAsync()
    {
        if (_replacementRunning || !_documentReady) return;
        _replacementRunning = true;
        try
        {
            while (_documentReady && _pendingHtml is { } html)
            {
                _pendingHtml = null;
                try
                {
                    var encodedHtml = JsonSerializer.Serialize(html);
                    await _view.CoreWebView2.ExecuteScriptAsync(
                        $"document.open();document.write({encodedHtml});document.close();");
                }
                catch
                {
                    // Recover with a normal navigation if the current script context disappeared.
                    _documentReady = false;
                    _pendingHtml = html;
                    NavigatePendingDocument();
                }
            }
        }
        finally
        {
            _replacementRunning = false;
            if (_documentReady && _pendingHtml is not null)
                _ = FlushReplacementAsync();
        }
    }
}
