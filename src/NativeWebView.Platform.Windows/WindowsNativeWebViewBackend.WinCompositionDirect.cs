using System.Numerics;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace NativeWebView.Platform.Windows;

public sealed partial class WindowsNativeWebViewBackend
{
    private sealed class WinCompositionDirectHost : IDisposable
    {
        private readonly object? _dispatcherQueueController;
        private readonly Compositor _compositor;
        private readonly DesktopWindowTarget _target;
        private readonly ContainerVisual _rootVisual;
        private readonly IntPtr _windowHandle;
        private bool _disposed;

        public WinCompositionDirectHost(IntPtr parentWindowHandle)
        {
            var instanceHandle = Win32.GetModuleHandle(null);
            _windowHandle = Win32.CreateWindowEx(
                0,
                Win32.ChildWindowClassName,
                string.Empty,
                Win32.WindowStyles.WS_CHILD |
                Win32.WindowStyles.WS_VISIBLE |
                Win32.WindowStyles.WS_CLIPSIBLINGS |
                Win32.WindowStyles.WS_CLIPCHILDREN,
                0,
                0,
                1,
                1,
                parentWindowHandle,
                IntPtr.Zero,
                instanceHandle,
                IntPtr.Zero);
            if (_windowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create Windows direct composition child window.");
            }

            _dispatcherQueueController = EnsureDispatcherQueue();
            _compositor = new Compositor();
            _rootVisual = _compositor.CreateContainerVisual();
            _target = CreateDesktopWindowTarget(_compositor, _windowHandle);
            _target.Root = _rootVisual;
        }

        public ContainerVisual WebViewVisual => _rootVisual;

        public IntPtr WindowHandle => _windowHandle;

        public void Resize(int width, int height)
        {
            if (_disposed)
            {
                return;
            }

            _rootVisual.Size = new Vector2(Math.Max(1, width), Math.Max(1, height));
            _ = Win32.SetWindowPos(
                _windowHandle,
                IntPtr.Zero,
                0,
                0,
                Math.Max(1, width),
                Math.Max(1, height),
                Win32.SetWindowPosFlags.NoZOrder | Win32.SetWindowPosFlags.NoActivate | Win32.SetWindowPosFlags.ShowWindow);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _target.Root = null;
            }
            catch
            {
                // Best-effort disconnect from the HWND target.
            }

            _target.Dispose();
            _rootVisual.Dispose();
            _compositor.Dispose();
            if (_windowHandle != IntPtr.Zero)
            {
                Win32.DestroyWindow(_windowHandle);
            }

            ReleaseComObject(_dispatcherQueueController);
        }

        private static DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, IntPtr windowHandle)
        {
            if (!ComWrappersSupport.TryUnwrapObject(compositor, out var objectReference))
            {
                throw new InvalidOperationException("Unable to unwrap Windows.UI.Composition.Compositor.");
            }

            using var desktopInteropReference =
                objectReference.As(Guid.Parse("29E691FA-4567-4DCA-B319-D0F207EB6807"));
            var desktopInterop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(desktopInteropReference.ThisPtr);
            Marshal.ThrowExceptionForHR(desktopInterop.CreateDesktopWindowTarget(windowHandle, false, out var targetPointer));
            try
            {
                return MarshalInterface<DesktopWindowTarget>.FromAbi(targetPointer);
            }
            finally
            {
                MarshalInterface<DesktopWindowTarget>.DisposeAbi(targetPointer);
            }
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

        [ComImport]
        [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICompositorDesktopInterop
        {
            [PreserveSig]
            int CreateDesktopWindowTarget(
                IntPtr hwndTarget,
                [MarshalAs(UnmanagedType.Bool)] bool isTopmost,
                out IntPtr desktopWindowTarget);

            void EnsureOnThread(int threadId);
        }
    }
}
