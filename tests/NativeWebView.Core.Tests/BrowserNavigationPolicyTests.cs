using NativeWebView.Controls;
using NativeWebView.Core;
using NativeWebView.Platform.Windows;
using NativeWebView.Platform.macOS;
using NativeWebView.Platform.Linux;

namespace NativeWebView.Core.Tests;

public class BrowserNavigationPolicyTests
{
    [Fact]
    public void NativeNavigationStateOverridesSyntheticStateAndSurvivesPresenterReplacement()
    {
        using var instance = new NativeWebViewInstance(new MacOSNativeWebViewBackend());
        var state = new NavigationState();
        using (var presenter = new NativeWebView.Controls.NativeWebView(instance))
        {
            presenter.Navigate(new Uri("https://example.test/initial"));
            instance.NativeNavigationState = state;
            // A host with no committed document must not report the synthetic requested URL.
            Assert.Null(presenter.CurrentUrl);
            state.CurrentUrl = new Uri("https://federation.test/sign-in");
            state.CanGoBack = true;
            Assert.Equal(state.CurrentUrl, presenter.Source);
            Assert.True(presenter.CanGoBack);
            Assert.False(presenter.CanGoForward);
        }
        using var replacement = new NativeWebView.Controls.NativeWebView(instance);
        Assert.Equal(state.CurrentUrl, replacement.CurrentUrl);
        state.CurrentUrl = new Uri("https://example.test/redirected");
        state.CanGoBack = false;
        state.CanGoForward = true;
        Assert.Equal(state.CurrentUrl, instance.CurrentUrl);
        Assert.Equal(state.CurrentUrl, replacement.Source);
        Assert.False(replacement.CanGoBack);
        Assert.True(replacement.CanGoForward);
        instance.Dispose();
        Assert.Null(instance.NativeNavigationState);
    }

    private sealed class NavigationState : INativeNavigationState
    {
        public Uri? CurrentUrl { get; set; }
        public bool CanGoBack { get; set; }
        public bool CanGoForward { get; set; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MacOSHostUsesEventPolicyWithoutChangingSavedConfiguration(bool enabled)
    {
        var configuration = new NativeWebViewInstanceConfiguration();
        configuration.ControllerOptions.IsJavaScriptEnabled = !enabled;
        using var instance = new NativeWebViewInstance(new MacOSNativeWebViewBackend(), configuration);
        using (var presenter = new NativeWebView.Controls.NativeWebView(instance))
        {
            presenter.CoreWebView2ControllerOptionsRequested += (_, e) => e.Options.IsJavaScriptEnabled = enabled;
            await presenter.InitializeAsync();
            Assert.Equal(enabled, instance.GetMacOSHostConfiguration().ControllerOptions.IsJavaScriptEnabled);
            Assert.Equal(!enabled, presenter.InstanceConfiguration.ControllerOptions.IsJavaScriptEnabled);
        }
        using var replacement = new NativeWebView.Controls.NativeWebView(instance);
        Assert.Equal(enabled, instance.GetMacOSHostConfiguration().ControllerOptions.IsJavaScriptEnabled);
        var snapshot = instance.GetMacOSHostConfiguration();
        snapshot.ControllerOptions.IsJavaScriptEnabled = !enabled;
        Assert.Equal(enabled, instance.GetMacOSHostConfiguration().ControllerOptions.IsJavaScriptEnabled);
    }

    [Fact]
    public void MacOSImplicitNavigationInitializesPolicyFirst()
    {
        using var instance = new NativeWebViewInstance(new MacOSNativeWebViewBackend());
        using var presenter = new NativeWebView.Controls.NativeWebView(instance);
        var calls = 0;
        presenter.CoreWebView2ControllerOptionsRequested += (_, e) =>
        {
            calls++;
            e.Options.IsJavaScriptEnabled = false;
        };
        presenter.Navigate(new Uri("https://example.test/first"));
        presenter.Navigate("https://example.test/second");
        Assert.Equal(1, calls);
        Assert.False(instance.GetMacOSHostConfiguration().ControllerOptions.IsJavaScriptEnabled);
    }

    [Fact]
    public void NavigationReplacementSupersedesAllowedActionAndPreservesNewRequest()
    {
        var version = 1;
        var expected = version;
        var allowed = NavigationPolicyDecision.IsAllowed(new Uri("https://example.test/old"), false, _ => version++);
        Assert.True(allowed);
        Assert.True(NavigationPolicyDecision.IsSuperseded(expected, version));
        Uri? pending = new("https://example.test/new");
        Assert.False(NavigationPolicyDecision.CancelPending(new Uri("https://example.test/old"), expected, ref version, ref pending));
        Assert.Equal("https://example.test/new", pending!.AbsoluteUri);
        Assert.False(NavigationPolicyDecision.IsSuperseded(version, version));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxUsesFinalizedJavaScriptPolicy(bool enabled)
    {
        using var backend = new LinuxNativeWebViewBackend();
        backend.CoreWebView2ControllerOptionsRequested += (_, e) => e.Options.IsJavaScriptEnabled = enabled;
        await backend.InitializeAsync();
        Assert.Equal(enabled, backend.IsPageJavaScriptEnabled);
    }

    [Fact]
    public void MacOSDialogRejectsUnsupportedJavaScriptPolicy()
    {
        using var backend = new MacOSNativeWebDialogBackend();
        var configuration = new NativeWebViewInstanceConfiguration();
        configuration.ControllerOptions.IsJavaScriptEnabled = false;
        Assert.Throws<NotSupportedException>(() => backend.ApplyInstanceConfiguration(configuration));
        configuration.ControllerOptions.IsJavaScriptEnabled = true;
        backend.ApplyInstanceConfiguration(configuration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsUsesFinalizedPolicyIncludingPrivateMode(bool privateMode)
    {
        using var backend = new WindowsNativeWebViewBackend();
        backend.CoreWebView2ControllerOptionsRequested += (_, e) =>
        {
            e.Options.IsJavaScriptEnabled = false;
            e.Options.IsPasswordAutosaveEnabled = true;
            e.Options.IsGeneralAutofillEnabled = false;
            e.Options.IsInPrivateModeEnabled = privateMode;
        };
        await backend.InitializeAsync();
        Assert.Equal((false, !privateMode, false), backend.GetBrowserPolicy());
    }

    [Fact]
    public async Task WindowsEventCanDisablePasswordSaving()
    {
        using var backend = new WindowsNativeWebViewBackend();
        backend.CoreWebView2ControllerOptionsRequested += (_, e) => e.Options.IsPasswordAutosaveEnabled = false;
        await backend.InitializeAsync();
        Assert.False(backend.GetBrowserPolicy().PasswordAutosave);
    }

    [Fact]
    public void CancelInvalidatesQueuedRetriesOnce()
    {
        Uri? pending = new("https://example.test/first");
        var canceled = pending;
        var version = 7;
        Assert.True(NavigationPolicyDecision.CancelPending(canceled, 7, ref version, ref pending));
        Assert.Null(pending);
        Assert.Equal(8, version);
        Assert.False(NavigationPolicyDecision.CancelPending(canceled, 7, ref version, ref pending));
    }

    [Theory]
    [InlineData("https://example.test/first", 8)]
    [InlineData("https://example.test/second", 7)]
    public void CancelPreservesReplacementEvenWhenUrlIsReused(string replacement, int currentVersion)
    {
        Uri? pending = new(replacement);
        var version = currentVersion;
        Assert.False(NavigationPolicyDecision.CancelPending(new Uri("https://example.test/first"), 7,
            ref version, ref pending));
        Assert.Equal(replacement, pending!.AbsoluteUri);
        Assert.Equal(currentVersion, version);
    }

    [Fact]
    public void ReverseCallbackContainsSubscriberAndDiagnosticExceptions()
    {
        var calls = 0;
        var reports = 0;
        NativeCallbackBoundary.Invoke(() =>
        {
            calls++;
            throw new InvalidOperationException("sensitive failure details");
        }, exception =>
        {
            reports++;
            Assert.IsType<InvalidOperationException>(exception);
            throw new Exception("diagnostic failure");
        });
        Assert.Equal(1, calls);
        Assert.Equal(1, reports);
    }

    [Fact]
    public void SubframeAndPopupActionsDoNotNotifyPageNavigationSubscribers()
    {
        Assert.True(NavigationPolicyDecision.IsAllowed(new Uri("about:blank"), false,
            _ => Assert.Fail("Frame load reported as page navigation"), isMainFrame: false));
        Assert.True(NavigationPolicyDecision.IsAllowed(null, false,
            _ => Assert.Fail("Popup reported as page navigation"), isMainFrame: false));
        Assert.False(NavigationPolicyDecision.IsAllowed(null, true,
            _ => Assert.Fail("Disposed host notified subscriber"), isMainFrame: false));
    }

    [Fact]
    public void OnlyBrowserAndMainRendererExitAreTerminal()
    {
        // WebView2's published ProcessFailedKind values: browser/main renderer exit are 0/1;
        // unresponsive renderer, frame, utility, sandbox, GPU and plugin failures follow.
        for (var kind = 0; kind <= 10; kind++)
            Assert.Equal(kind is 0 or 1,
                WindowsNativeWebViewBackend.IsTerminalProcessFailure(kind));
        Assert.False(WindowsNativeWebViewBackend.IsTerminalProcessFailure(int.MaxValue));
    }

    [Fact]
    public void MacOSOnlyEmbeddedBackendAdvertisesNavigationCancellation()
    {
        using var embedded = new MacOSNativeWebViewBackend();
        using var dialog = new MacOSNativeWebDialogBackend();
        using var authentication = new MacOSWebAuthenticationBrokerBackend();
        Assert.True(embedded.Features.Supports(NativeWebViewFeature.NavigationCancellation));
        Assert.False(dialog.Features.Supports(NativeWebViewFeature.NavigationCancellation));
        Assert.False(authentication.Features.Supports(NativeWebViewFeature.NavigationCancellation));
    }

    [Fact]
    public void NativeDecisionIsCompletedOnceEvenIfResolutionThrows()
    {
        var calls = 0;
        NavigationPolicyDecision.Complete(() => throw new InvalidOperationException(), policy =>
        {
            calls++;
            Assert.Equal((nint)0, policy);
        });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NativeDecisionPreservesAllowedPolicy()
    {
        var calls = 0;
        NavigationPolicyDecision.Complete(() => 1, policy =>
        {
            calls++;
            Assert.Equal((nint)1, policy);
        });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RedirectCancellationIsConsumedSynchronously()
    {
        var calls = 0;
        var allowed = NavigationPolicyDecision.IsAllowed(new Uri("https://example.test/callback?code=private"), false,
            args => { calls++; args.Cancel = true; });
        Assert.False(allowed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void SubscriberFailurePreventsNavigation() => Assert.False(
        NavigationPolicyDecision.IsAllowed(new Uri("https://example.test"), false, _ => throw new InvalidOperationException()));

    [Fact]
    public void DisposedHostDoesNotNotifySubscribers() => Assert.False(
        NavigationPolicyDecision.IsAllowed(new Uri("https://example.test"), true, _ => Assert.Fail("Disposed host notified subscriber")));

    [Fact]
    public void BrowserPolicySurvivesConfigurationCloning()
    {
        var configuration = new NativeWebViewInstanceConfiguration();
        configuration.ControllerOptions.IsJavaScriptEnabled = false;
        configuration.ControllerOptions.IsPasswordAutosaveEnabled = false;
        configuration.ControllerOptions.IsGeneralAutofillEnabled = false;
        configuration.ControllerOptions.RedactNavigationDetails = true;
        var clone = configuration.Clone();
        Assert.False(clone.ControllerOptions.IsJavaScriptEnabled);
        Assert.False(clone.ControllerOptions.IsPasswordAutosaveEnabled);
        Assert.False(clone.ControllerOptions.IsGeneralAutofillEnabled);
        Assert.True(clone.ControllerOptions.RedactNavigationDetails);
    }
}
