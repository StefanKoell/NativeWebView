using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using NativeWebView.Core;
using NativeWebView.Interop;

namespace NativeWebView.Controls;

public class NativeWebView : NativeControlHost, IDisposable, ICustomHitTest
{
    private const int MinRenderFramesPerSecond = 1;
    private const int MaxRenderFramesPerSecond = 60;
    private const int DefaultRenderFramesPerSecond = 60;

    private static readonly SolidColorBrush RenderBackgroundBrush = new(Color.FromRgb(15, 23, 42));
    private static readonly SolidColorBrush RenderOutlineBrush = new(Color.FromArgb(180, 148, 163, 184));
    private static readonly SolidColorBrush RenderTextBrush = new(Color.FromRgb(226, 232, 240));
    private static readonly SolidColorBrush InputHitTestBrush = new(Color.FromArgb(1, 0, 0, 0));
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
    private readonly Dictionary<IntPtr, CachedGpuCompositionImportedImage> _gpuCompositionImportedImages = [];
    private IntPtr _gpuCompositionImportedHandle;
    private string? _gpuCompositionImportedHandleType;
    private int _gpuCompositionImportedWidth;
    private int _gpuCompositionImportedHeight;
    private Rect _gpuCompositionVisualBounds;
    private int _lastRequestedFramePixelWidth;
    private int _lastRequestedFramePixelHeight;
    private double _lastRequestedFrameScale;
    private long _gpuCompositionUpdatedFrameId;
    private long _gpuCompositionUpdateCount;
    private long _gpuCompositionUpdateElapsedTicks;
    private long _gpuCompositionUpdateMaxTicks;
    private long _gpuCompositionUpdateOver16MillisecondsCount;
    private long _gpuCompositionUpdateOver33MillisecondsCount;
    private long _gpuCompositionUpdateOver50MillisecondsCount;
    private NativeWebViewGpuFrame? _pendingGpuCompositionFrame;
    private Rect _pendingGpuCompositionBounds;
    private string? _gpuInteropSupportedImageHandleTypes;
    private string? _gpuInteropSupportedSemaphoreTypes;
    private string? _gpuInteropSynchronizationCapabilities;

    private bool _isAttached;
    private TopLevel? _compositionInputTopLevel;
    private bool _frameCaptureInProgress;
    private bool _gpuCompositionUpdateInProgress;
    private int _gpuFrameArrivalCaptureScheduled;
    private bool _isGpuFrameNotificationPumpActive;
    private INativeWebViewGpuFrameNotificationSource? _gpuFrameNotificationSource;
    private long _gpuFrameArrivalSignalCount;
    private long _framePumpTickCount;
    private long _gpuFrameArrivalCaptureScheduledCount;
    private bool _isUsingSyntheticFrameSource;
    private bool _isMacOsCompositedPassthroughActive;
    private bool? _macOsCompositedPassthroughOverride;
    private long _compositionTopLevelPointerEventCount;
    private long _compositionTopLevelPointerRejectedCount;
    private long _compositionWin32PointerMessageCount;
    private long _compositionWin32PointerRejectedCount;
    private int _lastCompositionWin32ClientX;
    private int _lastCompositionWin32ClientY;
    private double _lastCompositionWin32TopLeftX;
    private double _lastCompositionWin32TopLeftY;
    private double _lastCompositionWin32LocalX;
    private double _lastCompositionWin32LocalY;
    private long _compositionMouseInputForwardedCount;
    private long _compositionMouseInputFailedCount;
    private long _compositionKeyboardMessageCount;
    private long _compositionKeyboardMessageForwardedCount;
    private long _compositionKeyboardMessageRejectedCount;
    private uint _lastCompositionKeyboardMessage;
    private NativeWebViewMouseInput? _lastCompositionMouseInput;
    private TopLevel? _compositionInputWndProcTopLevel;
    private readonly Win32Properties.CustomWndProcHookCallback? _compositionInputWndProcHook;
    private bool _hasCompositionKeyboardFocus;
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
        Focusable = true;
        if (OperatingSystem.IsWindows())
        {
            _compositionInputWndProcHook = CompositionInputWndProcHook;
        }

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

    public bool HitTest(Point point)
    {
        return new Rect(Bounds.Size).Contains(point);
    }

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

    public NativeWebViewInputDiagnosticsSnapshot GetInputDiagnosticsSnapshot()
    {
        var lastInput = _lastCompositionMouseInput;
        return new NativeWebViewInputDiagnosticsSnapshot(
            IsGpuCompositionRenderingActive(),
            Interlocked.Read(ref _compositionTopLevelPointerEventCount),
            Interlocked.Read(ref _compositionTopLevelPointerRejectedCount),
            Interlocked.Read(ref _compositionWin32PointerMessageCount),
            Interlocked.Read(ref _compositionWin32PointerRejectedCount),
            _lastCompositionWin32ClientX,
            _lastCompositionWin32ClientY,
            _lastCompositionWin32TopLeftX,
            _lastCompositionWin32TopLeftY,
            _lastCompositionWin32LocalX,
            _lastCompositionWin32LocalY,
            Interlocked.Read(ref _compositionMouseInputForwardedCount),
            Interlocked.Read(ref _compositionMouseInputFailedCount),
            _hasCompositionKeyboardFocus,
            Interlocked.Read(ref _compositionKeyboardMessageCount),
            Interlocked.Read(ref _compositionKeyboardMessageForwardedCount),
            Interlocked.Read(ref _compositionKeyboardMessageRejectedCount),
            _lastCompositionKeyboardMessage,
            lastInput?.Kind,
            lastInput?.X ?? 0,
            lastInput?.Y ?? 0,
            lastInput?.MouseData ?? 0);
    }

    public NativeWebViewGpuFrameDiagnosticsSnapshot GetGpuFrameDiagnosticsSnapshot()
    {
        try
        {
            var snapshot = _controller.TryGetBackend<INativeWebViewGpuFrameDiagnosticsSource>(out var diagnosticsSource)
                ? diagnosticsSource.GetGpuFrameDiagnosticsSnapshot()
                : default;
            return snapshot with
            {
                RequestedRenderMode = RenderMode,
                EffectiveRenderMode = GetEffectiveGpuInteropRenderMode(),
                RequestedFramePixelWidth = _lastRequestedFramePixelWidth,
                RequestedFramePixelHeight = _lastRequestedFramePixelHeight,
                RequestedFrameScale = _lastRequestedFrameScale,
                IsGpuFrameNotificationPumpActive = _isGpuFrameNotificationPumpActive,
                FramePumpTickCount = Interlocked.Read(ref _framePumpTickCount),
                GpuFrameArrivalCaptureScheduledCount = Interlocked.Read(ref _gpuFrameArrivalCaptureScheduledCount),
                GpuCompositionUpdateCount = Interlocked.Read(ref _gpuCompositionUpdateCount),
                GpuCompositionUpdateTotalMicroseconds = GetElapsedMicroseconds(
                    Interlocked.Read(ref _gpuCompositionUpdateElapsedTicks)),
                GpuCompositionUpdateAverageMicroseconds = GetAverageMicroseconds(
                    Interlocked.Read(ref _gpuCompositionUpdateElapsedTicks),
                    Interlocked.Read(ref _gpuCompositionUpdateCount)),
                GpuCompositionUpdateMaxMicroseconds = GetElapsedMicroseconds(
                    Interlocked.Read(ref _gpuCompositionUpdateMaxTicks)),
                GpuCompositionUpdateOver16MillisecondsCount = Interlocked.Read(
                    ref _gpuCompositionUpdateOver16MillisecondsCount),
                GpuCompositionUpdateOver33MillisecondsCount = Interlocked.Read(
                    ref _gpuCompositionUpdateOver33MillisecondsCount),
                GpuCompositionUpdateOver50MillisecondsCount = Interlocked.Read(
                    ref _gpuCompositionUpdateOver50MillisecondsCount),
                GpuInteropSupportedImageHandleTypes = _gpuInteropSupportedImageHandleTypes,
                GpuInteropSupportedSemaphoreTypes = _gpuInteropSupportedSemaphoreTypes,
                GpuInteropSynchronizationCapabilities = _gpuInteropSynchronizationCapabilities,
            };
        }
        catch (ObjectDisposedException)
        {
            return default;
        }
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
        context.FillRectangle(InputHitTestBrush, destinationRect);

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
        AttachGpuFrameNotificationSource();
        AttachCompositionInputFallback();
        ApplyRenderModeState(forceRefresh: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachCompositionInputFallback();
        DetachGpuFrameNotificationSource();
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        _hasCompositionKeyboardFocus = false;
        _gpuFrameArrivalCaptureScheduled = 0;
        _isGpuFrameNotificationPumpActive = false;
        StopFramePump();
        DisposeGpuCompositionSurface();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RenderModeProperty)
        {
            UpdateCompositionInputFallback();
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
            ApplyRenderModeToNativeHost();
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
        DetachCompositionInputFallback();
        DetachGpuFrameNotificationSource();
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
            UpdateFramePumpMode();
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

    private void AttachGpuFrameNotificationSource()
    {
        DetachGpuFrameNotificationSource();
        if (_controller.TryGetBackend<INativeWebViewGpuFrameNotificationSource>(out var notificationSource))
        {
            _gpuFrameNotificationSource = notificationSource;
            notificationSource.GpuFrameArrived += OnGpuFrameArrived;
        }
    }

    private void DetachGpuFrameNotificationSource()
    {
        if (_gpuFrameNotificationSource is null)
        {
            return;
        }

        _gpuFrameNotificationSource.GpuFrameArrived -= OnGpuFrameArrived;
        _gpuFrameNotificationSource = null;
    }

    private void OnGpuFrameArrived(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;

        if (!Volatile.Read(ref _isGpuFrameNotificationPumpActive))
        {
            return;
        }

        Interlocked.Increment(ref _gpuFrameArrivalSignalCount);
        ScheduleGpuFrameArrivalCapture();
    }

    private void ScheduleGpuFrameArrivalCapture()
    {
        if (Interlocked.Exchange(ref _gpuFrameArrivalCaptureScheduled, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _gpuFrameArrivalCaptureScheduledCount);
        Dispatcher.UIThread.Post(
            async () =>
            {
                var observedSignalCount = Interlocked.Read(ref _gpuFrameArrivalSignalCount);
                try
                {
                    if (ShouldUseGpuFrameNotificationPump())
                    {
                        await CaptureAndRenderFrameAsync().ConfigureAwait(true);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _gpuFrameArrivalCaptureScheduled, 0);
                    if (ShouldUseGpuFrameNotificationPump() &&
                        Interlocked.Read(ref _gpuFrameArrivalSignalCount) > observedSignalCount)
                    {
                        ScheduleGpuFrameArrivalCapture();
                    }
                }
            },
            DispatcherPriority.Render);
    }

    private bool ShouldUseGpuFrameNotificationPump()
    {
        return _isAttached &&
               RenderMode != NativeWebViewRenderMode.Embedded &&
               EnableExperimentalGpuInterop &&
               _gpuFrameNotificationSource is not null &&
               IsGpuCompositionRenderingActive();
    }

    private void UpdateFramePumpMode()
    {
        var useNotificationPump = ShouldUseGpuFrameNotificationPump();
        if (_isGpuFrameNotificationPumpActive == useNotificationPump)
        {
            return;
        }

        _isGpuFrameNotificationPumpActive = useNotificationPump;
        if (useNotificationPump)
        {
            StopFramePump();
        }
        else if (_isAttached && RenderMode != NativeWebViewRenderMode.Embedded)
        {
            EnsureFramePump();
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
        Interlocked.Increment(ref _framePumpTickCount);
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
            var gpuDiagnosticsBeforeCapture = GetGpuFrameDiagnosticsSnapshot();
            var frameRenderMode = GetEffectiveGpuInteropRenderMode();
            var frame = await CaptureFrameCoreAsync(frameRenderMode, frameRequest, cancellationToken).ConfigureAwait(true);
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
            else if (TryGetLatestGpuFrame() is { } gpuFrame)
            {
                TryUpdateGpuCompositionSurface(gpuFrame, new Rect(Bounds.Size));
            }

            _renderStatisticsTracker.MarkCaptureSuccess(frame);
            var gpuDiagnosticsAfterCapture = GetGpuFrameDiagnosticsSnapshot();
            if (IsGpuCompositionNoOpRetainedFrame(gpuDiagnosticsBeforeCapture, gpuDiagnosticsAfterCapture))
            {
                return frame;
            }

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

    private static bool IsGpuCompositionNoOpRetainedFrame(
        NativeWebViewGpuFrameDiagnosticsSnapshot before,
        NativeWebViewGpuFrameDiagnosticsSnapshot after)
    {
        return after.IsGpuFrameOnlyRenderingEnabled &&
               after.RetainedGpuOnlyFrameReturnCount > before.RetainedGpuOnlyFrameReturnCount &&
               after.GpuFrameCopyCount == before.GpuFrameCopyCount &&
               after.CpuFrameCopyCount == before.CpuFrameCopyCount;
    }

    private void AttachCompositionInputFallback()
    {
        if (RenderMode == NativeWebViewRenderMode.Embedded)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || ReferenceEquals(_compositionInputTopLevel, topLevel))
        {
            return;
        }

        DetachCompositionInputFallback();
        _compositionInputTopLevel = topLevel;
        topLevel.AddHandler(PointerMovedEvent, CompositionTopLevelOnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(PointerPressedEvent, CompositionTopLevelOnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(PointerReleasedEvent, CompositionTopLevelOnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(PointerWheelChangedEvent, CompositionTopLevelOnPointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);

        if (_compositionInputWndProcHook is not null)
        {
            Win32Properties.AddWndProcHookCallback(topLevel, _compositionInputWndProcHook);
            _compositionInputWndProcTopLevel = topLevel;
        }
    }

    private void DetachCompositionInputFallback()
    {
        if (_compositionInputWndProcTopLevel is not null && _compositionInputWndProcHook is not null)
        {
            Win32Properties.RemoveWndProcHookCallback(_compositionInputWndProcTopLevel, _compositionInputWndProcHook);
            _compositionInputWndProcTopLevel = null;
        }

        if (_compositionInputTopLevel is null)
        {
            return;
        }

        _compositionInputTopLevel.RemoveHandler(PointerMovedEvent, CompositionTopLevelOnPointerMoved);
        _compositionInputTopLevel.RemoveHandler(PointerPressedEvent, CompositionTopLevelOnPointerPressed);
        _compositionInputTopLevel.RemoveHandler(PointerReleasedEvent, CompositionTopLevelOnPointerReleased);
        _compositionInputTopLevel.RemoveHandler(PointerWheelChangedEvent, CompositionTopLevelOnPointerWheelChanged);
        _compositionInputTopLevel = null;
    }

    private void UpdateCompositionInputFallback()
    {
        if (!_isAttached || RenderMode == NativeWebViewRenderMode.Embedded)
        {
            DetachCompositionInputFallback();
            return;
        }

        AttachCompositionInputFallback();
    }

    private void CompositionTopLevelOnPointerMoved(object? sender, PointerEventArgs e)
    {
        _ = sender;
        Interlocked.Increment(ref _compositionTopLevelPointerEventCount);
        if (ShouldForwardCompositionTopLevelPointer(e))
        {
            _ = TrySendCompositionPointerInput(e, NativeWebViewMouseInputKind.Move);
        }
    }

    private void CompositionTopLevelOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = sender;
        Interlocked.Increment(ref _compositionTopLevelPointerEventCount);
        if (!ShouldForwardCompositionTopLevelPointer(e))
        {
            return;
        }

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
        }
    }

    private void CompositionTopLevelOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _ = sender;
        Interlocked.Increment(ref _compositionTopLevelPointerEventCount);
        if (!ShouldForwardCompositionTopLevelPointer(e))
        {
            return;
        }

        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonReleased => NativeWebViewMouseInputKind.LeftButtonUp,
            PointerUpdateKind.RightButtonReleased => NativeWebViewMouseInputKind.RightButtonUp,
            PointerUpdateKind.MiddleButtonReleased => NativeWebViewMouseInputKind.MiddleButtonUp,
            _ => NativeWebViewMouseInputKind.Move,
        };

        if (!TrySendCompositionPointerInput(e, kind))
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed &&
            !properties.IsRightButtonPressed &&
            !properties.IsMiddleButtonPressed)
        {
            e.Pointer.Capture(null);
        }
    }

    private void CompositionTopLevelOnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _ = sender;
        Interlocked.Increment(ref _compositionTopLevelPointerEventCount);
        if (!ShouldForwardCompositionTopLevelPointer(e))
        {
            return;
        }

        var kind = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y)
            ? NativeWebViewMouseInputKind.HorizontalWheel
            : NativeWebViewMouseInputKind.Wheel;
        var delta = kind == NativeWebViewMouseInputKind.HorizontalWheel
            ? e.Delta.X
            : e.Delta.Y;

        _ = TrySendCompositionPointerInput(e, kind, mouseData: (int)Math.Round(delta * 120));
    }

    private IntPtr CompositionInputWndProcHook(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (TryApplyCompositionCursor(hWnd, msg, ref handled))
        {
            return IntPtr.Zero;
        }

        if (TryForwardCompositionKeyboardMessage(hWnd, msg, wParam, lParam))
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (!TryMapWin32MouseMessage(hWnd, msg, wParam, lParam, out var kind, out var localPosition, out var modifiers, out var mouseData, out var focus))
        {
            return IntPtr.Zero;
        }

        Interlocked.Increment(ref _compositionWin32PointerMessageCount);
        if (!TrySendCompositionMouseInput(kind, localPosition, modifiers, mouseData, focus))
        {
            Interlocked.Increment(ref _compositionWin32PointerRejectedCount);
            return IntPtr.Zero;
        }

        handled = true;
        return IntPtr.Zero;
    }

    private bool TryApplyCompositionCursor(IntPtr hWnd, uint message, ref bool handled)
    {
        if (message != Win32Input.WmSetCursor ||
            RenderMode == NativeWebViewRenderMode.Embedded ||
            !_isAttached ||
            hWnd == IntPtr.Zero ||
            !_controller.TryGetBackend<INativeWebViewCompositionCursorSource>(out var cursorSource))
        {
            return false;
        }

        var cursorHandle = cursorSource.CurrentCompositionCursorHandle;
        if (cursorHandle == IntPtr.Zero ||
            !TryGetCurrentTopLevelCursorLocalPosition(hWnd, out var localPosition) ||
            !IsLocalPointInsideBounds(localPosition))
        {
            return false;
        }

        _ = Win32Input.SetCursor(cursorHandle);
        handled = true;
        return true;
    }

    private bool TryForwardCompositionKeyboardMessage(IntPtr topLevelHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (!Win32Input.IsKeyboardMessage(message))
        {
            return false;
        }

        Interlocked.Increment(ref _compositionKeyboardMessageCount);
        _lastCompositionKeyboardMessage = message;

        if (!_hasCompositionKeyboardFocus ||
            RenderMode == NativeWebViewRenderMode.Embedded ||
            !_isAttached ||
            topLevelHandle == IntPtr.Zero)
        {
            Interlocked.Increment(ref _compositionKeyboardMessageRejectedCount);
            return false;
        }

        if (_controller.TryGetBackend<INativeWebViewCompositionInputSink>(out var inputSink) &&
            inputSink.SendKeyboardInput(message, wParam, lParam))
        {
            Interlocked.Increment(ref _compositionKeyboardMessageForwardedCount);
            return true;
        }

        var posted = false;
        Win32Input.EnumChildWindows(
            topLevelHandle,
            (childHandle, _) =>
            {
                posted |= Win32Input.PostMessage(childHandle, message, wParam, lParam);
                return true;
            },
            IntPtr.Zero);

        if (posted)
        {
            Interlocked.Increment(ref _compositionKeyboardMessageForwardedCount);
        }
        else
        {
            Interlocked.Increment(ref _compositionKeyboardMessageRejectedCount);
        }

        return posted;
    }

    private bool TryMapWin32MouseMessage(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        out NativeWebViewMouseInputKind kind,
        out Point localPosition,
        out NativeWebViewMouseInputModifiers modifiers,
        out int mouseData,
        out bool focus)
    {
        kind = default;
        localPosition = default;
        modifiers = default;
        mouseData = 0;
        focus = false;

        if (RenderMode == NativeWebViewRenderMode.Embedded || !_isAttached)
        {
            return false;
        }

        kind = message switch
        {
            Win32Input.WmMouseMove => NativeWebViewMouseInputKind.Move,
            Win32Input.WmLeftButtonDown => NativeWebViewMouseInputKind.LeftButtonDown,
            Win32Input.WmLeftButtonUp => NativeWebViewMouseInputKind.LeftButtonUp,
            Win32Input.WmRightButtonDown => NativeWebViewMouseInputKind.RightButtonDown,
            Win32Input.WmRightButtonUp => NativeWebViewMouseInputKind.RightButtonUp,
            Win32Input.WmMiddleButtonDown => NativeWebViewMouseInputKind.MiddleButtonDown,
            Win32Input.WmMiddleButtonUp => NativeWebViewMouseInputKind.MiddleButtonUp,
            Win32Input.WmMouseWheel => NativeWebViewMouseInputKind.Wheel,
            Win32Input.WmMouseHWheel => NativeWebViewMouseInputKind.HorizontalWheel,
            _ => default,
        };

        if (kind == default)
        {
            return false;
        }

        focus = kind is NativeWebViewMouseInputKind.LeftButtonDown
            or NativeWebViewMouseInputKind.RightButtonDown
            or NativeWebViewMouseInputKind.MiddleButtonDown;

        var clientPoint = message is Win32Input.WmMouseWheel or Win32Input.WmMouseHWheel
            ? Win32Input.ScreenPointFromLParam(lParam)
            : Win32Input.ClientPointFromLParam(lParam);
        if (message is Win32Input.WmMouseWheel or Win32Input.WmMouseHWheel)
        {
            _ = Win32Input.ScreenToClient(hWnd, ref clientPoint);
            mouseData = Win32Input.GetSignedHighWord(wParam);
        }

        modifiers = CreateMouseModifiers(wParam);
        return TryConvertTopLevelClientPointToLocal(clientPoint, out localPosition);
    }

    private bool TryGetCurrentTopLevelCursorLocalPosition(IntPtr hWnd, out Point localPosition)
    {
        localPosition = default;
        if (!Win32Input.GetCursorPos(out var screenPoint))
        {
            return false;
        }

        _ = Win32Input.ScreenToClient(hWnd, ref screenPoint);
        return TryConvertTopLevelClientPointToLocal(screenPoint, out localPosition);
    }

    private bool TryConvertTopLevelClientPointToLocal(Win32Input.NativePoint clientPoint, out Point localPosition)
    {
        localPosition = default;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            Interlocked.Increment(ref _compositionWin32PointerRejectedCount);
            return false;
        }

        var topLeft = Avalonia.VisualExtensions.TranslatePoint(this, new Point(0, 0), topLevel);
        if (topLeft is null)
        {
            Interlocked.Increment(ref _compositionWin32PointerRejectedCount);
            return false;
        }

        var scaling = topLevel.RenderScaling;
        var topLevelPoint = new Point(clientPoint.X / scaling, clientPoint.Y / scaling);
        localPosition = topLevelPoint - topLeft.Value;
        _lastCompositionWin32ClientX = clientPoint.X;
        _lastCompositionWin32ClientY = clientPoint.Y;
        _lastCompositionWin32TopLeftX = topLeft.Value.X;
        _lastCompositionWin32TopLeftY = topLeft.Value.Y;
        _lastCompositionWin32LocalX = localPosition.X;
        _lastCompositionWin32LocalY = localPosition.Y;
        var withinBounds = IsLocalPointInsideBounds(localPosition);
        if (!withinBounds)
        {
            Interlocked.Increment(ref _compositionWin32PointerRejectedCount);
        }

        return withinBounds;
    }

    private bool IsLocalPointInsideBounds(Point localPosition)
    {
        return localPosition.X >= 0 &&
               localPosition.Y >= 0 &&
               localPosition.X < Bounds.Width &&
               localPosition.Y < Bounds.Height;
    }

    private void RecordGpuCompositionUpdateElapsed(long elapsedTicks)
    {
        Interlocked.Increment(ref _gpuCompositionUpdateCount);
        Interlocked.Add(ref _gpuCompositionUpdateElapsedTicks, elapsedTicks);
        RecordMax(ref _gpuCompositionUpdateMaxTicks, elapsedTicks);
        if (IsElapsedOverMilliseconds(elapsedTicks, 16))
        {
            Interlocked.Increment(ref _gpuCompositionUpdateOver16MillisecondsCount);
        }

        if (IsElapsedOverMilliseconds(elapsedTicks, 33))
        {
            Interlocked.Increment(ref _gpuCompositionUpdateOver33MillisecondsCount);
        }

        if (IsElapsedOverMilliseconds(elapsedTicks, 50))
        {
            Interlocked.Increment(ref _gpuCompositionUpdateOver50MillisecondsCount);
        }
    }

    private static bool IsElapsedOverMilliseconds(long elapsedTicks, int milliseconds)
    {
        return elapsedTicks * 1000 > Stopwatch.Frequency * (long)milliseconds;
    }

    private static long GetAverageMicroseconds(long elapsedTicks, long count)
    {
        return count <= 0
            ? 0
            : GetElapsedMicroseconds(elapsedTicks / count);
    }

    private static long GetElapsedMicroseconds(long elapsedTicks)
    {
        return elapsedTicks <= 0
            ? 0
            : elapsedTicks * 1_000_000 / Stopwatch.Frequency;
    }

    private static void RecordMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
            current = Interlocked.Read(ref target);
        }
    }

    private bool ShouldForwardCompositionTopLevelPointer(PointerEventArgs e)
    {
        if (RenderMode == NativeWebViewRenderMode.Embedded || !_isAttached)
        {
            Interlocked.Increment(ref _compositionTopLevelPointerRejectedCount);
            return false;
        }

        var position = e.GetPosition(this);
        var withinBounds = position.X >= 0 &&
                           position.Y >= 0 &&
                           position.X < Bounds.Width &&
                           position.Y < Bounds.Height;
        if (!withinBounds)
        {
            Interlocked.Increment(ref _compositionTopLevelPointerRejectedCount);
        }

        return withinBounds;
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
            Focus(NavigationMethod.Pointer);
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

        var sent = TrySendCompositionMouseInput(input, focus, markFailed: true);
        if (sent)
        {
            e.Handled = true;
        }

        return sent;
    }

    private bool TrySendCompositionMouseInput(
        NativeWebViewMouseInputKind kind,
        Point localPosition,
        NativeWebViewMouseInputModifiers modifiers,
        int mouseData,
        bool focus)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var input = new NativeWebViewMouseInput(
            kind,
            modifiers,
            (int)Math.Round(localPosition.X * scaling, MidpointRounding.AwayFromZero),
            (int)Math.Round(localPosition.Y * scaling, MidpointRounding.AwayFromZero),
            mouseData);

        return TrySendCompositionMouseInput(input, focus, markFailed: true);
    }

    private bool TrySendCompositionMouseInput(NativeWebViewMouseInput input, bool focus, bool markFailed)
    {
        if (RenderMode == NativeWebViewRenderMode.Embedded ||
            !_controller.TryGetBackend<INativeWebViewCompositionInputSink>(out var inputSink))
        {
            return false;
        }

        if (focus)
        {
            Focus(NavigationMethod.Pointer);
            inputSink.FocusCompositionInput();
        }

        if (!inputSink.SendMouseInput(input))
        {
            if (markFailed)
            {
                Interlocked.Increment(ref _compositionMouseInputFailedCount);
            }

            return false;
        }

        _lastCompositionMouseInput = input;
        if (focus)
        {
            _hasCompositionKeyboardFocus = true;
        }

        Interlocked.Increment(ref _compositionMouseInputForwardedCount);
        RequestFreshGpuFramesForInteraction(input.Kind);
        return true;
    }

    private void RequestFreshGpuFramesForInteraction(NativeWebViewMouseInputKind inputKind)
    {
        if (RenderMode == NativeWebViewRenderMode.Embedded ||
            !EnableExperimentalGpuInterop ||
            !_controller.TryGetBackend<INativeWebViewGpuFrameSource>(out var gpuFrameSource))
        {
            return;
        }

        var effectiveRenderMode = GetEffectiveGpuInteropRenderMode();
        if (!gpuFrameSource.SupportsGpuFrame(effectiveRenderMode))
        {
            return;
        }

        var duration = inputKind is NativeWebViewMouseInputKind.Wheel or NativeWebViewMouseInputKind.HorizontalWheel
            ? TimeSpan.FromMilliseconds(900)
            : TimeSpan.FromMilliseconds(250);
        gpuFrameSource.RequestFreshGpuFrames(duration);
    }

    private NativeWebViewGpuFrame? TryGetLatestGpuFrame()
    {
        var effectiveRenderMode = GetEffectiveGpuInteropRenderMode();
        if (effectiveRenderMode == NativeWebViewRenderMode.Embedded ||
            !EnableExperimentalGpuInterop ||
            !_controller.TryGetBackend<INativeWebViewGpuFrameSource>(out var gpuFrameSource) ||
            !gpuFrameSource.SupportsGpuFrame(effectiveRenderMode))
        {
            _gpuInteropDiagnosticsMessage = null;
            return null;
        }

        return gpuFrameSource.TryGetLatestGpuFrame(effectiveRenderMode);
    }

    private void UpdateGpuInteropDiagnostics(string? message)
    {
        _gpuInteropDiagnosticsMessage = message;
    }

    private NativeWebViewRenderMode GetEffectiveGpuInteropRenderMode()
    {
        if (RenderMode == NativeWebViewRenderMode.Offscreen &&
            EnableExperimentalGpuInterop &&
            Features.Supports(NativeWebViewFeature.GpuSurfaceRendering))
        {
            return NativeWebViewRenderMode.GpuSurface;
        }

        return RenderMode;
    }

    private bool TryUpdateGpuCompositionSurface(NativeWebViewGpuFrame frame, Rect bounds)
    {
        if (frame.SharedHandle == 0 || string.IsNullOrWhiteSpace(frame.SharedHandleType))
        {
            UpdateGpuInteropDiagnostics("GPU composition interop: WebView frame does not expose a shared GPU handle.");
            return false;
        }

        var sharedHandleType = frame.SharedHandleType;
        if (_gpuCompositionUpdatedFrameId == frame.FrameId)
        {
            UpdateGpuCompositionVisualBounds();
            return true;
        }

        if (_gpuCompositionUpdateInProgress)
        {
            _pendingGpuCompositionFrame = frame;
            _pendingGpuCompositionBounds = bounds;
        }
        else
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
            if (frame.SharedHandle == 0 || string.IsNullOrWhiteSpace(frame.SharedHandleType))
            {
                UpdateGpuInteropDiagnostics("GPU composition interop: WebView frame does not expose a shared GPU handle.");
                return;
            }

            var sharedHandleType = frame.SharedHandleType;
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

            UpdateGpuInteropCapabilityDiagnostics(_gpuCompositionInterop, sharedHandleType);

            if (!_gpuCompositionInterop.SupportedImageHandleTypes.Contains(sharedHandleType))
            {
                UpdateGpuInteropDiagnostics(
                    $"GPU composition interop: handle type '{sharedHandleType}' is not supported by this Avalonia compositor.");
                return;
            }

            EnsureGpuCompositionVisual(compositor, bounds);

            if (!_gpuCompositionImportedImages.TryGetValue(frame.SharedHandle, out var cachedImage) ||
                cachedImage.HandleType != sharedHandleType ||
                cachedImage.Width != frame.PixelWidth ||
                cachedImage.Height != frame.PixelHeight)
            {
                if (cachedImage is not null)
                {
                    _ = cachedImage.Image.DisposeAsync().AsTask();
                }

                var properties = new PlatformGraphicsExternalImageProperties
                {
                    Width = frame.PixelWidth,
                    Height = frame.PixelHeight,
                    Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                    TopLeftOrigin = true,
                };

                cachedImage = new CachedGpuCompositionImportedImage(
                    _gpuCompositionInterop.ImportImage(
                        new PlatformHandle(frame.SharedHandle, sharedHandleType),
                        properties),
                    sharedHandleType,
                    frame.PixelWidth,
                    frame.PixelHeight);
                _gpuCompositionImportedImages[frame.SharedHandle] = cachedImage;
            }

            _gpuCompositionImportedImage = cachedImage.Image;
            _gpuCompositionImportedHandle = frame.SharedHandle;
            _gpuCompositionImportedHandleType = sharedHandleType;
            _gpuCompositionImportedWidth = frame.PixelWidth;
            _gpuCompositionImportedHeight = frame.PixelHeight;
            if (_gpuCompositionSurface is null || _gpuCompositionImportedImage is null)
            {
                return;
            }

            var updateStarted = Stopwatch.GetTimestamp();
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

            RecordGpuCompositionUpdateElapsed(Stopwatch.GetTimestamp() - updateStarted);

            _gpuCompositionUpdatedFrameId = frame.FrameId;
            _gpuInteropDiagnosticsMessage = null;
            _gpuCompositionVisual!.Visible = true;
            SetGpuFrameOnlyRenderingEnabled(true);
            UpdateFramePumpMode();
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            UpdateGpuInteropDiagnostics($"GPU composition interop failed: {ex.GetType().Name}: {ex.Message}");
            SetGpuFrameOnlyRenderingEnabled(false);
            DisposeGpuCompositionSurface();
            UpdateFramePumpMode();
        }
        finally
        {
            _gpuCompositionUpdateInProgress = false;
            if (_pendingGpuCompositionFrame is { } pendingFrame &&
                pendingFrame.FrameId > _gpuCompositionUpdatedFrameId)
            {
                var pendingBounds = _pendingGpuCompositionBounds;
                _pendingGpuCompositionFrame = null;
                _pendingGpuCompositionBounds = default;
                TryUpdateGpuCompositionSurface(pendingFrame, pendingBounds);
            }
            else
            {
                _pendingGpuCompositionFrame = null;
                _pendingGpuCompositionBounds = default;
            }
        }
    }

    private void UpdateGpuInteropCapabilityDiagnostics(ICompositionGpuInterop interop, string imageHandleType)
    {
        _gpuInteropSupportedImageHandleTypes = string.Join(", ", interop.SupportedImageHandleTypes);
        _gpuInteropSupportedSemaphoreTypes = string.Join(", ", interop.SupportedSemaphoreTypes);
        _gpuInteropSynchronizationCapabilities = interop
            .GetSynchronizationCapabilities(imageHandleType)
            .ToString();
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

        if (_gpuCompositionVisualBounds == bounds)
        {
            return;
        }

        _gpuCompositionVisualBounds = bounds;
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
        _gpuCompositionVisualBounds = default;
        _gpuCompositionUpdatedFrameId = 0;
        _gpuCompositionUpdateInProgress = false;
        _pendingGpuCompositionFrame = null;
        _pendingGpuCompositionBounds = default;
        SetGpuFrameOnlyRenderingEnabled(false);
        _isGpuFrameNotificationPumpActive = false;
    }

    private void ClearImportedGpuImage()
    {
        foreach (var importedImage in _gpuCompositionImportedImages.Values)
        {
            _ = importedImage.Image.DisposeAsync().AsTask();
        }

        _gpuCompositionImportedImages.Clear();
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

    private sealed class CachedGpuCompositionImportedImage(
        ICompositionImportedGpuImage image,
        string handleType,
        int width,
        int height)
    {
        public ICompositionImportedGpuImage Image { get; } = image;

        public string HandleType { get; } = handleType;

        public int Width { get; } = width;

        public int Height { get; } = height;
    }

    private void SetGpuFrameOnlyRenderingEnabled(bool enabled)
    {
        INativeWebViewGpuFrameSource? gpuFrameSource;
        try
        {
            if (!_controller.TryGetBackend(out gpuFrameSource))
            {
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (gpuFrameSource is not null)
        {
            gpuFrameSource.SetGpuFrameOnlyRenderingEnabled(
                enabled &&
                EnableExperimentalGpuInterop &&
                RenderMode != NativeWebViewRenderMode.Embedded &&
                gpuFrameSource.SupportsGpuFrame(GetEffectiveGpuInteropRenderMode()));
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

    private static NativeWebViewMouseInputModifiers CreateMouseModifiers(IntPtr wParam)
    {
        var flags = wParam.ToInt64();
        var modifiers = NativeWebViewMouseInputModifiers.None;

        if ((flags & Win32Input.MkLeftButton) != 0)
        {
            modifiers |= NativeWebViewMouseInputModifiers.LeftButton;
        }

        if ((flags & Win32Input.MkRightButton) != 0)
        {
            modifiers |= NativeWebViewMouseInputModifiers.RightButton;
        }

        if ((flags & Win32Input.MkMiddleButton) != 0)
        {
            modifiers |= NativeWebViewMouseInputModifiers.MiddleButton;
        }

        if ((flags & Win32Input.MkXButton1) != 0)
        {
            modifiers |= NativeWebViewMouseInputModifiers.XButton1;
        }

        if ((flags & Win32Input.MkXButton2) != 0)
        {
            modifiers |= NativeWebViewMouseInputModifiers.XButton2;
        }

        if ((flags & Win32Input.MkShift) != 0)
        {
            modifiers |= NativeWebViewMouseInputModifiers.Shift;
        }

        if ((flags & Win32Input.MkControl) != 0)
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
        if (TryGetGpuCompositionScaleOverride(out var overrideScale))
        {
            scale *= overrideScale;
        }

        request.PixelWidth = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
        request.PixelHeight = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        _lastRequestedFramePixelWidth = request.PixelWidth;
        _lastRequestedFramePixelHeight = request.PixelHeight;
        _lastRequestedFrameScale = scale;
        return true;
    }

    private static bool TryGetGpuCompositionScaleOverride(out double scale)
    {
        scale = 1d;
        var value = Environment.GetEnvironmentVariable("NATIVEWEBVIEW_GPU_COMPOSITION_SCALE");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed <= 0 ||
            parsed > 1)
        {
            return false;
        }

        scale = parsed;
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
            renderModeTarget.SetRenderMode(GetEffectiveGpuInteropRenderMode());
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
            var effectiveRenderMode = GetEffectiveGpuInteropRenderMode();
            gpuFrameSource.SetGpuFrameCaptureEnabled(
                EnableExperimentalGpuInterop &&
                RenderMode != NativeWebViewRenderMode.Embedded &&
                gpuFrameSource.SupportsGpuFrame(effectiveRenderMode));
        }

        if (!EnableExperimentalGpuInterop || RenderMode == NativeWebViewRenderMode.Embedded)
        {
            _gpuInteropDiagnosticsMessage = null;
            DisposeGpuCompositionSurface();
        }

        UpdateFramePumpMode();
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

    private static class Win32Input
    {
        public const uint WmMouseMove = 0x0200;
        public const uint WmLeftButtonDown = 0x0201;
        public const uint WmLeftButtonUp = 0x0202;
        public const uint WmRightButtonDown = 0x0204;
        public const uint WmRightButtonUp = 0x0205;
        public const uint WmMiddleButtonDown = 0x0207;
        public const uint WmMiddleButtonUp = 0x0208;
        public const uint WmMouseWheel = 0x020A;
        public const uint WmMouseHWheel = 0x020E;
        public const uint WmKeyDown = 0x0100;
        public const uint WmKeyUp = 0x0101;
        public const uint WmChar = 0x0102;
        public const uint WmSysKeyDown = 0x0104;
        public const uint WmSysKeyUp = 0x0105;
        public const uint WmSysChar = 0x0106;
        public const uint WmSetCursor = 0x0020;

        public const int MkLeftButton = 0x0001;
        public const int MkRightButton = 0x0002;
        public const int MkShift = 0x0004;
        public const int MkControl = 0x0008;
        public const int MkMiddleButton = 0x0010;
        public const int MkXButton1 = 0x0020;
        public const int MkXButton2 = 0x0040;

        public static NativePoint ClientPointFromLParam(IntPtr lParam)
        {
            var value = lParam.ToInt64();
            return new NativePoint(GetSignedLowWord(value), GetSignedHighWord(value));
        }

        public static NativePoint ScreenPointFromLParam(IntPtr lParam)
        {
            return ClientPointFromLParam(lParam);
        }

        public static int GetSignedHighWord(IntPtr value)
        {
            return GetSignedHighWord(value.ToInt64());
        }

        public static bool IsKeyboardMessage(uint message)
        {
            return message is WmKeyDown
                or WmKeyUp
                or WmChar
                or WmSysKeyDown
                or WmSysKeyUp
                or WmSysChar;
        }

        private static int GetSignedLowWord(long value)
        {
            return unchecked((short)(value & 0xFFFF));
        }

        private static int GetSignedHighWord(long value)
        {
            return unchecked((short)((value >> 16) & 0xFFFF));
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativePoint
        {
            public int X;
            public int Y;

            public NativePoint(int x, int y)
            {
                X = x;
                Y = y;
            }
        }
    }
}
