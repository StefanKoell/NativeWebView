using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Threading;
using NativeWebView.Core;

namespace NativeWebView.Sample.RenderModeComparison;

public enum SelfTestScenario
{
    SyntheticComparison,
    LiveRoyalApps,
}

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim _stateSnapshotWriteGate = new(1, 1);
    private readonly string _modeName;
    private readonly string _profileName;
    private readonly bool _runSelfTest;
    private readonly SelfTestScenario _selfTestScenario;
    private readonly Uri? _initialUri;
    private readonly bool _isDirectCompositionProbe;
    private string? _initializationException;
    private FramePixelSummary? _lastFramePixelSummary;
    private bool _selfTestStarted;

    public MainWindow()
        : this("Offscreen/GpuSurface", NativeWebViewRenderMode.Offscreen, "offscreen")
    {
    }

    public MainWindow(
        string modeName,
        NativeWebViewRenderMode renderMode,
        string profileName,
        bool runSelfTest = false,
        SelfTestScenario selfTestScenario = SelfTestScenario.SyntheticComparison,
        Uri? initialUri = null)
    {
        _modeName = modeName;
        _profileName = profileName;
        _runSelfTest = runSelfTest;
        _selfTestScenario = selfTestScenario;
        _initialUri = initialUri;
        _isDirectCompositionProbe = IsDirectCompositionProbeEnabled();

        InitializeComponent();

        Title = $"NativeWebView {modeName} Comparison";
        ModeTextBlock.Text = modeName;
        WebView.RenderMode = renderMode;
        ConfigureWebView();

        WebView.NavigationCompleted += OnNavigationCompleted;
        WebView.RenderFrameCaptured += OnRenderFrameCaptured;
        _stateTimer.Tick += OnStateTimerTick;
        Opened += OnOpened;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _stateTimer.Stop();
        WebView.RenderFrameCaptured -= OnRenderFrameCaptured;
        WebView.Dispose();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        await InitializeComparisonAsync();
    }

    private async Task InitializeComparisonAsync()
    {
        NativeWebViewRuntime.EnsureCurrentPlatformRegistered();

        var testPageUri = RenderModeComparisonPage.EnsureCreated();
        var initialUri = _initialUri ?? testPageUri;
        UrlTextBox.Text = initialUri.AbsoluteUri;

        try
        {
            await WebView.InitializeAsync();
            StatusTextBlock.Text = $"Initialized, supports {WebView.RenderMode}={WebView.SupportsRenderMode(WebView.RenderMode).ToString(CultureInfo.InvariantCulture)}";
            WebView.Navigate(initialUri);
            _stateTimer.Start();
        }
        catch (Exception ex)
        {
            _initializationException = ex.ToString();
            StatusTextBlock.Text = $"Initialization failed: {ex.GetType().Name}: {ex.Message}";
            _stateTimer.Start();
        }
    }

    private void ConfigureWebView()
    {
        WebView.RenderFramesPerSecond = 60;
        WebView.InstanceConfiguration.EnvironmentOptions.UserDataFolder = Path.GetFullPath($"artifacts/render-mode-comparison/{_profileName}/userdata");
        WebView.InstanceConfiguration.EnvironmentOptions.CacheFolder = Path.GetFullPath($"artifacts/render-mode-comparison/{_profileName}/cache");
        WebView.InstanceConfiguration.EnvironmentOptions.CookieDataFolder = Path.GetFullPath($"artifacts/render-mode-comparison/{_profileName}/cookies");
        WebView.InstanceConfiguration.EnvironmentOptions.SessionDataFolder = Path.GetFullPath($"artifacts/render-mode-comparison/{_profileName}/session");
        WebView.InstanceConfiguration.ControllerOptions.ProfileName = $"render-mode-{_profileName}";
        WebView.InstanceConfiguration.ControllerOptions.ScriptLocale = "en-US";
    }

    private void ReloadButtonOnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WebView.Reload();
    }

    private async void ReadStateButtonOnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await ReadAndDisplayStateAsync();
    }

    private async void OnStateTimerTick(object? sender, EventArgs e)
    {
        await ReadAndDisplayStateAsync();
        await WriteStateSnapshotAsync();
    }

    private void OnNavigationCompleted(object? sender, NativeWebViewNavigationCompletedEventArgs e)
    {
        StatusTextBlock.Text = e.IsSuccess
            ? $"{_modeName}: loaded"
            : $"{_modeName}: failed {e.Error ?? e.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";

        if (e.IsSuccess && _runSelfTest && !_selfTestStarted)
        {
            _selfTestStarted = true;
            Dispatcher.UIThread.Post(async () =>
            {
                if (_selfTestScenario == SelfTestScenario.LiveRoyalApps)
                {
                    await RunLiveRoyalAppsSelfTestAsync();
                    return;
                }

                await RunSelfTestAsync();
            });
        }
    }

    private void OnRenderFrameCaptured(object? sender, NativeWebViewRenderFrameCapturedEventArgs e)
    {
        _lastFramePixelSummary = FramePixelSummary.FromFrame(e.Frame);
    }

    private async Task RunLiveRoyalAppsSelfTestAsync()
    {
        var resultPath = Path.GetFullPath($"artifacts/render-mode-comparison/{_profileName}-live-scroll-test.json");
        var screenshotPath = Path.GetFullPath($"artifacts/render-mode-comparison/screenshots/{_profileName}-live-scroll-test.png");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
        var exitCode = 1;
        var windowHandle = IntPtr.Zero;

        try
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var before = await WaitForLiveRoyalAppsProbeAsync(TimeSpan.FromSeconds(20));
            if (before is null)
            {
                errors.Add("Live page probe was not available.");
            }

            var beforeInputDiagnostics = WebView.GetInputDiagnosticsSnapshot();
            var beforeGpuFrameDiagnostics = WebView.GetGpuFrameDiagnosticsSnapshot();
            var expectsAvaloniaGpuComposition =
                WebView.RenderMode != NativeWebViewRenderMode.Embedded &&
                !_isDirectCompositionProbe;
            if (expectsAvaloniaGpuComposition && !beforeInputDiagnostics.IsGpuCompositionRenderingActive)
            {
                errors.Add("GPU composition rendering is not active.");
            }

            windowHandle = SelfTestWin32.FindTopLevelWindowHandle(Title ?? string.Empty);
            if (windowHandle == IntPtr.Zero)
            {
                errors.Add("Could not find the sample top-level HWND.");
            }

            LiveRoyalAppsProbeState? after = null;
            NativeWebViewGpuFrameDiagnosticsSnapshot? afterGpuFrameDiagnostics = null;
            AllocationDeltaSnapshot? allocationDelta = null;
            GpuFrameDeltaSnapshot? gpuDelta = null;
            TimeSpan scrollDuration = TimeSpan.Zero;
            if (errors.Count == 0)
            {
                await ResetLiveRoyalAppsScrollAsync();
                await PostCenteredWheelSequenceAsync(windowHandle, TimeSpan.FromMilliseconds(250));
                await Task.Delay(250);
                before = await ReadLiveRoyalAppsProbeAsync();
                beforeGpuFrameDiagnostics = WebView.GetGpuFrameDiagnosticsSnapshot();
                var beforeAllocation = AllocationSnapshot.Capture();
                var startedAt = DateTimeOffset.UtcNow;
                await StartLiveRoyalAppsScrollAnimationAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(5000);
                scrollDuration = DateTimeOffset.UtcNow - startedAt;
                var afterAllocation = AllocationSnapshot.Capture();
                after = await ReadLiveRoyalAppsProbeAsync();
                afterGpuFrameDiagnostics = WebView.GetGpuFrameDiagnosticsSnapshot();
                gpuDelta = GpuFrameDeltaSnapshot.Create(
                    beforeGpuFrameDiagnostics,
                    afterGpuFrameDiagnostics.Value,
                    scrollDuration);
                allocationDelta = AllocationDeltaSnapshot.Create(
                    beforeAllocation,
                    afterAllocation,
                    scrollDuration,
                    expectsAvaloniaGpuComposition
                        ? afterGpuFrameDiagnostics.Value.GpuFrameCopyCount -
                          beforeGpuFrameDiagnostics.GpuFrameCopyCount
                        : 0);

                if ((after?.ScrollEvents ?? 0) <= (before?.ScrollEvents ?? 0))
                {
                    warnings.Add(
                        "The live page did not report scroll events during the measurement window.");
                }

                if (expectsAvaloniaGpuComposition && gpuDelta.GpuFrameCopyRate < 50)
                {
                    warnings.Add(
                        "GPU frame copy rate during live-page scrolling was below 50 fps. " +
                        $"Actual={gpuDelta.GpuFrameCopyRate.ToString("F1", CultureInfo.InvariantCulture)} fps, " +
                        $"Signals={gpuDelta.FrameArrivedSignalRate.ToString("F1", CultureInfo.InvariantCulture)} fps, " +
                        $"Updates={gpuDelta.GpuCompositionUpdateRate.ToString("F1", CultureInfo.InvariantCulture)} fps.");
                }
            }

            await WriteStateSnapshotAsync();
            ScreenshotPixelSummary? screenshotPixels = null;
            var screenshotCaptured = windowHandle != IntPtr.Zero &&
                                     SelfTestWin32.TryCaptureWindowScreenshot(
                                         windowHandle,
                                         screenshotPath,
                                         overlayProbe: null,
                                         out screenshotPixels);
            exitCode = errors.Count == 0 ? 0 : 1;
            var result = new
            {
                passed = errors.Count == 0,
                errors,
                warnings,
                url = _initialUri?.AbsoluteUri ?? UrlTextBox.Text,
                scrollDurationSeconds = scrollDuration.TotalSeconds,
                screenshotPath = screenshotCaptured ? screenshotPath : null,
                screenshotPixels,
                before,
                after,
                beforeInputDiagnostics,
                beforeGpuFrameDiagnostics,
                afterGpuFrameDiagnostics,
                gpuDelta,
                allocationDelta,
                renderDiagnostics = WebView.RenderDiagnosticsMessage,
                renderStatistics = WebView.GetRenderStatisticsSnapshot(),
                framePixels = _lastFramePixelSummary,
                webViewScreen = CreateWebViewScreenSnapshot(),
            };

            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, JsonOptions));
        }
        catch (Exception ex)
        {
            ScreenshotPixelSummary? screenshotPixels = null;
            var screenshotCaptured = windowHandle != IntPtr.Zero &&
                                     SelfTestWin32.TryCaptureWindowScreenshot(
                                         windowHandle,
                                         screenshotPath,
                                         overlayProbe: null,
                                         out screenshotPixels);
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new
            {
                passed = false,
                errors = new[] { ex.ToString() },
                screenshotPath = screenshotCaptured ? screenshotPath : null,
                screenshotPixels,
            }, JsonOptions));
        }
        finally
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(exitCode);
            }
            else
            {
                Close();
            }
        }
    }

    private async Task ReadAndDisplayStateAsync()
    {
        StateTextBlock.Text = await ReadStateAsync();
    }

    private async Task WriteStateSnapshotAsync()
    {
        if (!await _stateSnapshotWriteGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            await WriteStateSnapshotCoreAsync();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            _stateSnapshotWriteGate.Release();
        }
    }

    private async Task WriteStateSnapshotCoreAsync()
    {
        var snapshot = new
        {
            capturedAtUtc = DateTimeOffset.UtcNow,
            mode = _modeName,
            renderMode = WebView.RenderMode.ToString(),
            initializationException = _initializationException,
            renderDiagnostics = WebView.RenderDiagnosticsMessage,
            renderStatistics = WebView.GetRenderStatisticsSnapshot(),
            inputDiagnostics = WebView.GetInputDiagnosticsSnapshot(),
            gpuFrameDiagnostics = WebView.GetGpuFrameDiagnosticsSnapshot(),
            framePixels = _lastFramePixelSummary,
            webViewScreen = CreateWebViewScreenSnapshot(),
            state = await TryReadRawDocumentStateForSnapshotAsync(),
        };

        var path = Path.GetFullPath($"artifacts/render-mode-comparison/{_profileName}-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    private async Task RunSelfTestAsync()
    {
        var resultPath = Path.GetFullPath($"artifacts/render-mode-comparison/{_profileName}-self-test.json");
        var screenshotPath = Path.GetFullPath($"artifacts/render-mode-comparison/screenshots/{_profileName}-self-test.png");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
        var exitCode = 1;
        var windowHandle = IntPtr.Zero;

        try
        {
            var before = await WaitForReadySelfTestStateAsync(TimeSpan.FromSeconds(15));
            var errors = new List<string>();
            if (before?.State?.Rects is null)
            {
                errors.Add("Comparison page did not expose element rectangles.");
            }

            var beforeInputDiagnostics = WebView.GetInputDiagnosticsSnapshot();
            var beforeGpuFrameDiagnostics = WebView.GetGpuFrameDiagnosticsSnapshot();
            var expectsAvaloniaGpuComposition =
                WebView.RenderMode != NativeWebViewRenderMode.Embedded &&
                !_isDirectCompositionProbe;
            var expectsCompositionInput = WebView.RenderMode != NativeWebViewRenderMode.Embedded;
            if (!beforeInputDiagnostics.IsGpuCompositionRenderingActive && expectsAvaloniaGpuComposition)
            {
                errors.Add("GPU composition rendering is not active.");
            }

            windowHandle = SelfTestWin32.FindTopLevelWindowHandle(Title ?? string.Empty);
            if (windowHandle == IntPtr.Zero)
            {
                errors.Add("Could not find the sample top-level HWND.");
            }

            if (errors.Count == 0 && before?.State?.Rects is not null)
            {
                await PostSelfTestInputAsync(windowHandle, before.State.Rects);
                await Task.Delay(1500);
            }

            var after = await TryReadRawDocumentStateForSnapshotAsync();
            var afterInputDiagnostics = WebView.GetInputDiagnosticsSnapshot();
            var afterGpuFrameDiagnostics = WebView.GetGpuFrameDiagnosticsSnapshot();
            var afterInputIsFocused = WebView.IsFocused;
            var expectedInput = $"{before?.State?.Input} gpu-self";
            if (!string.Equals(after?.State?.Input, expectedInput, StringComparison.Ordinal))
            {
                errors.Add($"Text input did not match expected value '{expectedInput}'. Actual value was '{after?.State?.Input ?? "<null>"}'.");
            }

            if ((after?.State?.Clicks ?? 0) <= (before?.State?.Clicks ?? 0))
            {
                errors.Add("Button click count did not increase.");
            }

            if ((after?.State?.ScrollTop ?? 0) <= (before?.State?.ScrollTop ?? 0))
            {
                errors.Add("Scroll position did not increase.");
            }

            if (expectsCompositionInput &&
                afterInputDiagnostics.CompositionMouseInputForwardedCount <= beforeInputDiagnostics.CompositionMouseInputForwardedCount)
            {
                errors.Add("Composition mouse input was not forwarded.");
            }

            if (expectsCompositionInput &&
                afterInputDiagnostics.CompositionMouseInputFailedCount != beforeInputDiagnostics.CompositionMouseInputFailedCount)
            {
                errors.Add("Composition mouse input failures increased.");
            }

            if (expectsCompositionInput && !afterInputDiagnostics.HasCompositionKeyboardFocus)
            {
                errors.Add("Composition keyboard focus was not established after pointer input.");
            }

            if (!afterInputIsFocused)
            {
                errors.Add("Avalonia NativeWebView control focus was not established after pointer input.");
            }

            if (expectsCompositionInput &&
                afterInputDiagnostics.CompositionKeyboardMessageForwardedCount <=
                beforeInputDiagnostics.CompositionKeyboardMessageForwardedCount)
            {
                errors.Add("Composition keyboard messages were not forwarded.");
            }

            if (expectsCompositionInput &&
                afterInputDiagnostics.CompositionKeyboardMessageRejectedCount >
                beforeInputDiagnostics.CompositionKeyboardMessageRejectedCount)
            {
                errors.Add("Composition keyboard message rejections increased.");
            }

            if (!string.IsNullOrWhiteSpace(WebView.RenderDiagnosticsMessage))
            {
                errors.Add($"Render diagnostics reported '{WebView.RenderDiagnosticsMessage}'.");
            }

            if (expectsAvaloniaGpuComposition && !afterGpuFrameDiagnostics.IsGpuFrameOnlyRenderingEnabled)
            {
                errors.Add("GPU-only frame rendering was not active after input.");
            }

            if (expectsAvaloniaGpuComposition && !afterGpuFrameDiagnostics.IsGpuFrameNotificationPumpActive)
            {
                errors.Add("GPU frame notification pump was not active after input.");
            }

            if (WebView.RenderMode != NativeWebViewRenderMode.Embedded &&
                !_isDirectCompositionProbe &&
                afterGpuFrameDiagnostics.IsDirectCompositionActive)
            {
                errors.Add("Direct composition child-HWND path is active; expected Avalonia compositor-tree GPU surface rendering.");
            }

            if (expectsAvaloniaGpuComposition &&
                afterGpuFrameDiagnostics.Transport != NativeWebViewGpuFrameTransport.WindowsGraphicsCaptureSharedD3D11Texture)
            {
                errors.Add(
                    "Unexpected GPU compositor transport. " +
                    $"Expected={NativeWebViewGpuFrameTransport.WindowsGraphicsCaptureSharedD3D11Texture}, " +
                    $"Actual={afterGpuFrameDiagnostics.Transport}.");
            }

            ComparisonDocumentState? afterSustainedScroll = null;
            NativeWebViewGpuFrameDiagnosticsSnapshot? afterSustainedGpuFrameDiagnostics = null;
            AllocationDeltaSnapshot? sustainedScrollAllocationDelta = null;
            GpuFrameDeltaSnapshot? sustainedScrollGpuDelta = null;
            if (errors.Count == 0 && before?.State?.Rects is not null)
            {
                var beforeSustainedScroll = after;
                var beforeSustainedGpuFrameDiagnostics = afterGpuFrameDiagnostics;
                var beforeSustainedAllocation = AllocationSnapshot.Capture();
                var sustainedScrollStartedAt = DateTimeOffset.UtcNow;
                await PostSelfTestWheelSequenceAsync(windowHandle, before.State.Rects, TimeSpan.FromSeconds(2));
                var sustainedScrollDuration = DateTimeOffset.UtcNow - sustainedScrollStartedAt;
                var afterSustainedAllocation = AllocationSnapshot.Capture();
                await Task.Delay(50);
                afterSustainedScroll = await TryReadRawDocumentStateForSnapshotAsync();
                afterSustainedGpuFrameDiagnostics = WebView.GetGpuFrameDiagnosticsSnapshot();
                sustainedScrollGpuDelta = GpuFrameDeltaSnapshot.Create(
                    beforeSustainedGpuFrameDiagnostics,
                    afterSustainedGpuFrameDiagnostics.Value,
                    sustainedScrollDuration);
                sustainedScrollAllocationDelta = AllocationDeltaSnapshot.Create(
                    beforeSustainedAllocation,
                    afterSustainedAllocation,
                    sustainedScrollDuration,
                    expectsAvaloniaGpuComposition
                        ? afterSustainedGpuFrameDiagnostics.Value.GpuFrameCopyCount -
                          beforeSustainedGpuFrameDiagnostics.GpuFrameCopyCount
                        : 0);

                if ((afterSustainedScroll?.State?.ScrollTop ?? 0) <= (beforeSustainedScroll?.State?.ScrollTop ?? 0))
                {
                    errors.Add("Sustained wheel input did not continue scrolling the page.");
                }

                if (expectsAvaloniaGpuComposition &&
                    afterSustainedGpuFrameDiagnostics.Value.CpuFrameCopyCount >
                    beforeSustainedGpuFrameDiagnostics.CpuFrameCopyCount)
                {
                    errors.Add(
                        "CPU frame readbacks continued after GPU-only rendering was active. " +
                        $"Before={beforeSustainedGpuFrameDiagnostics.CpuFrameCopyCount}, " +
                        $"After={afterSustainedGpuFrameDiagnostics.Value.CpuFrameCopyCount}.");
                }

                if (expectsAvaloniaGpuComposition &&
                    afterSustainedGpuFrameDiagnostics.Value.GpuFrameCopyCount <=
                    beforeSustainedGpuFrameDiagnostics.GpuFrameCopyCount)
                {
                    errors.Add("GPU frame copies did not advance during sustained scrolling.");
                }

                if (expectsAvaloniaGpuComposition &&
                    afterSustainedGpuFrameDiagnostics.Value.FrameArrivedSignalCount <=
                    beforeSustainedGpuFrameDiagnostics.FrameArrivedSignalCount)
                {
                    errors.Add("GPU frame-arrival signals did not advance during sustained scrolling.");
                }

                if (expectsAvaloniaGpuComposition &&
                    afterSustainedGpuFrameDiagnostics.Value.GpuFrameArrivalCaptureScheduledCount <=
                    beforeSustainedGpuFrameDiagnostics.GpuFrameArrivalCaptureScheduledCount)
                {
                    errors.Add("GPU frame-arrival captures were not scheduled during sustained scrolling.");
                }

                if (expectsAvaloniaGpuComposition)
                {
                    var gpuFrameRate = sustainedScrollGpuDelta!.GpuFrameCopyRate;
                    var signalFrameRate = sustainedScrollGpuDelta.FrameArrivedSignalRate;
                    if (gpuFrameRate < 50)
                    {
                        errors.Add(
                            $"GPU frame copy rate during sustained scrolling was below 50 fps. " +
                            $"Actual={gpuFrameRate.ToString("F1", CultureInfo.InvariantCulture)} fps, " +
                            $"Signals={signalFrameRate.ToString("F1", CultureInfo.InvariantCulture)} fps.");
                    }

                    if (afterSustainedGpuFrameDiagnostics.Value.LatestGpuFrameAgeMilliseconds > 250)
                    {
                        errors.Add(
                            "Latest GPU frame was stale after sustained scrolling. " +
                            $"Age={afterSustainedGpuFrameDiagnostics.Value.LatestGpuFrameAgeMilliseconds}ms.");
                    }
                }
            }

            await WriteStateSnapshotAsync();
            var overlayProbe = CreateOverlayProbeScreenSnapshot();
            ScreenshotPixelSummary? screenshotPixels = null;
            var screenshotCaptured = windowHandle != IntPtr.Zero &&
                                     SelfTestWin32.TryCaptureWindowScreenshot(
                                         windowHandle,
                                         screenshotPath,
                                         overlayProbe,
                                         out screenshotPixels);
            if (screenshotCaptured &&
                WebView.RenderMode != NativeWebViewRenderMode.Embedded &&
                screenshotPixels is not null &&
                !screenshotPixels.IsOverlayProbeVisible)
            {
                errors.Add(
                    "Avalonia overlay probe was not visible above the WebView. " +
                    "This indicates a native child HWND/direct-composition airspace path, not an Avalonia compositor-tree surface.");
            }

            exitCode = errors.Count == 0 ? 0 : 1;
            var result = new
            {
                passed = errors.Count == 0,
                errors,
                screenshotPath = screenshotCaptured ? screenshotPath : null,
                before,
                after,
                afterSustainedScroll,
                beforeInputDiagnostics,
                afterInputDiagnostics,
                beforeGpuFrameDiagnostics,
                afterGpuFrameDiagnostics,
                afterSustainedGpuFrameDiagnostics,
                sustainedScrollGpuDelta,
                sustainedScrollAllocationDelta,
                afterInputIsFocused,
                renderDiagnostics = WebView.RenderDiagnosticsMessage,
                renderStatistics = WebView.GetRenderStatisticsSnapshot(),
                framePixels = _lastFramePixelSummary,
                screenshotPixels,
                overlayProbe,
                webViewScreen = CreateWebViewScreenSnapshot(),
            };

            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, JsonOptions));
        }
        catch (Exception ex)
        {
            var overlayProbe = CreateOverlayProbeScreenSnapshot();
            ScreenshotPixelSummary? screenshotPixels = null;
            var screenshotCaptured = windowHandle != IntPtr.Zero &&
                                     SelfTestWin32.TryCaptureWindowScreenshot(
                                         windowHandle,
                                         screenshotPath,
                                         overlayProbe,
                                         out screenshotPixels);
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new
            {
                passed = false,
                errors = new[] { ex.ToString() },
                screenshotPath = screenshotCaptured ? screenshotPath : null,
                screenshotPixels,
                overlayProbe,
            }, JsonOptions));
        }
        finally
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(exitCode);
            }
            else
            {
                Close();
            }
        }
    }

    private async Task<ComparisonDocumentState?> WaitForReadySelfTestStateAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await TryReadRawDocumentStateForSnapshotAsync();
            if (state?.State?.Rects is not null &&
                (_isDirectCompositionProbe || WebView.GetInputDiagnosticsSnapshot().IsGpuCompositionRenderingActive))
            {
                return state;
            }

            await Task.Delay(250);
        }

        return await TryReadRawDocumentStateForSnapshotAsync();
    }

    private async Task<LiveRoyalAppsProbeState?> WaitForLiveRoyalAppsProbeAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await ReadLiveRoyalAppsProbeAsync();
            if (state is not null &&
                state.ReadyState is not null &&
                !string.Equals(state.ReadyState, "loading", StringComparison.OrdinalIgnoreCase) &&
                (_isDirectCompositionProbe || WebView.GetInputDiagnosticsSnapshot().IsGpuCompositionRenderingActive))
            {
                return state;
            }

            await Task.Delay(250);
        }

        return await ReadLiveRoyalAppsProbeAsync();
    }

    private static bool IsDirectCompositionProbeEnabled()
    {
        var value = Environment.GetEnvironmentVariable("NATIVEWEBVIEW_WINDOWS_DIRECT_COMPOSITION");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task PostSelfTestInputAsync(IntPtr windowHandle, IReadOnlyDictionary<string, ElementRect> rects)
    {
        var input = rects["input"];
        var button = rects["clicker"];
        var scrollbox = rects["scrollbox"];
        var webViewTopLeft = Avalonia.VisualExtensions.TranslatePoint(WebView, new Point(0, 0), this) ?? default;
        var webViewScreen = CreateWebViewScreenSnapshot();
        var scaling = webViewScreen.RenderScaling;
        var inputX = ToPhysicalClientX(input.Right - 12);
        var inputY = ToPhysicalClientY((input.Top + input.Bottom) / 2);
        var buttonX = ToPhysicalClientX((button.Left + button.Right) / 2);
        var buttonY = ToPhysicalClientY((button.Top + button.Bottom) / 2);
        var scrollLocalX = (int)Math.Round((scrollbox.Left + scrollbox.Right) / 2, MidpointRounding.AwayFromZero);
        var scrollLocalY = (int)Math.Round((scrollbox.Top + scrollbox.Bottom) / 2, MidpointRounding.AwayFromZero);
        var wheelScreenX = (int)Math.Round(webViewScreen.X + scrollLocalX * scaling, MidpointRounding.AwayFromZero);
        var wheelScreenY = (int)Math.Round(webViewScreen.Y + scrollLocalY * scaling, MidpointRounding.AwayFromZero);

        SelfTestWin32.PostMouseMove(windowHandle, inputX, inputY);
        SelfTestWin32.PostLeftButtonDown(windowHandle, inputX, inputY);
        SelfTestWin32.PostLeftButtonUp(windowHandle, inputX, inputY);
        await Task.Delay(300);

        foreach (var character in " gpu-self")
        {
            SelfTestWin32.PostChar(windowHandle, character);
        }

        await Task.Delay(300);
        SelfTestWin32.PostLeftButtonDown(windowHandle, buttonX, buttonY);
        SelfTestWin32.PostLeftButtonUp(windowHandle, buttonX, buttonY);
        await Task.Delay(300);

        SelfTestWin32.PostMouseWheel(windowHandle, wheelScreenX, wheelScreenY, -120);

        int ToPhysicalClientX(double localX)
        {
            return (int)Math.Round((webViewTopLeft.X + localX) * scaling, MidpointRounding.AwayFromZero);
        }

        int ToPhysicalClientY(double localY)
        {
            return (int)Math.Round((webViewTopLeft.Y + localY) * scaling, MidpointRounding.AwayFromZero);
        }
    }

    private async Task PostSelfTestWheelSequenceAsync(
        IntPtr windowHandle,
        IReadOnlyDictionary<string, ElementRect> rects,
        TimeSpan duration)
    {
        var scrollbox = rects["scrollbox"];
        var webViewScreen = CreateWebViewScreenSnapshot();
        var scaling = webViewScreen.RenderScaling;
        var scrollLocalX = (int)Math.Round((scrollbox.Left + scrollbox.Right) / 2, MidpointRounding.AwayFromZero);
        var scrollLocalY = (int)Math.Round((scrollbox.Top + scrollbox.Bottom) / 2, MidpointRounding.AwayFromZero);
        var wheelScreenX = (int)Math.Round(webViewScreen.X + scrollLocalX * scaling, MidpointRounding.AwayFromZero);
        var wheelScreenY = (int)Math.Round(webViewScreen.Y + scrollLocalY * scaling, MidpointRounding.AwayFromZero);
        var stopAt = DateTimeOffset.UtcNow + duration;

        while (DateTimeOffset.UtcNow < stopAt)
        {
            SelfTestWin32.PostMouseWheel(windowHandle, wheelScreenX, wheelScreenY, -120);
            await Task.Delay(16);
        }
    }

    private async Task PostCenteredWheelSequenceAsync(IntPtr windowHandle, TimeSpan duration)
    {
        var webViewScreen = CreateWebViewScreenSnapshot();
        var wheelScreenX = (int)Math.Round(
            webViewScreen.X + webViewScreen.Width * webViewScreen.RenderScaling / 2,
            MidpointRounding.AwayFromZero);
        var wheelScreenY = (int)Math.Round(
            webViewScreen.Y + webViewScreen.Height * webViewScreen.RenderScaling / 2,
            MidpointRounding.AwayFromZero);
        var clientX = (int)Math.Round(
            webViewScreen.Width * webViewScreen.RenderScaling / 2,
            MidpointRounding.AwayFromZero);
        var clientY = (int)Math.Round(
            webViewScreen.Height * webViewScreen.RenderScaling / 2,
            MidpointRounding.AwayFromZero);
        var stopAt = DateTimeOffset.UtcNow + duration;

        SelfTestWin32.PostMouseMove(windowHandle, clientX, clientY);
        await Task.Delay(100);

        while (DateTimeOffset.UtcNow < stopAt)
        {
            SelfTestWin32.PostMouseWheel(windowHandle, wheelScreenX, wheelScreenY, -120);
            await Task.Delay(16);
        }
    }

    private WebViewScreenSnapshot CreateWebViewScreenSnapshot()
    {
        var topLeftInWindow = Avalonia.VisualExtensions.TranslatePoint(WebView, new Avalonia.Point(0, 0), this) ?? default;
        var topLeft = Avalonia.VisualExtensions.PointToScreen(this, topLeftInWindow);
        return new WebViewScreenSnapshot
        {
            X = topLeft.X,
            Y = topLeft.Y,
            Width = WebView.Bounds.Width,
            Height = WebView.Bounds.Height,
            RenderScaling = RenderScaling,
            WindowPositionX = Position.X,
            WindowPositionY = Position.Y,
        };
    }

    private OverlayProbeScreenSnapshot CreateOverlayProbeScreenSnapshot()
    {
        var topLeftInWindow = Avalonia.VisualExtensions.TranslatePoint(OverlayProbe, new Avalonia.Point(0, 0), this) ?? default;
        var topLeft = Avalonia.VisualExtensions.PointToScreen(this, topLeftInWindow);
        return new OverlayProbeScreenSnapshot
        {
            X = topLeft.X,
            Y = topLeft.Y,
            Width = OverlayProbe.Bounds.Width,
            Height = OverlayProbe.Bounds.Height,
            RenderScaling = RenderScaling,
        };
    }

    private async Task ResetLiveRoyalAppsScrollAsync()
    {
        const string script = """
(() => {
  window.scrollTo(0, 0);
  const scrollers = Array.from(document.querySelectorAll('*')).filter(element => {
    const style = getComputedStyle(element);
    return /(auto|scroll)/.test(style.overflowY) && element.scrollHeight > element.clientHeight;
  });
  for (const scroller of scrollers.slice(0, 8)) {
    scroller.scrollTop = 0;
  }
  return true;
})()
""";
        _ = await WebView.ExecuteScriptAsync(script);
    }

    private async Task StartLiveRoyalAppsScrollAnimationAsync(TimeSpan duration)
    {
        var durationMilliseconds = Math.Max(250, (int)Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero));
        var script = $$"""
(() => {
  const durationMs = {{durationMilliseconds.ToString(CultureInfo.InvariantCulture)}};
  const documentElement = document.documentElement;
  const body = document.body;
  const maxScrollY = Math.max(
    0,
    (documentElement?.scrollHeight || 0) - window.innerHeight,
    (body?.scrollHeight || 0) - window.innerHeight);
  if (maxScrollY <= 0) {
    return false;
  }

  window.__nativeWebViewRoyalAppsScrollRun = (window.__nativeWebViewRoyalAppsScrollRun || 0) + 1;
  const runId = window.__nativeWebViewRoyalAppsScrollRun;
  const startedAt = performance.now();
  const step = now => {
    if (window.__nativeWebViewRoyalAppsScrollRun !== runId) {
      return;
    }

    const elapsed = now - startedAt;
    const progress = Math.min(1, elapsed / durationMs);
    const wave = (1 - Math.cos(progress * Math.PI * 8)) / 2;
    window.scrollTo(0, wave * maxScrollY);
    if (elapsed < durationMs) {
      requestAnimationFrame(step);
    }
  };

  requestAnimationFrame(step);
  return true;
})()
""";
        _ = await WebView.ExecuteScriptAsync(script);
    }

    private async Task<LiveRoyalAppsProbeState?> ReadLiveRoyalAppsProbeAsync()
    {
        const string script = """
(() => {
  if (!window.__nativeWebViewRoyalAppsProbe) {
    const probe = {
      rafTotal: 0,
      rafFps: -1,
      lastRafSampleAt: performance.now(),
      lastRafTotal: 0,
      scrollEvents: 0,
      lastScrollAt: -1
    };
    const tick = now => {
      probe.rafTotal += 1;
      const elapsed = now - probe.lastRafSampleAt;
      if (elapsed >= 500) {
        probe.rafFps = (probe.rafTotal - probe.lastRafTotal) * 1000 / elapsed;
        probe.lastRafTotal = probe.rafTotal;
        probe.lastRafSampleAt = now;
      }
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
    window.addEventListener('scroll', () => {
      probe.scrollEvents += 1;
      probe.lastScrollAt = performance.now();
    }, { capture: true, passive: true });
    window.__nativeWebViewRoyalAppsProbe = probe;
  }

  const documentElement = document.documentElement;
  const body = document.body;
  const scrollY = window.scrollY || documentElement.scrollTop || body?.scrollTop || 0;
  const maxScrollY = Math.max(
    0,
    (documentElement?.scrollHeight || 0) - window.innerHeight,
    (body?.scrollHeight || 0) - window.innerHeight);
  const probe = window.__nativeWebViewRoyalAppsProbe;
  return {
    href: location.href,
    readyState: document.readyState,
    title: document.title,
    scrollY,
    maxScrollY,
    viewportWidth: window.innerWidth,
    viewportHeight: window.innerHeight,
    rafFps: probe.rafFps,
    rafTotal: probe.rafTotal,
    scrollEvents: probe.scrollEvents,
    lastScrollAgeMs: probe.lastScrollAt < 0 ? -1 : performance.now() - probe.lastScrollAt,
    sampleTime: performance.now()
  };
})()
""";
        var json = await WebView.ExecuteScriptAsync(script);
        return json is null
            ? null
            : JsonSerializer.Deserialize<LiveRoyalAppsProbeState>(json, JsonOptions);
    }

    private async Task<string> ReadStateAsync()
    {
        try
        {
            var state = await ReadRawStateAsync();
            if (state is null)
            {
                return $"{_modeName}: state unavailable";
            }

            return $"{_modeName}: active='{state.ActiveElement}', input='{state.Input}', textarea='{state.Notes}', clicks={state.Clicks}, scrollTop={state.ScrollTop}";
        }
        catch (Exception ex)
        {
            return $"{_modeName}: script failed ({ex.GetType().Name})";
        }
    }

    private async Task<ComparisonState?> ReadRawStateAsync()
    {
        var documentState = await ReadRawDocumentStateAsync();
        return documentState?.State;
    }

    private async Task<ComparisonDocumentState?> ReadRawDocumentStateAsync()
    {
        const string script = """
(() => ({
  href: location.href,
  readyState: document.readyState,
  title: document.title,
  hasComparisonState: typeof window.nativeWebViewComparisonState === 'function',
  state: typeof window.nativeWebViewComparisonState === 'function' ? window.nativeWebViewComparisonState() : null
}))()
""";
        var json = await WebView.ExecuteScriptAsync(script);
        return json is null
            ? null
            : JsonSerializer.Deserialize<ComparisonDocumentState>(json, JsonOptions);
    }

    private async Task<ComparisonDocumentState?> TryReadRawDocumentStateForSnapshotAsync()
    {
        try
        {
            return await ReadRawDocumentStateAsync();
        }
        catch (Exception ex)
        {
            _initializationException ??= ex.ToString();
            return null;
        }
    }

    private sealed class ComparisonDocumentState
    {
        public string? Href { get; set; }

        public string? ReadyState { get; set; }

        public string? Title { get; set; }

        public bool HasComparisonState { get; set; }

        public ComparisonState? State { get; set; }
    }

    private sealed class ComparisonState
    {
        public string? Input { get; set; }

        public string? Notes { get; set; }

        public int Clicks { get; set; }

        public double ScrollTop { get; set; }

        public string? ActiveElement { get; set; }

        public Dictionary<string, ElementRect>? Rects { get; set; }

        public PointerSnapshot? LastPointer { get; set; }
    }

    private sealed class LiveRoyalAppsProbeState
    {
        public string? Href { get; set; }

        public string? ReadyState { get; set; }

        public string? Title { get; set; }

        public double ScrollY { get; set; }

        public double MaxScrollY { get; set; }

        public double ViewportWidth { get; set; }

        public double ViewportHeight { get; set; }

        public double RafFps { get; set; }

        public long RafTotal { get; set; }

        public long ScrollEvents { get; set; }

        public double LastScrollAgeMs { get; set; }

        public double SampleTime { get; set; }
    }

    private sealed class PointerSnapshot
    {
        public string? Type { get; set; }

        public string? Target { get; set; }

        public double ClientX { get; set; }

        public double ClientY { get; set; }
    }

    private readonly record struct AllocationSnapshot(
        long TotalAllocatedBytes,
        int Gen0CollectionCount,
        int Gen1CollectionCount,
        int Gen2CollectionCount)
    {
        public static AllocationSnapshot Capture()
        {
            return new AllocationSnapshot(
                GC.GetTotalAllocatedBytes(precise: false),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2));
        }
    }

    private sealed class AllocationDeltaSnapshot
    {
        public long TotalAllocatedBytesDelta { get; init; }

        public double AllocatedBytesPerSecond { get; init; }

        public double AllocatedBytesPerGpuFrame { get; init; }

        public int Gen0CollectionsDelta { get; init; }

        public int Gen1CollectionsDelta { get; init; }

        public int Gen2CollectionsDelta { get; init; }

        public static AllocationDeltaSnapshot Create(
            AllocationSnapshot before,
            AllocationSnapshot after,
            TimeSpan duration,
            long gpuFrameDelta)
        {
            var elapsedSeconds = Math.Max(0.001, duration.TotalSeconds);
            var allocatedBytesDelta = Math.Max(0, after.TotalAllocatedBytes - before.TotalAllocatedBytes);
            return new AllocationDeltaSnapshot
            {
                TotalAllocatedBytesDelta = allocatedBytesDelta,
                AllocatedBytesPerSecond = allocatedBytesDelta / elapsedSeconds,
                AllocatedBytesPerGpuFrame = gpuFrameDelta > 0
                    ? allocatedBytesDelta / (double)gpuFrameDelta
                    : 0,
                Gen0CollectionsDelta = Math.Max(0, after.Gen0CollectionCount - before.Gen0CollectionCount),
                Gen1CollectionsDelta = Math.Max(0, after.Gen1CollectionCount - before.Gen1CollectionCount),
                Gen2CollectionsDelta = Math.Max(0, after.Gen2CollectionCount - before.Gen2CollectionCount),
            };
        }
    }

    private sealed class GpuFrameDeltaSnapshot
    {
        public long FrameArrivedSignalDelta { get; init; }

        public long GpuFrameCopyDelta { get; init; }

        public long CpuFrameCopyDelta { get; init; }

        public long GpuCompositionUpdateDelta { get; init; }

        public double FrameArrivedSignalRate { get; init; }

        public double GpuFrameCopyRate { get; init; }

        public double GpuCompositionUpdateRate { get; init; }

        public long GpuFrameCopyDeltaAverageMicroseconds { get; init; }

        public long GpuCompositionUpdateDeltaAverageMicroseconds { get; init; }

        public long LatestGpuFrameAgeMilliseconds { get; init; }

        public long GpuCompositionUpdateOver16MillisecondsDelta { get; init; }

        public long GpuCompositionUpdateOver33MillisecondsDelta { get; init; }

        public long GpuCompositionUpdateOver50MillisecondsDelta { get; init; }

        public static GpuFrameDeltaSnapshot Create(
            NativeWebViewGpuFrameDiagnosticsSnapshot before,
            NativeWebViewGpuFrameDiagnosticsSnapshot after,
            TimeSpan duration)
        {
            var elapsedSeconds = Math.Max(0.001, duration.TotalSeconds);
            var signalDelta = Math.Max(0, after.FrameArrivedSignalCount - before.FrameArrivedSignalCount);
            var gpuFrameDelta = Math.Max(0, after.GpuFrameCopyCount - before.GpuFrameCopyCount);
            var cpuFrameDelta = Math.Max(0, after.CpuFrameCopyCount - before.CpuFrameCopyCount);
            var compositionUpdateDelta = Math.Max(0, after.GpuCompositionUpdateCount - before.GpuCompositionUpdateCount);
            var gpuCopyElapsedDelta = Math.Max(0, after.GpuFrameCopyTotalMicroseconds - before.GpuFrameCopyTotalMicroseconds);
            var compositionUpdateElapsedDelta = Math.Max(
                0,
                after.GpuCompositionUpdateTotalMicroseconds - before.GpuCompositionUpdateTotalMicroseconds);
            var compositionUpdateOver16MillisecondsDelta = Math.Max(
                0,
                after.GpuCompositionUpdateOver16MillisecondsCount -
                before.GpuCompositionUpdateOver16MillisecondsCount);
            var compositionUpdateOver33MillisecondsDelta = Math.Max(
                0,
                after.GpuCompositionUpdateOver33MillisecondsCount -
                before.GpuCompositionUpdateOver33MillisecondsCount);
            var compositionUpdateOver50MillisecondsDelta = Math.Max(
                0,
                after.GpuCompositionUpdateOver50MillisecondsCount -
                before.GpuCompositionUpdateOver50MillisecondsCount);

            return new GpuFrameDeltaSnapshot
            {
                FrameArrivedSignalDelta = signalDelta,
                GpuFrameCopyDelta = gpuFrameDelta,
                CpuFrameCopyDelta = cpuFrameDelta,
                GpuCompositionUpdateDelta = compositionUpdateDelta,
                FrameArrivedSignalRate = signalDelta / elapsedSeconds,
                GpuFrameCopyRate = gpuFrameDelta / elapsedSeconds,
                GpuCompositionUpdateRate = compositionUpdateDelta / elapsedSeconds,
                GpuFrameCopyDeltaAverageMicroseconds = gpuFrameDelta > 0
                    ? gpuCopyElapsedDelta / gpuFrameDelta
                    : 0,
                GpuCompositionUpdateDeltaAverageMicroseconds = compositionUpdateDelta > 0
                    ? compositionUpdateElapsedDelta / compositionUpdateDelta
                    : 0,
                LatestGpuFrameAgeMilliseconds = after.LatestGpuFrameAgeMilliseconds,
                GpuCompositionUpdateOver16MillisecondsDelta = compositionUpdateOver16MillisecondsDelta,
                GpuCompositionUpdateOver33MillisecondsDelta = compositionUpdateOver33MillisecondsDelta,
                GpuCompositionUpdateOver50MillisecondsDelta = compositionUpdateOver50MillisecondsDelta,
            };
        }
    }

    private sealed class WebViewScreenSnapshot
    {
        public int X { get; set; }

        public int Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public double RenderScaling { get; set; }

        public int WindowPositionX { get; set; }

        public int WindowPositionY { get; set; }
    }

    private sealed class OverlayProbeScreenSnapshot
    {
        public int X { get; set; }

        public int Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public double RenderScaling { get; set; }
    }

    private sealed class FramePixelSummary
    {
        public long FrameId { get; set; }

        public int PixelWidth { get; set; }

        public int PixelHeight { get; set; }

        public bool IsSynthetic { get; set; }

        public string? Origin { get; set; }

        public double AverageLuminance { get; set; }

        public double NonBlackRatio { get; set; }

        public static FramePixelSummary FromFrame(NativeWebViewRenderFrame frame)
        {
            var sampleStep = Math.Max(1, frame.PixelWidth * frame.PixelHeight / 4096);
            long luminanceTotal = 0;
            long sampled = 0;
            long nonBlack = 0;

            for (var pixel = 0; pixel < frame.PixelWidth * frame.PixelHeight; pixel += sampleStep)
            {
                var row = pixel / frame.PixelWidth;
                var column = pixel % frame.PixelWidth;
                var offset = row * frame.BytesPerRow + column * 4;
                if (offset + 2 >= frame.PixelData.Length)
                {
                    continue;
                }

                var blue = frame.PixelData[offset];
                var green = frame.PixelData[offset + 1];
                var red = frame.PixelData[offset + 2];
                var luminance = (red * 299 + green * 587 + blue * 114) / 1000;
                luminanceTotal += luminance;
                sampled++;
                if (red > 8 || green > 8 || blue > 8)
                {
                    nonBlack++;
                }
            }

            return new FramePixelSummary
            {
                FrameId = frame.FrameId,
                PixelWidth = frame.PixelWidth,
                PixelHeight = frame.PixelHeight,
                IsSynthetic = frame.IsSynthetic,
                Origin = frame.Origin.ToString(),
                AverageLuminance = sampled == 0 ? 0 : (double)luminanceTotal / sampled,
                NonBlackRatio = sampled == 0 ? 0 : (double)nonBlack / sampled,
            };
        }
    }

    private sealed class ElementRect
    {
        public double Left { get; set; }

        public double Top { get; set; }

        public double Right { get; set; }

        public double Bottom { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }
    }
}

internal sealed class ScreenshotPixelSummary
{
    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    public int OverlaySampledPixels { get; set; }

    public double OverlayProbeColorRatio { get; set; }

    public bool IsOverlayProbeVisible { get; set; }
}

internal static class SelfTestWin32
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmChar = 0x0102;

    public static IntPtr FindTopLevelWindowHandle(string title)
    {
        var result = IntPtr.Zero;
        EnumWindows(
            (windowHandle, _) =>
            {
                var text = new StringBuilder(256);
                _ = GetWindowText(windowHandle, text, text.Capacity);
                if (string.Equals(text.ToString(), title, StringComparison.Ordinal))
                {
                    result = windowHandle;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);

        return result;
    }

    public static void PostMouseMove(IntPtr windowHandle, int x, int y)
    {
        _ = PostMessage(windowHandle, WmMouseMove, IntPtr.Zero, MakeLParam(x, y));
    }

    public static void PostLeftButtonDown(IntPtr windowHandle, int x, int y)
    {
        _ = PostMessage(windowHandle, WmLeftButtonDown, new IntPtr(1), MakeLParam(x, y));
    }

    public static void PostLeftButtonUp(IntPtr windowHandle, int x, int y)
    {
        _ = PostMessage(windowHandle, WmLeftButtonUp, IntPtr.Zero, MakeLParam(x, y));
    }

    public static void PostMouseWheel(IntPtr windowHandle, int screenX, int screenY, int delta)
    {
        _ = PostMessage(windowHandle, WmMouseWheel, new IntPtr((delta & 0xFFFF) << 16), MakeLParam(screenX, screenY));
    }

    public static void PostChar(IntPtr windowHandle, char character)
    {
        _ = PostMessage(windowHandle, WmChar, new IntPtr(character), IntPtr.Zero);
    }

    public static bool TryCaptureWindowScreenshot(
        IntPtr windowHandle,
        string path,
        object? overlayProbe,
        out ScreenshotPixelSummary? screenshotPixels)
    {
        screenshotPixels = null;
        if (!GetWindowRect(windowHandle, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        using var bitmap = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height));
        screenshotPixels = CreateScreenshotPixelSummary(bitmap, rect, overlayProbe);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return true;
    }

    public static bool TryCaptureWindowScreenshot(IntPtr windowHandle, string path)
    {
        return TryCaptureWindowScreenshot(windowHandle, path, overlayProbe: null, out _);
    }

    private static ScreenshotPixelSummary CreateScreenshotPixelSummary(
        System.Drawing.Bitmap bitmap,
        NativeRect windowRect,
        object? overlayProbe)
    {
        var overlaySampledPixels = 0;
        var overlayProbeColorPixels = 0;

        if (overlayProbe is not null)
        {
            var type = overlayProbe.GetType();
            var screenX = Convert.ToInt32(type.GetProperty("X")?.GetValue(overlayProbe), CultureInfo.InvariantCulture);
            var screenY = Convert.ToInt32(type.GetProperty("Y")?.GetValue(overlayProbe), CultureInfo.InvariantCulture);
            var width = Convert.ToDouble(type.GetProperty("Width")?.GetValue(overlayProbe), CultureInfo.InvariantCulture);
            var height = Convert.ToDouble(type.GetProperty("Height")?.GetValue(overlayProbe), CultureInfo.InvariantCulture);
            var scaling = Convert.ToDouble(type.GetProperty("RenderScaling")?.GetValue(overlayProbe), CultureInfo.InvariantCulture);
            var left = Math.Clamp(screenX - windowRect.Left, 0, bitmap.Width - 1);
            var top = Math.Clamp(screenY - windowRect.Top, 0, bitmap.Height - 1);
            var right = Math.Clamp(left + Math.Max(1, (int)Math.Round(width * scaling, MidpointRounding.AwayFromZero)), 0, bitmap.Width);
            var bottom = Math.Clamp(top + Math.Max(1, (int)Math.Round(height * scaling, MidpointRounding.AwayFromZero)), 0, bitmap.Height);
            var stepX = Math.Max(1, (right - left) / 48);
            var stepY = Math.Max(1, (bottom - top) / 16);

            for (var y = top; y < bottom; y += stepY)
            {
                for (var x = left; x < right; x += stepX)
                {
                    var color = bitmap.GetPixel(x, y);
                    overlaySampledPixels++;
                    if ((color.R > 220 && color.B > 220 && color.G < 80) ||
                        (color.G > 220 && color.B > 220 && color.R < 80))
                    {
                        overlayProbeColorPixels++;
                    }
                }
            }
        }

        var overlayRatio = overlaySampledPixels == 0
            ? 0d
            : (double)overlayProbeColorPixels / overlaySampledPixels;

        return new ScreenshotPixelSummary
        {
            PixelWidth = bitmap.Width,
            PixelHeight = bitmap.Height,
            OverlaySampledPixels = overlaySampledPixels,
            OverlayProbeColorRatio = overlayRatio,
            IsOverlayProbeVisible = overlayRatio >= 0.15,
        };
    }

    private static IntPtr MakeLParam(int x, int y)
    {
        return new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal static class RenderModeComparisonPage
{
    public static Uri EnsureCreated()
    {
        var directory = Path.GetFullPath("artifacts/render-mode-comparison/page");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "index.html");
        var html = CreateHtml();
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path, Encoding.UTF8), html, StringComparison.Ordinal))
        {
            File.WriteAllText(path, html, Encoding.UTF8);
        }

        return new Uri(path);
    }

    private static string CreateHtml()
    {
        return """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>NativeWebView Render Mode Test</title>
  <style>
    :root {
      color-scheme: light;
      font-family: "Segoe UI", system-ui, sans-serif;
      background: #ffffff;
      color: #1f2328;
    }
    body {
      margin: 0;
      min-height: 100vh;
      background: #ffffff;
    }
    header {
      padding: 16px 18px;
      border-bottom: 1px solid #d0d7de;
      background: #f6f8fa;
    }
    h1 {
      margin: 0;
      font-size: 22px;
      font-weight: 650;
    }
    main {
      padding: 18px;
      display: grid;
      gap: 16px;
      align-content: start;
    }
    label {
      display: grid;
      gap: 6px;
      font-size: 13px;
      font-weight: 600;
    }
    input, textarea {
      font: inherit;
      padding: 10px 12px;
      border: 1px solid #8c959f;
      border-radius: 6px;
    }
    textarea {
      min-height: 72px;
      resize: vertical;
    }
    button {
      justify-self: start;
      min-width: 130px;
      min-height: 38px;
      border: 1px solid #1f6feb;
      border-radius: 6px;
      background: #0969da;
      color: #ffffff;
      font: inherit;
      font-weight: 650;
    }
    .status {
      padding: 12px;
      border: 1px solid #d0d7de;
      border-radius: 6px;
      background: #f6f8fa;
    }
    .scrollbox {
      height: 170px;
      overflow: auto;
      border: 1px solid #d0d7de;
      border-radius: 6px;
      background: linear-gradient(#ffffff, #f6f8fa);
    }
    .scrollitem {
      padding: 11px 12px;
      border-bottom: 1px solid #d8dee4;
    }
  </style>
</head>
<body>
  <header>
    <h1>NativeWebView render mode behavior test</h1>
  </header>
  <main>
    <label>
      Text input
      <input id="input" value="initial text" autocomplete="off">
    </label>
    <label>
      Notes
      <textarea id="notes">initial notes</textarea>
    </label>
    <button id="clicker" type="button">Click me</button>
    <div class="status" id="status">Clicks: 0</div>
    <div class="scrollbox" id="scrollbox" tabindex="0">
      <div class="scrollitem">Scroll row 01</div>
      <div class="scrollitem">Scroll row 02</div>
      <div class="scrollitem">Scroll row 03</div>
      <div class="scrollitem">Scroll row 04</div>
      <div class="scrollitem">Scroll row 05</div>
      <div class="scrollitem">Scroll row 06</div>
      <div class="scrollitem">Scroll row 07</div>
      <div class="scrollitem">Scroll row 08</div>
      <div class="scrollitem">Scroll row 09</div>
      <div class="scrollitem">Scroll row 10</div>
      <div class="scrollitem">Scroll row 11</div>
      <div class="scrollitem">Scroll row 12</div>
    </div>
  </main>
  <script>
    let clicks = 0;
    const status = document.getElementById('status');
    let lastPointer = null;
    const scrollbox = document.getElementById('scrollbox');
    for (let i = 13; i <= 600; i++) {
      const row = document.createElement('div');
      row.className = 'scrollitem';
      row.textContent = `Scroll row ${String(i).padStart(2, '0')}`;
      scrollbox.appendChild(row);
    }

    document.getElementById('clicker').addEventListener('click', () => {
      clicks += 1;
      status.textContent = `Clicks: ${clicks}`;
    });

    for (const eventType of ['pointerdown', 'pointerup', 'click']) {
      document.addEventListener(eventType, event => {
        lastPointer = {
          type: event.type,
          target: event.target?.id || event.target?.tagName || null,
          clientX: event.clientX,
          clientY: event.clientY
        };
      }, true);
    }
    const rectFor = id => {
      const rect = document.getElementById(id).getBoundingClientRect();
      return {
        left: rect.left,
        top: rect.top,
        right: rect.right,
        bottom: rect.bottom,
        width: rect.width,
        height: rect.height
      };
    };

    window.nativeWebViewComparisonState = () => ({
      input: document.getElementById('input').value,
      notes: document.getElementById('notes').value,
      clicks,
      scrollTop: scrollbox.scrollTop,
      activeElement: document.activeElement?.id || document.activeElement?.tagName || null,
      lastPointer,
      rects: {
        input: rectFor('input'),
        notes: rectFor('notes'),
        clicker: rectFor('clicker'),
        scrollbox: rectFor('scrollbox')
      }
    });
  </script>
</body>
</html>
""";
    }
}
