---
title: "Environment and Controller Options"
---

# Environment and Controller Options

`NativeWebView` exposes two option-request events during initialization so applications can adjust backend defaults before the platform engine is created. For multi-instance hosting, use `NativeWebView.InstanceConfiguration` to seed per-instance defaults before these public handlers run.

## Environment Options Event

Event: `CoreWebView2EnvironmentRequested`

Option model:

- `BrowserExecutableFolder`
- `UserDataFolder`
- `CacheFolder`
- `CookieDataFolder`
- `SessionDataFolder`
- `Language`
- `AdditionalBrowserArguments`
- `TargetCompatibleBrowserVersion`
- `AllowSingleSignOnUsingOSPrimaryAccount`
- `Proxy.Server`
- `Proxy.BypassList`
- `Proxy.AutoConfigUrl`

Use this event to mutate environment defaults before initialization completes.

## Controller Options Event

Event: `CoreWebView2ControllerOptionsRequested`

Option model:

- `ProfileName`
- `IsInPrivateModeEnabled`
- `ScriptLocale`
- `IsJavaScriptEnabled` (default `true`)
- `IsPasswordAutosaveEnabled` (default `true`)
- `IsGeneralAutofillEnabled` (default `true`)
- `RedactNavigationDetails` (default `false`)

Use this event to configure profile, private-mode, and script-locale behavior where the backend supports it.

### Browser policy options (12.0.4.9)

| Option | Runtime mapping |
| --- | --- |
| `IsJavaScriptEnabled` | Applied by Windows, Linux and embedded macOS. The macOS dialog backend rejects `false` with `NotSupportedException`. |
| `IsPasswordAutosaveEnabled` | Applied to WebView2 password saving on Windows; private profiles always disable password saving there. |
| `IsGeneralAutofillEnabled` | Applied to WebView2 general autofill on Windows. This is separate from application-provided autofill scripts. |
| `RedactNavigationDetails` | Omits navigation details from the embedded macOS host's diagnostics. It does not sanitize application logs or arbitrary native-engine output. |

These options are cloned with the instance configuration. The finalized JavaScript
policy, including changes made in `CoreWebView2ControllerOptionsRequested`, is
applied before navigation on supported embedded backends. Embedded macOS retains
that policy when its presenter is replaced. Do not assume password-saving or general
autofill toggles have native mappings on other platforms merely because the option
properties exist.

For a private embedded session, configure `IsInPrivateModeEnabled = true` before
initialization or native attachment. Applications remain responsible for validating
authentication redirects, limiting script execution to trusted origins and keeping
credentials and authorization URLs out of their own logs.

## Per-Instance Proxy Support Matrix

| Platform | Native engine capability | Current repo status |
| --- | --- | --- |
| macOS | `WKWebsiteDataStore.proxyConfigurations` (`macOS 14+`) | Implemented for `NativeWebView` and `NativeWebDialog` with explicit `http`, `https`, and `socks5` proxy servers plus bypass domains. |
| Windows | WebView2 environment arguments / Chromium proxy flags | Runtime-applied for the embedded Windows `NativeWebView` control. |
| Linux | WebKitGTK website data manager proxy settings | Runtime-applied for the embedded Linux `NativeWebView` control on the X11 host path with explicit proxy servers and bypass domains. |
| iOS | `WKWebsiteDataStore.proxyConfigurations` (`iOS 17+`) | Runtime-applied for the embedded iOS `NativeWebView` control when the iOS backend is built with the .NET 8 Apple workload; supports explicit `http`, `https`, and `socks5` proxy servers, credentials, and bypass domains. |
| Android | AndroidX `ProxyController` | Unsupported for per-instance use because the official override is app-wide, not per-WebView. |
| Browser | Host browser integration | Unsupported because the hosted browser target does not expose per-instance engine proxy control on the implemented iframe runtime path. |

Use `NativeWebViewProxyPlatformSupportMatrix.Get(platform)` when you need the same support verdict in code rather than documentation.

## Translation Helpers

The core package now exposes backend-translation helpers for platforms that have official runtime APIs but do not yet have real runtime backends in this repo:

- `NativeWebViewWindowsProxyArgumentsBuilder.Build(...)` / `Merge(...)`
  Converts `NativeWebViewProxyOptions` into Chromium/WebView2 command-line arguments for `AdditionalBrowserArguments`.
- `NativeWebViewLinuxProxySettingsBuilder.Build(...)`
  Converts `NativeWebViewProxyOptions` into a WebKitGTK-style default-proxy URI plus ignore-host list.

These helpers are also used by the current Windows and Linux runtime backends; they remain useful for custom integrations on other targets.

## Example

```csharp
WebViewControl.InstanceConfiguration.EnvironmentOptions.UserDataFolder = "./artifacts/sample-a/userdata";
WebViewControl.InstanceConfiguration.EnvironmentOptions.CacheFolder = "./artifacts/sample-a/cache";
WebViewControl.InstanceConfiguration.EnvironmentOptions.CookieDataFolder = "./artifacts/sample-a/cookies";
WebViewControl.InstanceConfiguration.EnvironmentOptions.SessionDataFolder = "./artifacts/sample-a/session";
WebViewControl.InstanceConfiguration.EnvironmentOptions.Proxy = new NativeWebViewProxyOptions
{
    Server = "http://localhost:8888",
    BypassList = "localhost;127.0.0.1"
};
WebViewControl.InstanceConfiguration.ControllerOptions.ProfileName = "sample-a";

WebViewControl.CoreWebView2EnvironmentRequested += (_, e) =>
{
    e.Options.Language ??= "en-US";
    e.Options.UserDataFolder ??= "./artifacts/sample-webview-userdata";
};

WebViewControl.CoreWebView2ControllerOptionsRequested += (_, e) =>
{
    e.Options.ProfileName ??= "sample-profile";
    e.Options.ScriptLocale ??= "en-US";
};
```

## Notes

- `InstanceConfiguration` is copied per control instance, so multiple `NativeWebView` controls can start from the same template and then diverge safely.
- Exact `UserDataFolder` / `CacheFolder` / `CookieDataFolder` / `SessionDataFolder` semantics are backend-specific. On Linux, `UserDataFolder` (or `SessionDataFolder` as a fallback) and `CacheFolder` configure the WebKitGTK website data manager, while `CookieDataFolder` configures its persistent cookie database; private mode uses an ephemeral web context. The macOS and iOS `WKWebView` runtime paths use storage-path and profile-name values as part of isolated data-store identity rather than direct physical directory mapping. Private mode uses a non-persistent data store; dedicated persistent identities require macOS 14+ or iOS 17+.
- On macOS, assign proxy settings before the control is attached or before a dialog is shown so the native `WKWebView` can be created with a dedicated data store.
- `Proxy.AutoConfigUrl` remains contract-only in the current repo implementation; the macOS, Linux, and iOS runtime paths apply explicit server proxies only.
- Events are raised once per backend initialization in the current implementation.
- Platforms that cannot apply some values may still expose the event for compatibility.
- Keep event handlers deterministic because these values become part of environment reproducibility and CI diagnostics.
