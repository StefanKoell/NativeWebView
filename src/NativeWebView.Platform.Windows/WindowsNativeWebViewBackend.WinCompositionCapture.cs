using System.Numerics;
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
        private readonly object _gate = new();
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
        private D3D.ID3D11Texture2D? _latestGpuTexture;
        private int _latestGpuTextureWidth;
        private int _latestGpuTextureHeight;
        private bool _gpuFrameCaptureEnabled;
        private bool _gpuFrameOnlyRenderingEnabled;
        private bool _disposed;

        public WinCompositionCaptureHost()
        {
            _dispatcherQueueController = EnsureDispatcherQueue();
            _compositor = new Compositor();
            WebViewVisual = _compositor.CreateContainerVisual();
            _winRtDevice = D3D.CreateDevice(out _d3d11Device, out _d3d11Context);
        }

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
                    ReleaseComObject(_latestGpuTexture);
                    _latestGpuTexture = null;
                    _latestGpuTextureWidth = 0;
                    _latestGpuTextureHeight = 0;
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

        public async Task<NativeWebViewRenderFrame?> CaptureFrameAsync(
            NativeWebViewRenderMode renderMode,
            NativeWebViewRenderFrameRequest request,
            long frameId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCaptureSession(request.PixelWidth, request.PixelHeight);

            if (TryCaptureLatestAvailableFrame(renderMode, frameId, out var availableFrame))
            {
                return availableFrame;
            }

            lock (_gate)
            {
                if (_latestFrame is not null)
                {
                    return _latestFrame;
                }
            }

            for (var attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(16, cancellationToken).ConfigureAwait(true);
                if (TryCaptureLatestAvailableFrame(renderMode, frameId, out availableFrame))
                {
                    return availableFrame;
                }
            }

            lock (_gate)
            {
                return _latestFrame;
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
                    3,
                    _currentSize);
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
                    3,
                    _currentSize);
            }
        }

        private bool TryCaptureLatestAvailableFrame(
            NativeWebViewRenderMode renderMode,
            long frameId,
            out NativeWebViewRenderFrame? renderedFrame)
        {
            renderedFrame = null;
            if (_framePool is null)
            {
                return false;
            }

            try
            {
                while (true)
                {
                    using var frame = _framePool.TryGetNextFrame();
                    if (frame is null)
                    {
                        return renderedFrame is not null;
                    }

                    var useGpuOnly = false;
                    NativeWebViewGpuFrame? gpuFrame = null;
                    lock (_gate)
                    {
                        if (_gpuFrameCaptureEnabled)
                        {
                            gpuFrame = CopyFrameToGpu(frame, renderMode, frameId);
                            useGpuOnly = _gpuFrameOnlyRenderingEnabled && _latestFrame is not null;
                        }
                    }

                    if (!useGpuOnly)
                    {
                        renderedFrame = CopyFrameToCpu(frame, renderMode, frameId);
                    }

                    lock (_gate)
                    {
                        if (renderedFrame is not null)
                        {
                            _latestFrame = renderedFrame;
                        }
                        else
                        {
                            renderedFrame = _latestFrame;
                        }

                        if (gpuFrame is not null)
                        {
                            _latestGpuFrame = gpuFrame;
                        }
                    }
                }
            }
            catch
            {
                return renderedFrame is not null;
            }
        }

        private NativeWebViewGpuFrame CopyFrameToGpu(
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
                if (_latestGpuTexture is null ||
                    _latestGpuTextureWidth != width ||
                    _latestGpuTextureHeight != height)
                {
                    ReleaseComObject(_latestGpuTexture);
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

                    D3D.CreateD3D11Texture(_d3d11Device, desc, out _latestGpuTexture);
                    _latestGpuTextureWidth = width;
                    _latestGpuTextureHeight = height;
                }

                D3D.CopyResourceWithKeyedMutex(_d3d11Context, _latestGpuTexture, sourceTexture);
                var sharedHandle = D3D.GetSharedHandle(_latestGpuTexture);
                return new NativeWebViewGpuFrame(
                    width,
                    height,
                    NativeWebViewGpuFrameBackend.D3D11Texture2D,
                    _latestGpuTexture,
                    frameId,
                    DateTimeOffset.UtcNow,
                    renderMode,
                    sharedHandle,
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
            _framePool?.Dispose();
            _captureItem = null;
            _latestGpuFrame = null;
            ReleaseComObject(_latestGpuTexture);
            _latestGpuTexture = null;
            _latestGpuTextureWidth = 0;
            _latestGpuTextureHeight = 0;
            WebViewVisual.Dispose();
            _compositor.Dispose();
            _winRtDevice.Dispose();
            ReleaseComObject(_d3d11Context);
            ReleaseComObject(_d3d11Device);
            ReleaseComObject(_dispatcherQueueController);
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

        public static void CopyResourceWithKeyedMutex(
            ID3D11DeviceContext context,
            ID3D11Texture2D destination,
            ID3D11Texture2D source)
        {
            var mutex = QueryInterface<IDXGIKeyedMutex>(destination, IDXGIKeyedMutexGuid);
            try
            {
                Marshal.ThrowExceptionForHR(mutex.AcquireSync(0, 100));
                try
                {
                    CopyResource(context, destination, source);
                }
                finally
                {
                    Marshal.ThrowExceptionForHR(mutex.ReleaseSync(1));
                }
            }
            finally
            {
                Marshal.ReleaseComObject(mutex);
            }
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
