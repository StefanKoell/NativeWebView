using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.System;
using Windows.UI.Composition;
using NativeWebView.Core;
using WinRT;

namespace NativeWebView.Platform.Windows;

public sealed partial class WindowsNativeWebViewBackend
{
    private sealed class WinCompositionCaptureHost : IDisposable
    {
        private const int FramePoolBufferCount = 3;
        private readonly object _gate = new();
        private readonly object _frameDrainGate = new();
        private readonly Compositor _compositor;
        private readonly IDirect3DDevice _winRtDevice;
        private readonly D3D.ID3D11Device _d3d11Device;
        private readonly D3D.ID3D11DeviceContext _d3d11Context;
        private readonly object? _dispatcherQueueController;

        private GraphicsCaptureItem? _captureItem;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _captureSession;
        private SizeInt32 _currentSize;
        private NativeWebViewRenderFrame? _latestFrame;
        private NativeWebViewGpuFrame? _latestGpuFrame;
        private GpuTextureSlot[] _gpuTextureSlots = [];
        private int _gpuTextureSlotIndex = -1;
        private int _gpuTextureSlotsWidth;
        private int _gpuTextureSlotsHeight;
        private bool _gpuFrameCaptureEnabled;
        private bool _gpuFrameOnlyRenderingEnabled;
        private NativeWebViewRenderMode _currentRenderMode = NativeWebViewRenderMode.Offscreen;
        private long _frameSequence;
        private int _pendingFrameArrived;
        private long _frameArrivedSignalCount;
        private long _gpuFrameCopyCount;
        private long _gpuFrameCopyElapsedTicks;
        private long _gpuFrameCopyMaxTicks;
        private long _cpuFrameCopyCount;
        private long _retainedGpuOnlyFrameReturnCount;
        private long _latestGpuFrameId;
        private long _latestCpuFrameId;
        private long _latestGpuFrameCapturedAtUtcTicks;
        private long _latestCpuFrameCapturedAtUtcTicks;
        private long _freshGpuFrameDemandUntilUtcTicks;
        private int _freshGpuFrameCommitPulseRunning;
        private bool _disposed;

        public WinCompositionCaptureHost()
        {
            _dispatcherQueueController = EnsureDispatcherQueue();
            _compositor = new Compositor();
            WebViewVisual = _compositor.CreateContainerVisual();
            _winRtDevice = D3D.CreateDevice(out _d3d11Device, out _d3d11Context);
        }

        public event EventHandler? FrameArrived;

        public ContainerVisual WebViewVisual { get; }

        public NativeWebViewGpuFrame? TryGetLatestGpuFrame(NativeWebViewRenderMode renderMode)
        {
            lock (_gate)
            {
                return _latestGpuFrame is not null && _latestGpuFrame.RenderMode == renderMode
                    ? _latestGpuFrame
                    : null;
            }
        }

        public void SetGpuFrameCaptureEnabled(bool enabled)
        {
            lock (_gate)
            {
                _gpuFrameCaptureEnabled = enabled;
                if (!enabled)
                {
                    _gpuFrameOnlyRenderingEnabled = false;
                }

                if (!enabled)
                {
                    _latestGpuFrame = null;
                    ReleaseGpuTextureSlots();
                }
            }
        }

        public void SetGpuFrameOnlyRenderingEnabled(bool enabled)
        {
            lock (_gate)
            {
                _gpuFrameOnlyRenderingEnabled = enabled && _gpuFrameCaptureEnabled;
            }
        }

        public void RequestFreshGpuFrames(TimeSpan duration)
        {
            var untilTicks = DateTimeOffset.UtcNow.Add(duration).UtcTicks;
            var current = Interlocked.Read(ref _freshGpuFrameDemandUntilUtcTicks);
            while (untilTicks > current &&
                   Interlocked.CompareExchange(ref _freshGpuFrameDemandUntilUtcTicks, untilTicks, current) != current)
            {
                current = Interlocked.Read(ref _freshGpuFrameDemandUntilUtcTicks);
            }

            _ = _compositor.RequestCommitAsync();
            EnsureFreshGpuFrameCommitPulse();
        }

        private void EnsureFreshGpuFrameCommitPulse()
        {
            if (Interlocked.Exchange(ref _freshGpuFrameCommitPulseRunning, 1) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_disposed && IsFreshGpuFrameDemandActive())
                    {
                        _ = _compositor.RequestCommitAsync();
                        await Task.Delay(16).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _freshGpuFrameCommitPulseRunning, 0);
                    if (!_disposed && IsFreshGpuFrameDemandActive())
                    {
                        EnsureFreshGpuFrameCommitPulse();
                    }
                }
            });
        }

        public NativeWebViewGpuFrameDiagnosticsSnapshot GetGpuFrameDiagnosticsSnapshot()
        {
            lock (_gate)
            {
                return new NativeWebViewGpuFrameDiagnosticsSnapshot(
                    NativeWebViewGpuFrameTransport.WindowsGraphicsCaptureSharedD3D11Texture,
                    _currentRenderMode,
                    _currentRenderMode,
                    _currentSize.Width,
                    _currentSize.Height,
                    0,
                    _gpuFrameCaptureEnabled,
                    _gpuFrameOnlyRenderingEnabled,
                    false,
                    false,
                    false,
                    0,
                    0,
                    Interlocked.Read(ref _frameArrivedSignalCount),
                    Interlocked.Read(ref _gpuFrameCopyCount),
                    Interlocked.Read(ref _cpuFrameCopyCount),
                    Interlocked.Read(ref _retainedGpuOnlyFrameReturnCount),
                    Interlocked.Read(ref _latestGpuFrameId),
                    Interlocked.Read(ref _latestCpuFrameId),
                    GetFrameAgeMilliseconds(Interlocked.Read(ref _latestGpuFrameCapturedAtUtcTicks)),
                    GetFrameAgeMilliseconds(Interlocked.Read(ref _latestCpuFrameCapturedAtUtcTicks)),
                    0,
                    GetElapsedMicroseconds(Interlocked.Read(ref _gpuFrameCopyElapsedTicks)),
                    GetAverageMicroseconds(
                        Interlocked.Read(ref _gpuFrameCopyElapsedTicks),
                        Interlocked.Read(ref _gpuFrameCopyCount)),
                    GetElapsedMicroseconds(Interlocked.Read(ref _gpuFrameCopyMaxTicks)),
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    0,
                    false,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false,
                    0,
                    0,
                    0,
                    0);
            }
        }

        public async Task<NativeWebViewRenderFrame?> CaptureFrameAsync(
            NativeWebViewRenderMode renderMode,
            NativeWebViewRenderFrameRequest request,
            long frameId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _currentRenderMode = renderMode;
            }

            EnsureCaptureSession(request.PixelWidth, request.PixelHeight);
            _ = _compositor.RequestCommitAsync();

            if (TryDrainLatestAvailableFrame(renderMode, out var availableFrame))
            {
                return availableFrame;
            }

            if (!HasPendingFrameArrived() &&
                TryGetLatestRetainedFrame(gpuOnly: true, out var retainedGpuOnlyFrame))
            {
                Interlocked.Increment(ref _retainedGpuOnlyFrameReturnCount);
                return retainedGpuOnlyFrame;
            }

            for (var attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(16, cancellationToken).ConfigureAwait(true);
                if (TryDrainLatestAvailableFrame(renderMode, out availableFrame))
                {
                    return availableFrame;
                }
            }

            return TryGetLatestRetainedFrame(gpuOnly: false, out var retainedFrame)
                ? retainedFrame
                : null;
        }

        private bool TryGetLatestRetainedFrame(bool gpuOnly, out NativeWebViewRenderFrame? frame)
        {
            lock (_gate)
            {
                frame = _latestFrame;
                return frame is not null && (!gpuOnly || _gpuFrameOnlyRenderingEnabled);
            }
        }

        private void EnsureCaptureSession(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            WebViewVisual.Size = new Vector2(width, height);
            _ = _compositor.RequestCommitAsync();

            if (_captureItem is null)
            {
                _captureItem = GraphicsCaptureItem.CreateFromVisual(WebViewVisual);
            }

            var size = new SizeInt32 { Width = width, Height = height };
            if (_framePool is null || _captureSession is null)
            {
                _currentSize = size;
                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winRtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    FramePoolBufferCount,
                    _currentSize);
                _framePool.FrameArrived += FramePoolOnFrameArrived;
                _captureSession = _framePool.CreateCaptureSession(_captureItem);
                _captureSession.StartCapture();
                return;
            }

            if (_currentSize.Width != width || _currentSize.Height != height)
            {
                _currentSize = size;
                _framePool.Recreate(
                    _winRtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    FramePoolBufferCount,
                    _currentSize);
            }
        }

        private void FramePoolOnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            Interlocked.Exchange(ref _pendingFrameArrived, 1);
            Interlocked.Increment(ref _frameArrivedSignalCount);
            if (IsFreshGpuFrameDemandActive())
            {
                _ = _compositor.RequestCommitAsync();
            }

            FrameArrived?.Invoke(this, EventArgs.Empty);
        }

        private bool IsFreshGpuFrameDemandActive()
        {
            return Interlocked.Read(ref _freshGpuFrameDemandUntilUtcTicks) > DateTimeOffset.UtcNow.UtcTicks;
        }

        private bool TryDrainLatestAvailableFrame(
            NativeWebViewRenderMode renderMode,
            out NativeWebViewRenderFrame? renderedFrame)
        {
            renderedFrame = null;
            if (_framePool is null)
            {
                return false;
            }

            try
            {
                lock (_frameDrainGate)
                {
                    Interlocked.Exchange(ref _pendingFrameArrived, 0);
                    while (true)
                    {
                        using var frame = _framePool.TryGetNextFrame();
                        if (frame is null)
                        {
                            return renderedFrame is not null;
                        }

                        var currentFrameId = Interlocked.Increment(ref _frameSequence);
                        var useGpuOnly = false;
                        var captureGpuFrame = false;
                        NativeWebViewGpuFrame? gpuFrame = null;
                        lock (_gate)
                        {
                            captureGpuFrame = _gpuFrameCaptureEnabled;
                            useGpuOnly = _gpuFrameOnlyRenderingEnabled && _latestFrame is not null;
                        }

                        if (captureGpuFrame)
                        {
                            gpuFrame = CopyFrameToGpu(frame, renderMode, currentFrameId);
                        }

                        if (!useGpuOnly)
                        {
                            renderedFrame = CopyFrameToCpu(frame, renderMode, currentFrameId);
                        }

                        lock (_gate)
                        {
                            if (renderedFrame is not null)
                            {
                                _latestFrame = renderedFrame;
                                Interlocked.Exchange(ref _latestCpuFrameId, renderedFrame.FrameId);
                                Interlocked.Exchange(ref _latestCpuFrameCapturedAtUtcTicks, renderedFrame.CapturedAtUtc.UtcTicks);
                            }
                            else
                            {
                                renderedFrame = _latestFrame;
                            }

                            if (gpuFrame is not null)
                            {
                                _latestGpuFrame = gpuFrame;
                                Interlocked.Exchange(ref _latestGpuFrameId, gpuFrame.FrameId);
                                Interlocked.Exchange(ref _latestGpuFrameCapturedAtUtcTicks, gpuFrame.CapturedAtUtc.UtcTicks);
                            }
                        }
                    }
                }
            }
            catch
            {
                return renderedFrame is not null;
            }
        }

        private bool HasPendingFrameArrived()
        {
            return Volatile.Read(ref _pendingFrameArrived) != 0;
        }

        private static long GetFrameAgeMilliseconds(long capturedAtUtcTicks)
        {
            if (capturedAtUtcTicks <= 0)
            {
                return -1;
            }

            var elapsedTicks = DateTimeOffset.UtcNow.UtcTicks - capturedAtUtcTicks;
            return Math.Max(0, elapsedTicks / TimeSpan.TicksPerMillisecond);
        }

        private void RecordGpuFrameCopyElapsed(long elapsedTicks)
        {
            Interlocked.Add(ref _gpuFrameCopyElapsedTicks, elapsedTicks);
            RecordMax(ref _gpuFrameCopyMaxTicks, elapsedTicks);
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

        private NativeWebViewGpuFrame? CopyFrameToGpu(
            Direct3D11CaptureFrame frame,
            NativeWebViewRenderMode renderMode,
            long frameId)
        {
            var width = Math.Max(1, frame.ContentSize.Width);
            var height = Math.Max(1, frame.ContentSize.Height);

            D3D.ID3D11Texture2D? sourceTexture = null;
            try
            {
                sourceTexture = GetFrameTexture(frame);
                EnsureGpuTextureSlots(width, height);
                GpuTextureSlot? copiedSlot = null;
                var copyStarted = Stopwatch.GetTimestamp();
                for (var attempt = 0; attempt < _gpuTextureSlots.Length; attempt++)
                {
                    var slot = TryGetWritableGpuTextureSlot();
                    if (slot is null)
                    {
                        break;
                    }

                    if (!D3D.TryCopyResourceWithKeyedMutex(
                            _d3d11Context,
                            slot.Mutex,
                            slot.Texture,
                            sourceTexture,
                            timeoutMilliseconds: 1))
                    {
                        continue;
                    }

                    copiedSlot = slot;
                    break;
                }

                if (copiedSlot is null)
                {
                    return null;
                }

                RecordGpuFrameCopyElapsed(Stopwatch.GetTimestamp() - copyStarted);
                Interlocked.Increment(ref _gpuFrameCopyCount);
                return new NativeWebViewGpuFrame(
                    width,
                    height,
                    NativeWebViewGpuFrameBackend.D3D11Texture2D,
                    copiedSlot.Texture,
                    frameId,
                    DateTimeOffset.UtcNow,
                    renderMode,
                    copiedSlot.SharedHandle,
                    D3D.D3D11TextureGlobalSharedHandleType,
                    requiresKeyedMutex: true,
                    keyedMutexAcquireKey: 1,
                    keyedMutexReleaseKey: 0);
            }
            finally
            {
                ReleaseComObject(sourceTexture);
            }
        }

        private void EnsureGpuTextureSlots(int width, int height)
        {
            const int requestedSlotCount = 1;
            if (_gpuTextureSlots.Length == requestedSlotCount &&
                _gpuTextureSlotsWidth == width &&
                _gpuTextureSlotsHeight == height)
            {
                return;
            }

            ReleaseGpuTextureSlots();
            _gpuTextureSlots = new GpuTextureSlot[requestedSlotCount];
            _gpuTextureSlotIndex = -1;
            _gpuTextureSlotsWidth = width;
            _gpuTextureSlotsHeight = height;

            var desc = new D3D.Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = D3D.Format.B8G8R8A8_UNorm,
                SampleDescription = new D3D.DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D.D3D11_USAGE.Default,
                BindFlags = D3D.D3D11_BIND_FLAG.ShaderResource | D3D.D3D11_BIND_FLAG.RenderTarget,
                CpuAccessFlags = 0,
                OptionFlags = D3D.OptionFlags.SharedKeyedMutex,
            };

            for (var index = 0; index < _gpuTextureSlots.Length; index++)
            {
                D3D.CreateD3D11Texture(_d3d11Device, desc, out var texture);
                _gpuTextureSlots[index] = new GpuTextureSlot(
                    texture,
                    D3D.GetKeyedMutex(texture),
                    D3D.GetSharedHandle(texture));
            }
        }

        private GpuTextureSlot? TryGetWritableGpuTextureSlot()
        {
            if (_gpuTextureSlots.Length == 0)
            {
                return null;
            }

            for (var attempt = 0; attempt < _gpuTextureSlots.Length; attempt++)
            {
                var index = (_gpuTextureSlotIndex + 1 + attempt) % _gpuTextureSlots.Length;
                var slot = _gpuTextureSlots[index];
                if (slot is null)
                {
                    continue;
                }

                _gpuTextureSlotIndex = index;
                return slot;
            }

            return null;
        }

        private NativeWebViewRenderFrame CopyFrameToCpu(
            Direct3D11CaptureFrame frame,
            NativeWebViewRenderMode renderMode,
            long frameId)
        {
            var width = Math.Max(1, frame.ContentSize.Width);
            var height = Math.Max(1, frame.ContentSize.Height);
            var desc = new D3D.Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = D3D.Format.B8G8R8A8_UNorm,
                SampleDescription = new D3D.DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D.D3D11_USAGE.Staging,
                BindFlags = 0,
                CpuAccessFlags = D3D.D3D11_CPU_ACCESS_READ,
                OptionFlags = 0,
            };

            D3D.ID3D11Texture2D? sourceTexture = null;
            D3D.ID3D11Texture2D? stagingTexture = null;
            try
            {
                sourceTexture = GetFrameTexture(frame);
                D3D.CreateD3D11Texture(_d3d11Device, desc, out stagingTexture);
                D3D.CopyResource(_d3d11Context, stagingTexture, sourceTexture);

                Marshal.ThrowExceptionForHR(_d3d11Context.Map(
                    stagingTexture,
                    0,
                    D3D.D3D11_MAP.Read,
                    0,
                    out var mapped));
                try
                {
                    var bytesPerRow = checked(width * 4);
                    var pixels = new byte[checked(bytesPerRow * height)];
                    for (var row = 0; row < height; row++)
                    {
                        Marshal.Copy(
                            mapped.DataPointer + checked(row * (int)mapped.RowPitch),
                            pixels,
                            row * bytesPerRow,
                            bytesPerRow);
                    }

                    return new NativeWebViewRenderFrame(
                        width,
                        height,
                        bytesPerRow,
                        NativeWebViewRenderPixelFormat.Bgra8888Premultiplied,
                        pixels,
                        isSynthetic: false,
                        frameId,
                        DateTimeOffset.UtcNow,
                        renderMode,
                        NativeWebViewRenderFrameOrigin.NativeCapture);
                }
                finally
                {
                    _d3d11Context.Unmap(stagingTexture, 0);
                    Interlocked.Increment(ref _cpuFrameCopyCount);
                }
            }
            finally
            {
                ReleaseComObject(stagingTexture);
                ReleaseComObject(sourceTexture);
            }
        }

        private static D3D.ID3D11Texture2D GetFrameTexture(Direct3D11CaptureFrame frame)
        {
            var surfaceMarshaler = MarshalInterface<IDirect3DSurface>.CreateMarshaler(frame.Surface);
            var surfacePointer = MarshalInterface<IDirect3DSurface>.GetAbi(surfaceMarshaler);
            try
            {
                var accessId = typeof(D3D.IDirect3DDxgiInterfaceAccess).GUID;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePointer, in accessId, out var accessPointer));
                try
                {
                    var access = (D3D.IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPointer);
                    var textureId = new Guid(D3D.ID3D11Texture2DGuid);
                    Marshal.ThrowExceptionForHR(access.GetInterface(ref textureId, out var texturePointer));
                    try
                    {
                        return (D3D.ID3D11Texture2D)Marshal.GetObjectForIUnknown(texturePointer);
                    }
                    finally
                    {
                        Marshal.Release(texturePointer);
                    }
                }
                finally
                {
                    Marshal.Release(accessPointer);
                }
            }
            finally
            {
                MarshalInterface<IDirect3DSurface>.DisposeMarshaler(surfaceMarshaler);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _captureSession?.Dispose();
            if (_framePool is not null)
            {
                _framePool.FrameArrived -= FramePoolOnFrameArrived;
            }

            _framePool?.Dispose();
            _captureItem = null;
            _latestGpuFrame = null;
            ReleaseGpuTextureSlots();
            WebViewVisual.Dispose();
            _compositor.Dispose();
            _winRtDevice.Dispose();
            ReleaseComObject(_d3d11Context);
            ReleaseComObject(_d3d11Device);
            ReleaseComObject(_dispatcherQueueController);
        }

        private void ReleaseGpuTextureSlots()
        {
            foreach (var slot in _gpuTextureSlots)
            {
                if (slot is null)
                {
                    continue;
                }

                ReleaseComObject(slot.Mutex);
                ReleaseComObject(slot.Texture);
            }

            _gpuTextureSlots = [];
            _gpuTextureSlotIndex = -1;
            _gpuTextureSlotsWidth = 0;
            _gpuTextureSlotsHeight = 0;
        }

        private sealed class GpuTextureSlot(
            D3D.ID3D11Texture2D texture,
            D3D.IDXGIKeyedMutex mutex,
            IntPtr sharedHandle)
        {
            public D3D.ID3D11Texture2D Texture { get; } = texture;

            public D3D.IDXGIKeyedMutex Mutex { get; } = mutex;

            public IntPtr SharedHandle { get; } = sharedHandle;
        }

        private static object? EnsureDispatcherQueue()
        {
            if (DispatcherQueue.GetForCurrentThread() is not null)
            {
                return null;
            }

            var options = new DispatcherQueueOptions
            {
                Size = Marshal.SizeOf<DispatcherQueueOptions>(),
                ThreadType = 2,
                ApartmentType = 2,
            };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
            return controller;
        }

        private static void ReleaseComObject(object? value)
        {
            if (value is not null && Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
            }
        }

        [DllImport("CoreMessaging.dll", PreserveSig = true)]
        private static extern int CreateDispatcherQueueController(
            DispatcherQueueOptions options,
            [MarshalAs(UnmanagedType.IUnknown)] out object dispatcherQueueController);

        [StructLayout(LayoutKind.Sequential)]
        private struct DispatcherQueueOptions
        {
            public int Size;
            public int ThreadType;
            public int ApartmentType;
        }

    }

    private static class D3D
    {
        public const string ID3D11Texture2DGuid = "6f15aaf2-d208-4e89-9ab4-489535d34f9c";
        public const string IDXGIResourceGuid = "035f3ab4-482e-4e50-b41f-8a7f8bd8960b";
        public const string IDXGIKeyedMutexGuid = "9d8e1289-d7b3-465f-8126-250e349af85d";
        public const string D3D11TextureGlobalSharedHandleType = "D3D11TextureGlobalSharedHandle";
        public const int D3D11_SDK_VERSION = 7;
        public const int D3D11_CPU_ACCESS_READ = 0x20000;
        public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);

        public enum Format
        {
            B8G8R8A8_UNorm = 87,
        }

        public enum D3D11_USAGE
        {
            Default = 0,
            Immutable = 1,
            Dynamic = 2,
            Staging = 3,
        }

        [Flags]
        public enum D3D11_BIND_FLAG
        {
            ShaderResource = 8,
            RenderTarget = 0x20,
        }

        [Flags]
        public enum OptionFlags
        {
            None = 0,
            Shared = 2,
            SharedKeyedMutex = 0x100,
        }

        public enum D3D11_MAP
        {
            Read = 1,
        }

        [Flags]
        public enum CreateDeviceFlags : uint
        {
            BgraSupport = 0x20,
            VideoSupport = 0x800,
        }

        public enum DriverType
        {
            Hardware = 1,
        }

        public enum FeatureLevel
        {
            Level_11_0 = 45056,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DXGI_SAMPLE_DESC
        {
            public uint Count;
            public uint Quality;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Texture2DDescription
        {
            public int Width;
            public int Height;
            public int MipLevels;
            public int ArraySize;
            public Format Format;
            public DXGI_SAMPLE_DESC SampleDescription;
            public D3D11_USAGE Usage;
            public D3D11_BIND_FLAG BindFlags;
            public int CpuAccessFlags;
            public OptionFlags OptionFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MappedSubresource
        {
            public IntPtr DataPointer;
            public uint RowPitch;
            public uint DepthPitch;
        }

        [ComImport]
        [Guid("DB6F6DDB-AC77-4E88-8253-819DF9BBF140")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ID3D11Device
        {
            int CreateBuffer();
            int CreateTexture1D();

            [PreserveSig]
            int CreateTexture2D(
                [In] ref Texture2DDescription description,
                [In] IntPtr initialData,
                out ID3D11Texture2D texture);
        }

        [ComImport]
        [Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ID3D11DeviceContext
        {
            int GetDevice();
            int GetPrivateData();
            int SetPrivateData();
            int SetPrivateDataInterface();
            int VSSetConstantBuffers();
            int PSSetShaderResources();
            int PSSetShader();
            int PSSetSamplers();
            int VSSetShader();
            int DrawIndexed();
            int Draw();

            [PreserveSig]
            int Map(
                [In] ID3D11Texture2D resource,
                uint subresource,
                D3D11_MAP mapType,
                uint mapFlags,
                out MappedSubresource mappedResource);

            [PreserveSig]
            void Unmap([In] ID3D11Texture2D resource, uint subresource);

            int PSSetConstantBuffers();
            int IASetInputLayout();
            int IASetVertexBuffers();
            int IASetIndexBuffer();
            int DrawIndexedInstanced();
            int DrawInstanced();
            int GSSetConstantBuffers();
            int GSSetShader();
            int IASetPrimitiveTopology();
            int VSSetShaderResources();
            int VSSetSamplers();
            int Begin();
            int End();
            int GetData();
            int SetPredication();
            int GSSetShaderResources();
            int GSSetSamplers();
            int OMSetRenderTargets();
            int OMSetRenderTargetsAndUnorderedAccessViews();
            int OMSetBlendState();
            int OMSetDepthStencilState();
            int SOSetTargets();
            int DrawAuto();
            int DrawIndexedInstancedIndirect();
            int DrawInstancedIndirect();
            int Dispatch();
            int DispatchIndirect();
            int RSSetState();
            int RSSetViewports();
            int RSSetScissorRects();
            int CopySubresourceRegion();

            [PreserveSig]
            int CopyResource([In] ID3D11Texture2D destination, [In] ID3D11Texture2D source);
        }

        [ComImport]
        [Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ID3D11Texture2D
        {
        }

        [ComImport]
        [Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIResource
        {
            int SetPrivateData();
            int SetPrivateDataInterface();
            int GetPrivateData();
            int GetParent();
            int GetDevice();

            [PreserveSig]
            int GetSharedHandle(out IntPtr sharedHandle);
        }

        [ComImport]
        [Guid("9d8e1289-d7b3-465f-8126-250e349af85d")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIKeyedMutex
        {
            int SetPrivateData();
            int SetPrivateDataInterface();
            int GetPrivateData();
            int GetParent();
            int GetDevice();

            [PreserveSig]
            int AcquireSync(ulong key, uint milliseconds);

            [PreserveSig]
            int ReleaseSync(ulong key);
        }

        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDirect3DDxgiInterfaceAccess
        {
            [PreserveSig]
            int GetInterface([In] ref Guid iid, out IntPtr result);
        }

        public static IDirect3DDevice CreateDevice(
            out ID3D11Device device,
            out ID3D11DeviceContext deviceContext)
        {
            var flags = CreateDeviceFlags.BgraSupport | CreateDeviceFlags.VideoSupport;
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                IntPtr.Zero,
                flags,
                IntPtr.Zero,
                0,
                D3D11_SDK_VERSION,
                out device,
                out _,
                out deviceContext));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(device, out var graphicsDevice));
            try
            {
                return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
            }
            finally
            {
                MarshalInterface<IDirect3DDevice>.DisposeAbi(graphicsDevice);
            }
        }

        public static void CreateD3D11Texture(
            ID3D11Device device,
            Texture2DDescription description,
            out ID3D11Texture2D texture)
        {
            Marshal.ThrowExceptionForHR(device.CreateTexture2D(ref description, IntPtr.Zero, out texture));
        }

        public static void CopyResource(
            ID3D11DeviceContext context,
            ID3D11Texture2D destination,
            ID3D11Texture2D source)
        {
            Marshal.ThrowExceptionForHR(context.CopyResource(destination, source));
        }

        public static bool TryCopyResourceWithKeyedMutex(
            ID3D11DeviceContext context,
            IDXGIKeyedMutex mutex,
            ID3D11Texture2D destination,
            ID3D11Texture2D source,
            uint timeoutMilliseconds)
        {
            _ = destination;
            var acquireResult = mutex.AcquireSync(0, timeoutMilliseconds);
            if (acquireResult == DXGI_ERROR_WAIT_TIMEOUT)
            {
                return false;
            }

            Marshal.ThrowExceptionForHR(acquireResult);
            try
            {
                CopyResource(context, destination, source);
                return true;
            }
            finally
            {
                Marshal.ThrowExceptionForHR(mutex.ReleaseSync(1));
            }
        }

        public static IDXGIKeyedMutex GetKeyedMutex(ID3D11Texture2D texture)
        {
            return QueryInterface<IDXGIKeyedMutex>(texture, IDXGIKeyedMutexGuid);
        }

        public static IntPtr GetSharedHandle(ID3D11Texture2D texture)
        {
            var resource = QueryInterface<IDXGIResource>(texture, IDXGIResourceGuid);
            try
            {
                Marshal.ThrowExceptionForHR(resource.GetSharedHandle(out var sharedHandle));
                return sharedHandle;
            }
            finally
            {
                Marshal.ReleaseComObject(resource);
            }
        }

        private static T QueryInterface<T>(object source, string interfaceId)
        {
            var unknown = Marshal.GetIUnknownForObject(source);
            try
            {
                var iid = new Guid(interfaceId);
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var result));
                try
                {
                    return (T)Marshal.GetObjectForIUnknown(result);
                }
                finally
                {
                    Marshal.Release(result);
                }
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true, PreserveSig = true)]
        private static extern int D3D11CreateDevice(
            IntPtr adapter,
            DriverType driverType,
            IntPtr software,
            CreateDeviceFlags flags,
            IntPtr featureLevels,
            uint featureLevelsCount,
            uint sdkVersion,
            out ID3D11Device device,
            out FeatureLevel featureLevel,
            out ID3D11DeviceContext immediateContext);

        [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true, PreserveSig = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(
            ID3D11Device dxgiDevice,
            out IntPtr graphicsDevice);
    }
}
