namespace Orders.E2E.Hooks;

/// <summary>
/// Fresh BrowserContext per scenario (own cookies/storage), with trace and video recording.
/// Injected by Reqnroll into hooks and steps; one instance per scenario.
/// </summary>
public class ScenarioBrowser
{
    private IBrowserContext? _context;
    private IPage? _page;

    public IPage Page => _page ?? throw new InvalidOperationException("No browser context open for this scenario.");

    // Video needs Playwright's ffmpeg for the installed package version. E2E_VIDEO=0 turns it off
    // locally where that can't be installed; CI keeps it on (trace + screenshot are always kept).
    private static string? VideoDir => Environment.GetEnvironmentVariable("E2E_VIDEO") == "0"
        ? null
        : Path.Combine(TestEnvironment.ArtifactsDir, "videos");

    /// <summary>Opens a new context, replacing any context opened earlier in this scenario.</summary>
    public async Task OpenAsync(string? storageState = null)
    {
        if (_context is not null)
        {
            await _context.Tracing.StopAsync();
            await _context.CloseAsync();
            if (_page?.Video is { } oldVideo) await oldVideo.DeleteAsync();
        }

        _context = await TestEnvironment.NewContextAsync(storageState, VideoDir);
        await _context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        _page = await _context.NewPageAsync();
    }

    /// <summary>Keeps trace + screenshot + video on failure, discards them on success. Always closes the context.</summary>
    public async Task CloseAsync(string scenarioTitle, bool failed)
    {
        if (_context is null || _page is null) return;

        var name = Path.Combine(TestEnvironment.ArtifactsDir, Sanitize(scenarioTitle));
        var video = _page.Video;
        try
        {
            if (failed)
            {
                Directory.CreateDirectory(TestEnvironment.ArtifactsDir);
                await _page.ScreenshotAsync(new() { Path = $"{name}.png", FullPage = true });
                await _context.Tracing.StopAsync(new() { Path = $"{name}.zip" });
            }
            else
            {
                await _context.Tracing.StopAsync();
            }
        }
        finally
        {
            await _context.CloseAsync();   // video is only finished after the context closes
            if (video is not null)
            {
                if (failed) await video.SaveAsAsync($"{name}.webm");
                await video.DeleteAsync();
            }
            _context = null;
            _page = null;
        }
    }

    private static string Sanitize(string title) =>
        string.Concat(title.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
