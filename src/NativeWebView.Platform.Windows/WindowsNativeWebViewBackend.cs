using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Web.WebView2.Core;
using NativeWebView.Core;
using NativeWebView.Interop;

namespace NativeWebView.Platform.Windows;

public sealed partial class WindowsNativeWebViewBackend
    : INativeWebViewBackend,
      INativeWebViewFrameSource,
      INativeWebViewGpuFrameSource,
      INativeWebViewGpuFrameDiagnosticsSource,
      INativeWebViewGpuFrameNotificationSource,
      INativeWebViewRenderModeTarget,
      INativeWebViewPlatformHandleProvider,
      INativeWebViewInstanceConfigurationTarget,
      INativeWebViewCompositionInputSink,
      INativeWebViewCompositionCursorSource,
      INativeWebViewNativeControlAttachment
{
    private const int EInvalidArgHResult = unchecked((int)0x80070057);
    private const int ErrorInvalidStateHResult = unchecked((int)0x8007139F);
    private static readonly TimeSpan TransientReparentNavigationSuppressionWindow = TimeSpan.FromMilliseconds(750);
    internal const string ControllerOptionsFallbackOriginalExceptionDataKey =
        "NativeWebView.Windows.ControllerOptionsFallbackOriginalException";
    private static readonly TimeSpan[] ControllerCreationRetryDelays =
    [
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromMilliseconds(750),
    ];
    private static readonly NativePlatformHandle PlaceholderPlatformHandle = new(0x1001, "HWND");
    private static readonly NativePlatformHandle PlaceholderViewHandle = new(0x1002, "ICoreWebView2");
    private static readonly NativePlatformHandle PlaceholderControllerHandle = new(0x1003, "ICoreWebView2Controller");
    private static readonly Lock WindowClassGate = new();
    private static readonly Win32.WndProc ChildWindowProcDelegate = ChildWindowProc;

    private static ushort _childWindowClassAtom;

    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly List<Uri> _history = [];
    private readonly INativeWebViewCommandManager _commandManager = NativeWebViewBackendSupport.NoopCommandManagerInstance;
    private readonly INativeWebViewCookieManager _cookieManager = NativeWebViewBackendSupport.NoopCookieManagerInstance;

    private TaskCompletionSource<bool> _attachmentTcs = CreatePendingAttachmentSource();
    private NativeWebViewInstanceConfiguration _instanceConfiguration = new();

    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2CompositionController? _compositionController;
    private CoreWebView2? _coreWebView;
    private NativeWebViewEnvironmentOptions? _preparedEnvironmentOptions;
    private NativeWebViewControllerOptions? _preparedControllerOptions;

    private Uri? _currentUrl;
    private Uri? _pendingNavigationUri;

    private GCHandle _selfHandle;
    private nint _parentWindowHandle;
    private nint _childWindowHandle;
    private nint _nativeHostPlaceholderHandle;
    private nint _viewComHandle;
    private nint _controllerComHandle;
    private int _lastAttachedWidth = 1;
    private int _lastAttachedHeight = 1;

    private int _historyIndex = -1;
    private long _frameSequence;
    private NativeWebViewRenderMode _renderMode = NativeWebViewRenderMode.Embedded;

    private bool _isStubInitialized;
    private bool _isRuntimeInitialized;
    private bool _coreInitializedRaised;
    private bool _runtimeInitializationRequested;
    private bool _disposed;

    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isDevToolsEnabled;
    private bool _isContextMenuEnabled;
    private bool _isStatusBarEnabled;
    private bool _isZoomControlEnabled;

    private double _zoomFactor;
    private DateTimeOffset _suppressSameUrlNavigationUntilUtc;
    private bool _suppressNextSameUrlNavigationCompletion;
    private int _compositionNavigationRetryVersion;
    private WinCompositionCaptureHost? _winCompositionCaptureHost;
    private WinCompositionDirectHost? _winCompositionDirectHost;
    private bool _gpuFrameCaptureRequested;
    private string? _headerString;
    private string? _userAgentString;

    public WindowsNativeWebViewBackend()
    {
        Platform = NativeWebViewPlatform.Windows;
        Features = WindowsPlatformFeatures.Instance;
        _zoomFactor = 1.0;
        _isDevToolsEnabled = Features.Supports(NativeWebViewFeature.DevTools);
        _isContextMenuEnabled = Features.Supports(NativeWebViewFeature.ContextMenu);
        _isStatusBarEnabled = Features.Supports(NativeWebViewFeature.StatusBar);
        _isZoomControlEnabled = Features.Supports(NativeWebViewFeature.ZoomControl);
    }

    public NativeWebViewPlatform Platform { get; }

    public IWebViewPlatformFeatures Features { get; }

    public event EventHandler? GpuFrameArrived;

    public Uri? CurrentUrl => _currentUrl;

    public bool IsInitialized => _isRuntimeInitialized || _isStubInitialized;

    public bool CanGoBack => _canGoBack;

    public bool CanGoForward => _canGoForward;

    public bool IsDevToolsEnabled
    {
        get => _isDevToolsEnabled;
        set
        {
            EnsureNotDisposed();
            _isDevToolsEnabled = value;
            ApplyRuntimeSettings();
        }
    }

    public bool IsContextMenuEnabled
    {
        get => _isContextMenuEnabled;
        set
        {
            EnsureNotDisposed();
            _isContextMenuEnabled = value;
            ApplyRuntimeSettings();
        }
    }

    public bool IsStatusBarEnabled
    {
        get => _isStatusBarEnabled;
        set
        {
            EnsureNotDisposed();
            _isStatusBarEnabled = value;
            ApplyRuntimeSettings();
        }
    }

    public bool IsZoomControlEnabled
    {
        get => _isZoomControlEnabled;
        set
        {
            EnsureNotDisposed();
            _isZoomControlEnabled = value;
            ApplyRuntimeSettings();
        }
    }

    public double ZoomFactor => _zoomFactor;

    public string? HeaderString => _headerString;

    public string? UserAgentString => _userAgentString;

    public event EventHandler<CoreWebViewInitializedEventArgs>? CoreWebView2Initialized;

    public event EventHandler<NativeWebViewNavigationStartedEventArgs>? NavigationStarted;

    public event EventHandler<NativeWebViewNavigationCompletedEventArgs>? NavigationCompleted;

    public event EventHandler<NativeWebViewMessageReceivedEventArgs>? WebMessageReceived;

    public event EventHandler<NativeWebViewOpenDevToolsRequestedEventArgs>? OpenDevToolsRequested;

    public event EventHandler<NativeWebViewDestroyRequestedEventArgs>? DestroyRequested;

#pragma warning disable CS0067
    public event EventHandler<NativeWebViewRequestCustomChromeEventArgs>? RequestCustomChrome;

    public event EventHandler<NativeWebViewRequestParentWindowPositionEventArgs>? RequestParentWindowPosition;

    public event EventHandler<NativeWebViewBeginMoveDragEventArgs>? BeginMoveDrag;

    public event EventHandler<NativeWebViewBeginResizeDragEventArgs>? BeginResizeDrag;
#pragma warning restore CS0067

    public event EventHandler<NativeWebViewNewWindowRequestedEventArgs>? NewWindowRequested;

    public event EventHandler<NativeWebViewResourceRequestedEventArgs>? WebResourceRequested;

    public event EventHandler<NativeWebViewContextMenuRequestedEventArgs>? ContextMenuRequested;

    public event EventHandler<NativeWebViewNavigationHistoryChangedEventArgs>? NavigationHistoryChanged;

    public event EventHandler<CoreWebViewEnvironmentRequestedEventArgs>? CoreWebView2EnvironmentRequested;

    public event EventHandler<CoreWebViewControllerOptionsRequestedEventArgs>? CoreWebView2ControllerOptionsRequested;

    public void ApplyInstanceConfiguration(NativeWebViewInstanceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureNotDisposed();

        _instanceConfiguration = configuration.Clone();

        if (!_coreInitializedRaised)
        {
            _preparedEnvironmentOptions = null;
            _preparedControllerOptions = null;
        }
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        EnsureFeature(NativeWebViewFeature.EmbeddedView, nameof(InitializeAsync));

        if (OperatingSystem.IsWindows())
        {
            _runtimeInitializationRequested = true;

            if (_childWindowHandle != IntPtr.Zero)
            {
                await EnsureRuntimeInitializedAsync(cancellationToken).ConfigureAwait(true);
                return;
            }
        }

        EnsureStubInitialized();
    }

    public void Navigate(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
        {
            throw new ArgumentException($"Invalid URL: {url}", nameof(url));
        }

        Navigate(uri);
    }

    public void Navigate(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        EnsureNotDisposed();
        EnsureFeature(NativeWebViewFeature.EmbeddedView, nameof(Navigate));

        if (ShouldUseRuntimePath())
        {
            _currentUrl = uri;
            _pendingNavigationUri = uri;
            _runtimeInitializationRequested = true;

            if (_coreWebView is not null)
            {
                NavigateCore(uri);
            }
            else
            {
                _ = TryInitializeRuntimeInBackgroundAsync();
            }

            return;
        }

        if (OperatingSystem.IsWindows())
        {
            _currentUrl = uri;
            _pendingNavigationUri = uri;
            _runtimeInitializationRequested = true;
            return;
        }

        NavigateFallback(uri);
    }

    public void Reload()
    {
        EnsureNotDisposed();
        EnsureFeature(NativeWebViewFeature.EmbeddedView, nameof(Reload));

        if (ShouldUseRuntimePath())
        {
            if (_coreWebView is not null)
            {
                try
                {
                    _coreWebView.Reload();
                }
                catch (Exception ex) when (IsInvalidRuntimeStateException(ex))
                {
                    RecoverInvalidRuntimeState(_currentUrl);
                }
            }
            else if (_currentUrl is not null)
            {
                _pendingNavigationUri = _currentUrl;
                _ = TryInitializeRuntimeInBackgroundAsync();
            }

            return;
        }

        if (_currentUrl is null)
        {
            return;
        }

        NavigationStarted?.Invoke(this, new NativeWebViewNavigationStartedEventArgs(_currentUrl, isRedirected: false));
        NavigationCompleted?.Invoke(this, new NativeWebViewNavigationCompletedEventArgs(_currentUrl, isSuccess: true, httpStatusCode: 200));
    }

    public void Stop()
    {
        EnsureNotDisposed();
        EnsureFeature(NativeWebViewFeature.EmbeddedView, nameof(Stop));

        if (_coreWebView is not null)
        {
            try
            {
                _coreWebView.Stop();
            }
            catch (Exception ex) when (IsInvalidRuntimeStateException(ex))
            {
                RecoverInvalidRuntimeState(_currentUrl);
            }
        }
    }

    public void GoBack()
    {
        EnsureNotDisposed();
        EnsureFeature(NativeWebViewFeature.EmbeddedView, nameof(GoBack));

        if (ShouldUseRuntimePath())
        {
            try
            {
                if (_coreWebView is not null && _coreWebView.CanGoBack)
                {
                    _coreWebView.GoBack();
                }
            }
            catch (Exception ex) when (IsInvalidRuntimeStateException(ex))
            {
                RecoverInvalidRuntimeState(_currentUrl);
            }

            return;
        }

        if (!CanGoBack)
        {
            return;
        }

        _historyIndex--;
        _currentUrl = _history[_historyIndex];
        UpdateHistorySnapshot(_historyIndex > 0, _historyIndex < _history.Count - 1);
        NavigationCompleted?.Invoke(this, new NativeWebViewNavigationCompletedEventArgs(_currentUrl, isSuccess: true, httpStatusCode: 200));
    }

    public void GoForward()
    {
        EnsureNotDisposed();
        EnsureFeature(NativeWebViewFeature.EmbeddedView, nameof(GoForward));

        if (ShouldUseRuntimePath())
        {
            try
            {
                if (_coreWebView is not null && _coreWebView.CanGoForward)
                {
                    _coreWebView.GoForward();
                }
            }
            catch (Exception ex) when (IsInvalidRuntimeStateException(ex))
            {
                RecoverInvalidRuntimeState(_currentUrl);
            }

            return;
        }

        if (!CanGoForward)
        {
            return;
        }

        _historyIndex++;
        _currentUrl = _history[_historyIndex];
        UpdateHistorySnapshot(_historyIndex > 0, _historyIndex < _history.Count - 1);
        NavigationCompleted?.Invoke(this, new NativeWebViewNavigationCompletedEventArgs(_currentUrl, isSuccess: true, httpStatusCode: 200));
    }

    public async Task<string?> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        EnsureFeature(NativeWebViewFeature.ScriptExecution, nameof(ExecuteScriptAsync));

        if (ShouldUseRuntimePath())
        {
            await EnsureRuntimeInitializedAsync(cancellationToken).ConfigureAwait(true);
            return await _coreWebView!.ExecuteScriptAsync(script).ConfigureAwait(true);
        }

        EnsureStubInitialized();
        return "null";
    }

    public async Task PostWebMessageAsJsonAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        EnsureFeature(NativeWebViewFeature.WebMessageChannel, nameof(PostWebMessageAsJsonAsync));
        var jsonMessage = NativeWebViewBackendSupport.NormalizeJsonMessagePayload(message);

        if (ShouldUseRuntimePath())
        {
            await EnsureRuntimeInitializedAsync(cancellationToken).ConfigureAwait(true);
            _coreWebView!.PostWebMessageAsJson(jsonMessage);
            return;
        }

        EnsureStubInitialized();
        WebMessageReceived?.Invoke(this, new NativeWebViewMessageReceivedEventArgs(message: null, json: jsonMessage));
    }

    public async Task PostWebMessageAsStringAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        EnsureFeature(NativeWebViewFeature.WebMessageChannel, nameof(PostWebMessageAsStringAsync));

        if (ShouldUseRuntimePath())
        {
            await EnsureRuntimeInitializedAsync(cancellationToken).ConfigureAwait(true);
            _coreWebView!.PostWebMessageAsString(message);
            return;
        }

        EnsureStubInitialized();
        WebMessageReceived?.Invoke(this, new NativeWebViewMessageReceivedEventArgs(message, json: null));
    }

    public void OpenDevToolsWindow()
    {
        EnsureNotDisposed();
        EnsureFeature(NativeWebViewFeature.DevTools, nameof(OpenDevToolsWindow));

        if (ShouldUseRuntimePath() && _coreWebView is not null && _isDevToolsEnabled)
        {
            _coreWebView.OpenDevToolsWindow();
        }

        OpenDevToolsRequested?.Invoke(this, new NativeWebViewOpenDevToolsRequestedEventArgs());
    }

    public async Task<NativeWebViewPrintResult> PrintAsync(
        NativeWebViewPrintSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();

        if (!Features.Supports(NativeWebViewFeature.Printing))
        {
            return new NativeWebViewPrintResult(NativeWebViewPrintStatus.NotSupported);
        }

        if (ShouldUseRuntimePath())
        {
            await EnsureRuntimeInitializedAsync(cancellationToken).ConfigureAwait(true);

            try
            {
                if (!string.IsNullOrWhiteSpace(settings?.OutputPath))
                {
                    var fullPath = Path.GetFullPath(settings.OutputPath!);
                    var directory = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var printed = await _coreWebView!.PrintToPdfAsync(fullPath, CreatePrintSettings(settings)).ConfigureAwait(true);
                    return printed
                        ? new NativeWebViewPrintResult(NativeWebViewPrintStatus.Success)
                        : new NativeWebViewPrintResult(NativeWebViewPrintStatus.Failed, $"Failed to write PDF to '{fullPath}'.");
                }

                var status = await _coreWebView!.PrintAsync(CreatePrintSettings(settings)).ConfigureAwait(true);
                return status == CoreWebView2PrintStatus.Succeeded
                    ? new NativeWebViewPrintResult(NativeWebViewPrintStatus.Success)
                    : new NativeWebViewPrintResult(NativeWebViewPrintStatus.Failed, status.ToString());
            }
            catch (Exception ex)
            {
                return new NativeWebViewPrintResult(NativeWebViewPrintStatus.Failed, ex.Message);
            }
        }

        EnsureStubInitialized();
        return new NativeWebViewPrintResult(NativeWebViewPrintStatus.Success);
    }

    public async Task<bool> ShowPrintUiAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();

        if (!Features.Supports(NativeWebViewFeature.PrintUi))
        {
            return false;
        }

        if (ShouldUseRuntimePath())
        {
            await EnsureRuntimeInitializedAsync(cancellationToken).ConfigureAwait(true);

            try
            {
                _coreWebView!.ShowPrintUI();
                return true;
            }
            catch
            {
                return false;
            }
        }

        EnsureStubInitialized();
        return true;
    }

    public void SetZoomFactor(double zoomFactor)
    {
        EnsureNotDisposed();

        if (zoomFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zoomFactor), zoomFactor, "Zoom factor must be greater than zero.");
        }

        _zoomFactor = zoomFactor;

        if (_controller is not null)
        {
            _controller.ZoomFactor = zoomFactor;
        }
    }

    public void SetUserAgent(string? userAgent)
    {
        EnsureNotDisposed();
        _userAgentString = userAgent;
        ApplyRuntimeSettings();
    }

    public void SetHeader(string? header)
    {
        EnsureNotDisposed();
        _headerString = header;
    }

    public bool TryGetCommandManager(out INativeWebViewCommandManager? commandManager)
    {
        EnsureNotDisposed();

        if (Features.Supports(NativeWebViewFeature.CommandManager))
        {
            commandManager = _commandManager;
            return true;
        }

        commandManager = null;
        return false;
    }

    public bool TryGetCookieManager(out INativeWebViewCookieManager? cookieManager)
    {
        EnsureNotDisposed();

        if (Features.Supports(NativeWebViewFeature.CookieManager))
        {
            cookieManager = _cookieManager;
            return true;
        }

        cookieManager = null;
        return false;
    }

    public void MoveFocus(NativeWebViewFocusMoveDirection direction)
    {
        EnsureNotDisposed();
        EnsureFeature(NativeWebViewFeature.EmbeddedView, nameof(MoveFocus));

        if (_controller is null)
        {
            return;
        }

        _controller.MoveFocus(direction switch
        {
            NativeWebViewFocusMoveDirection.Next => CoreWebView2MoveFocusReason.Next,
            NativeWebViewFocusMoveDirection.Previous => CoreWebView2MoveFocusReason.Previous,
            _ => CoreWebView2MoveFocusReason.Programmatic,
        });
    }

    public void FocusCompositionInput()
    {
        EnsureNotDisposed();

        if (_compositionController is null || _childWindowHandle == IntPtr.Zero)
        {
            return;
        }

        _ = Win32.SetFocus(_childWindowHandle);
        _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
    }

    public bool SendMouseInput(NativeWebViewMouseInput input)
    {
        EnsureNotDisposed();

        if (_compositionController is null)
        {
            return false;
        }

        var eventKind = input.Kind switch
        {
            NativeWebViewMouseInputKind.Move => CoreWebView2MouseEventKind.Move,
            NativeWebViewMouseInputKind.LeftButtonDown => CoreWebView2MouseEventKind.LeftButtonDown,
            NativeWebViewMouseInputKind.LeftButtonUp => CoreWebView2MouseEventKind.LeftButtonUp,
            NativeWebViewMouseInputKind.RightButtonDown => CoreWebView2MouseEventKind.RightButtonDown,
            NativeWebViewMouseInputKind.RightButtonUp => CoreWebView2MouseEventKind.RightButtonUp,
            NativeWebViewMouseInputKind.MiddleButtonDown => CoreWebView2MouseEventKind.MiddleButtonDown,
            NativeWebViewMouseInputKind.MiddleButtonUp => CoreWebView2MouseEventKind.MiddleButtonUp,
            NativeWebViewMouseInputKind.Wheel => CoreWebView2MouseEventKind.Wheel,
            NativeWebViewMouseInputKind.HorizontalWheel => CoreWebView2MouseEventKind.HorizontalWheel,
            NativeWebViewMouseInputKind.Leave => CoreWebView2MouseEventKind.Leave,
            _ => (CoreWebView2MouseEventKind)(-1),
        };

        if ((int)eventKind == -1)
        {
            return false;
        }

        try
        {
            _compositionController.SendMouseInput(
                eventKind,
                ToWebView2MouseModifiers(input.Modifiers),
                unchecked((uint)input.MouseData),
                new Point(input.X, input.Y));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SendKeyboardInput(uint message, nint wParam, nint lParam)
    {
        EnsureNotDisposed();

        if (_compositionController is null)
        {
            return false;
        }

        var parentHandle = _parentWindowHandle != IntPtr.Zero
            ? _parentWindowHandle
            : _childWindowHandle;
        if (parentHandle == IntPtr.Zero)
        {
            return false;
        }

        var posted = false;
        Win32.EnumChildWindows(
            parentHandle,
            (childHandle, _) =>
            {
                posted |= Win32.PostMessage(childHandle, message, wParam, lParam);
                return true;
            },
            IntPtr.Zero);

        return posted;
    }

    public bool SupportsRenderMode(NativeWebViewRenderMode renderMode)
    {
        return renderMode switch
        {
            NativeWebViewRenderMode.Embedded => Features.Supports(NativeWebViewFeature.EmbeddedView),
            NativeWebViewRenderMode.GpuSurface => Features.Supports(NativeWebViewFeature.GpuSurfaceRendering),
            NativeWebViewRenderMode.Offscreen => Features.Supports(NativeWebViewFeature.OffscreenRendering),
            _ => false,
        };
    }

    public async Task<NativeWebViewRenderFrame?> CaptureFrameAsync(
        NativeWebViewRenderMode renderMode,
        NativeWebViewRenderFrameRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(request);

        if (renderMode == NativeWebViewRenderMode.Embedded || !SupportsRenderMode(renderMode))
        {
            return null;
        }

        if (ShouldUseRuntimePath())
        {
            try
            {
                await EnsureRuntimeInitializedAsync(cancellationToken).ConfigureAwait(true);
                UpdateCompositedCaptureSize(request);
                var captured = await CapturePreviewFrameAsync(renderMode, request, cancellationToken).ConfigureAwait(false);
                if (captured is not null)
                {
                    return captured;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Fall back to a deterministic diagnostic frame if WebView2 capture is temporarily unavailable.
            }
        }

        return
            NativeWebViewBackendSupport.CreateSyntheticRenderFrame(
                Platform,
                _currentUrl,
                ref _frameSequence,
            renderMode,
            request.PixelWidth,
            request.PixelHeight);
    }

    public bool SupportsGpuFrame(NativeWebViewRenderMode renderMode)
    {
        return renderMode != NativeWebViewRenderMode.Embedded &&
               SupportsRenderMode(renderMode);
    }

    public NativeWebViewGpuFrame? TryGetLatestGpuFrame(NativeWebViewRenderMode renderMode)
    {
        EnsureNotDisposed();

        if (!SupportsGpuFrame(renderMode))
        {
            return null;
        }

        SetGpuFrameCaptureEnabled(true);
        return _winCompositionCaptureHost?.TryGetLatestGpuFrame(renderMode);
    }

    public void SetGpuFrameCaptureEnabled(bool enabled)
    {
        _gpuFrameCaptureRequested = enabled;
        _winCompositionCaptureHost?.SetGpuFrameCaptureEnabled(enabled);
    }

    public void SetGpuFrameOnlyRenderingEnabled(bool enabled)
    {
        _winCompositionCaptureHost?.SetGpuFrameOnlyRenderingEnabled(enabled);
    }

    public void RequestFreshGpuFrames(TimeSpan duration)
    {
        _winCompositionCaptureHost?.RequestFreshGpuFrames(duration);
    }

    public NativeWebViewGpuFrameDiagnosticsSnapshot GetGpuFrameDiagnosticsSnapshot()
    {
        var snapshot = _winCompositionCaptureHost?.GetGpuFrameDiagnosticsSnapshot() ?? default;
        var directRequested = ShouldUseDirectCompositionHost();
        var directHost = _winCompositionDirectHost;
        var childWindow = GetNativeWindowSnapshot(_childWindowHandle);
        var placeholderWindow = GetNativeWindowSnapshot(_nativeHostPlaceholderHandle);
        var parentWindow = GetNativeWindowSnapshot(_parentWindowHandle);

        return snapshot with
        {
            RequestedRenderMode = _renderMode,
            EffectiveRenderMode = _renderMode,
            Transport = directHost is not null
                ? NativeWebViewGpuFrameTransport.DirectCompositionChildWindow
                : snapshot.Transport,
            IsDirectCompositionRequested = directRequested,
            IsDirectCompositionActive = directHost is not null,
            DirectCompositionWindowHandle = directHost?.WindowHandle.ToInt64() ?? 0,
            NativeHostChildWindowHandle = childWindow.Handle,
            IsNativeHostChildWindowVisible = childWindow.IsVisible,
            NativeHostChildWindowLeft = childWindow.Left,
            NativeHostChildWindowTop = childWindow.Top,
            NativeHostChildWindowRight = childWindow.Right,
            NativeHostChildWindowBottom = childWindow.Bottom,
            NativeHostPlaceholderWindowHandle = placeholderWindow.Handle,
            IsNativeHostPlaceholderWindowVisible = placeholderWindow.IsVisible,
            NativeHostPlaceholderWindowLeft = placeholderWindow.Left,
            NativeHostPlaceholderWindowTop = placeholderWindow.Top,
            NativeHostPlaceholderWindowRight = placeholderWindow.Right,
            NativeHostPlaceholderWindowBottom = placeholderWindow.Bottom,
            NativeHostParentWindowHandle = parentWindow.Handle,
            IsNativeHostParentWindowVisible = parentWindow.IsVisible,
            NativeHostParentWindowLeft = parentWindow.Left,
            NativeHostParentWindowTop = parentWindow.Top,
            NativeHostParentWindowRight = parentWindow.Right,
            NativeHostParentWindowBottom = parentWindow.Bottom
        };
    }

    public nint CurrentCompositionCursorHandle => GetCompositionCursorHandle();

    private static NativeWindowDiagnosticsSnapshot GetNativeWindowSnapshot(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !Win32.IsWindow(handle))
        {
            return default;
        }

        _ = Win32.GetWindowRect(handle, out var rect);
        return new NativeWindowDiagnosticsSnapshot(
            handle.ToInt64(),
            Win32.IsWindowVisible(handle),
            rect.Left,
            rect.Top,
            rect.Right,
            rect.Bottom);
    }

    private void UpdateCompositedCaptureSize(NativeWebViewRenderFrameRequest request)
    {
        if (_compositionController is null)
        {
            return;
        }

        NotifyParentWindowPositionChanged();
        _lastAttachedWidth = Math.Max(1, request.PixelWidth);
        _lastAttachedHeight = Math.Max(1, request.PixelHeight);
        HideNativeHostPlaceholder();
        ResizeChildWindowForCompositedRendering();
        UpdateControllerBounds();
    }

    public void SetRenderMode(NativeWebViewRenderMode renderMode)
    {
        EnsureNotDisposed();

        if (_renderMode == renderMode)
        {
            return;
        }

        _renderMode = renderMode;

        if (_coreWebView is not null && IsCompositionMode(renderMode) != (_compositionController is not null))
        {
            SyncNavigationSnapshotFromRuntime();
            DestroyRuntimeController();
            _pendingNavigationUri = _currentUrl;
            _ = TryInitializeRuntimeInBackgroundAsync();
            return;
        }

        ApplyRenderModeVisibility();
    }

    public bool TryGetPlatformHandle(out NativePlatformHandle handle)
    {
        handle = _childWindowHandle != IntPtr.Zero
            ? new NativePlatformHandle(_childWindowHandle, "HWND")
            : PlaceholderPlatformHandle;
        return true;
    }

    public bool TryGetViewHandle(out NativePlatformHandle handle)
    {
        handle = _viewComHandle != IntPtr.Zero
            ? new NativePlatformHandle(_viewComHandle, "ICoreWebView2")
            : PlaceholderViewHandle;
        return true;
    }

    public bool TryGetControllerHandle(out NativePlatformHandle handle)
    {
        handle = _controllerComHandle != IntPtr.Zero
            ? new NativePlatformHandle(_controllerComHandle, "ICoreWebView2Controller")
            : PlaceholderControllerHandle;
        return true;
    }

    public NativePlatformHandle AttachToNativeParent(NativePlatformHandle parentHandle)
    {
        EnsureNotDisposed();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows native control attachment can only run on Windows.");
        }

        if (parentHandle.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Parent native handle is invalid.");
        }

        if (!string.Equals(parentHandle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Windows native control attachment requires an HWND parent, but received '{parentHandle.HandleDescriptor}'.");
        }

        var isCompositionMode = IsCompositionMode(_renderMode);
        if (_childWindowHandle != IntPtr.Zero)
        {
            _parentWindowHandle = parentHandle.Handle;
            if (isCompositionMode)
            {
                var placeholderHandle = EnsureNativeHostPlaceholder(parentHandle.Handle);
                _attachmentTcs.TrySetResult(true);
                return new NativePlatformHandle(placeholderHandle, "HWND");
            }

            if (_parentWindowHandle == parentHandle.Handle)
            {
                ShowPreservedChildWindow();
                return new NativePlatformHandle(_childWindowHandle, "HWND");
            }

            ReparentPreservedChildWindow(parentHandle.Handle);
            return new NativePlatformHandle(_childWindowHandle, "HWND");
        }

        EnsureChildWindowClassRegistered();

        var instanceHandle = Win32.GetModuleHandle(null);
        _parentWindowHandle = parentHandle.Handle;

        var childHandle = Win32.CreateWindowEx(
            0,
            Win32.ChildWindowClassName,
            string.Empty,
            Win32.WindowStyles.WS_CHILD |
            (isCompositionMode ? 0 : Win32.WindowStyles.WS_VISIBLE) |
            Win32.WindowStyles.WS_CLIPSIBLINGS |
            Win32.WindowStyles.WS_CLIPCHILDREN,
            0,
            0,
            1,
            1,
            parentHandle.Handle,
            IntPtr.Zero,
            instanceHandle,
            IntPtr.Zero);

        if (childHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Failed to create Windows child host window. Win32 error: {Marshal.GetLastWin32Error()}.");
        }

        _childWindowHandle = childHandle;
        _attachmentTcs.TrySetResult(true);

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        _selfHandle = GCHandle.Alloc(this);
        Win32.SetWindowLongPtr(childHandle, Win32.WindowLongIndex.GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));
        ResizeChildWindowToParent();

        UpdateControllerBounds();
        ApplyRenderModeVisibility();

        if (_runtimeInitializationRequested)
        {
            _ = TryInitializeRuntimeInBackgroundAsync();
        }

        if (isCompositionMode)
        {
            return new NativePlatformHandle(EnsureNativeHostPlaceholder(parentHandle.Handle), "HWND");
        }

        return new NativePlatformHandle(childHandle, "HWND");
    }

    public void DetachFromNativeParent()
    {
        EnsureNotDisposed();
        DetachFromNativeParentCore(preserveRuntime: false);
    }

    public void DetachFromNativeParent(bool preserveRuntime)
    {
        EnsureNotDisposed();
        DetachFromNativeParentCore(preserveRuntime);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            DetachFromNativeParentCore(preserveRuntime: false);
        }
        catch
        {
            // Best-effort shutdown for native resources.
        }

        _disposed = true;

        ReleaseComHandle(ref _viewComHandle);
        ReleaseComHandle(ref _controllerComHandle);

        _environment = null;
        _coreWebView = null;
        _controller = null;
        _preparedEnvironmentOptions = null;
        _preparedControllerOptions = null;

        DestroyRequested?.Invoke(this, new NativeWebViewDestroyRequestedEventArgs("Disposed"));
        _runtimeGate.Dispose();
    }

    private void DetachFromNativeParentCore(bool preserveRuntime)
    {
        DestroyNativeHostPlaceholder();

        if (preserveRuntime && _childWindowHandle != IntPtr.Zero)
        {
            HidePreservedChildWindow();
            _parentWindowHandle = IntPtr.Zero;
            _attachmentTcs = CreatePendingAttachmentSource();
            return;
        }

        SyncNavigationSnapshotFromRuntime();
        DestroyRuntimeController();

        if (_childWindowHandle != IntPtr.Zero)
        {
            Win32.SetWindowLongPtr(_childWindowHandle, Win32.WindowLongIndex.GWLP_USERDATA, IntPtr.Zero);

            if (Win32.IsWindow(_childWindowHandle))
            {
                Win32.DestroyWindow(_childWindowHandle);
            }
        }

        DestroyCompositionHost();

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        _childWindowHandle = IntPtr.Zero;
        _nativeHostPlaceholderHandle = IntPtr.Zero;
        _parentWindowHandle = IntPtr.Zero;
        _attachmentTcs = CreatePendingAttachmentSource();
    }

    private void ReparentPreservedChildWindow(nint parentWindowHandle)
    {
        if (_childWindowHandle == IntPtr.Zero)
        {
            return;
        }

        SuppressTransientSameUrlNavigation();
        _ = Win32.SetParent(_childWindowHandle, parentWindowHandle);

        _parentWindowHandle = parentWindowHandle;
        _attachmentTcs.TrySetResult(true);
        ShowPreservedChildWindow();
        ResizeChildWindowToParent();
        UpdateControllerBounds();
        ApplyRenderModeVisibility();
    }

    private void HidePreservedChildWindow()
    {
        SyncNavigationSnapshotFromRuntime();

        _ = Win32.ShowWindow(_childWindowHandle, Win32.ShowWindowCommand.Hide);
    }

    private void ShowPreservedChildWindow()
    {
        SuppressTransientSameUrlNavigation();
        _ = Win32.ShowWindow(_childWindowHandle, Win32.ShowWindowCommand.Show);
        ResizeChildWindowToParent();
        UpdateControllerBounds();
    }

    private nint EnsureNativeHostPlaceholder(nint parentWindowHandle)
    {
        if (_nativeHostPlaceholderHandle != IntPtr.Zero &&
            Win32.IsWindow(_nativeHostPlaceholderHandle))
        {
            if (Win32.GetParent(_nativeHostPlaceholderHandle) != parentWindowHandle)
            {
                _ = Win32.SetParent(_nativeHostPlaceholderHandle, parentWindowHandle);
            }

            HideNativeHostPlaceholder();
            return _nativeHostPlaceholderHandle;
        }

        EnsureChildWindowClassRegistered();

        var instanceHandle = Win32.GetModuleHandle(null);
        _nativeHostPlaceholderHandle = Win32.CreateWindowEx(
            0,
            Win32.ChildWindowClassName,
            string.Empty,
            Win32.WindowStyles.WS_CHILD |
            Win32.WindowStyles.WS_CLIPSIBLINGS |
            Win32.WindowStyles.WS_CLIPCHILDREN,
            -32000,
            -32000,
            1,
            1,
            parentWindowHandle,
            IntPtr.Zero,
            instanceHandle,
            IntPtr.Zero);

        if (_nativeHostPlaceholderHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Failed to create Windows native host placeholder. Win32 error: {Marshal.GetLastWin32Error()}.");
        }

        HideNativeHostPlaceholder();
        return _nativeHostPlaceholderHandle;
    }

    private void HideNativeHostPlaceholder()
    {
        if (_nativeHostPlaceholderHandle == IntPtr.Zero ||
            !Win32.IsWindow(_nativeHostPlaceholderHandle))
        {
            HideNativeHostWrapperForCompositedRendering();
            return;
        }

        _ = Win32.ShowWindow(_nativeHostPlaceholderHandle, Win32.ShowWindowCommand.ShowNoActivate);
        _ = Win32.SetWindowPos(
            _nativeHostPlaceholderHandle,
            IntPtr.Zero,
            -32000,
            -32000,
            1,
            1,
            Win32.SetWindowPosFlags.NoZOrder | Win32.SetWindowPosFlags.NoActivate | Win32.SetWindowPosFlags.ShowWindow);
        HideNativeHostWrapperForCompositedRendering();
    }

    private void HideNativeHostWrapperForCompositedRendering()
    {
        if (!IsCompositionMode(_renderMode) ||
            _parentWindowHandle == IntPtr.Zero ||
            !Win32.IsWindow(_parentWindowHandle))
        {
            return;
        }

        _ = Win32.ShowWindow(_parentWindowHandle, Win32.ShowWindowCommand.ShowNoActivate);
        _ = Win32.SetWindowPos(
            _parentWindowHandle,
            IntPtr.Zero,
            -32000,
            -32000,
            1,
            1,
            Win32.SetWindowPosFlags.NoZOrder | Win32.SetWindowPosFlags.NoActivate | Win32.SetWindowPosFlags.ShowWindow);
    }

    private void ShowNativeHostWrapperForEmbeddedRendering()
    {
        if (_parentWindowHandle == IntPtr.Zero ||
            !Win32.IsWindow(_parentWindowHandle))
        {
            return;
        }

        _ = Win32.ShowWindow(_parentWindowHandle, Win32.ShowWindowCommand.Show);
    }

    private void DestroyNativeHostPlaceholder()
    {
        if (_nativeHostPlaceholderHandle == IntPtr.Zero)
        {
            return;
        }

        if (Win32.IsWindow(_nativeHostPlaceholderHandle))
        {
            Win32.DestroyWindow(_nativeHostPlaceholderHandle);
        }

        _nativeHostPlaceholderHandle = IntPtr.Zero;
    }

    private void ResizeChildWindowToParent()
    {
        if (_childWindowHandle == IntPtr.Zero || _parentWindowHandle == IntPtr.Zero)
        {
            return;
        }

        if (!Win32.GetClientRect(_parentWindowHandle, out var rect))
        {
            return;
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        _lastAttachedWidth = width;
        _lastAttachedHeight = height;
        _ = Win32.SetWindowPos(
            _childWindowHandle,
            IntPtr.Zero,
            0,
            0,
            width,
            height,
            Win32.SetWindowPosFlags.NoZOrder | Win32.SetWindowPosFlags.NoActivate | Win32.SetWindowPosFlags.ShowWindow);
    }

    private void ResizeChildWindowForCompositedRendering()
    {
        if (_childWindowHandle == IntPtr.Zero)
        {
            return;
        }

        _ = Win32.SetWindowPos(
            _childWindowHandle,
            IntPtr.Zero,
            0,
            0,
            Math.Max(1, _lastAttachedWidth),
            Math.Max(1, _lastAttachedHeight),
            Win32.SetWindowPosFlags.NoZOrder | Win32.SetWindowPosFlags.NoActivate);
    }

    private static TaskCompletionSource<bool> CreatePendingAttachmentSource()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static IntPtr ChildWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var userData = Win32.GetWindowLongPtr(hwnd, Win32.WindowLongIndex.GWLP_USERDATA);
        if (userData != IntPtr.Zero)
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.IsAllocated && handle.Target is WindowsNativeWebViewBackend backend)
            {
                return backend.ProcessChildWindowMessage(hwnd, message, wParam, lParam);
            }
        }

        return Win32.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private IntPtr ProcessChildWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case Win32.WindowMessage.WM_SIZE:
                UpdateControllerBounds();
                break;

            case Win32.WindowMessage.WM_SETFOCUS:
                if (_controller is not null)
                {
                    _controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                }

                break;
        }

        return Win32.DefWindowProc(hwnd, message, wParam, lParam);
    }

    [SupportedOSPlatform("windows")]
    private async Task TryInitializeRuntimeInBackgroundAsync()
    {
        try
        {
            await EnsureRuntimeInitializedAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            // Explicit InitializeAsync should surface failures. Background warmup is best effort.
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task EnsureRuntimeInitializedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_isRuntimeInitialized && _coreWebView is not null && _controller is not null)
        {
            return;
        }

        await _runtimeGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_isRuntimeInitialized && _coreWebView is not null && _controller is not null)
            {
                return;
            }

            await WaitForAttachmentAsync(cancellationToken).ConfigureAwait(true);
            EnsurePreparedInitializationOptions();

            if (_environment is null)
            {
                _environment = await CreateRuntimeEnvironmentAsync(_preparedEnvironmentOptions!)
                    .ConfigureAwait(true);
            }

            if (_childWindowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Cannot initialize WebView2 without an attached child HWND.");
            }

            var controllerParentWindowHandle =
                IsCompositionMode(_renderMode) && _parentWindowHandle != IntPtr.Zero
                    ? _parentWindowHandle
                    : _childWindowHandle;
            _controller = await CreateRuntimeControllerAsync(
                _environment,
                controllerParentWindowHandle,
                _preparedControllerOptions,
                _renderMode,
                cancellationToken).ConfigureAwait(true);
            _compositionController = _controller as CoreWebView2CompositionController;

            _coreWebView = _controller.CoreWebView2;
            CaptureRuntimeHandles();
            AttachRuntimeEvents();
            ApplyRuntimeSettings();
            UpdateControllerBounds();
            EnsureCompositionRoot();
            ApplyRenderModeVisibility();
            var requestedPendingNavigationUri = _pendingNavigationUri;
            SyncNavigationSnapshotFromRuntime();
            _pendingNavigationUri = ResolveInitializationNavigationTarget(requestedPendingNavigationUri, _currentUrl);

            if (requestedPendingNavigationUri is not null)
            {
                NavigateCore(requestedPendingNavigationUri);
            }

            _isRuntimeInitialized = true;
            RaiseInitializedIfNeeded(success: true, initializationException: null, nativeObject: _coreWebView);
        }
        catch (Exception ex)
        {
            RaiseInitializedIfNeeded(success: false, initializationException: ex, nativeObject: null);
            throw;
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private async Task WaitForAttachmentAsync(CancellationToken cancellationToken)
    {
        if (_childWindowHandle != IntPtr.Zero)
        {
            return;
        }

        if (!cancellationToken.CanBeCanceled)
        {
            await _attachmentTcs.Task.ConfigureAwait(true);
            return;
        }

        var cancellationSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellationSource);

        var completed = await Task.WhenAny(_attachmentTcs.Task, cancellationSource.Task).ConfigureAwait(true);
        if (completed == cancellationSource.Task)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        await _attachmentTcs.Task.ConfigureAwait(true);
    }

    private void EnsurePreparedInitializationOptions()
    {
        if (_preparedEnvironmentOptions is not null)
        {
            return;
        }

        var environmentOptions = new NativeWebViewEnvironmentOptions();
        var controllerOptions = new NativeWebViewControllerOptions();
        _instanceConfiguration.ApplyEnvironmentOptions(environmentOptions);
        _instanceConfiguration.ApplyControllerOptions(controllerOptions);

        if (Features.Supports(NativeWebViewFeature.EnvironmentOptions))
        {
            CoreWebView2EnvironmentRequested?.Invoke(this, new CoreWebViewEnvironmentRequestedEventArgs(environmentOptions));
        }

        if (Features.Supports(NativeWebViewFeature.ControllerOptions))
        {
            CoreWebView2ControllerOptionsRequested?.Invoke(this, new CoreWebViewControllerOptionsRequestedEventArgs(controllerOptions));
        }

        _preparedEnvironmentOptions = environmentOptions.Clone();
        _preparedControllerOptions = controllerOptions.Clone();
    }

    private void EnsureStubInitialized()
    {
        if (_isStubInitialized)
        {
            return;
        }

        EnsurePreparedInitializationOptions();
        _isStubInitialized = true;
        RaiseInitializedIfNeeded(success: true, initializationException: null, nativeObject: null);
    }

    private void RaiseInitializedIfNeeded(bool success, Exception? initializationException, object? nativeObject)
    {
        if (_coreInitializedRaised)
        {
            return;
        }

        _coreInitializedRaised = true;
        CoreWebView2Initialized?.Invoke(this, new CoreWebViewInitializedEventArgs(success, initializationException, nativeObject));
    }

    [SupportedOSPlatformGuard("windows")]
    private bool ShouldUseRuntimePath()
    {
        return OperatingSystem.IsWindows() && _childWindowHandle != IntPtr.Zero;
    }

    private void NavigateFallback(Uri uri)
    {
        EnsureStubInitialized();

        var started = new NativeWebViewNavigationStartedEventArgs(uri, isRedirected: false);
        NavigationStarted?.Invoke(this, started);
        if (started.Cancel)
        {
            return;
        }

        if (_historyIndex < _history.Count - 1)
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        }

        _history.Add(uri);
        _historyIndex = _history.Count - 1;
        _currentUrl = uri;
        _pendingNavigationUri = uri;
        UpdateHistorySnapshot(_historyIndex > 0, _historyIndex < _history.Count - 1);
        NavigationCompleted?.Invoke(this, new NativeWebViewNavigationCompletedEventArgs(uri, isSuccess: true, httpStatusCode: 200));
    }

    private void UpdateHistorySnapshot(bool canGoBack, bool canGoForward)
    {
        var changed = _canGoBack != canGoBack || _canGoForward != canGoForward;
        _canGoBack = canGoBack;
        _canGoForward = canGoForward;

        if (changed)
        {
            NavigationHistoryChanged?.Invoke(this, new NativeWebViewNavigationHistoryChangedEventArgs(_canGoBack, _canGoForward));
        }
    }

    private void NavigateCore(Uri uri)
    {
        if (_coreWebView is null)
        {
            return;
        }

        _pendingNavigationUri = uri;
        var navigationTarget = uri.IsAbsoluteUri
            ? uri.AbsoluteUri
            : uri.ToString();

        if (!string.IsNullOrWhiteSpace(_headerString) &&
            _environment is not null &&
            (uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            var request = _environment.CreateWebResourceRequest(
                navigationTarget,
                "GET",
                null,
                _headerString);
            try
            {
                _coreWebView.NavigateWithWebResourceRequest(request);
            }
            catch (Exception ex) when (IsInvalidRuntimeStateException(ex))
            {
                RecoverInvalidRuntimeState(uri);
            }

            return;
        }

        try
        {
            _coreWebView.Navigate(navigationTarget);
            ScheduleCompositionNavigationRetry(uri, navigationTarget);
        }
        catch (Exception ex) when (IsInvalidRuntimeStateException(ex))
        {
            RecoverInvalidRuntimeState(uri);
        }
    }

    private async void ScheduleCompositionNavigationRetry(Uri uri, string navigationTarget)
    {
        if (_compositionController is null)
        {
            return;
        }

        var version = Interlocked.Increment(ref _compositionNavigationRetryVersion);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(true);

            if (_disposed ||
                version != _compositionNavigationRetryVersion ||
                _compositionController is null ||
                _coreWebView is null)
            {
                return;
            }

            if (!TryGetRuntimeSource(out var source))
            {
                RecoverInvalidRuntimeState(uri);
                return;
            }

            if (!IsBlankRuntimeSource(source))
            {
                return;
            }

            try
            {
                _coreWebView.Navigate(navigationTarget);
            }
            catch (Exception ex) when (IsInvalidRuntimeStateException(ex))
            {
                RecoverInvalidRuntimeState(uri);
                return;
            }
        }
    }

    private static bool IsBlankRuntimeSource(string? source)
    {
        return string.IsNullOrWhiteSpace(source) ||
            string.Equals(source, "about:blank", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyRuntimeSettings()
    {
        if (_coreWebView is null || _controller is null)
        {
            return;
        }

        var settings = _coreWebView.Settings;
        settings.AreDevToolsEnabled = _isDevToolsEnabled;
        settings.AreDefaultContextMenusEnabled = _isContextMenuEnabled;
        settings.IsStatusBarEnabled = _isStatusBarEnabled;
        settings.IsZoomControlEnabled = _isZoomControlEnabled;
        settings.IsWebMessageEnabled = true;
        settings.UserAgent = NormalizeRuntimeUserAgent(_userAgentString);

        if (_zoomFactor > 0)
        {
            _controller.ZoomFactor = _zoomFactor;
        }
    }

    internal static string NormalizeRuntimeUserAgent(string? userAgent)
    {
        return userAgent ?? string.Empty;
    }

    private void AttachRuntimeEvents()
    {
        if (_coreWebView is null || _controller is null)
        {
            return;
        }

        _coreWebView.NavigationStarting += OnNavigationStarting;
        _coreWebView.NavigationCompleted += OnNavigationCompleted;
        _coreWebView.WebMessageReceived += OnWebMessageReceived;
        _coreWebView.HistoryChanged += OnHistoryChanged;
        _coreWebView.NewWindowRequested += OnNewWindowRequested;
        _coreWebView.ContextMenuRequested += OnContextMenuRequested;
        _coreWebView.WindowCloseRequested += OnWindowCloseRequested;
        _coreWebView.WebResourceRequested += OnWebResourceRequested;
        _coreWebView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _controller.ZoomFactorChanged += OnZoomFactorChanged;

        if (_compositionController is not null)
        {
            _compositionController.CursorChanged += OnCompositionCursorChanged;
        }
    }

    private void DetachRuntimeEvents()
    {
        if (_coreWebView is not null)
        {
            try
            {
                _coreWebView.NavigationStarting -= OnNavigationStarting;
                _coreWebView.NavigationCompleted -= OnNavigationCompleted;
                _coreWebView.WebMessageReceived -= OnWebMessageReceived;
                _coreWebView.HistoryChanged -= OnHistoryChanged;
                _coreWebView.NewWindowRequested -= OnNewWindowRequested;
                _coreWebView.ContextMenuRequested -= OnContextMenuRequested;
                _coreWebView.WindowCloseRequested -= OnWindowCloseRequested;
                _coreWebView.WebResourceRequested -= OnWebResourceRequested;
            }
            catch
            {
                // Ignore teardown failures from a WebView2 instance that is already invalid.
            }

            try
            {
                _coreWebView.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            }
            catch
            {
                // Ignore teardown failures from optional filters.
            }
        }

        if (_controller is not null)
        {
            try
            {
                _controller.ZoomFactorChanged -= OnZoomFactorChanged;
            }
            catch
            {
                // Ignore teardown failures from a controller that is already invalid.
            }
        }

        if (_compositionController is not null)
        {
            try
            {
                _compositionController.CursorChanged -= OnCompositionCursorChanged;
            }
            catch
            {
                // Ignore teardown failures from a composition controller that is already invalid.
            }
        }
    }

    private void OnCompositionCursorChanged(object? sender, object e)
    {
        _ = sender;
        _ = e;

        ApplyCompositionCursor();
    }

    private void ApplyCompositionCursor()
    {
        try
        {
            var cursorHandle = GetCompositionCursorHandle();
            if (cursorHandle != IntPtr.Zero)
            {
                _ = Win32.SetCursor(cursorHandle);
            }
        }
        catch
        {
            // Cursor updates are best-effort; input forwarding must continue even if WebView2 is tearing down.
        }
    }

    private nint GetCompositionCursorHandle()
    {
        if (_compositionController is null)
        {
            return IntPtr.Zero;
        }

        return _compositionController.SystemCursorId != 0
            ? Win32.LoadCursor(IntPtr.Zero, unchecked((int)_compositionController.SystemCursorId))
            : _compositionController.Cursor;
    }

    private void DestroyRuntimeController()
    {
        if (_controller is null && _coreWebView is null)
        {
            return;
        }

        DetachRuntimeEvents();
        ReleaseComHandle(ref _viewComHandle);
        ReleaseComHandle(ref _controllerComHandle);
        DestroyCompositionHost();

        if (_controller is not null)
        {
            try
            {
                _controller.Close();
            }
            catch
            {
                // Best-effort shutdown.
            }
        }

        _controller = null;
        _compositionController = null;
        _coreWebView = null;
        _isRuntimeInitialized = false;
        UpdateHistorySnapshot(canGoBack: false, canGoForward: false);
    }

    [SupportedOSPlatform("windows")]
    private void CaptureRuntimeHandles()
    {
        ReleaseComHandle(ref _viewComHandle);
        ReleaseComHandle(ref _controllerComHandle);

        if (_coreWebView is not null)
        {
            _viewComHandle = Marshal.GetIUnknownForObject(_coreWebView);
        }

        if (_controller is not null)
        {
            _controllerComHandle = Marshal.GetIUnknownForObject(_controller);
        }
    }

    private void UpdateControllerBounds()
    {
        if (_controller is null || _childWindowHandle == IntPtr.Zero)
        {
            return;
        }

        NotifyParentWindowPositionChanged();
        if (_compositionController is not null)
        {
            _controller.Bounds = new Rectangle(
                0,
                0,
                Math.Max(1, _lastAttachedWidth),
                Math.Max(1, _lastAttachedHeight));
            return;
        }

        if (!Win32.GetClientRect(_childWindowHandle, out var rect))
        {
            return;
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        _controller.Bounds = new Rectangle(0, 0, width, height);
    }

    private void NotifyParentWindowPositionChanged()
    {
        if (_controller is null)
        {
            return;
        }

        try
        {
            _controller.NotifyParentWindowPositionChanged();
        }
        catch
        {
            // Best-effort synchronization for composition-hosted WebView2.
        }
    }

    private void EnsureCompositionRoot()
    {
        if (_compositionController is null || _childWindowHandle == IntPtr.Zero)
        {
            return;
        }

        if (ShouldUseDirectCompositionHost())
        {
            EnsureDirectCompositionRoot();
            return;
        }

        DestroyDirectCompositionHost();
        if (_winCompositionCaptureHost is null)
        {
            _winCompositionCaptureHost = new WinCompositionCaptureHost();
            _winCompositionCaptureHost.FrameArrived += OnCompositionCaptureFrameArrived;
            _winCompositionCaptureHost.SetGpuFrameCaptureEnabled(_gpuFrameCaptureRequested);
        }

        _compositionController.RootVisualTarget = _winCompositionCaptureHost.WebViewVisual;
        UpdateControllerBounds();
    }

    private void EnsureDirectCompositionRoot()
    {
        if (_compositionController is null || _childWindowHandle == IntPtr.Zero)
        {
            return;
        }

        DestroyCaptureCompositionHost();
        if (_parentWindowHandle == IntPtr.Zero)
        {
            return;
        }

        EnsureChildWindowClassRegistered();
        _winCompositionDirectHost ??= new WinCompositionDirectHost(_parentWindowHandle);
        _winCompositionDirectHost.Resize(Math.Max(1, _lastAttachedWidth), Math.Max(1, _lastAttachedHeight));
        _compositionController.RootVisualTarget = _winCompositionDirectHost.WebViewVisual;
        ResizeChildWindowForCompositedRendering();
        UpdateControllerBounds();
    }

    private static bool ShouldUseDirectCompositionHost()
    {
        var value = Environment.GetEnvironmentVariable("NATIVEWEBVIEW_WINDOWS_DIRECT_COMPOSITION");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private void OnCompositionCaptureFrameArrived(object? sender, EventArgs e)
    {
        GpuFrameArrived?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyRenderModeVisibility()
    {
        if (_childWindowHandle == IntPtr.Zero)
        {
            return;
        }

        var isEmbedded = _renderMode == NativeWebViewRenderMode.Embedded;
        if (isEmbedded)
        {
            RestoreEmbeddedChildWindow();
        }
        else if (_compositionController is not null)
        {
            HideNativeHostPlaceholder();
        }
        else
        {
            HideNativeHostPlaceholder();
        }

        if (_controller is not null)
        {
            try
            {
                _controller.IsVisible = true;
            }
            catch
            {
                // Visibility is best-effort; the HWND z-order is what affects Avalonia overlays.
            }
        }

        if (isEmbedded)
        {
            ResizeChildWindowToParent();
            UpdateControllerBounds();
        }
        else if (_compositionController is not null)
        {
            ResizeChildWindowForCompositedRendering();
            UpdateControllerBounds();
            EnsureCompositionRoot();
        }
    }

    private void RestoreEmbeddedChildWindow()
    {
        if (_childWindowHandle == IntPtr.Zero)
        {
            return;
        }

        if (_parentWindowHandle != IntPtr.Zero &&
            Win32.GetParent(_childWindowHandle) != _parentWindowHandle)
        {
            ShowNativeHostWrapperForEmbeddedRendering();
            SuppressTransientSameUrlNavigation();
            _ = Win32.SetParent(_childWindowHandle, _parentWindowHandle);
        }

        ShowNativeHostWrapperForEmbeddedRendering();
        _ = Win32.ShowWindow(_childWindowHandle, Win32.ShowWindowCommand.Show);
    }

    [SupportedOSPlatform("windows")]
    private async Task<NativeWebViewRenderFrame?> CapturePreviewFrameAsync(
        NativeWebViewRenderMode renderMode,
        NativeWebViewRenderFrameRequest request,
        CancellationToken cancellationToken)
    {
        if (_coreWebView is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_compositionController is not null &&
            _winCompositionCaptureHost is not null &&
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            var capturedFrame = await _winCompositionCaptureHost
                .CaptureFrameAsync(renderMode, request, Interlocked.Increment(ref _frameSequence), cancellationToken)
                .ConfigureAwait(true);
            if (capturedFrame is not null)
            {
                return capturedFrame;
            }
        }

        await using var stream = new MemoryStream();
        await _coreWebView.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
            .ConfigureAwait(true);

        cancellationToken.ThrowIfCancellationRequested();

        if (stream.Length == 0)
        {
            return null;
        }

        stream.Position = 0;
        var width = Math.Max(1, request.PixelWidth);
        var height = Math.Max(1, request.PixelHeight);
        using var source = new Bitmap(stream);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImage(source, 0, 0, width, height);
        }

        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytesPerRow = width * 4;
            var pixelData = new byte[bytesPerRow * height];
            var sourceStride = Math.Abs(data.Stride);

            for (var row = 0; row < height; row++)
            {
                var sourceRow = data.Stride >= 0
                    ? IntPtr.Add(data.Scan0, row * data.Stride)
                    : IntPtr.Add(data.Scan0, (height - 1 - row) * sourceStride);
                Marshal.Copy(sourceRow, pixelData, row * bytesPerRow, bytesPerRow);
            }

            return new NativeWebViewRenderFrame(
                width,
                height,
                bytesPerRow,
                NativeWebViewRenderPixelFormat.Bgra8888Premultiplied,
                pixelData,
                isSynthetic: false,
                frameId: Interlocked.Increment(ref _frameSequence),
                capturedAtUtc: DateTimeOffset.UtcNow,
                renderMode: renderMode,
                origin: NativeWebViewRenderFrameOrigin.NativeCapture);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void SyncNavigationSnapshotFromRuntime()
    {
        if (_coreWebView is null)
        {
            return;
        }

        try
        {
            _currentUrl = TryCreateUri(_coreWebView.Source) ?? _currentUrl;
            _pendingNavigationUri = _currentUrl;
            UpdateHistorySnapshot(_coreWebView.CanGoBack, _coreWebView.CanGoForward);
        }
        catch (Exception ex) when (IsInvalidRuntimeStateException(ex))
        {
            RecoverInvalidRuntimeState(_currentUrl);
        }
    }

    internal static Uri? ResolveInitializationNavigationTarget(Uri? requestedPendingNavigationUri, Uri? runtimeCurrentUri)
    {
        return requestedPendingNavigationUri ?? runtimeCurrentUri;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = TryCreateUri(e.Uri);
        if (ShouldSuppressTransientSameUrlNavigation(uri, e.IsRedirected))
        {
            e.Cancel = true;
            _suppressNextSameUrlNavigationCompletion = true;
            return;
        }

        var forwarded = new NativeWebViewNavigationStartedEventArgs(uri, e.IsRedirected);
        NavigationStarted?.Invoke(this, forwarded);
        e.Cancel = forwarded.Cancel;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_suppressNextSameUrlNavigationCompletion)
        {
            _suppressNextSameUrlNavigationCompletion = false;
            SyncNavigationSnapshotFromRuntime();
            return;
        }

        SyncNavigationSnapshotFromRuntime();
        var uri = _currentUrl ?? TryCreateUri(_coreWebView?.Source);
        var statusCode = e.IsSuccess ? TryConvertHttpStatusCode(e.HttpStatusCode) : null;
        var error = e.IsSuccess ? null : e.WebErrorStatus.ToString();
        NavigationCompleted?.Invoke(this, new NativeWebViewNavigationCompletedEventArgs(uri, e.IsSuccess, statusCode, error));
    }

    private void SuppressTransientSameUrlNavigation()
    {
        // WebView2 can emit a same-URL navigation while a preserved HWND is reparented.
        // Treat that as native-host churn, not page navigation.
        _suppressSameUrlNavigationUntilUtc = DateTimeOffset.UtcNow.Add(TransientReparentNavigationSuppressionWindow);
    }

    private bool ShouldSuppressTransientSameUrlNavigation(Uri? uri, bool isRedirected)
    {
        if (isRedirected ||
            uri is null ||
            _currentUrl is null ||
            DateTimeOffset.UtcNow > _suppressSameUrlNavigationUntilUtc)
        {
            return false;
        }

        return string.Equals(
            NormalizeNavigationComparisonUri(uri),
            NormalizeNavigationComparisonUri(_currentUrl),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeNavigationComparisonUri(Uri uri)
    {
        return uri.IsAbsoluteUri
            ? uri.AbsoluteUri
            : uri.ToString();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? message = null;

        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch
        {
            // The payload was not a simple string message.
        }

        WebMessageReceived?.Invoke(this, new NativeWebViewMessageReceivedEventArgs(message, e.WebMessageAsJson));
    }

    private void OnHistoryChanged(object? sender, object e)
    {
        if (_coreWebView is null)
        {
            return;
        }

        UpdateHistorySnapshot(_coreWebView.CanGoBack, _coreWebView.CanGoForward);
    }

    private void OnZoomFactorChanged(object? sender, object e)
    {
        if (_controller is not null)
        {
            _zoomFactor = _controller.ZoomFactor;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var forwarded = new NativeWebViewNewWindowRequestedEventArgs(TryCreateUri(e.Uri));
        NewWindowRequested?.Invoke(this, forwarded);
        e.Handled = forwarded.Handled;
    }

    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var forwarded = new NativeWebViewContextMenuRequestedEventArgs(e.Location.X, e.Location.Y);
        ContextMenuRequested?.Invoke(this, forwarded);
        e.Handled = forwarded.Handled;
    }

    private void OnWindowCloseRequested(object? sender, object e)
    {
        DestroyRequested?.Invoke(this, new NativeWebViewDestroyRequestedEventArgs("WindowCloseRequested"));
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var forwarded = new NativeWebViewResourceRequestedEventArgs(
            TryCreateUri(e.Request.Uri),
            e.Request.Method);

        WebResourceRequested?.Invoke(this, forwarded);
        if (!forwarded.Handled || _environment is null)
        {
            return;
        }

        var responseBody = forwarded.ResponseBody ?? string.Empty;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(responseBody));
        var headers = string.IsNullOrWhiteSpace(forwarded.ContentType)
            ? string.Empty
            : $"Content-Type: {forwarded.ContentType}";

        e.Response = _environment.CreateWebResourceResponse(
            stream,
            forwarded.StatusCode,
            "Handled",
            headers);
    }

    internal static bool RequiresRuntimeEnvironmentOptions(NativeWebViewEnvironmentOptions? options)
    {
        if (options is null)
        {
            return false;
        }

        var browserArguments = NativeWebViewWindowsProxyArgumentsBuilder.Merge(
            options.AdditionalBrowserArguments,
            options.Proxy);

        return !string.IsNullOrWhiteSpace(browserArguments) ||
            options.AllowSingleSignOnUsingOSPrimaryAccount ||
            !string.IsNullOrWhiteSpace(options.Language) ||
            !string.IsNullOrWhiteSpace(options.TargetCompatibleBrowserVersion);
    }

    internal static bool ShouldRetryEnvironmentCreationWithoutOptions(
        NativeWebViewEnvironmentOptions? options,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return RequiresRuntimeEnvironmentOptions(options) &&
            IsTransientEnvironmentCreationFailure(exception);
    }

    internal static bool IsTransientEnvironmentCreationFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is ArgumentException ||
            exception is COMException { HResult: EInvalidArgHResult };
    }

    private static async Task<CoreWebView2Environment> CreateRuntimeEnvironmentAsync(NativeWebViewEnvironmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var browserExecutableFolder = NormalizePath(options.BrowserExecutableFolder);
        var userDataFolder = NormalizePath(options.UserDataFolder);

        if (!RequiresRuntimeEnvironmentOptions(options))
        {
            return await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: browserExecutableFolder,
                    userDataFolder: userDataFolder,
                    options: null)
                .ConfigureAwait(true);
        }

        try
        {
            return await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: browserExecutableFolder,
                    userDataFolder: userDataFolder,
                    options: CreateRuntimeEnvironmentOptions(options))
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ShouldRetryEnvironmentCreationWithoutOptions(options, ex))
        {
            return await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: browserExecutableFolder,
                    userDataFolder: userDataFolder,
                    options: null)
                .ConfigureAwait(true);
        }
    }

    private static CoreWebView2EnvironmentOptions CreateRuntimeEnvironmentOptions(NativeWebViewEnvironmentOptions options)
    {
        return new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = NativeWebViewWindowsProxyArgumentsBuilder.Merge(
                options.AdditionalBrowserArguments,
                options.Proxy),
            AllowSingleSignOnUsingOSPrimaryAccount = options.AllowSingleSignOnUsingOSPrimaryAccount,
            Language = options.Language,
            TargetCompatibleBrowserVersion = options.TargetCompatibleBrowserVersion,
        };
    }

    internal static bool RequiresRuntimeControllerOptions(NativeWebViewControllerOptions? options)
    {
        return options is not null &&
            (!string.IsNullOrWhiteSpace(options.ProfileName) ||
             options.IsInPrivateModeEnabled ||
             !string.IsNullOrWhiteSpace(options.ScriptLocale));
    }

    internal static bool ShouldRetryControllerCreationWithoutOptions(
        NativeWebViewControllerOptions? options,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return RequiresRuntimeControllerOptions(options) &&
            IsTransientControllerCreationFailure(exception);
    }

    internal static bool IsTransientControllerCreationFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is ArgumentException ||
            exception is COMException { HResult: EInvalidArgHResult };
    }

    private static bool IsInvalidRuntimeStateException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is InvalidOperationException { InnerException: COMException { HResult: ErrorInvalidStateHResult } } ||
            exception is COMException { HResult: ErrorInvalidStateHResult };
    }

    private bool TryGetRuntimeSource(out string? source)
    {
        source = null;
        if (_coreWebView is null)
        {
            return false;
        }

        try
        {
            source = _coreWebView.Source;
            return true;
        }
        catch (Exception ex) when (IsInvalidRuntimeStateException(ex))
        {
            return false;
        }
    }

    private void RecoverInvalidRuntimeState(Uri? navigationTarget)
    {
        if (_disposed)
        {
            return;
        }

        _pendingNavigationUri = navigationTarget ?? _currentUrl;
        _runtimeInitializationRequested = true;
        DestroyRuntimeController();

        if (ShouldUseRuntimePath())
        {
            _ = TryInitializeRuntimeInBackgroundAsync();
        }
    }

    private static bool IsCompositionMode(NativeWebViewRenderMode renderMode)
    {
        return renderMode is NativeWebViewRenderMode.GpuSurface or NativeWebViewRenderMode.Offscreen;
    }

    private static CoreWebView2MouseEventVirtualKeys ToWebView2MouseModifiers(NativeWebViewMouseInputModifiers modifiers)
    {
        var result = CoreWebView2MouseEventVirtualKeys.None;

        if (modifiers.HasFlag(NativeWebViewMouseInputModifiers.LeftButton))
        {
            result |= CoreWebView2MouseEventVirtualKeys.LeftButton;
        }

        if (modifiers.HasFlag(NativeWebViewMouseInputModifiers.RightButton))
        {
            result |= CoreWebView2MouseEventVirtualKeys.RightButton;
        }

        if (modifiers.HasFlag(NativeWebViewMouseInputModifiers.Shift))
        {
            result |= CoreWebView2MouseEventVirtualKeys.Shift;
        }

        if (modifiers.HasFlag(NativeWebViewMouseInputModifiers.Control))
        {
            result |= CoreWebView2MouseEventVirtualKeys.Control;
        }

        if (modifiers.HasFlag(NativeWebViewMouseInputModifiers.MiddleButton))
        {
            result |= CoreWebView2MouseEventVirtualKeys.MiddleButton;
        }

        if (modifiers.HasFlag(NativeWebViewMouseInputModifiers.XButton1))
        {
            result |= CoreWebView2MouseEventVirtualKeys.XButton1;
        }

        if (modifiers.HasFlag(NativeWebViewMouseInputModifiers.XButton2))
        {
            result |= CoreWebView2MouseEventVirtualKeys.XButton2;
        }

        return result;
    }

    private static async Task<CoreWebView2Controller> CreateRuntimeControllerAsync(
        CoreWebView2Environment environment,
        IntPtr childWindowHandle,
        NativeWebViewControllerOptions? options,
        NativeWebViewRenderMode renderMode,
        CancellationToken cancellationToken)
    {
        if (IsCompositionMode(renderMode))
        {
            return await CreateRuntimeCompositionControllerAsync(
                    environment,
                    childWindowHandle,
                    options,
                    cancellationToken)
                .ConfigureAwait(true);
        }

        if (!RequiresRuntimeControllerOptions(options))
        {
            return await CreateRuntimeControllerWithRetryAsync(
                    () => environment.CreateCoreWebView2ControllerAsync(childWindowHandle),
                    cancellationToken)
                .ConfigureAwait(true);
        }

        try
        {
            var controllerOptions = CreateRuntimeControllerOptions(environment, options!);
            return await CreateRuntimeControllerWithRetryAsync(
                    () => environment.CreateCoreWebView2ControllerAsync(childWindowHandle, controllerOptions),
                    cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ShouldRetryControllerCreationWithoutOptions(options, ex))
        {
            try
            {
                return await CreateRuntimeControllerWithRetryAsync(
                        () => environment.CreateCoreWebView2ControllerAsync(childWindowHandle),
                        cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception retryException)
            {
                AttachControllerOptionsFallbackExceptionContext(retryException, ex);
                throw;
            }
        }
    }

    private static async Task<CoreWebView2Controller> CreateRuntimeCompositionControllerAsync(
        CoreWebView2Environment environment,
        IntPtr childWindowHandle,
        NativeWebViewControllerOptions? options,
        CancellationToken cancellationToken)
    {
        if (!RequiresRuntimeControllerOptions(options))
        {
            return await CreateRuntimeControllerWithRetryAsync(
                    () => environment.CreateCoreWebView2CompositionControllerAsync(childWindowHandle),
                    cancellationToken)
                .ConfigureAwait(true);
        }

        try
        {
            var controllerOptions = CreateRuntimeControllerOptions(environment, options!);
            return await CreateRuntimeControllerWithRetryAsync(
                    () => environment.CreateCoreWebView2CompositionControllerAsync(childWindowHandle, controllerOptions),
                    cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ShouldRetryControllerCreationWithoutOptions(options, ex))
        {
            try
            {
                return await CreateRuntimeControllerWithRetryAsync(
                        () => environment.CreateCoreWebView2CompositionControllerAsync(childWindowHandle),
                        cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception retryException)
            {
                AttachControllerOptionsFallbackExceptionContext(retryException, ex);
                throw;
            }
        }
    }

    private static async Task<TController> CreateRuntimeControllerWithRetryAsync<TController>(
        Func<Task<TController>> createAsync,
        CancellationToken cancellationToken)
        where TController : CoreWebView2Controller
    {
        ArgumentNullException.ThrowIfNull(createAsync);

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await createAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (IsTransientControllerCreationFailure(ex) &&
                                       attempt < ControllerCreationRetryDelays.Length)
            {
                await Task.Delay(ControllerCreationRetryDelays[attempt], cancellationToken).ConfigureAwait(true);
            }
        }
    }

    private static CoreWebView2ControllerOptions CreateRuntimeControllerOptions(
        CoreWebView2Environment environment,
        NativeWebViewControllerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!RequiresRuntimeControllerOptions(options))
        {
            throw new InvalidOperationException("Runtime controller options were requested without any customized values.");
        }

        var controllerOptions = environment.CreateCoreWebView2ControllerOptions();

        if (!string.IsNullOrWhiteSpace(options.ProfileName))
        {
            controllerOptions.ProfileName = options.ProfileName;
        }

        controllerOptions.IsInPrivateModeEnabled = options.IsInPrivateModeEnabled;

        if (options.ScriptLocale is not null)
        {
            controllerOptions.ScriptLocale = options.ScriptLocale;
        }

        return controllerOptions;
    }

    internal static void AttachControllerOptionsFallbackExceptionContext(
        Exception fallbackException,
        Exception originalException)
    {
        ArgumentNullException.ThrowIfNull(fallbackException);
        ArgumentNullException.ThrowIfNull(originalException);

        if (!fallbackException.Data.Contains(ControllerOptionsFallbackOriginalExceptionDataKey))
        {
            fallbackException.Data[ControllerOptionsFallbackOriginalExceptionDataKey] = originalException;
        }
    }

    private CoreWebView2PrintSettings? CreatePrintSettings(NativeWebViewPrintSettings? settings)
    {
        if (_environment is null)
        {
            return null;
        }

        var printSettings = _environment.CreatePrintSettings();
        if (settings is null)
        {
            return printSettings;
        }

        printSettings.ShouldPrintBackgrounds = settings.BackgroundsEnabled;
        printSettings.Orientation = settings.Landscape
            ? CoreWebView2PrintOrientation.Landscape
            : CoreWebView2PrintOrientation.Portrait;
        return printSettings;
    }

    private static string? NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path);
    }

    private static Uri? TryCreateUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri)
            ? uri
            : null;
    }

    private static int? TryConvertHttpStatusCode(long statusCode)
    {
        return statusCode is >= int.MinValue and <= int.MaxValue
            ? (int)statusCode
            : null;
    }

    private static void ReleaseComHandle(ref nint handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        Marshal.Release(handle);
        handle = IntPtr.Zero;
    }

    private void DestroyCompositionHost()
    {
        try
        {
            if (_compositionController is not null)
            {
                _compositionController.RootVisualTarget = null!;
            }
        }
        catch
        {
            // Best-effort disconnect before releasing the host visual tree.
        }

        DestroyCaptureCompositionHost();
        DestroyDirectCompositionHost();
    }

    private void DestroyCaptureCompositionHost()
    {
        if (_winCompositionCaptureHost is not null)
        {
            _winCompositionCaptureHost.FrameArrived -= OnCompositionCaptureFrameArrived;
            _winCompositionCaptureHost.Dispose();
        }

        _winCompositionCaptureHost = null;
    }

    private void DestroyDirectCompositionHost()
    {
        if (_winCompositionDirectHost is not null)
        {
            _winCompositionDirectHost.Dispose();
        }

        _winCompositionDirectHost = null;
    }

    private static void EnsureChildWindowClassRegistered()
    {
        if (_childWindowClassAtom != 0)
        {
            return;
        }

        lock (WindowClassGate)
        {
            if (_childWindowClassAtom != 0)
            {
                return;
            }

            var instanceHandle = Win32.GetModuleHandle(null);
            var windowClass = new Win32.WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<Win32.WndClassEx>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ChildWindowProcDelegate),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = instanceHandle,
                hIcon = IntPtr.Zero,
                hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.CursorIdcArrow),
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = Win32.ChildWindowClassName,
                hIconSm = IntPtr.Zero,
            };

            _childWindowClassAtom = Win32.RegisterClassEx(ref windowClass);
            if (_childWindowClassAtom == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != Win32.ErrorClassAlreadyExists)
                {
                    throw new InvalidOperationException(
                        $"Failed to register Windows child host window class. Win32 error: {error}.");
                }

                _childWindowClassAtom = ushort.MaxValue;
            }
        }
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void EnsureFeature(NativeWebViewFeature feature, string operationName)
    {
        if (!Features.Supports(feature))
        {
            throw new PlatformNotSupportedException(
                $"Operation '{operationName}' is not supported on platform '{Platform}'.");
        }
    }

    private readonly record struct NativeWindowDiagnosticsSnapshot(
        long Handle,
        bool IsVisible,
        int Left,
        int Top,
        int Right,
        int Bottom);

    private static class Win32
    {
        public const int CursorIdcArrow = 32512;
        public const int ErrorClassAlreadyExists = 1410;
        public const string ChildWindowClassName = "NativeWebView.WebView2HostWindow";

        internal static class WindowLongIndex
        {
            public const int GWLP_USERDATA = -21;
        }

        internal static class WindowStyles
        {
            public const uint WS_POPUP = 0x80000000;
            public const uint WS_CHILD = 0x40000000;
            public const uint WS_VISIBLE = 0x10000000;
            public const uint WS_CLIPSIBLINGS = 0x04000000;
            public const uint WS_CLIPCHILDREN = 0x02000000;
        }

        internal static class WindowMessage
        {
            public const uint WM_SIZE = 0x0005;
            public const uint WM_SETFOCUS = 0x0007;
        }

        internal static class ShowWindowCommand
        {
            public const int Hide = 0;
            public const int ShowNoActivate = 4;
            public const int Show = 5;
        }

        [Flags]
        internal enum SetWindowPosFlags : uint
        {
            NoZOrder = 0x0004,
            NoActivate = 0x0010,
            ShowWindow = 0x0040,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WndClassEx
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszClassName;
            public IntPtr hIconSm;
        }

        internal delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassEx([In] ref WndClassEx lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            SetWindowPosFlags uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
        internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetFocus(IntPtr hWnd);
    }
}
