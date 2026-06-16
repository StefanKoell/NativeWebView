using NativeWebView.Core;

namespace NativeWebView.Controls;

public sealed class NativeWebViewInstance : IDisposable
{
    private bool _isDisposed;

    public NativeWebViewInstance() : this(CreateDefaultBackend(), instanceConfiguration: null)
    {
    }

    public NativeWebViewInstance(NativeWebViewInstanceConfiguration? instanceConfiguration) : this(CreateDefaultBackend(), instanceConfiguration)
    {
    }

    public NativeWebViewInstance(INativeWebViewBackend backend, NativeWebViewInstanceConfiguration? instanceConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        Controller = new NativeWebViewController(backend);
        InstanceConfiguration = instanceConfiguration?.Clone() ?? new NativeWebViewInstanceConfiguration();
    }

    internal NativeWebViewController Controller { get; }

    internal NativeWebViewInstanceConfiguration InstanceConfiguration { get; private set; }

    internal MacOSNativeWebViewHost? MacOSHost { get; set; }

    internal long ActivePresenterId
    {
        get => Interlocked.Read(ref field);
        set => Interlocked.Exchange(ref field, value);
    }

    public bool IsDisposed => _isDisposed;

    public NativeWebViewPlatform Platform => Controller.Platform;

    public IWebViewPlatformFeatures Features => Controller.Features;

    public NativeWebComponentState LifecycleState => Controller.State;

    public Uri? CurrentUrl => Controller.CurrentUrl;

    public bool IsInitialized => Controller.IsInitialized;

    public void ApplyInstanceConfiguration(NativeWebViewInstanceConfiguration instanceConfiguration)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(instanceConfiguration);
        InstanceConfiguration = instanceConfiguration.Clone();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        MacOSHost?.Dispose();
        MacOSHost = null;
        Controller.Dispose();
    }

    private static INativeWebViewBackend CreateDefaultBackend()
    {
        NativeWebViewRuntime.EnsureCurrentPlatformRegistered();
        NativeWebViewRuntime.Factory.TryCreateNativeWebViewBackend(NativeWebViewRuntime.CurrentPlatform, out var backend);
        return backend;
    }
}
