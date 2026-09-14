using NativeWebView.Core;

namespace NativeWebView.Platform.macOS;

internal static class MacOSPlatformFeatures
{
    private const NativeWebViewFeature BaseFeatures =
        NativeWebViewFeature.EmbeddedView |
        NativeWebViewFeature.Dialog |
        NativeWebViewFeature.AuthenticationBroker |
        NativeWebViewFeature.ContextMenu |
        NativeWebViewFeature.ZoomControl |
        NativeWebViewFeature.Printing |
        NativeWebViewFeature.PrintUi |
        NativeWebViewFeature.WebResourceRequestInterception |
        NativeWebViewFeature.NewWindowRequestInterception |
        NativeWebViewFeature.EnvironmentOptions |
        NativeWebViewFeature.ControllerOptions |
        NativeWebViewFeature.NativePlatformHandle |
        NativeWebViewFeature.CookieManager |
        NativeWebViewFeature.CommandManager |
        NativeWebViewFeature.ScriptExecution |
        NativeWebViewFeature.WebMessageChannel |
        NativeWebViewFeature.GpuSurfaceRendering |
        NativeWebViewFeature.OffscreenRendering |
        NativeWebViewFeature.RenderFrameCapture |
        NativeWebViewFeature.EmbeddedSnapshotCapture |
        NativeWebViewFeature.Favicon |
        NativeWebViewFeature.Downloads;

    public static IWebViewPlatformFeatures EmbeddedInstance => Create(
        BaseFeatures |
        NativeWebViewFeature.NavigationCancellation |
        NativeWebViewFeature.DocumentStartScriptInjection |
        NativeWebViewFeature.ZoomFactorChangeNotification);

    public static IWebViewPlatformFeatures DialogInstance => Create(BaseFeatures);

    public static IWebViewPlatformFeatures AuthenticationInstance => Create(BaseFeatures);

    private static IWebViewPlatformFeatures Create(NativeWebViewFeature features) => new WebViewPlatformFeatures(
        NativeWebViewPlatform.MacOS,
        features |
        (OperatingSystem.IsMacOSVersionAtLeast(14)
            ? NativeWebViewFeature.ProxyConfiguration
            : NativeWebViewFeature.None));
}
