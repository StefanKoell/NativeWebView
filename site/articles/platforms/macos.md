---
title: "macOS"
---

# macOS

## Backend

- Package: `NativeWebView.Platform.macOS`
- Platform enum: `NativeWebViewPlatform.MacOS`
- Native engine: `WKWebView`

## Current Repo Implementation Status

- `NativeWebView`: implemented.
- `NativeWebDialog`: implemented.
- `WebAuthenticationBroker`: implemented through the macOS dialog runtime and a `WKWebView` auth window.
- Check `NativeWebViewPlatformImplementationStatusMatrix.Get(NativeWebViewPlatform.MacOS)` when you need to distinguish implemented runtime paths from broader capability contracts.

## Platform Engine Capability

- Embedded view
- GPU surface rendering
- Offscreen rendering
- Dialog
- Authentication broker
- Context menu and zoom
- Printing and print UI
- New window and resource request interception
- Environment and controller options
- Native handles
- Cookie manager and command manager

## Registration

```csharp
factory.UseNativeWebViewMacOS();
```

## Composition Notes

macOS supports composited hosting paths and passthrough decisions for scenarios such as hardware-accelerated video playback. Review [Render Modes](../rendering/render-modes.md) before forcing capture-based composition across all content types.

## Proxy Notes

- Per-instance proxy application is implemented for `NativeWebView` and `NativeWebDialog` on `macOS 14+`.
- The current runtime path uses a dedicated persistent `WKWebsiteDataStore` identity derived from the instance configuration.
- Private mode uses a non-persistent `WKWebsiteDataStore`. Explicit profile names or storage paths use a dedicated persistent identity and therefore require macOS 14+.
- Explicit `http`, `https`, and `socks5` proxy servers plus bypass domains are supported.
- PAC (`AutoConfigUrl`) is not applied by the current macOS integration.

## Authentication Notes

- Embedded `NativeWebView` supports synchronous navigation cancellation from 12.0.4.9. This capability is not advertised for the macOS dialog or authentication-broker instances.
- Both WebKit action-policy callback variants honor cancellation. Register handlers before navigating, and decide synchronously.
- The embedded host supports bidirectional `chrome.webview` messaging, native URL/history state, and context-menu enablement. Retaining a `NativeWebViewInstance` across presenter replacement preserves the current page.
- Configure private storage and browser policies before native attachment. See [Controller Options](../rendering/environment-and-controller-options.md) and [NativeWebView](../controls/nativewebview.md) for platform-specific behavior and messaging requirements.
- Deterministic policy and bridge tests do not establish native OAuth, MFA, federation or RDP authentication compatibility. Verify these flows in the consuming application with the packaged runtime.

- The current macOS `WebAuthenticationBroker` implementation uses a dedicated dialog-hosted `WKWebView` session and completes when navigation reaches the callback scheme/host/path.
- `UseHttpPost` is not currently implemented on the macOS runtime path.

## Download Notes

- The embedded backend advertises `NativeWebViewFeature.Downloads` and provides a `WKDownload` delegate bridge with destination, progress, completion/failure and cancellation handling.
- Use the download manager's events to enforce application policy, including canceling downloads when they are not permitted. Verify native behavior on the deployed macOS/WebKit version.
