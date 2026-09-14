using NativeWebView.Core;

namespace NativeWebView.Controls;

internal static class NavigationPolicyDecision
{
    internal static bool IsSuperseded(int expectedVersion, int currentVersion) => expectedVersion != currentVersion;

    internal static bool CancelPending(Uri? canceledUri, int expectedVersion, ref int pendingVersion, ref Uri? pendingUri)
    {
        if (expectedVersion != pendingVersion || canceledUri is null || pendingUri is null ||
            !string.Equals(canceledUri.AbsoluteUri, pendingUri.AbsoluteUri, StringComparison.Ordinal))
            return false;
        pendingUri = null;
        pendingVersion++;
        return true;
    }

    // Both WebKit policy signatures use this boundary so resolution failures still complete exactly once.
    internal static void Complete(Func<nint> resolve, Action<nint> decide)
    {
        nint policy = 0;
        try { policy = resolve(); }
        catch { /* Cancel if native state or subscriber code cannot be evaluated. */ }
        decide(policy);
    }

    // A reverse native callback must always produce a decision, including when a subscriber fails.
    internal static bool IsAllowed(Uri? uri, bool disposed, Action<NativeWebViewNavigationStartedEventArgs> notify,
        bool isMainFrame = true)
    {
        if (disposed)
            return false;
        // Frame loads are not page navigation; popups use the separate new-window policy.
        if (!isMainFrame)
            return true;
        if (uri is null)
            return false;
        var args = new NativeWebViewNavigationStartedEventArgs(uri, isRedirected: false);
        try
        {
            notify(args);
            return !args.Cancel;
        }
        catch
        {
            return false;
        }
    }
}
