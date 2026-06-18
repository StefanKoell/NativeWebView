---
title: "Windows GPU Surface Experiment"
---

# Windows GPU Surface Experiment

The Windows WebView2 GPU surface experiment currently has two separate transport paths. They solve different parts of the problem and should not be treated as equivalent.

## Avalonia compositor path

`RenderMode="GpuSurface"` with `EnableExperimentalGpuInterop="True"` uses the Avalonia compositor tree. WebView2 renders into a Windows composition visual, Windows Graphics Capture captures that visual, the Windows backend copies the newest frame into one reusable shared keyed-mutex `ID3D11Texture2D`, and Avalonia imports that shared D3D11 texture into a `CompositionDrawingSurface`.

This path preserves Avalonia overlay ordering because the final image is an Avalonia compositor surface. It also avoids sustained CPU readback after the GPU-only path is active. The diagnostics transport is:

`WindowsGraphicsCaptureSharedD3D11Texture`

The render-mode comparison sample has a sustained-scroll gate that requires at least 50 fresh GPU frame copies per second, at least 50 compositor updates per second, no sustained CPU readback, a non-stale latest GPU frame, and a visible Avalonia overlay. The focused `GpuSurface` self-test currently passes that gate on the WGC shared-texture path. Literal `Offscreen` with `EnableExperimentalGpuInterop=True` resolves to effective `GpuSurface`, so an offscreen configuration can use the same compositor-tree GPU transport without changing the public render-mode setting.

The generated self-test page uses a long scrollbox and a sustained wheel-input sequence so the gate keeps producing visual changes for the full measurement window. Earlier 120-row content could hit the scroll limit before the measurement finished.

The current Avalonia runtime reports the following GPU interop capabilities for this path:

- Image handles: `D3D11TextureGlobalSharedHandle, D3D11TextureNtHandle`
- Semaphore handles: none
- Imported image synchronization: `KeyedMutex`

That rules out the public semaphore/timeline-semaphore `CompositionDrawingSurface` update path for this D3D11 WGC transport on the current backend. The retained-frame timer is stopped after WGC frame notifications become active, compositor surface updates are scheduled immediately after fresh GPU frame capture, and a follow-up update is scheduled when a newer frame arrives while a keyed update is in flight. The capture-side keyed-mutex acquire uses a short timeout; when Avalonia is still consuming the shared texture, capture skips that WGC frame instead of blocking the pipeline for up to 100 ms.

Latest repeated focused checks:

| Mode | Runs | Sustained copy fps | Sustained update fps | Sustained CPU readback delta | Overlay |
|---|---:|---|---|---:|---|
| `Offscreen` effective `GpuSurface` | 3/3 passed | 57.8, 56.4, 59.3 | 57.3, 55.9, 58.9 | 0 | visible |
| `GpuSurface` | 3/3 passed | 57.9, 56.2, 55.8 | 57.9, 56.2, 55.8 | 0 | visible |

Rejected probes:

- Starting the keyed Avalonia update inline from the capture callback regressed frame rate and increased update latency.
- Waiting for a fresh WGC frame during active wheel input regressed frame rate and frame freshness.
- Disabling WGC cursor capture regressed frame rate.
- Plain shared textures without keyed mutex are rejected by Avalonia with `PlatformGraphicsContextLostException`.
- A keyed shared texture ring with imported-image caching regressed frame rate and import latency.
- NT shared D3D11 handles were attempted through `IDXGIResource1.CreateSharedHandle`, but the probe caused an access violation and was reverted.
- Avoiding per-frame Avalonia invalidation regressed compositor update cadence.
- Keeping an Avalonia timer pump active alongside WGC notifications during interaction regressed WGC notification cadence.

Run the focused check with:

```powershell
$env:NATIVEWEBVIEW_WINDOWS_DIRECT_COMPOSITION=$null
dotnet run --project samples\NativeWebView.Sample.RenderModeComparison\NativeWebView.Sample.RenderModeComparison.csproj -c Debug --no-build -- --self-test-gpu-surface
```

The JSON result is written to:

`artifacts/render-mode-comparison/gpu-surface-self-test.json`

The screenshot is written to:

`artifacts/render-mode-comparison/screenshots/gpu-surface-self-test.png`

The comparison sample also exposes focused self-tests for the related baselines:

```powershell
$env:NATIVEWEBVIEW_WINDOWS_DIRECT_COMPOSITION=$null
dotnet run --project samples\NativeWebView.Sample.RenderModeComparison\NativeWebView.Sample.RenderModeComparison.csproj -c Debug --no-build -- --self-test-embedded
dotnet run --project samples\NativeWebView.Sample.RenderModeComparison\NativeWebView.Sample.RenderModeComparison.csproj -c Debug --no-build -- --self-test-offscreen

$env:NATIVEWEBVIEW_WINDOWS_DIRECT_COMPOSITION='1'
dotnet run --project samples\NativeWebView.Sample.RenderModeComparison\NativeWebView.Sample.RenderModeComparison.csproj -c Debug --no-build -- --self-test-direct-composition
$env:NATIVEWEBVIEW_WINDOWS_DIRECT_COMPOSITION=$null
```

These write JSON and screenshots under:

- `artifacts/render-mode-comparison/embedded-self-test.json`
- `artifacts/render-mode-comparison/offscreen-self-test.json`
- `artifacts/render-mode-comparison/direct-composition-self-test.json`
- `artifacts/render-mode-comparison/screenshots/embedded-self-test.png`
- `artifacts/render-mode-comparison/screenshots/offscreen-self-test.png`
- `artifacts/render-mode-comparison/screenshots/direct-composition-self-test.png`

## DirectComposition child-window path

Setting `NATIVEWEBVIEW_WINDOWS_DIRECT_COMPOSITION=1` enables an experimental DirectComposition child-window transport. WebView2 renders directly into a DirectComposition visual tree rooted on a child HWND. This bypasses Windows Graphics Capture and can be much smoother, but it is not an Avalonia compositor-tree surface.

The diagnostics transport is:

`DirectCompositionChildWindow`

This path is useful as a performance comparison, but it can reintroduce native HWND airspace and overlay-ordering problems. It is not the target implementation for an overlay-preserving offscreen/Avalonia-compositor render mode.

## Public API status

The current local WebView2 SDK exposes `ICoreWebView2CompositionController.RootVisualTarget`, which accepts a host app `IDCompositionVisual` or `Windows.UI.Composition.ContainerVisual`. It does not expose a WebView frame texture, swapchain, or shared image callback that can be imported directly into Avalonia's `CompositionDrawingSurface`.

Avalonia exposes public GPU import APIs such as `ICompositionGpuInterop.ImportImage(...)` and `CompositionDrawingSurface.UpdateWithKeyedMutexAsync(...)`. Those APIs can import shared GPU images and synchronize them, but they do not accept a WebView2 WinComp/DComp visual subtree as a compositor child.

Because of that API shape, the current public bridge into the Avalonia compositor tree is the WGC shared-texture path. The current experiment reaches the focused 50 fps gate in the sample, but a lower-latency production implementation would still benefit from either:

- a WebView2 API that exposes rendered frames as shared GPU textures or a composable swapchain; or
- an Avalonia Windows platform extension that can host an external WinComp/DComp visual inside Avalonia's compositor tree without using a child HWND.

Until one of those exists, the sample treats the WGC path as the current overlay-preserving compositor-tree implementation, with focused sample validation for the 50 fps target and diagnostics to catch future cadence regressions.
