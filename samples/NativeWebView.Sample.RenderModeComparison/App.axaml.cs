using Avalonia;
using Avalonia.Controls;
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
            var selfTestGpuSurface = args.Any(static arg =>
                string.Equals(arg, "--self-test-gpu-surface", StringComparison.OrdinalIgnoreCase));
            var selfTestDirectComposition = args.Any(static arg =>
                string.Equals(arg, "--self-test-direct-composition", StringComparison.OrdinalIgnoreCase));
            var selfTestOffscreen = args.Any(static arg =>
                string.Equals(arg, "--self-test-offscreen", StringComparison.OrdinalIgnoreCase));
            var selfTestEmbedded = args.Any(static arg =>
                string.Equals(arg, "--self-test-embedded", StringComparison.OrdinalIgnoreCase));
            var selfTestRoyalApps = args.Any(static arg =>
                string.Equals(arg, "--self-test-royalapps", StringComparison.OrdinalIgnoreCase));
            var selfTestRoyalAppsGpuSurface = args.Any(static arg =>
                string.Equals(arg, "--self-test-royalapps-gpu-surface", StringComparison.OrdinalIgnoreCase));
            var selfTestRoyalAppsDirectComposition = args.Any(static arg =>
                string.Equals(arg, "--self-test-royalapps-direct-composition", StringComparison.OrdinalIgnoreCase));
            var manualRoyalAppsDirectComposition = args.Any(static arg =>
                string.Equals(arg, "--manual-royalapps-direct-composition", StringComparison.OrdinalIgnoreCase));

            if (manualRoyalAppsDirectComposition)
            {
                Environment.SetEnvironmentVariable("NATIVEWEBVIEW_WINDOWS_DIRECT_COMPOSITION", "1");
                desktop.MainWindow = new MainWindow(
                    "RoyalApps DirectComposition",
                    NativeWebViewRenderMode.Offscreen,
                    "royalapps-direct-composition-manual",
                    runSelfTest: false,
                    selfTestScenario: SelfTestScenario.LiveRoyalApps,
                    initialUri: new Uri("https://royalapps.com/"))
                {
                    WindowState = WindowState.Maximized,
                };

                base.OnFrameworkInitializationCompleted();
                return;
            }

            if (selfTestRoyalApps || selfTestRoyalAppsGpuSurface || selfTestRoyalAppsDirectComposition)
            {
                if (selfTestRoyalAppsDirectComposition)
                {
                    Environment.SetEnvironmentVariable("NATIVEWEBVIEW_WINDOWS_DIRECT_COMPOSITION", "1");
                }

                var renderMode = selfTestRoyalAppsGpuSurface
                    ? NativeWebViewRenderMode.GpuSurface
                    : NativeWebViewRenderMode.Offscreen;
                var profileName = selfTestRoyalAppsDirectComposition
                    ? "royalapps-direct-composition"
                    : selfTestRoyalAppsGpuSurface
                        ? "royalapps-gpu-surface"
                        : "royalapps-offscreen";
                var modeName = selfTestRoyalAppsDirectComposition
                    ? "RoyalApps DirectComposition"
                    : selfTestRoyalAppsGpuSurface
                        ? "RoyalApps GpuSurface"
                        : "RoyalApps Offscreen/GpuSurface";
                desktop.MainWindow = new MainWindow(
                    modeName,
                    renderMode,
                    profileName,
                    runSelfTest: true,
                    selfTestScenario: SelfTestScenario.LiveRoyalApps,
                    initialUri: new Uri("https://royalapps.com/"))
                {
                    WindowState = WindowState.Maximized,
                };

                base.OnFrameworkInitializationCompleted();
                return;
            }

            if (selfTestGpuSurface || selfTestDirectComposition || selfTestOffscreen || selfTestEmbedded)
            {
                var modeName = selfTestEmbedded ? "Embedded" : selfTestOffscreen ? "Offscreen/GpuSurface" : selfTestDirectComposition ? "DirectComposition" : "GpuSurface";
                var renderMode = selfTestEmbedded
                    ? NativeWebViewRenderMode.Embedded
                    : selfTestOffscreen
                        ? NativeWebViewRenderMode.Offscreen
                    : NativeWebViewRenderMode.GpuSurface;
                var profileName = selfTestEmbedded ? "embedded" : selfTestOffscreen ? "offscreen" : selfTestDirectComposition ? "direct-composition" : "gpu-surface";
                desktop.MainWindow = new MainWindow(modeName, renderMode, profileName, runSelfTest: true)
                {
                    Position = selfTestEmbedded
                        ? new PixelPoint(20, 20)
                        : new PixelPoint(780, 20),
                };

                base.OnFrameworkInitializationCompleted();
                return;
            }

            var embeddedWindow = new MainWindow("Embedded", NativeWebViewRenderMode.Embedded, "embedded")
            {
                Position = new PixelPoint(20, 20),
            };
            var offscreenWindow = new MainWindow("Offscreen/GpuSurface", NativeWebViewRenderMode.Offscreen, "offscreen")
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
