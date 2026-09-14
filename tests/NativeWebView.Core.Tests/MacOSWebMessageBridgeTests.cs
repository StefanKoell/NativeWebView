using System.Text.Json;
using NativeWebView.Controls;
using NativeWebView.Platform.macOS;

namespace NativeWebView.Core.Tests;

public class MacOSWebMessageBridgeTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  password ' \" \\ \n雪 </script>  ")]
    [InlineData("');globalThis.unexpected=true;//")]
    public void StringPayloadIsEncodedAsOneLiteral(string value)
    {
        var script = MacOSWebMessageBridge.CreateDispatchScript(value, false);
        var argument = script["globalThis.__nativeWebViewDispatchMessage(".Length..^2];
        Assert.Equal(value, JsonSerializer.Deserialize<string>(argument));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{\"__proto__\":{\"marker\":true},\"nested\":{\"__proto__\":null}}")]
    [InlineData("{\"value\":\"');globalThis.unexpected=true;//\"}")]
    [InlineData("[1,\"two\"]")]
    [InlineData("{\"password\":\"  雪 \\\" \\n  \"}")]
    public void JsonPayloadRetainsItsTypeAndValue(string json)
    {
        var script = MacOSWebMessageBridge.CreateDispatchScript(json, true);
        using var expected = JsonDocument.Parse(json);
        const string prefix = "globalThis.__nativeWebViewDispatchMessage(JSON.parse(";
        Assert.StartsWith(prefix, script);
        Assert.EndsWith("));", script);
        var decoded = JsonSerializer.Deserialize<string>(script[prefix.Length..^3]);
        Assert.Equal(json, decoded);
        using var actual = JsonDocument.Parse(decoded!);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
    }

    [Fact]
    public void InvalidJsonIsRejected()
    {
        Assert.ThrowsAny<JsonException>(() => MacOSWebMessageBridge.CreateDispatchScript("{};alert(1)", true));
    }

    [Fact]
    public async Task UnattachedMacOSMessagesNeverEchoIntoManagedReceivers()
    {
        using var instance = new NativeWebViewInstance(new MacOSNativeWebViewBackend());
        using var presenter = new NativeWebView.Controls.NativeWebView(instance);
        var received = 0;
        presenter.WebMessageReceived += (_, _) => received++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.PostWebMessageAsJsonAsync("{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.PostWebMessageAsStringAsync("message"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => presenter.PostWebMessageAsJsonAsync("{}", new CancellationToken(true)));
        presenter.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => presenter.PostWebMessageAsStringAsync("message"));
        Assert.Equal(0, received);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContextMenuPreferenceSurvivesPresenterReplacement(bool enabled)
    {
        using var instance = new NativeWebViewInstance(new MacOSNativeWebViewBackend());
        using (var presenter = new NativeWebView.Controls.NativeWebView(instance))
            presenter.IsContextMenuEnabled = enabled;
        using var replacement = new NativeWebView.Controls.NativeWebView(instance);
        Assert.Equal(enabled, replacement.IsContextMenuEnabled);
    }
}
