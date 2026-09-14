using NativeWebView.Core;

namespace NativeWebView.Controls;

public sealed class NativeWebViewInstance : IDisposable
{
    internal const string ConstructionCleanupExceptionDataKey =
        "NativeWebView.InstanceConstructionCleanupException";

    private bool _isDisposed;
    private bool _isConfigurationCommitted;
    private NativeWebViewInstanceConfiguration? _configurationBeforeScriptMutation;

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
        InstanceConfiguration = new NativeWebViewInstanceConfiguration();
        try
        {
            ApplyInstanceConfigurationCore(instanceConfiguration ?? InstanceConfiguration, validateLifecycle: false);
        }
        catch (Exception constructionException)
        {
            try
            {
                Controller.Dispose();
            }
            catch (Exception cleanupException)
            {
                constructionException.Data[ConstructionCleanupExceptionDataKey] = cleanupException;
            }

            throw;
        }
    }

    internal NativeWebViewController Controller { get; }

    internal NativeWebViewInstanceConfiguration InstanceConfiguration { get; private set; }

    internal MacOSNativeWebViewHost? MacOSHost { get; set; }

    internal INativeNavigationState? NativeNavigationState { get; set; }

    private bool? _finalizedMacOSJavaScriptEnabled;

    internal NativeWebViewInstanceConfiguration GetMacOSHostConfiguration()
    {
        var configuration = InstanceConfiguration.Clone();
        if (_finalizedMacOSJavaScriptEnabled is { } enabled)
            configuration.ControllerOptions.IsJavaScriptEnabled = enabled;
        return configuration;
    }

    internal void ApplyFinalizedMacOSJavaScriptPolicy(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _finalizedMacOSJavaScriptEnabled = enabled;
        MacOSHost?.SetPageJavaScriptEnabled(enabled);
    }

    internal long ActivePresenterId
    {
        get => Interlocked.Read(ref field);
        set => Interlocked.Exchange(ref field, value);
    }

    public bool IsDisposed => _isDisposed;

    public NativeWebViewPlatform Platform => Controller.Platform;

    public IWebViewPlatformFeatures Features => Controller.Features;

    public NativeWebComponentState LifecycleState => Controller.State;

    public Uri? CurrentUrl => NativeNavigationState is { } native ? native.CurrentUrl : Controller.CurrentUrl;

    internal bool CanGoBack => NativeNavigationState?.CanGoBack ?? Controller.CanGoBack;

    internal bool CanGoForward => NativeNavigationState?.CanGoForward ?? Controller.CanGoForward;

    public bool IsInitialized => Controller.IsInitialized;

    /// <summary>
    /// Applies a cloned instance configuration to the native backend before initialization or navigation begins.
    /// </summary>
    /// <param name="instanceConfiguration">The configuration to clone and apply.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when native host creation, initialization, or navigation has already started.
    /// </exception>
    public void ApplyInstanceConfiguration(NativeWebViewInstanceConfiguration instanceConfiguration)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(instanceConfiguration);
        ApplyInstanceConfigurationCore(instanceConfiguration, validateLifecycle: true);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        DetachConfigurationEvents(InstanceConfiguration);
        _configurationBeforeScriptMutation = null;
        AttachDisposedConfigurationGuard(InstanceConfiguration);
        MacOSHost?.Dispose();
        MacOSHost = null;
        NativeNavigationState = null;
        Controller.Dispose();
    }

    private static INativeWebViewBackend CreateDefaultBackend()
    {
        NativeWebViewRuntime.EnsureCurrentPlatformRegistered();
        NativeWebViewRuntime.Factory.TryCreateNativeWebViewBackend(NativeWebViewRuntime.CurrentPlatform, out var backend);
        return backend;
    }

    private void ApplyInstanceConfigurationCore(
        NativeWebViewInstanceConfiguration instanceConfiguration,
        bool validateLifecycle)
    {
        if (validateLifecycle)
            ValidateConfigurationCanChange();

        var clone = instanceConfiguration.Clone();
        ApplyConfigurationToBackend(clone, InstanceConfiguration);

        DetachConfigurationEvents(InstanceConfiguration);
        InstanceConfiguration = clone;
        AttachConfigurationEvents(InstanceConfiguration);
    }

    internal void CommitInstanceConfiguration()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _isConfigurationCommitted = true;
    }

    private void AttachConfigurationEvents(NativeWebViewInstanceConfiguration configuration)
    {
        configuration.DocumentStartScriptsChanging += OnDocumentStartScriptsChanging;
        configuration.DocumentStartScriptsChanged += OnDocumentStartScriptsChanged;
    }

    private void DetachConfigurationEvents(NativeWebViewInstanceConfiguration configuration)
    {
        configuration.DocumentStartScriptsChanging -= OnDocumentStartScriptsChanging;
        configuration.DocumentStartScriptsChanged -= OnDocumentStartScriptsChanged;
    }

    private static void AttachDisposedConfigurationGuard(NativeWebViewInstanceConfiguration configuration)
    {
        configuration.DocumentStartScriptsChanging += ThrowDisposedConfigurationMutation;
    }

    private static void ThrowDisposedConfigurationMutation()
    {
        throw new ObjectDisposedException(nameof(NativeWebViewInstance));
    }

    private void OnDocumentStartScriptsChanging()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ValidateConfigurationCanChange();
        _configurationBeforeScriptMutation = InstanceConfiguration.Clone();
    }

    private void OnDocumentStartScriptsChanged()
    {
        var previous = _configurationBeforeScriptMutation ?? InstanceConfiguration.Clone();
        try
        {
            ApplyConfigurationToBackend(InstanceConfiguration.Clone(), previous);
        }
        finally
        {
            _configurationBeforeScriptMutation = null;
        }
    }

    private void ApplyConfigurationToBackend(
        NativeWebViewInstanceConfiguration configuration,
        NativeWebViewInstanceConfiguration? rollbackConfiguration)
    {
        if (!Controller.TryGetBackend<INativeWebViewInstanceConfigurationTarget>(out var target))
            return;

        try
        {
            target.ApplyInstanceConfiguration(configuration);
        }
        catch (Exception applicationException)
        {
            if (rollbackConfiguration is not null)
            {
                try
                {
                    target.ApplyInstanceConfiguration(rollbackConfiguration.Clone());
                }
                catch (Exception rollbackException)
                {
                    applicationException.Data["NativeWebView.InstanceConfigurationRollbackException"] = rollbackException;
                }
            }

            throw;
        }
    }

    private void ValidateConfigurationCanChange()
    {
        if (_isConfigurationCommitted ||
            LifecycleState != NativeWebComponentState.Created ||
            CurrentUrl is not null)
        {
            throw new InvalidOperationException(
                "The native WebView instance configuration cannot change after native host creation, initialization, or navigation begins.");
        }
    }
}
