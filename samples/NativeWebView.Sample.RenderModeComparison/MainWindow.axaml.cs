using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using NativeWebView.Core;

namespace NativeWebView.Sample.RenderModeComparison;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _modeName;
    private readonly string _profileName;
    private string? _initializationException;
    private FramePixelSummary? _lastFramePixelSummary;

    public MainWindow()
        : this("Offscreen", NativeWebViewRenderMode.Offscreen, "offscreen")
    {
    }

    public MainWindow(string modeName, NativeWebViewRenderMode renderMode, string profileName)
    {
        _modeName = modeName;
        _profileName = profileName;

        InitializeComponent();

        Title = $"NativeWebView {modeName} Comparison";
        ModeTextBlock.Text = modeName;
        WebView.RenderMode = renderMode;
        ConfigureWebView();

        WebView.NavigationCompleted += OnNavigationCompleted;
        WebView.RenderFrameCaptured += OnRenderFrameCaptured;
        _stateTimer.Tick += OnStateTimerTick;
        Opened += OnOpened;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _stateTimer.Stop();
        WebView.RenderFrameCaptured -= OnRenderFrameCaptured;
        WebView.Dispose();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        await InitializeComparisonAsync();
    }

    private async Task InitializeComparisonAsync()
    {
        NativeWebViewRuntime.EnsureCurrentPlatformRegistered();

        var testPageUri = RenderModeComparisonPage.EnsureCreated();
        UrlTextBox.Text = testPageUri.AbsoluteUri;

        try
        {
            await WebView.InitializeAsync();
            StatusTextBlock.Text = $"Initialized, supports {WebView.RenderMode}={WebView.SupportsRenderMode(WebView.RenderMode).ToString(CultureInfo.InvariantCulture)}";
            WebView.Navigate(testPageUri);
            _stateTimer.Start();
        }
        catch (Exception ex)
        {
            _initializationException = ex.ToString();
            StatusTextBlock.Text = $"Initialization failed: {ex.GetType().Name}: {ex.Message}";
            _stateTimer.Start();
        }
    }

    private void ConfigureWebView()
    {
        WebView.RenderFramesPerSecond = 60;
        WebView.InstanceConfiguration.EnvironmentOptions.UserDataFolder = Path.GetFullPath($"artifacts/render-mode-comparison/{_profileName}/userdata");
        WebView.InstanceConfiguration.EnvironmentOptions.CacheFolder = Path.GetFullPath($"artifacts/render-mode-comparison/{_profileName}/cache");
        WebView.InstanceConfiguration.EnvironmentOptions.CookieDataFolder = Path.GetFullPath($"artifacts/render-mode-comparison/{_profileName}/cookies");
        WebView.InstanceConfiguration.EnvironmentOptions.SessionDataFolder = Path.GetFullPath($"artifacts/render-mode-comparison/{_profileName}/session");
        WebView.InstanceConfiguration.ControllerOptions.ProfileName = $"render-mode-{_profileName}";
        WebView.InstanceConfiguration.ControllerOptions.ScriptLocale = "en-US";
    }

    private void ReloadButtonOnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WebView.Reload();
    }

    private async void ReadStateButtonOnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await ReadAndDisplayStateAsync();
    }

    private async void OnStateTimerTick(object? sender, EventArgs e)
    {
        await ReadAndDisplayStateAsync();
        await WriteStateSnapshotAsync();
    }

    private void OnNavigationCompleted(object? sender, NativeWebViewNavigationCompletedEventArgs e)
    {
        StatusTextBlock.Text = e.IsSuccess
            ? $"{_modeName}: loaded"
            : $"{_modeName}: failed {e.Error ?? e.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
    }

    private void OnRenderFrameCaptured(object? sender, NativeWebViewRenderFrameCapturedEventArgs e)
    {
        _lastFramePixelSummary = FramePixelSummary.FromFrame(e.Frame);
    }

    private async Task ReadAndDisplayStateAsync()
    {
        StateTextBlock.Text = await ReadStateAsync();
    }

    private async Task WriteStateSnapshotAsync()
    {
        var snapshot = new
        {
            capturedAtUtc = DateTimeOffset.UtcNow,
            mode = _modeName,
            renderMode = WebView.RenderMode.ToString(),
            initializationException = _initializationException,
            renderDiagnostics = WebView.RenderDiagnosticsMessage,
            renderStatistics = WebView.GetRenderStatisticsSnapshot(),
            framePixels = _lastFramePixelSummary,
            state = await TryReadRawDocumentStateForSnapshotAsync(),
        };

        var path = Path.GetFullPath($"artifacts/render-mode-comparison/{_profileName}-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    private async Task<string> ReadStateAsync()
    {
        try
        {
            var state = await ReadRawStateAsync();
            if (state is null)
            {
                return $"{_modeName}: state unavailable";
            }

            return $"{_modeName}: active='{state.ActiveElement}', input='{state.Input}', textarea='{state.Notes}', clicks={state.Clicks}, scrollTop={state.ScrollTop}";
        }
        catch (Exception ex)
        {
            return $"{_modeName}: script failed ({ex.GetType().Name})";
        }
    }

    private async Task<ComparisonState?> ReadRawStateAsync()
    {
        var documentState = await ReadRawDocumentStateAsync();
        return documentState?.State;
    }

    private async Task<ComparisonDocumentState?> ReadRawDocumentStateAsync()
    {
        const string script = """
(() => ({
  href: location.href,
  readyState: document.readyState,
  title: document.title,
  hasComparisonState: typeof window.nativeWebViewComparisonState === 'function',
  state: typeof window.nativeWebViewComparisonState === 'function' ? window.nativeWebViewComparisonState() : null
}))()
""";
        var json = await WebView.ExecuteScriptAsync(script);
        return json is null
            ? null
            : JsonSerializer.Deserialize<ComparisonDocumentState>(json, JsonOptions);
    }

    private async Task<ComparisonDocumentState?> TryReadRawDocumentStateForSnapshotAsync()
    {
        try
        {
            return await ReadRawDocumentStateAsync();
        }
        catch (Exception ex)
        {
            _initializationException ??= ex.ToString();
            return null;
        }
    }

    private sealed class ComparisonDocumentState
    {
        public string? Href { get; set; }

        public string? ReadyState { get; set; }

        public string? Title { get; set; }

        public bool HasComparisonState { get; set; }

        public ComparisonState? State { get; set; }
    }

    private sealed class ComparisonState
    {
        public string? Input { get; set; }

        public string? Notes { get; set; }

        public int Clicks { get; set; }

        public double ScrollTop { get; set; }

        public string? ActiveElement { get; set; }

        public Dictionary<string, ElementRect>? Rects { get; set; }

        public PointerSnapshot? LastPointer { get; set; }
    }

    private sealed class PointerSnapshot
    {
        public string? Type { get; set; }

        public string? Target { get; set; }

        public double ClientX { get; set; }

        public double ClientY { get; set; }
    }

    private sealed class FramePixelSummary
    {
        public long FrameId { get; set; }

        public int PixelWidth { get; set; }

        public int PixelHeight { get; set; }

        public bool IsSynthetic { get; set; }

        public string? Origin { get; set; }

        public double AverageLuminance { get; set; }

        public double NonBlackRatio { get; set; }

        public static FramePixelSummary FromFrame(NativeWebViewRenderFrame frame)
        {
            var sampleStep = Math.Max(1, frame.PixelWidth * frame.PixelHeight / 4096);
            long luminanceTotal = 0;
            long sampled = 0;
            long nonBlack = 0;

            for (var pixel = 0; pixel < frame.PixelWidth * frame.PixelHeight; pixel += sampleStep)
            {
                var row = pixel / frame.PixelWidth;
                var column = pixel % frame.PixelWidth;
                var offset = row * frame.BytesPerRow + column * 4;
                if (offset + 2 >= frame.PixelData.Length)
                {
                    continue;
                }

                var blue = frame.PixelData[offset];
                var green = frame.PixelData[offset + 1];
                var red = frame.PixelData[offset + 2];
                var luminance = (red * 299 + green * 587 + blue * 114) / 1000;
                luminanceTotal += luminance;
                sampled++;
                if (red > 8 || green > 8 || blue > 8)
                {
                    nonBlack++;
                }
            }

            return new FramePixelSummary
            {
                FrameId = frame.FrameId,
                PixelWidth = frame.PixelWidth,
                PixelHeight = frame.PixelHeight,
                IsSynthetic = frame.IsSynthetic,
                Origin = frame.Origin.ToString(),
                AverageLuminance = sampled == 0 ? 0 : (double)luminanceTotal / sampled,
                NonBlackRatio = sampled == 0 ? 0 : (double)nonBlack / sampled,
            };
        }
    }

    private sealed class ElementRect
    {
        public double Left { get; set; }

        public double Top { get; set; }

        public double Right { get; set; }

        public double Bottom { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }
    }
}

internal static class RenderModeComparisonPage
{
    public static Uri EnsureCreated()
    {
        var directory = Path.GetFullPath("artifacts/render-mode-comparison/page");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "index.html");
        File.WriteAllText(path, CreateHtml(), Encoding.UTF8);
        return new Uri(path);
    }

    private static string CreateHtml()
    {
        return """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>NativeWebView Render Mode Test</title>
  <style>
    :root {
      color-scheme: light;
      font-family: "Segoe UI", system-ui, sans-serif;
      background: #ffffff;
      color: #1f2328;
    }
    body {
      margin: 0;
      min-height: 100vh;
      background: #ffffff;
    }
    header {
      padding: 16px 18px;
      border-bottom: 1px solid #d0d7de;
      background: #f6f8fa;
    }
    h1 {
      margin: 0;
      font-size: 22px;
      font-weight: 650;
    }
    main {
      padding: 18px;
      display: grid;
      gap: 16px;
      align-content: start;
    }
    label {
      display: grid;
      gap: 6px;
      font-size: 13px;
      font-weight: 600;
    }
    input, textarea {
      font: inherit;
      padding: 10px 12px;
      border: 1px solid #8c959f;
      border-radius: 6px;
    }
    textarea {
      min-height: 72px;
      resize: vertical;
    }
    button {
      justify-self: start;
      min-width: 130px;
      min-height: 38px;
      border: 1px solid #1f6feb;
      border-radius: 6px;
      background: #0969da;
      color: #ffffff;
      font: inherit;
      font-weight: 650;
    }
    .status {
      padding: 12px;
      border: 1px solid #d0d7de;
      border-radius: 6px;
      background: #f6f8fa;
    }
    .scrollbox {
      height: 170px;
      overflow: auto;
      border: 1px solid #d0d7de;
      border-radius: 6px;
      background: linear-gradient(#ffffff, #f6f8fa);
    }
    .scrollitem {
      padding: 11px 12px;
      border-bottom: 1px solid #d8dee4;
    }
  </style>
</head>
<body>
  <header>
    <h1>NativeWebView render mode behavior test</h1>
  </header>
  <main>
    <label>
      Text input
      <input id="input" value="initial text" autocomplete="off">
    </label>
    <label>
      Notes
      <textarea id="notes">initial notes</textarea>
    </label>
    <button id="clicker" type="button">Click me</button>
    <div class="status" id="status">Clicks: 0</div>
    <div class="scrollbox" id="scrollbox" tabindex="0">
      <div class="scrollitem">Scroll row 01</div>
      <div class="scrollitem">Scroll row 02</div>
      <div class="scrollitem">Scroll row 03</div>
      <div class="scrollitem">Scroll row 04</div>
      <div class="scrollitem">Scroll row 05</div>
      <div class="scrollitem">Scroll row 06</div>
      <div class="scrollitem">Scroll row 07</div>
      <div class="scrollitem">Scroll row 08</div>
      <div class="scrollitem">Scroll row 09</div>
      <div class="scrollitem">Scroll row 10</div>
      <div class="scrollitem">Scroll row 11</div>
      <div class="scrollitem">Scroll row 12</div>
    </div>
  </main>
  <script>
    let clicks = 0;
    const status = document.getElementById('status');
    let lastPointer = null;
    document.getElementById('clicker').addEventListener('click', () => {
      clicks += 1;
      status.textContent = `Clicks: ${clicks}`;
    });

    for (const eventType of ['pointerdown', 'pointerup', 'click']) {
      document.addEventListener(eventType, event => {
        lastPointer = {
          type: event.type,
          target: event.target?.id || event.target?.tagName || null,
          clientX: event.clientX,
          clientY: event.clientY
        };
      }, true);
    }
    const rectFor = id => {
      const rect = document.getElementById(id).getBoundingClientRect();
      return {
        left: rect.left,
        top: rect.top,
        right: rect.right,
        bottom: rect.bottom,
        width: rect.width,
        height: rect.height
      };
    };

    window.nativeWebViewComparisonState = () => ({
      input: document.getElementById('input').value,
      notes: document.getElementById('notes').value,
      clicks,
      scrollTop: document.getElementById('scrollbox').scrollTop,
      activeElement: document.activeElement?.id || document.activeElement?.tagName || null,
      lastPointer,
      rects: {
        input: rectFor('input'),
        notes: rectFor('notes'),
        clicker: rectFor('clicker'),
        scrollbox: rectFor('scrollbox')
      }
    });
  </script>
</body>
</html>
""";
    }
}
