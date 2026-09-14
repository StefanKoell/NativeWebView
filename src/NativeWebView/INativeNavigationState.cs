namespace NativeWebView.Controls;

// State belongs to the retained native browser, not its replaceable presenter.
internal interface INativeNavigationState
{
    Uri? CurrentUrl { get; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }
}
