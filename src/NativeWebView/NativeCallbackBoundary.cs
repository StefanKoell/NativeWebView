namespace NativeWebView.Controls;

internal static class NativeCallbackBoundary
{
    internal static void Invoke(Action callback, Action<Exception> report)
    {
        try { callback(); }
        catch (Exception exception)
        {
            try { report(exception); }
            catch { /* Diagnostic subscribers must not escape a reverse native callback either. */ }
        }
    }
}
