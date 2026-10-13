namespace Samlgate.Browser;

public enum BrowserCaptureMode
{
    Interactive,
    Auto,
}

/// <summary>Provider-independent browser preferences, validated before launching a process.</summary>
public static class BrowserPreferences
{
    public static BrowserCaptureMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "interactive" => BrowserCaptureMode.Interactive,
        "auto" => BrowserCaptureMode.Auto,
        _ => throw new SamlgateException("browser_mode must be 'interactive' or 'auto'."),
    };

    public static string? ValidateLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var language = value.Trim();
        var parts = language.Split('-');
        if (parts[0].Length is < 2 or > 8 || !parts[0].All(char.IsAsciiLetter) ||
            parts.Skip(1).Any(p => p.Length is < 1 or > 8 || !p.All(char.IsAsciiLetterOrDigit)))
        {
            throw new SamlgateException("browser_language must be a language tag such as en-US or ko-KR.");
        }

        return language;
    }
}
