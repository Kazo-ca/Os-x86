using Microsoft.Playwright;

namespace OsX86.Tests.Guest;

/// <summary>Launches the headless browser of the <c>guest</c> tests, or skips the calling test when none can be.</summary>
internal static class GuestBrowser
{
    /// <summary>
    /// The Chromium of Playwright, then the installed Chrome, then Edge; or the one named by the
    /// <c>OSX86_BROWSER_CHANNEL</c> environment variable (<c>chromium</c>, <c>chrome</c>, <c>msedge</c>).
    /// </summary>
    public static async Task<IBrowser> LaunchAsync(IPlaywright playwright)
    {
        string? configured = Environment.GetEnvironmentVariable("OSX86_BROWSER_CHANNEL");
        string?[] channels = configured is null ? [null, "chrome", "msedge"] : [configured == "chromium" ? null : configured];
        var failures = new List<string>();
        foreach (string? channel in channels)
        {
            try
            {
                return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Channel = channel });
            }
            catch (PlaywrightException exception)
            {
                failures.Add($"{channel ?? "chromium"}: {exception.Message.Split('\n')[0]}");
            }
        }

        Skip.If(true, "No Chromium-based browser could be launched (install one with `pwsh bin/Release/net10.0/playwright.ps1 install chromium`, or set OSX86_BROWSER_CHANNEL): " + string.Join("; ", failures));
        throw new InvalidOperationException("unreachable");
    }
}
