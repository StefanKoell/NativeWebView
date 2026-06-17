using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NativeWebView.Core;

namespace NativeWebView.Sample.RenderModeComparison;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var includeGpuSurface = args.Any(static arg =>
                string.Equals(arg, "--include-gpu-surface", StringComparison.OrdinalIgnoreCase));

            var embeddedWindow = new MainWindow("Embedded", NativeWebViewRenderMode.Embedded, "embedded")
            {
                Position = new PixelPoint(20, 20),
            };
            var offscreenWindow = new MainWindow("Offscreen", NativeWebViewRenderMode.Offscreen, "offscreen")
            {
                Position = new PixelPoint(400, 20),
            };

            desktop.MainWindow = embeddedWindow;
            try
            {
                offscreenWindow.Show();
            }
            catch (Exception ex)
            {
                var path = Path.GetFullPath("artifacts/render-mode-comparison/offscreen-startup-exception.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, ex.ToString());
            }

            if (includeGpuSurface)
            {
                var gpuSurfaceWindow = new MainWindow("GpuSurface", NativeWebViewRenderMode.GpuSurface, "gpu-surface")
                {
                    Position = new PixelPoint(780, 20),
                };

                try
                {
                    gpuSurfaceWindow.Show();
                }
                catch (Exception ex)
                {
                    var path = Path.GetFullPath("artifacts/render-mode-comparison/gpu-surface-startup-exception.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, ex.ToString());
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
