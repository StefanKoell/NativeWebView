using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using NativeWebView.Core;
using NativeWebView.Interop;

namespace NativeWebView.Controls;

public class NativeWebView : NativeControlHost, IDisposable
{
    private const int MinRenderFramesPerSecond = 1;
    private const int MaxRenderFramesPerSecond = 60;
    private const int DefaultRenderFramesPerSecond = 60;

    private static readonly SolidColorBrush RenderBackgroundBrush = new(Color.FromRgb(15, 23, 42));
    private static readonly SolidColorBrush RenderOutlineBrush = new(Color.FromArgb(180, 148, 163, 184));
    private static readonly SolidColorBrush RenderTextBrush = new(Color.FromRgb(226, 232, 240));
    private const string MacOsCompositedVideoPassthroughMessage =
        "Video host detected. Using native passthrough in composited mode to preserve hardware-accelerated playback.";
    private const string MacOsCompositedForcedPassthroughMessage =
        "Manual override active. Native passthrough is forced in composited mode.";
    private const string MacOsCompositedForcedDisabledMessage =
        "Manual override active. Native passthrough is disabled in composited mode.";
    private static readonly string[] MacOsCompositedPassthroughVideoHosts =
    [
        "youtube.com",
        "youtu.be",
        "vimeo.com",
        "twitch.tv",
        "dailymotion.com",
        "netflix.com",
    ];

    private readonly NativeWebViewInstance _instance;
    private readonly bool _ownsInstance;
    private readonly NativeWebViewController _controller;
    private readonly NativeWebViewRenderStatisticsTracker _renderStatisticsTracker = new();
    private static long s_nextPresenterId;

    private MacOSNativeWebViewHost? _macOSHost;
    private readonly long _presenterId = Interlocked.Increment(ref s_nextPresenterId);
    private DispatcherTimer? _framePump;
    private WriteableBitmap? _gpuSurfaceBitmap;
    private WriteableBitmap? _offscreenBitmap;
    private Vector _gpuSurfaceDpi = new(96, 96);
    private Vector _offscreenDpi = new(96, 96);
    private CompositionSurfaceVisual? _gpuCompositionVisual;
    private CompositionDrawingSurface? _gpuCompositionSurface;
    private ICompositionGpuInterop? _gpuCompositionInterop;
    private ICompositionImportedGpuImage? _gpuCompositionImportedImage;
    private IntPtr _gpuCompositionImportedHandle;
    private string? _gpuCompositionImportedHandleType;
    private int _gpuCompositionImportedWidth;
    private int _gpuCompositionImportedHeight;
    private long _gpuCompositionUpdatedFrameId;

    private bool _isAttached;
    private bool _frameCaptureInProgress;
    private bool _gpuCompositionUpdateInProgress;
    private bool _isUsingSyntheticFrameSource;
    private bool _isMacOsCompositedPassthroughActive;
    private bool? _macOsCompositedPassthroughOverride;
    private DateTimeOffset _suppressEmptyResizeFramesUntilUtc;
    private string? _renderDiagnosticsMessage;
    private string? _gpuInteropDiagnosticsMessage;

    public static readonly StyledProperty<NativeWebViewRenderMode> RenderModeProperty =
        AvaloniaProperty.Register<NativeWebView, NativeWebViewRenderMode>(nameof(RenderMode), NativeWebViewRenderMode.Embedded);

    public static readonly StyledProperty<int> RenderFramesPerSecondProperty =
        AvaloniaProperty.Register<NativeWebView, int>(nameof(RenderFramesPerSecond), DefaultRenderFramesPerSecond);

    public static readonly StyledProperty<bool> EnableExperimentalGpuInteropProperty =
        AvaloniaProperty.Register<NativeWebView, bool>(nameof(EnableExperimentalGpuInterop));

    public NativeWebView()
        : this(new NativeWebViewInstance(), ownsInstance: true)
    {
    }

    public NativeWebView(NativeWebViewInstanceConfiguration instanceConfiguration)
        : this(new NativeWebViewInstance(instanceConfiguration), ownsInstance: true)
    {
    }

    public NativeWebView(INativeWebViewBackend backend)
        : this(new NativeWebViewInstance(backend), ownsInstance: true)
    {
    }

    public NativeWebView(INativeWebViewBackend backend, NativeWebViewInstanceConfiguration? instanceConfiguration)
        : this(new NativeWebViewInstance(backend, instanceConfiguration), ownsInstance: true)
    {
    }

    public NativeWebView(NativeWebViewInstance instance)
        : this(instance, ownsInstance: false)
    {
    }

    private NativeWebView(NativeWebViewInstance instance, bool ownsInstance)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _ownsInstance = ownsInstance;
        _controller = _instance.Controller;
        _macOSHost = _instance.MacOSHost;
        _controller.CoreWebView2EnvironmentRequested += OnCoreWebView2EnvironmentRequestedInternal;
        _controller.CoreWebView2ControllerOptionsRequested += OnCoreWebView2ControllerOptionsRequestedInternal;
        ApplyInstanceConfigurationToBackend();
    }

    public NativeWebViewPlatform Platform => _controller.Platform;

    public IWebViewPlatformFeatures Features => _controller.Features;

    public NativeWebComponentState LifecycleState => _controller.State;

    public NativeWebViewInstanceConfiguration InstanceConfiguration
    {
        get => _instance.InstanceConfiguration;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _instance.ApplyInstanceConfiguration(value);
            ApplyInstanceConfigurationToBackend();
        }
    }

    public NativeWebViewRenderMode RenderMode
    {
        get => GetValue(RenderModeProperty);
        set => SetValue(RenderModeProperty, value);
    }

    public int RenderFramesPerSecond
    {
        get => GetValue(RenderFramesPerSecondProperty);
        set => SetValue(RenderFramesPerSecondProperty, value);
    }

    public bool EnableExperimentalGpuInterop
    {
        get => GetValue(EnableExperimentalGpuInteropProperty);
        set => SetValue(EnableExperimentalGpuInteropProperty, value);
    }

    public bool IsUsingSyntheticFrameSource => _isUsingSyntheticFrameSource;

    public string? RenderDiagnosticsMessage => _gpuInteropDiagnosticsMessage ?? _renderDiagnosticsMessage;

    public NativeWebViewRenderStatistics RenderStatistics => _renderStatisticsTracker.CreateSnapshot();

    public bool? MacOsCompositedPassthroughOverride => _macOsCompositedPassthroughOverride;

    public Uri? Source
    {
        get => _controller.CurrentUrl;
        set
        {
            if (value is not null)
            {
                Navigate(value);
            }
        }
    }

    public Uri? CurrentUrl => _controller.CurrentUrl;

    public new bool IsInitialized => _controller.IsInitialized;

    public bool CanGoBack => _controller.CanGoBack;

    public bool CanGoForward => _controller.CanGoForward;

    public bool IsDevToolsEnabled
    {
        get => _controller.IsDevToolsEnabled;
        set => _controller.IsDevToolsEnabled = value;
    }

    public bool IsContextMenuEnabled
    {
        get => _controller.IsContextMenuEnabled;
        set => _controller.IsContextMenuEnabled = value;
    }

    public bool IsStatusBarEnabled
    {
        get => _controller.IsStatusBarEnabled;
        set => _controller.IsStatusBarEnabled = value;
    }

    public bool IsZoomControlEnabled
    {
        get => _controller.IsZoomControlEnabled;
        set => _controller.IsZoomControlEnabled = value;
    }

    public double ZoomFactor => _controller.ZoomFactor;

    public string? HeaderString => _controller.HeaderString;

    public string? UserAgentString => _controller.UserAgentString;

    public event EventHandler<CoreWebViewInitializedEventArgs>? CoreWebView2Initialized
    {
        add => _controller.CoreWebView2Initialized += value;
        remove => _controller.CoreWebView2Initialized -= value;
    }

    public event EventHandler<NativeWebViewNavigationStartedEventArgs>? NavigationStarted
    {
        add => _controller.NavigationStarted += value;
        remove => _controller.NavigationStarted -= value;
    }

    public event EventHandler<NativeWebViewNavigationCompletedEventArgs>? NavigationCompleted
    {
        add => _controller.NavigationCompleted += value;
        remove => _controller.NavigationCompleted -= value;
    }

    public event EventHandler<NativeWebViewMessageReceivedEventArgs>? WebMessageReceived
    {
        add => _controller.WebMessageReceived += value;
        remove => _controller.WebMessageReceived -= value;
    }

    public event EventHandler<NativeWebViewOpenDevToolsRequestedEventArgs>? OpenDevToolsRequested
    {
        add => _controller.OpenDevToolsRequested += value;
        remove => _controller.OpenDevToolsRequested -= value;
    }

    public event EventHandler<NativeWebViewDestroyRequestedEventArgs>? DestroyRequested
    {
        add => _controller.DestroyRequested += value;
        remove => _controller.DestroyRequested -= value;
    }

    public event EventHandler<NativeWebViewRequestCustomChromeEventArgs>? RequestCustomChrome
    {
        add => _controller.RequestCustomChrome += value;
        remove => _controller.RequestCustomChrome -= value;
    }

    public event EventHandler<NativeWebViewRequestParentWindowPositionEventArgs>? RequestParentWindowPosition
    {
        add => _controller.RequestParentWindowPosition += value;
        remove => _controller.RequestParentWindowPosition -= value;
    }

    public event EventHandler<NativeWebViewBeginMoveDragEventArgs>? BeginMoveDrag
    {
        add => _controller.BeginMoveDrag += value;
        remove => _controller.BeginMoveDrag -= value;
    }

    public event EventHandler<NativeWebViewBeginResizeDragEventArgs>? BeginResizeDrag
    {
        add => _controller.BeginResizeDrag += value;
        remove => _controller.BeginResizeDrag -= value;
    }

    public event EventHandler<NativeWebViewNewWindowRequestedEventArgs>? NewWindowRequested
    {
        add => _controller.NewWindowRequested += value;
        remove => _controller.NewWindowRequested -= value;
    }

    public event EventHandler<NativeWebViewResourceRequestedEventArgs>? WebResourceRequested
    {
        add => _controller.WebResourceRequested += value;
        remove => _controller.WebResourceRequested -= value;
    }

    public event EventHandler<NativeWebViewContextMenuRequestedEventArgs>? ContextMenuRequested
    {
        add => _controller.ContextMenuRequested += value;
        remove => _controller.ContextMenuRequested -= value;
    }

    public event EventHandler<NativeWebViewNavigationHistoryChangedEventArgs>? NavigationHistoryChanged
    {
        add => _controller.NavigationHistoryChanged += value;
        remove => _controller.NavigationHistoryChanged -= value;
    }

    public event EventHandler<CoreWebViewEnvironmentRequestedEventArgs>? CoreWebView2EnvironmentRequested
    {
        add => _controller.CoreWebView2EnvironmentRequested += value;
        remove => _controller.CoreWebView2EnvironmentRequested -= value;
    }

    public event EventHandler<CoreWebViewControllerOptionsRequestedEventArgs>? CoreWebView2ControllerOptionsRequested
    {
        add => _controller.CoreWebView2ControllerOptionsRequested += value;
        remove => _controller.CoreWebView2ControllerOptionsRequested -= value;
    }

    public event EventHandler<NativeWebViewRenderFrameCapturedEventArgs>? RenderFrameCaptured;

    public bool SupportsRenderMode(NativeWebViewRenderMode renderMode)
    {
        if (renderMode == NativeWebViewRenderMode.Embedded)
        {
            return Features.Supports(NativeWebViewFeature.EmbeddedView);
        }

        if (renderMode == NativeWebViewRenderMode.GpuSurface &&
            !Features.Supports(NativeWebViewFeature.GpuSurfaceRendering))
        {
            return false;
        }

        if (renderMode == NativeWebViewRenderMode.Offscreen &&
            !Features.Supports(NativeWebViewFeature.OffscreenRendering))
        {
            return false;
        }

        if (_macOSHost is not null && _macOSHost.SupportsRenderMode(renderMode))
        {
            return true;
        }

        return _controller.TryGetBackend<INativeWebViewFrameSource>(out var frameSource) &&
               frameSource.SupportsRenderMode(renderMode);
    }

    public async Task<NativeWebViewRenderFrame?> CaptureRenderFrameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Features.Supports(NativeWebViewFeature.RenderFrameCapture))
        {
            _renderDiagnosticsMessage =
                $"Frame capture is not supported on platform '{Platform}'.";
            _renderStatisticsTracker.MarkCaptureSkipped("Frame capture is not supported on this backend.");
            InvalidateVisual();
            return null;
        }

        return await CaptureAndRenderFrameAsync(cancellationToken).ConfigureAwait(true);
    }

    public NativeWebViewRenderStatistics GetRenderStatisticsSnapshot()
    {
        return _renderStatisticsTracker.CreateSnapshot();
    }

    public void ResetRenderStatistics()
    {
        _renderStatisticsTracker.Reset();
    }

    public void SetCompositedPassthroughOverride(bool? enabled)
    {
        _macOsCompositedPassthroughOverride = enabled;
        UpdateMacOsCompositedPassthroughPolicy();

        if (RenderMode != NativeWebViewRenderMode.Embedded)
        {
            _ = CaptureAndRenderFrameAsync();
        }
    }

    public async Task<bool> SaveRenderFrameAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var frame = await CaptureRenderFrameAsync(cancellationToken).ConfigureAwait(true);
        if (frame is null)
        {
            return false;
        }

        try
        {
            await SaveFramePngAsync(frame, outputPath, cancellationToken).ConfigureAwait(true);
            _renderDiagnosticsMessage = null;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _renderDiagnosticsMessage = $"Failed to save render frame: {ex.GetType().Name}: {ex.Message}";
            InvalidateVisual();
            return false;
        }
    }

    public async Task<bool> SaveRenderFrameWithMetadataAsync(
        string outputPath,
        string? metadataOutputPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var frame = await CaptureRenderFrameAsync(cancellationToken).ConfigureAwait(true);
        if (frame is null)
        {
            return false;
        }

        // Snapshot render state immediately after capture so sidecar metadata
        // stays consistent with the frame being exported.
        var statisticsSnapshot = _renderStatisticsTracker.CreateSnapshot();
        var platform = Platform;
        var renderMode = RenderMode;
        var renderFramesPerSecond = NormalizeRenderFramesPerSecond(RenderFramesPerSecond);
        var isUsingSyntheticFrameSource = _isUsingSyntheticFrameSource;
        var renderDiagnosticsMessage = _renderDiagnosticsMessage;
        var currentUrl = CurrentUrl;

        try
        {
            await SaveFramePngAsync(frame, outputPath, cancellationToken).ConfigureAwait(true);

            var metadataPath = string.IsNullOrWhiteSpace(metadataOutputPath)
                ? $"{Path.GetFullPath(outputPath)}.json"
                : metadataOutputPath;

            var metadata = NativeWebViewRenderFrameMetadataSerializer.Create(
                frame,
                statisticsSnapshot,
                platform,
                renderMode,
                renderFramesPerSecond,
                isUsingSyntheticFrameSource,
                renderDiagnosticsMessage,
                currentUrl);

            await NativeWebViewRenderFrameMetadataSerializer
                .WriteToFileAsync(metadata, metadataPath, cancellationToken)
                .ConfigureAwait(true);

            _renderDiagnosticsMessage = null;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _renderDiagnosticsMessage = $"Failed to save render frame with metadata: {ex.GetType().Name}: {ex.Message}";
            InvalidateVisual();
            return false;
        }
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        return _controller.InitializeAsync(cancellationToken);
    }

    public void Navigate(string url)
    {
        if (_macOSHost is not null &&
            Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var parsedUri) &&
            parsedUri.IsAbsoluteUri)
        {
            _macOSHost.Navigate(parsedUri);
        }

        _controller.Navigate(url);
        UpdateMacOsCompositedPassthroughPolicy();
    }

    public void Navigate(Uri uri)
    {
        if (_macOSHost is not null && uri.IsAbsoluteUri)
        {
            _macOSHost.Navigate(uri);
        }

        _controller.Navigate(uri);
        UpdateMacOsCompositedPassthroughPolicy();
    }

    public void Reload()
    {
        _macOSHost?.Reload();
        _controller.Reload();
        UpdateMacOsCompositedPassthroughPolicy();
    }

    public void Stop()
    {
        _macOSHost?.Stop();
        _controller.Stop();
    }

    public void GoBack()
    {
        _macOSHost?.GoBack();
        _controller.GoBack();
        UpdateMacOsCompositedPassthroughPolicy();
    }

    public void GoForward()
    {
        _macOSHost?.GoForward();
        _controller.GoForward();
        UpdateMacOsCompositedPassthroughPolicy();
    }

    public Task<string?> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        return _controller.ExecuteScriptAsync(script, cancellationToken);
    }

    public Task PostWebMessageAsJsonAsync(string message, CancellationToken cancellationToken = default)
    {
        return _controller.PostWebMessageAsJsonAsync(message, cancellationToken);
    }

    public Task PostWebMessageAsStringAsync(string message, CancellationToken cancellationToken = default)
    {
        return _controller.PostWebMessageAsStringAsync(message, cancellationToken);
    }

    public void OpenDevToolsWindow()
    {
        _controller.OpenDevToolsWindow();
    }

    public Task<NativeWebViewPrintResult> PrintAsync(NativeWebViewPrintSettings? settings = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_macOSHost is not null && OperatingSystem.IsMacOS())
        {
            return Task.FromResult(_macOSHost.Print(settings));
        }

        return _controller.PrintAsync(settings, cancellationToken);
    }

    public Task<bool> ShowPrintUiAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_macOSHost is not null && OperatingSystem.IsMacOS())
        {
            return Task.FromResult(_macOSHost.ShowPrintUi());
        }

        return _controller.ShowPrintUiAsync(cancellationToken);
    }

    public void SetZoomFactor(double zoomFactor)
    {
        _macOSHost?.SetZoomFactor(zoomFactor);
        _controller.SetZoomFactor(zoomFactor);
    }

    public void SetUserAgent(string? userAgent)
    {
        _macOSHost?.SetUserAgent(userAgent);
        _controller.SetUserAgent(userAgent);
    }

    public void SetHeader(string? header)
    {
        _controller.SetHeader(header);
    }

    public bool TryGetCommandManager(out INativeWebViewCommandManager? commandManager)
    {
        return _controller.TryGetCommandManager(out commandManager);
    }

    public bool TryGetCookieManager(out INativeWebViewCookieManager? cookieManager)
    {
        return _controller.TryGetCookieManager(out cookieManager);
    }

    public bool TryGetPlatformHandle(out NativePlatformHandle handle)
    {
        if (_macOSHost is not null && _macOSHost.ViewHandle != IntPtr.Zero)
        {
            handle = new NativePlatformHandle(_macOSHost.ViewHandle, "NSView");
            return true;
        }

        if (_controller.TryGetBackend<INativeWebViewPlatformHandleProvider>(out var provider) &&
            provider.TryGetPlatformHandle(out handle))
        {
            return true;
        }

        handle = default;
        return false;
    }

    public bool TryGetViewHandle(out NativePlatformHandle handle)
    {
        if (_macOSHost is not null && _macOSHost.ViewHandle != IntPtr.Zero)
        {
            handle = new NativePlatformHandle(_macOSHost.ViewHandle, "WKWebView");
            return true;
        }

        if (_controller.TryGetBackend<INativeWebViewPlatformHandleProvider>(out var provider) &&
            provider.TryGetViewHandle(out handle))
        {
            return true;
        }

        handle = default;
        return false;
    }

    public bool TryGetControllerHandle(out NativePlatformHandle handle)
    {
        if (_macOSHost is not null && _macOSHost.ConfigurationHandle != IntPtr.Zero)
        {
            handle = new NativePlatformHandle(_macOSHost.ConfigurationHandle, "WKWebViewConfiguration");
            return true;
        }

        if (_controller.TryGetBackend<INativeWebViewPlatformHandleProvider>(out var provider) &&
            provider.TryGetControllerHandle(out handle))
        {
            return true;
        }

        handle = default;
        return false;
    }

    public void MoveFocus(NativeWebViewFocusMoveDirection direction)
    {
        _controller.MoveFocus(direction);
    }

    public override void Render(DrawingContext context)
    {
        if (RenderMode == NativeWebViewRenderMode.Embedded)
        {
            base.Render(context);
            return;
        }

        var destinationRect = new Rect(Bounds.Size);
        var surface = RenderMode == NativeWebViewRenderMode.GpuSurface
            ? _gpuSurfaceBitmap ?? _offscreenBitmap
            : _offscreenBitmap ?? _gpuSurfaceBitmap;
        var gpuFrame = TryGetLatestGpuFrame();

        if (gpuFrame is not null && TryUpdateGpuCompositionSurface(gpuFrame, destinationRect))
        {
            return;
        }

        if (surface is not null)
        {
            var sourceRect = new Rect(surface.Size);
            context.DrawImage(surface, sourceRect, destinationRect);
            return;
        }

        if (!string.IsNullOrWhiteSpace(RenderDiagnosticsMessage))
        {
            DrawRenderFallback(context, destinationRect);
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (_instance.IsDisposed)
        {
            return base.CreateNativeControlCore(parent);
        }

        _instance.ActivePresenterId = _presenterId;

        if (_controller.Platform == NativeWebViewPlatform.MacOS && OperatingSystem.IsMacOS())
        {
            if (_macOSHost is not null)
            {
                _macOSHost.AttachToParent(parent);
                ApplyRenderModeToNativeHost();
                return _macOSHost.PlatformHandle;
            }

            _macOSHost = new MacOSNativeWebViewHost(parent, _instance.InstanceConfiguration);
            _instance.MacOSHost = _macOSHost;

            _macOSHost.SetUserAgent(_controller.UserAgentString);

            if (_controller.ZoomFactor > 0)
            {
                _macOSHost.SetZoomFactor(_controller.ZoomFactor);
            }

            if (_controller.CurrentUrl is { } currentUrl && currentUrl.IsAbsoluteUri)
            {
                _macOSHost.Navigate(currentUrl);
            }

            ApplyRenderModeToNativeHost();
            return _macOSHost.PlatformHandle;
        }

        if (OperatingSystem.IsBrowser() &&
            _controller.TryGetBackend<INativeWebViewManagedControlHandleProvider>(out var managedControlHandleProvider))
        {
            var managedControlHandle = managedControlHandleProvider.CreateManagedControlHandle();
            if (managedControlHandle is not IPlatformHandle platformHandle)
            {
                throw new InvalidOperationException(
                    $"Browser managed control handle provider returned '{managedControlHandle.GetType().FullName ?? "<null>"}' instead of an Avalonia platform handle.");
            }

            return platformHandle;
        }

        if (TryGetNativeControlAttachment(out var nativeControlAttachment, out var defaultParentDescriptor))
        {
            var handle = nativeControlAttachment.AttachToNativeParent(
                new NativePlatformHandle(parent.Handle, parent.HandleDescriptor ?? defaultParentDescriptor));
            return new PlatformHandle(handle.Handle, handle.HandleDescriptor);
        }

        return base.CreateNativeControlCore(parent);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_instance.IsDisposed)
        {
            return;
        }

        if (_instance.ActivePresenterId != _presenterId)
        {
            return;
        }

        if (_macOSHost is not null)
        {
            _macOSHost.DetachFromParent(preserveRuntime: true);

            if (_instance.ActivePresenterId == _presenterId)
            {
                _instance.ActivePresenterId = 0;
            }

            return;
        }

        if (OperatingSystem.IsBrowser() &&
            _controller.TryGetBackend<INativeWebViewManagedControlHandleProvider>(out var managedControlHandleProvider))
        {
            managedControlHandleProvider.ReleaseManagedControlHandle(control);
            base.DestroyNativeControlCore(control);
            if (_instance.ActivePresenterId == _presenterId)
            {
                _instance.ActivePresenterId = 0;
            }

            return;
        }

        if (TryGetNativeControlAttachment(out var nativeControlAttachment, out _))
        {
            nativeControlAttachment.DetachFromNativeParent(preserveRuntime: true);
            if (_instance.ActivePresenterId == _presenterId)
            {
                _instance.ActivePresenterId = 0;
            }

            return;
        }

        base.DestroyNativeControlCore(control);
        if (_instance.ActivePresenterId == _presenterId)
        {
            _instance.ActivePresenterId = 0;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        ApplyRenderModeState(forceRefresh: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        StopFramePump();
        DisposeGpuCompositionSurface();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RenderModeProperty)
        {
            ApplyRenderModeState(forceRefresh: true);
            return;
        }

        if (change.Property == RenderFramesPerSecondProperty)
        {
            ApplyRenderModeState(forceRefresh: false);
            return;
        }

        if (change.Property == EnableExperimentalGpuInteropProperty)
        {
            ApplyExperimentalGpuInteropState();
            InvalidateVisual();
            return;
        }

        if (change.Property == BoundsProperty && RenderMode != NativeWebViewRenderMode.Embedded)
        {
            _suppressEmptyResizeFramesUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(750);
            UpdateGpuCompositionVisualBounds();
            _macOSHost?.UpdateLayoutForCurrentMode();
            SyncNativeHostCaptureSize();
            _ = CaptureAndRenderFrameAsync();
            return;
        }

        if (change.Property == BoundsProperty)
        {
            _macOSHost?.UpdateLayoutForCurrentMode();
        }
    }

    public void Dispose()
    {
        StopFramePump();
        DisposeRenderSurfaces();
        DisposeGpuCompositionSurface();

        _controller.CoreWebView2EnvironmentRequested -= OnCoreWebView2EnvironmentRequestedInternal;
        _controller.CoreWebView2ControllerOptionsRequested -= OnCoreWebView2ControllerOptionsRequestedInternal;

        if (_ownsInstance)
        {
            _instance.Dispose();
        }

        _macOSHost = null;
    }

    private void ApplyInstanceConfigurationToBackend()
    {
        if (_controller.TryGetBackend<INativeWebViewInstanceConfigurationTarget>(out var target))
        {
            target.ApplyInstanceConfiguration(_instance.InstanceConfiguration.Clone());
        }
    }

    private bool TryGetNativeControlAttachment(
        out INativeWebViewNativeControlAttachment nativeControlAttachment,
        out string defaultParentDescriptor)
    {
        nativeControlAttachment = default!;
        defaultParentDescriptor = string.Empty;

        if (_controller.Platform == NativeWebViewPlatform.Windows &&
            OperatingSystem.IsWindows() &&
            _controller.TryGetBackend<INativeWebViewNativeControlAttachment>(out var windowsAttachment))
        {
            nativeControlAttachment = windowsAttachment;
            defaultParentDescriptor = "HWND";
            return true;
        }

        if (_controller.Platform == NativeWebViewPlatform.Linux &&
            OperatingSystem.IsLinux() &&
            _controller.TryGetBackend<INativeWebViewNativeControlAttachment>(out var linuxAttachment))
        {
            nativeControlAttachment = linuxAttachment;
            defaultParentDescriptor = "XID";
            return true;
        }

        if (_controller.Platform == NativeWebViewPlatform.IOS &&
            OperatingSystem.IsIOS() &&
            _controller.TryGetBackend<INativeWebViewNativeControlAttachment>(out var iosAttachment))
        {
            nativeControlAttachment = iosAttachment;
            defaultParentDescriptor = "UIView";
            return true;
        }

        if (_controller.Platform == NativeWebViewPlatform.Android &&
            OperatingSystem.IsAndroid() &&
            _controller.TryGetBackend<INativeWebViewNativeControlAttachment>(out var androidAttachment))
        {
            nativeControlAttachment = androidAttachment;
            defaultParentDescriptor = "android.view.View";
            return true;
        }

        return false;
    }

    private void OnCoreWebView2EnvironmentRequestedInternal(object? sender, CoreWebViewEnvironmentRequestedEventArgs e)
    {
        _instance.InstanceConfiguration.ApplyEnvironmentOptions(e.Options);
    }

    private void OnCoreWebView2ControllerOptionsRequestedInternal(object? sender, CoreWebViewControllerOptionsRequestedEventArgs e)
    {
        _instance.InstanceConfiguration.ApplyControllerOptions(e.Options);
    }

    private void ApplyRenderModeState(bool forceRefresh)
    {
        ApplyRenderModeToNativeHost();
        ApplyExperimentalGpuInteropState();
        UpdateMacOsCompositedPassthroughPolicy();
        _macOSHost?.UpdateLayoutForCurrentMode();

        if (RenderMode == NativeWebViewRenderMode.Embedded)
        {
            StopFramePump();
            DisposeRenderSurfaces();
            if (!IsPassthroughDiagnosticsMessage(_renderDiagnosticsMessage))
            {
                _renderDiagnosticsMessage = null;
            }
            _isUsingSyntheticFrameSource = false;

            if (forceRefresh)
            {
                InvalidateVisual();
            }

            return;
        }

        SyncNativeHostCaptureSize();

        if (_isAttached)
        {
            EnsureFramePump();
        }

        if (forceRefresh)
        {
            _ = CaptureAndRenderFrameAsync();
        }
    }

    private void EnsureFramePump()
    {
        _framePump ??= new DispatcherTimer(DispatcherPriority.Render);
        _framePump.Interval = TimeSpan.FromMilliseconds(1000.0 / NormalizeRenderFramesPerSecond(RenderFramesPerSecond));

        if (!_framePump.IsEnabled)
        {
            _framePump.Tick += FramePumpOnTick;
            _framePump.Start();
        }
    }

    private void StopFramePump()
    {
        if (_framePump is null)
        {
            return;
        }

        if (_framePump.IsEnabled)
        {
            _framePump.Stop();
            _framePump.Tick -= FramePumpOnTick;
        }
    }

    private void FramePumpOnTick(object? sender, EventArgs e)
    {
        _ = CaptureAndRenderFrameAsync();
    }

    private async Task<NativeWebViewRenderFrame?> CaptureAndRenderFrameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_frameCaptureInProgress)
        {
            _renderStatisticsTracker.MarkCaptureSkipped("Frame capture skipped because another capture is already in progress.");
            return null;
        }

        if (RenderMode == NativeWebViewRenderMode.Embedded)
        {
            _renderStatisticsTracker.MarkCaptureSkipped("Frame capture skipped because RenderMode is Embedded.");
            return null;
        }

        if (!TryCreateFrameRequest(out var frameRequest))
        {
            _renderStatisticsTracker.MarkCaptureSkipped("Frame capture skipped because control bounds are not ready.");
            return null;
        }

        _frameCaptureInProgress = true;
        _renderStatisticsTracker.MarkCaptureAttempt();

        try
        {
            var frame = await CaptureFrameCoreAsync(RenderMode, frameRequest, cancellationToken).ConfigureAwait(true);
            if (frame is null)
            {
                _renderDiagnosticsMessage =
                    $"Frame source is unavailable for render mode '{RenderMode}' on platform '{Platform}'.";
                _renderStatisticsTracker.MarkCaptureFailure(_renderDiagnosticsMessage);
                InvalidateVisual();
                return null;
            }

            _isUsingSyntheticFrameSource = frame.IsSynthetic;
            _renderDiagnosticsMessage = null;

            if (!IsGpuCompositionRenderingActive())
            {
                UpdateCapturedRenderSurface(frame);
            }

            _renderStatisticsTracker.MarkCaptureSuccess(frame);
            RaiseRenderFrameCaptured(frame);
            InvalidateVisual();
            return frame;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _renderStatisticsTracker.MarkCaptureSkipped("Frame capture canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _renderDiagnosticsMessage = $"Frame capture failed: {ex.GetType().Name}: {ex.Message}";
            _renderStatisticsTracker.MarkCaptureFailure(_renderDiagnosticsMessage);
            InvalidateVisual();
            return null;
        }
        finally
        {
            _frameCaptureInProgress = false;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (TrySendCompositionPointerInput(e, NativeWebViewMouseInputKind.Move))
        {
            return;
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => NativeWebViewMouseInputKind.LeftButtonDown,
            PointerUpdateKind.RightButtonPressed => NativeWebViewMouseInputKind.RightButtonDown,
            PointerUpdateKind.MiddleButtonPressed => NativeWebViewMouseInputKind.MiddleButtonDown,
            _ => NativeWebViewMouseInputKind.Move,
        };

        if (TrySendCompositionPointerInput(e, kind, focus: true))
        {
            e.Pointer.Capture(this);
            return;
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonReleased => NativeWebViewMouseInputKind.LeftButtonUp,
            PointerUpdateKind.RightButtonReleased => NativeWebViewMouseInputKind.RightButtonUp,
            PointerUpdateKind.MiddleButtonReleased => NativeWebViewMouseInputKind.MiddleButtonUp,
            _ => NativeWebViewMouseInputKind.Move,
        };

        if (TrySendCompositionPointerInput(e, kind))
        {
            var properties = e.GetCurrentPoint(this).Properties;
            if (!properties.IsLeftButtonPressed &&
                !properties.IsRightButtonPressed &&
                !properties.IsMiddleButtonPressed)
            {
                e.Pointer.Capture(null);
            }

            return;
        }

        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var kind = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y)
            ? NativeWebViewMouseInputKind.HorizontalWheel
            : NativeWebViewMouseInputKind.Wheel;
        var delta = kind == NativeWebViewMouseInputKind.HorizontalWheel
            ? e.Delta.X
            : e.Delta.Y;

        if (TrySendCompositionPointerInput(e, kind, mouseData: (int)Math.Round(delta * 120)))
        {
            return;
        }

        base.OnPointerWheelChanged(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (TrySendCompositionPointerInput(e, NativeWebViewMouseInputKind.Leave))
        {
            return;
        }

        base.OnPointerExited(e);
    }

    private bool TrySendCompositionPointerInput(
        PointerEventArgs e,
        NativeWebViewMouseInputKind kind,
        bool focus = false,
        int mouseData = 0)
    {
        if (RenderMode == NativeWebViewRenderMode.Embedded ||
            !_controller.TryGetBackend<INativeWebViewCompositionInputSink>(out var inputSink))
        {
            return false;
        }

        if (focus)
        {
            inputSink.FocusCompositionInput();
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var position = kind == NativeWebViewMouseInputKind.Leave
            ? default
            : e.GetPosition(this);
        var point = e.GetCurrentPoint(this);
        var input = new NativeWebViewMouseInput(
            kind,
            CreateMouseModifiers(e, point.Properties),
            (int)Math.Round(position.X * scaling, MidpointRounding.AwayFromZero),
            (int)Math.Round(position.Y * scaling, MidpointRounding.AwayFromZero),
            mouseData);

        if (!inputSink.SendMouseInput(input))
        {
            return false;
        }

        e.Handled = true;
        return true;
    }

    private NativeWebViewGpuFrame? TryGetLatestGpuFrame()
    {
        if (RenderMode == NativeWebViewRenderMode.Embedded ||
            !EnableExperimentalGpuInterop ||
            !_controller.TryGetBackend<INativeWebViewGpuFrameSource>(out var gpuFrameSource) ||
            !gpuFrameSource.SupportsGpuFrame(RenderMode))
        {
            _gpuInteropDiagnosticsMessage = null;
            return null;
        }

        return gpuFrameSource.TryGetLatestGpuFrame(RenderMode);
    }

    private void UpdateGpuInteropDiagnostics(string? message)
    {
        _gpuInteropDiagnosticsMessage = message;
    }

    private bool TryUpdateGpuCompositionSurface(NativeWebViewGpuFrame frame, Rect bounds)
    {
        if (frame.SharedHandle == 0 || string.IsNullOrWhiteSpace(frame.SharedHandleType))
        {
            UpdateGpuInteropDiagnostics("GPU composition interop: WebView frame does not expose a shared GPU handle.");
            return false;
        }

        if (_gpuCompositionUpdatedFrameId == frame.FrameId)
        {
            UpdateGpuCompositionVisualBounds();
            return true;
        }

        if (!_gpuCompositionUpdateInProgress)
        {
            _gpuCompositionUpdateInProgress = true;
            Dispatcher.UIThread.Post(
                () => _ = UpdateGpuCompositionSurfaceAsync(frame, bounds),
                DispatcherPriority.Render);
        }

        return _gpuCompositionUpdatedFrameId > 0 &&
               _gpuCompositionVisual is { Visible: true };
    }

    private async Task UpdateGpuCompositionSurfaceAsync(NativeWebViewGpuFrame frame, Rect bounds)
    {
        try
        {
            var compositionVisual = ElementComposition.GetElementVisual(this);
            if (compositionVisual is null)
            {
                UpdateGpuInteropDiagnostics("GPU composition interop: Avalonia composition visual is unavailable.");
                return;
            }

            var compositor = compositionVisual.Compositor;
            _gpuCompositionInterop ??= await compositor.TryGetCompositionGpuInterop();
            if (_gpuCompositionInterop is null || _gpuCompositionInterop.IsLost)
            {
                UpdateGpuInteropDiagnostics("GPU composition interop: Avalonia compositor GPU interop is unavailable.");
                return;
            }

            if (!_gpuCompositionInterop.SupportedImageHandleTypes.Contains(frame.SharedHandleType))
            {
                UpdateGpuInteropDiagnostics(
                    $"GPU composition interop: handle type '{frame.SharedHandleType}' is not supported by this Avalonia compositor.");
                return;
            }

            EnsureGpuCompositionVisual(compositor, bounds);

            if (_gpuCompositionImportedImage is null ||
                _gpuCompositionImportedHandle != frame.SharedHandle ||
                _gpuCompositionImportedHandleType != frame.SharedHandleType ||
                _gpuCompositionImportedWidth != frame.PixelWidth ||
                _gpuCompositionImportedHeight != frame.PixelHeight)
            {
                ClearImportedGpuImage();

                var properties = new PlatformGraphicsExternalImageProperties
                {
                    Width = frame.PixelWidth,
                    Height = frame.PixelHeight,
                    Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                    TopLeftOrigin = true,
                };

                _gpuCompositionImportedImage = _gpuCompositionInterop.ImportImage(
                    new PlatformHandle(frame.SharedHandle, frame.SharedHandleType),
                    properties);
                _gpuCompositionImportedHandle = frame.SharedHandle;
                _gpuCompositionImportedHandleType = frame.SharedHandleType;
                _gpuCompositionImportedWidth = frame.PixelWidth;
                _gpuCompositionImportedHeight = frame.PixelHeight;
            }

            if (_gpuCompositionSurface is null || _gpuCompositionImportedImage is null)
            {
                return;
            }

            if (frame.RequiresKeyedMutex)
            {
                await _gpuCompositionSurface.UpdateWithKeyedMutexAsync(
                    _gpuCompositionImportedImage,
                    frame.KeyedMutexAcquireKey,
                    frame.KeyedMutexReleaseKey);
            }
            else
            {
                await _gpuCompositionSurface.UpdateAsync(_gpuCompositionImportedImage);
            }

            _gpuCompositionUpdatedFrameId = frame.FrameId;
            _gpuInteropDiagnosticsMessage = null;
            _gpuCompositionVisual!.Visible = true;
            SetGpuFrameOnlyRenderingEnabled(true);
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            UpdateGpuInteropDiagnostics($"GPU composition interop failed: {ex.GetType().Name}: {ex.Message}");
            SetGpuFrameOnlyRenderingEnabled(false);
            DisposeGpuCompositionSurface();
        }
        finally
        {
            _gpuCompositionUpdateInProgress = false;
        }
    }

    private void EnsureGpuCompositionVisual(Compositor compositor, Rect bounds)
    {
        if (_gpuCompositionVisual is null)
        {
            _gpuCompositionSurface = compositor.CreateDrawingSurface();
            _gpuCompositionVisual = compositor.CreateSurfaceVisual();
            _gpuCompositionVisual.Surface = _gpuCompositionSurface;
            _gpuCompositionVisual.ClipToBounds = true;
            _gpuCompositionVisual.Visible = false;
            ElementComposition.SetElementChildVisual(this, _gpuCompositionVisual);
        }

        UpdateGpuCompositionVisualBounds(bounds);
    }

    private void UpdateGpuCompositionVisualBounds()
    {
        UpdateGpuCompositionVisualBounds(new Rect(Bounds.Size));
    }

    private void UpdateGpuCompositionVisualBounds(Rect bounds)
    {
        if (_gpuCompositionVisual is null)
        {
            return;
        }

        _gpuCompositionVisual.Offset = new Vector3D(bounds.X, bounds.Y, 0);
        _gpuCompositionVisual.Size = new Vector(Math.Max(0, bounds.Width), Math.Max(0, bounds.Height));
    }

    private void DisposeGpuCompositionSurface()
    {
        ElementComposition.SetElementChildVisual(this, null);
        ClearImportedGpuImage();
        _gpuCompositionSurface?.Dispose();
        _gpuCompositionSurface = null;
        _gpuCompositionVisual = null;
        _gpuCompositionInterop = null;
        _gpuCompositionUpdatedFrameId = 0;
        _gpuCompositionUpdateInProgress = false;
        SetGpuFrameOnlyRenderingEnabled(false);
    }

    private void ClearImportedGpuImage()
    {
        if (_gpuCompositionImportedImage is not null)
        {
            _ = _gpuCompositionImportedImage.DisposeAsync().AsTask();
        }

        _gpuCompositionImportedImage = null;
        _gpuCompositionImportedHandle = 0;
        _gpuCompositionImportedHandleType = null;
        _gpuCompositionImportedWidth = 0;
        _gpuCompositionImportedHeight = 0;
        _gpuCompositionUpdatedFrameId = 0;
    }

    private bool IsGpuCompositionRenderingActive()
    {
        return EnableExperimentalGpuInterop &&
               RenderMode != NativeWebViewRenderMode.Embedded &&
               _gpuCompositionVisual is { Visible: true } &&
               _gpuCompositionUpdatedFrameId > 0;
    }

    private void SetGpuFrameOnlyRenderingEnabled(bool enabled)
    {
        if (_controller.TryGetBackend<INativeWebViewGpuFrameSource>(out var gpuFrameSource))
        {
            gpuFrameSource.SetGpuFrameOnlyRenderingEnabled(
                enabled &&
                EnableExperimentalGpuInterop &&
                RenderMode != NativeWebViewRenderMode.Embedded &&
                gpuFrameSource.SupportsGpuFrame(RenderMode));
        }
    }

    private static NativeWebViewMouseInputModifiers CreateMouseModifiers(
        PointerEventArgs e,
        PointerPointProperties properties)
    {
        var modifiers = NativeWebViewMouseInputModifiers.None;

        if (properties.IsLeftButtonPressed)
        {
            modifiers |= NativeWebViewMouseInputModifiers.LeftButton;
        }

        if (properties.IsRightButtonPressed)
        {
            modifiers |= NativeWebViewMouseInputModifiers.RightButton;
        }

        if (properties.IsMiddleButtonPressed)
        {
            modifiers |= NativeWebViewMouseInputModifiers.MiddleButton;
        }

        if (properties.IsXButton1Pressed)
        {
            modifiers |= NativeWebViewMouseInputModifiers.XButton1;
        }

        if (properties.IsXButton2Pressed)
        {
            modifiers |= NativeWebViewMouseInputModifiers.XButton2;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            modifiers |= NativeWebViewMouseInputModifiers.Shift;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            modifiers |= NativeWebViewMouseInputModifiers.Control;
        }

        return modifiers;
    }

    private void UpdateCapturedRenderSurface(NativeWebViewRenderFrame frame)
    {
        if (frame.IsSynthetic && HasRetainedCompositedFrame(RenderMode))
        {
            return;
        }

        if (!frame.IsSynthetic &&
            HasRetainedCompositedFrame(RenderMode) &&
            DateTimeOffset.UtcNow <= _suppressEmptyResizeFramesUntilUtc &&
            IsLikelyEmptyFrame(frame))
        {
            return;
        }

        if (RenderMode == NativeWebViewRenderMode.GpuSurface)
        {
            UpdateGpuSurfaceFrame(frame);
            if (!frame.IsSynthetic)
            {
                DisposeOffscreenSurface();
            }
        }
        else
        {
            UpdateOffscreenFrame(frame);
            if (!frame.IsSynthetic)
            {
                DisposeGpuSurface();
            }
        }
    }

    private bool HasRetainedCompositedFrame(NativeWebViewRenderMode renderMode)
    {
        return renderMode == NativeWebViewRenderMode.GpuSurface
            ? _gpuSurfaceBitmap is not null || _offscreenBitmap is not null
            : _offscreenBitmap is not null || _gpuSurfaceBitmap is not null;
    }

    private async Task<NativeWebViewRenderFrame?> CaptureFrameCoreAsync(
        NativeWebViewRenderMode renderMode,
        NativeWebViewRenderFrameRequest frameRequest,
        CancellationToken cancellationToken)
    {
        if (_macOSHost is not null &&
            _macOSHost.TryCaptureFrame(renderMode, frameRequest.PixelWidth, frameRequest.PixelHeight, out var hostFrame))
        {
            return hostFrame;
        }

        if (_controller.TryGetBackend<INativeWebViewFrameSource>(out var frameSource) &&
            frameSource.SupportsRenderMode(renderMode))
        {
            return await frameSource.CaptureFrameAsync(renderMode, frameRequest, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private void UpdateGpuSurfaceFrame(NativeWebViewRenderFrame frame)
    {
        if (frame.PixelFormat != NativeWebViewRenderPixelFormat.Bgra8888Premultiplied)
        {
            throw new NotSupportedException($"Unsupported frame pixel format '{frame.PixelFormat}'.");
        }

        var frameDpi = ResolveFrameDpi(frame.PixelWidth, frame.PixelHeight);

        if (_gpuSurfaceBitmap is null ||
            _gpuSurfaceBitmap.PixelSize.Width != frame.PixelWidth ||
            _gpuSurfaceBitmap.PixelSize.Height != frame.PixelHeight ||
            !AreClose(_gpuSurfaceDpi, frameDpi))
        {
            DisposeGpuSurface();
            _gpuSurfaceBitmap = new WriteableBitmap(
                new PixelSize(frame.PixelWidth, frame.PixelHeight),
                frameDpi,
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
            _gpuSurfaceDpi = frameDpi;
        }

        CopyFramePixels(frame, _gpuSurfaceBitmap);
    }

    private void UpdateOffscreenFrame(NativeWebViewRenderFrame frame)
    {
        if (frame.PixelFormat != NativeWebViewRenderPixelFormat.Bgra8888Premultiplied)
        {
            throw new NotSupportedException($"Unsupported frame pixel format '{frame.PixelFormat}'.");
        }

        var frameDpi = ResolveFrameDpi(frame.PixelWidth, frame.PixelHeight);
        if (_offscreenBitmap is null ||
            _offscreenBitmap.PixelSize.Width != frame.PixelWidth ||
            _offscreenBitmap.PixelSize.Height != frame.PixelHeight ||
            !AreClose(_offscreenDpi, frameDpi))
        {
            DisposeOffscreenSurface();
            _offscreenBitmap = new WriteableBitmap(
                new PixelSize(frame.PixelWidth, frame.PixelHeight),
                frameDpi,
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
            _offscreenDpi = frameDpi;
        }

        CopyFramePixels(frame, _offscreenBitmap);
    }

    private static void CopyFramePixels(NativeWebViewRenderFrame frame, WriteableBitmap bitmap)
    {
        using var framebuffer = bitmap.Lock();

        var copyRows = Math.Min(frame.PixelHeight, framebuffer.Size.Height);
        var rowCopyBytes = Math.Min(frame.BytesPerRow, framebuffer.RowBytes);

        for (var row = 0; row < copyRows; row++)
        {
            var sourceOffset = row * frame.BytesPerRow;
            var destination = IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes);
            Marshal.Copy(frame.PixelData, sourceOffset, destination, rowCopyBytes);
        }
    }

    private static async Task SaveFramePngAsync(
        NativeWebViewRenderFrame frame,
        string outputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var bitmap = new WriteableBitmap(
            new PixelSize(frame.PixelWidth, frame.PixelHeight),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        CopyFramePixels(frame, bitmap);

        await using var stream = File.Create(fullPath);
        bitmap.Save(stream);
    }

    private void RaiseRenderFrameCaptured(NativeWebViewRenderFrame frame)
    {
        var handlers = RenderFrameCaptured;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<NativeWebViewRenderFrameCapturedEventArgs>)handler).Invoke(
                    this,
                    new NativeWebViewRenderFrameCapturedEventArgs(frame));
            }
            catch
            {
                // ignored
            }
        }
    }

    private void DrawRenderFallback(DrawingContext context, Rect destinationRect)
    {
        context.FillRectangle(RenderBackgroundBrush, destinationRect);
        context.DrawRectangle(null, new Pen(RenderOutlineBrush, 1), destinationRect.Deflate(0.5));

        var status = RenderDiagnosticsMessage ??
                     $"{RenderMode} active. Waiting for first rendered web frame.";
        var formattedText = new FormattedText(
            status,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            13,
            RenderTextBrush);

        context.DrawText(formattedText, new Point(12, 12));
    }

    private void DisposeRenderSurfaces()
    {
        DisposeGpuSurface();
        DisposeOffscreenSurface();
    }

    private void DisposeGpuSurface()
    {
        _gpuSurfaceBitmap?.Dispose();
        _gpuSurfaceBitmap = null;
        _gpuSurfaceDpi = new Vector(96, 96);
    }

    private void DisposeOffscreenSurface()
    {
        _offscreenBitmap?.Dispose();
        _offscreenBitmap = null;
        _offscreenDpi = new Vector(96, 96);
    }

    private static bool IsLikelyEmptyFrame(NativeWebViewRenderFrame frame)
    {
        if (frame.PixelData.Length == 0 || frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
        {
            return true;
        }

        var sampleStep = Math.Max(1, frame.PixelWidth * frame.PixelHeight / 2048);
        var sampled = 0;
        var nearBlack = 0;

        for (var pixel = 0; pixel < frame.PixelWidth * frame.PixelHeight; pixel += sampleStep)
        {
            var row = pixel / frame.PixelWidth;
            var column = pixel % frame.PixelWidth;
            var offset = row * frame.BytesPerRow + column * 4;
            if (offset + 3 >= frame.PixelData.Length)
            {
                continue;
            }

            sampled++;
            var blue = frame.PixelData[offset];
            var green = frame.PixelData[offset + 1];
            var red = frame.PixelData[offset + 2];
            var alpha = frame.PixelData[offset + 3];
            if (alpha <= 4 || red + green + blue <= 12)
            {
                nearBlack++;
            }
        }

        return sampled > 0 && nearBlack >= sampled * 0.995;
    }

    private bool TryCreateFrameRequest(out NativeWebViewRenderFrameRequest request)
    {
        request = new NativeWebViewRenderFrameRequest();

        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return false;
        }

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        request.PixelWidth = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
        request.PixelHeight = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        return true;
    }

    private static int NormalizeRenderFramesPerSecond(int value)
    {
        if (value < MinRenderFramesPerSecond)
        {
            return MinRenderFramesPerSecond;
        }

        if (value > MaxRenderFramesPerSecond)
        {
            return MaxRenderFramesPerSecond;
        }

        return value;
    }

    private void ApplyRenderModeToNativeHost()
    {
        if (_controller.TryGetBackend<INativeWebViewRenderModeTarget>(out var renderModeTarget))
        {
            renderModeTarget.SetRenderMode(RenderMode);
        }

        if (_macOSHost is null)
        {
            return;
        }

        _macOSHost.SetRenderMode(RenderMode);
    }

    private void ApplyExperimentalGpuInteropState()
    {
        if (_controller.TryGetBackend<INativeWebViewGpuFrameSource>(out var gpuFrameSource))
        {
            gpuFrameSource.SetGpuFrameCaptureEnabled(
                EnableExperimentalGpuInterop &&
                RenderMode != NativeWebViewRenderMode.Embedded &&
                gpuFrameSource.SupportsGpuFrame(RenderMode));
        }

        if (!EnableExperimentalGpuInterop || RenderMode == NativeWebViewRenderMode.Embedded)
        {
            _gpuInteropDiagnosticsMessage = null;
            DisposeGpuCompositionSurface();
        }
    }

    private void SyncNativeHostCaptureSize()
    {
        if (_macOSHost is null)
        {
            return;
        }

        if (!TryCreateFrameRequest(out var request))
        {
            return;
        }

        _macOSHost.SetCaptureSize(request.PixelWidth, request.PixelHeight);
    }

    private Vector ResolveFrameDpi(int pixelWidth, int pixelHeight)
    {
        if (TryCreateFrameRequest(out var request) &&
            request.PixelWidth > 0 &&
            request.PixelHeight > 0)
        {
            var dpiX = 96d * pixelWidth / request.PixelWidth;
            var dpiY = 96d * pixelHeight / request.PixelHeight;
            if (double.IsFinite(dpiX) && double.IsFinite(dpiY) && dpiX > 0 && dpiY > 0)
            {
                return new Vector(dpiX, dpiY);
            }
        }

        return new Vector(96d, 96d);
    }

    private static bool AreClose(Vector left, Vector right)
    {
        const double epsilon = 0.01;
        return Math.Abs(left.X - right.X) < epsilon && Math.Abs(left.Y - right.Y) < epsilon;
    }

    private void UpdateMacOsCompositedPassthroughPolicy()
    {
        if (_macOSHost is null || !OperatingSystem.IsMacOS())
        {
            _isMacOsCompositedPassthroughActive = false;
            return;
        }

        var shouldEnable = ResolveMacOsCompositedPassthroughEnabled();

        _macOSHost.SetCompositedPassthrough(shouldEnable);

        if (_isMacOsCompositedPassthroughActive == shouldEnable)
        {
            return;
        }

        _isMacOsCompositedPassthroughActive = shouldEnable;
        var passthroughDiagnostics = ResolvePassthroughDiagnosticsMessage(shouldEnable);
        if (!string.IsNullOrWhiteSpace(passthroughDiagnostics))
        {
            _renderDiagnosticsMessage = passthroughDiagnostics;
        }
        else if (IsPassthroughDiagnosticsMessage(_renderDiagnosticsMessage))
        {
            _renderDiagnosticsMessage = null;
        }
    }

    private bool ResolveMacOsCompositedPassthroughEnabled()
    {
        if (RenderMode == NativeWebViewRenderMode.Embedded)
        {
            return false;
        }

        if (_macOsCompositedPassthroughOverride.HasValue)
        {
            return _macOsCompositedPassthroughOverride.Value;
        }

        return IsKnownVideoHost(CurrentUrl);
    }

    private string? ResolvePassthroughDiagnosticsMessage(bool passthroughEnabled)
    {
        if (_macOsCompositedPassthroughOverride.HasValue)
        {
            return passthroughEnabled
                ? MacOsCompositedForcedPassthroughMessage
                : MacOsCompositedForcedDisabledMessage;
        }

        return passthroughEnabled
            ? MacOsCompositedVideoPassthroughMessage
            : null;
    }

    private static bool IsPassthroughDiagnosticsMessage(string? message)
    {
        return string.Equals(message, MacOsCompositedVideoPassthroughMessage, StringComparison.Ordinal) ||
               string.Equals(message, MacOsCompositedForcedPassthroughMessage, StringComparison.Ordinal) ||
               string.Equals(message, MacOsCompositedForcedDisabledMessage, StringComparison.Ordinal);
    }

    private static bool IsKnownVideoHost(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        for (var i = 0; i < MacOsCompositedPassthroughVideoHosts.Length; i++)
        {
            var videoHost = MacOsCompositedPassthroughVideoHosts[i];
            if (host.Equals(videoHost, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith($".{videoHost}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
