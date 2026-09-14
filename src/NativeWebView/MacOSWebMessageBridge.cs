using System.Text.Json;

namespace NativeWebView.Controls;

internal static class MacOSWebMessageBridge
{
    internal const string Bootstrap = """
        (() => {
          const events = new EventTarget();
          globalThis.chrome ??= {};
          globalThis.chrome.webview = {
            addEventListener: events.addEventListener.bind(events),
            removeEventListener: events.removeEventListener.bind(events),
            postMessage(value) {
              const kind = typeof value === 'string' ? 'string' : 'json';
              const payload = kind === 'string' ? value : (JSON.stringify(value) ?? 'null');
              globalThis.webkit.messageHandlers.nativeWebViewMessage.postMessage(
                JSON.stringify({ nativeWebViewVersion: 1, kind, payload }));
            }
          };
          globalThis.__nativeWebViewDispatchMessage = data =>
            events.dispatchEvent(new MessageEvent('message', { data }));
        })();
        """;

    internal static string CreateDispatchScript(string message, bool isJson)
    {
        ArgumentNullException.ThrowIfNull(message);
        string payload;
        if (isJson)
        {
            using var document = JsonDocument.Parse(message);
            // Decode data as JSON, not as an object literal (where __proto__ has special meaning).
            payload = "JSON.parse(" + JsonSerializer.Serialize(message) + ")";
        }
        else
            payload = JsonSerializer.Serialize(message);
        return "globalThis.__nativeWebViewDispatchMessage(" + payload + ");";
    }
}
