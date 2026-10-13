namespace Samlgate.Browser;

/// <summary>A Chromium-based browser that speaks the Chrome DevTools Protocol.</summary>
public sealed record BrowserInfo(string Kind, string ExecutablePath)
{
    public string DisplayName => Kind switch
    {
        "edge" => "Microsoft Edge",
        "chrome" => "Google Chrome",
        "brave" => "Brave",
        "chromium" => "Chromium",
        "whale" => "NAVER Whale",
        _ => Path.GetFileNameWithoutExtension(ExecutablePath),
    };
}

/// <summary>
/// Finds an installed Chromium-based browser (Edge, Chrome, Brave, Chromium, Whale).
/// Safari and Firefox do not support the DevTools Protocol and are not supported yet.
/// </summary>
public static class BrowserLocator
{
    public static readonly string[] Kinds = ["edge", "chrome", "brave", "chromium", "whale"];

    public static BrowserInfo Locate(string? browserType, string? executablePath)
    {
        var kind = NormalizeKind(browserType);

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var path = AppPaths.ExpandHome(executablePath);
            return File.Exists(path)
                ? new BrowserInfo(kind ?? "custom", path)
                : throw new SamlgateException($"Browser executable not found: {path}");
        }

        var order = kind is not null ? [kind] : DefaultOrder();
        foreach (var candidateKind in order)
        {
            if (Candidates(candidateKind).FirstOrDefault(File.Exists) is { } found)
            {
                return new BrowserInfo(candidateKind, found);
            }
        }

        throw new SamlgateException(kind is not null
            ? $"{kind} was not found. Install it, or set browser_executable_path / --browser-path."
            : "No Chromium-based browser (Edge, Chrome, Brave, Chromium, Whale) was found. Install one, or set --browser-path.");
    }

    /// <summary>Accepts samlgate names plus saml2aws' browser_type values (msedge, chrome, chromium).</summary>
    internal static string? NormalizeKind(string? browserType)
    {
        if (string.IsNullOrWhiteSpace(browserType))
        {
            return null;
        }

        return browserType.Trim().ToLowerInvariant() switch
        {
            "edge" or "msedge" or "microsoft-edge" => "edge",
            "chrome" or "google-chrome" => "chrome",
            "brave" => "brave",
            "chromium" => "chromium",
            "whale" or "naver-whale" => "whale",
            "firefox" or "webkit" or "safari" => throw new SamlgateException($"browser_type '{browserType}' is not supported yet — samlgate currently needs a Chromium-based browser (edge, chrome, brave, chromium, whale)."),
            _ => throw new SamlgateException($"Unknown browser_type '{browserType}'. Use one of: {string.Join(", ", Kinds)}."),
        };
    }

    private static string[] DefaultOrder()
    {
        if (OperatingSystem.IsWindows())
        {
            return ["edge", "chrome", "brave", "chromium", "whale"]; // Edge ships with Windows
        }

        return OperatingSystem.IsMacOS()
            ? ["chrome", "edge", "brave", "chromium", "whale"]
            : ["chrome", "chromium", "edge", "brave", "whale"];
    }

    private static IEnumerable<string> Candidates(string kind)
    {
        if (OperatingSystem.IsWindows())
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            }.Where(r => r.Length > 0);

            var relative = kind switch
            {
                "edge" => @"Microsoft\Edge\Application\msedge.exe",
                "chrome" => @"Google\Chrome\Application\chrome.exe",
                "brave" => @"BraveSoftware\Brave-Browser\Application\brave.exe",
                "whale" => @"Naver\Naver Whale\Application\whale.exe",
                _ => @"Chromium\Application\chrome.exe",
            };
            return roots.Select(root => Path.Combine(root, relative));
        }

        if (OperatingSystem.IsMacOS())
        {
            var app = kind switch
            {
                "edge" => "Microsoft Edge",
                "chrome" => "Google Chrome",
                "brave" => "Brave Browser",
                "whale" => "Whale",
                _ => "Chromium",
            };
            return new[] { "/Applications", Path.Combine(AppPaths.Home, "Applications") }
                .Select(root => Path.Combine(root, $"{app}.app", "Contents", "MacOS", app));
        }

        var names = kind switch
        {
            "edge" => new[] { "microsoft-edge", "microsoft-edge-stable" },
            "chrome" => ["google-chrome", "google-chrome-stable"],
            "brave" => ["brave-browser", "brave"],
            "whale" => ["naver-whale", "naver-whale-stable"],
            _ => ["chromium", "chromium-browser"],
        };
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return names.SelectMany(name => pathDirs.Select(dir => Path.Combine(dir, name)));
    }
}
