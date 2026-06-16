using Avalonia;
using Avalonia.Media.Imaging;
using NativeWebView.Platform.Windows;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NativeWebView.Core.Tests;

public sealed class NativeWebViewRenderCaptureBehaviorTests
{
    [Fact]
    public async Task CaptureRenderFrameAsync_CanceledToken_ThrowsOperationCanceledException()
    {
        using var backend = new WindowsNativeWebViewBackend();
        using var webView = new NativeWebView.Controls.NativeWebView(backend)
        {
            RenderMode = NativeWebViewRenderMode.GpuSurface,
        };

        webView.Measure(new Size(320, 200));
        webView.Arrange(new Rect(0, 0, 320, 200));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => webView.CaptureRenderFrameAsync(cancellation.Token));
    }

    [Fact]
    public async Task SaveRenderFrameAsync_CanceledToken_ThrowsOperationCanceledException()
    {
        using var backend = new WindowsNativeWebViewBackend();
        using var webView = new NativeWebView.Controls.NativeWebView(backend)
        {
            RenderMode = NativeWebViewRenderMode.GpuSurface,
        };

        webView.Measure(new Size(320, 200));
        webView.Arrange(new Rect(0, 0, 320, 200));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            webView.SaveRenderFrameAsync("artifacts/tests/should-not-write.png", cancellation.Token));
    }

    [Fact]
    public async Task CaptureRenderFrameAsync_EventHandlerException_DoesNotFailCapture()
    {
        using var backend = new WindowsNativeWebViewBackend();
        using var webView = new NativeWebView.Controls.NativeWebView(backend)
        {
            RenderMode = NativeWebViewRenderMode.GpuSurface,
        };

        webView.Measure(new Size(320, 200));
        webView.Arrange(new Rect(0, 0, 320, 200));

        webView.RenderFrameCaptured += static (_, _) =>
        {
            throw new InvalidOperationException("handler failure");
        };

        var frame = new NativeWebViewRenderFrame(
            pixelWidth: 4,
            pixelHeight: 2,
            bytesPerRow: 16,
            pixelFormat: NativeWebViewRenderPixelFormat.Bgra8888Premultiplied,
            pixelData: new byte[32],
            isSynthetic: true,
            frameId: 1,
            capturedAtUtc: DateTimeOffset.UtcNow,
            renderMode: NativeWebViewRenderMode.GpuSurface,
            origin: NativeWebViewRenderFrameOrigin.SyntheticFallback);

        var method = typeof(NativeWebView.Controls.NativeWebView).GetMethod(
            "RaiseRenderFrameCaptured",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var exception = Record.Exception(() =>
        {
            method!.Invoke(webView, [frame]);
        }

        );

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(NativeWebViewRenderMode.GpuSurface)]
    [InlineData(NativeWebViewRenderMode.Offscreen)]
    public void UpdateCapturedRenderSurface_SyntheticFrameWithoutRetainedSurface_RendersSurface(
        NativeWebViewRenderMode renderMode)
    {
        using var backend = new WindowsNativeWebViewBackend();
        using var webView = new NativeWebView.Controls.NativeWebView(backend)
        {
            RenderMode = renderMode,
        };

        var frame = CreateSyntheticFrame(renderMode);
        var exception = Record.Exception(() => InvokeUpdateCapturedRenderSurface(webView, frame));
        if (UnwrapInvocationException(exception) is InvalidOperationException invalidOperationException &&
            invalidOperationException.Message.Contains("IPlatformRenderInterface", StringComparison.Ordinal))
        {
            return;
        }

        Assert.Null(exception);
        Assert.NotNull(GetRetainedCompositedBitmap(webView, renderMode));
    }

    [Theory]
    [InlineData(NativeWebViewRenderMode.GpuSurface)]
    [InlineData(NativeWebViewRenderMode.Offscreen)]
    public void UpdateCapturedRenderSurface_SyntheticFrameWithRetainedSurface_KeepsRetainedSurface(
        NativeWebViewRenderMode renderMode)
    {
        using var backend = new WindowsNativeWebViewBackend();
        using var webView = new NativeWebView.Controls.NativeWebView(backend)
        {
            RenderMode = renderMode,
        };

        var retainedBitmap = CreateUninitializedBitmapSentinel();
        SetRetainedCompositedBitmap(webView, renderMode, retainedBitmap);

        try
        {
            var frame = CreateSyntheticFrame(renderMode);
            InvokeUpdateCapturedRenderSurface(webView, frame);

            Assert.Same(retainedBitmap, GetRetainedCompositedBitmap(webView, renderMode));
        }
        finally
        {
            SetRetainedCompositedBitmap(webView, renderMode, value: null);
        }
    }

    [Fact]
    public async Task SaveRenderFrameWithMetadataAsync_CanceledToken_ThrowsOperationCanceledException()
    {
        using var backend = new WindowsNativeWebViewBackend();
        using var webView = new NativeWebView.Controls.NativeWebView(backend)
        {
            RenderMode = NativeWebViewRenderMode.GpuSurface,
        };

        webView.Measure(new Size(320, 200));
        webView.Arrange(new Rect(0, 0, 320, 200));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            webView.SaveRenderFrameWithMetadataAsync(
                "artifacts/tests/should-not-write-frame.png",
                "artifacts/tests/should-not-write-frame.json",
                cancellation.Token));
    }

    [Fact]
    public async Task SaveRenderFrameWithMetadataAsync_EmbeddedMode_ReturnsFalse()
    {
        using var backend = new WindowsNativeWebViewBackend();
        using var webView = new NativeWebView.Controls.NativeWebView(backend)
        {
            RenderMode = NativeWebViewRenderMode.Embedded,
        };

        var outputDirectory = Path.Combine(Path.GetTempPath(), "NativeWebView.Tests", Guid.NewGuid().ToString("N"));
        var imagePath = Path.Combine(outputDirectory, "frame.png");
        var metadataPath = Path.Combine(outputDirectory, "frame.json");

        try
        {
            var saved = await webView.SaveRenderFrameWithMetadataAsync(imagePath, metadataPath);

            Assert.False(saved);
            Assert.False(File.Exists(imagePath));
            Assert.False(File.Exists(metadataPath));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private static WriteableBitmap CreateUninitializedBitmapSentinel()
    {
        return (WriteableBitmap)RuntimeHelpers.GetUninitializedObject(typeof(WriteableBitmap));
    }

    private static void SetRetainedCompositedBitmap(
        NativeWebView.Controls.NativeWebView webView,
        NativeWebViewRenderMode renderMode,
        object? value)
    {
        GetRetainedCompositedBitmapField(renderMode).SetValue(webView, value);
    }

    private static object? GetRetainedCompositedBitmap(
        NativeWebView.Controls.NativeWebView webView,
        NativeWebViewRenderMode renderMode)
    {
        return GetRetainedCompositedBitmapField(renderMode).GetValue(webView);
    }

    private static FieldInfo GetRetainedCompositedBitmapField(NativeWebViewRenderMode renderMode)
    {
        var fieldName = renderMode == NativeWebViewRenderMode.GpuSurface
            ? "_gpuSurfaceBitmap"
            : "_offscreenBitmap";
        var field = typeof(NativeWebView.Controls.NativeWebView).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        return field!;
    }

    private static NativeWebViewRenderFrame CreateSyntheticFrame(NativeWebViewRenderMode renderMode)
    {
        return new NativeWebViewRenderFrame(
            pixelWidth: 4,
            pixelHeight: 2,
            bytesPerRow: 16,
            pixelFormat: NativeWebViewRenderPixelFormat.Bgra8888Premultiplied,
            pixelData: new byte[32],
            isSynthetic: true,
            frameId: 1,
            capturedAtUtc: DateTimeOffset.UtcNow,
            renderMode: renderMode,
            origin: NativeWebViewRenderFrameOrigin.SyntheticFallback);
    }

    private static void InvokeUpdateCapturedRenderSurface(
        NativeWebView.Controls.NativeWebView webView,
        NativeWebViewRenderFrame frame)
    {
        var method = typeof(NativeWebView.Controls.NativeWebView).GetMethod(
            "UpdateCapturedRenderSurface",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method!.Invoke(webView, [frame]);
    }

    private static Exception? UnwrapInvocationException(Exception? exception)
    {
        return exception is TargetInvocationException { InnerException: { } innerException }
            ? innerException
            : exception;
    }
}
