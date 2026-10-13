using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Samlgate.Aws;

namespace Samlgate.Browser;

public sealed record CaptureOptions
{
    public required Uri IdpUrl { get; init; }
    public required BrowserInfo Browser { get; init; }

    /// <summary>Dedicated browser profile, so IdP cookies persist between logins without touching the user's profile.</summary>
    public required string ProfileDirectory { get; init; }

    public string? TargetUrl { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Tests or an existing-session refresh. User authentication requires an interactive fallback.</summary>
    public bool Headless { get; init; }
    public string? Language { get; init; }
}

/// <summary>
/// Runs the IdP sign-in in a real Chromium browser and captures the SAMLResponse.
///
/// SAML has no localhost callback: the IdP makes the browser POST the assertion to the fixed AWS ACS URL
/// (signin.aws.amazon.com/saml). samlgate launches the browser with remote debugging, intercepts exactly that
/// request with the DevTools Fetch domain, reads the form body, and answers it with a local "done" page
/// instead of letting it reach AWS.
/// </summary>
public static class BrowserSamlCapture
{
    private const string PortFileName = "DevToolsActivePort";

    public static async Task<string> CaptureAsync(
        CaptureOptions options, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.ProfileDirectory);
        var portFile = Path.Combine(options.ProfileDirectory, PortFileName);
        File.Delete(portFile);

        using var process = StartBrowser(options);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);

            try
            {
                return await CaptureWithBrowserAsync(process, portFile, options, onStatus, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SamlgateException($"Timed out after {options.Timeout.TotalSeconds:0} seconds waiting for the sign-in to finish.");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task<string> CaptureWithBrowserAsync(
        Process process, string portFile, CaptureOptions options, Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        var webSocketUrl = await WaitForDevToolsAsync(process, portFile, cancellationToken);
        await using var cdp = await CdpConnection.ConnectAsync(webSocketUrl, cancellationToken);
        try
        {
            return await RunAsync(cdp, options, onStatus, cancellationToken);
        }
        catch (CdpClosedException)
        {
            // The window was closed while a DevTools command was in flight
            throw BrowserClosed();
        }
        finally
        {
            await CloseGracefullyAsync(cdp, process);
        }
    }

    private static Process StartBrowser(CaptureOptions options)
    {
        var language = BrowserPreferences.ValidateLanguage(options.Language);
        var startInfo = new ProcessStartInfo(options.Browser.ExecutablePath)
        {
            UseShellExecute = false,
            // Chromium logs to stderr; keep it out of the terminal (and out of stdout for credential_process)
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in new[]
                 {
                     "--remote-debugging-port=0",
                     $"--user-data-dir={options.ProfileDirectory}",
                     "--no-first-run",
                     "--no-default-browser-check",
                     "--hide-crash-restore-bubble",
                     "--window-size=560,760",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (options.Headless)
        {
            startInfo.ArgumentList.Add("--headless=new");
        }

        if (language is not null)
        {
            startInfo.ArgumentList.Add($"--accept-lang={language}");
            startInfo.ArgumentList.Add($"--lang={language}");
        }

        // Start on a blank page and navigate only after interception is in place, otherwise an IdP
        // session that is still valid could reach the ACS before samlgate is listening
        startInfo.ArgumentList.Add("about:blank");

        var process = Process.Start(startInfo) ?? throw new SamlgateException($"Could not start {options.Browser.ExecutablePath}.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Chromium writes the chosen port and browser WebSocket path to DevToolsActivePort in the profile.</summary>
    private static async Task<Uri> WaitForDevToolsAsync(Process process, string portFile, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(portFile))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(portFile, cancellationToken);
                    if (lines.Length >= 2 && int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                    {
                        return new Uri($"ws://127.0.0.1:{port}{lines[1].Trim()}");
                    }
                }
                catch (IOException)
                {
                    // Still being written
                }
            }

            if (process.HasExited)
            {
                // With the same --user-data-dir already open, Chromium hands off to that instance and exits
                throw new SamlgateException("The browser exited right away. If a samlgate browser window is still open, close it and try again.");
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new SamlgateException("The browser did not open its DevTools port.");
    }

    private static async Task<string> RunAsync(
        CdpConnection cdp, CaptureOptions options, Action<string>? onStatus, CancellationToken cancellationToken)
    {
        var patterns = new JsonArray();
        foreach (var pattern in AcsEndpoints.FetchUrlPatterns(options.TargetUrl))
        {
            patterns.Add((JsonNode)new JsonObject { ["urlPattern"] = pattern, ["requestStage"] = "Request" });
        }

        var fetchParams = new JsonObject { ["patterns"] = patterns };
        var openPages = new HashSet<string>();
        var attachedTargets = new HashSet<string>();
        string? firstSession = null;

        async Task<string> AttachAsync(string targetId, string? sessionId, bool waitingForDebugger)
        {
            sessionId ??= (await cdp.SendAsync("Target.attachToTarget",
                new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, cancellationToken: cancellationToken))
                ["sessionId"]!.GetValue<string>();

            await cdp.SendAsync("Fetch.enable", fetchParams.DeepClone().AsObject(), sessionId, cancellationToken);
            if (waitingForDebugger)
            {
                await cdp.SendAsync("Runtime.runIfWaitingForDebugger", sessionId: sessionId, cancellationToken: cancellationToken);
            }

            return sessionId;
        }

        await cdp.SendAsync("Target.setDiscoverTargets", new JsonObject { ["discover"] = true },
            cancellationToken: cancellationToken);
        // Pages opened later (popups, new tabs) are paused until Fetch is enabled on them
        await cdp.SendAsync("Target.setAutoAttach", new JsonObject
        {
            ["autoAttach"] = true,
            ["waitForDebuggerOnStart"] = true,
            ["flatten"] = true,
        }, cancellationToken: cancellationToken);

        var targets = await cdp.SendAsync("Target.getTargets", cancellationToken: cancellationToken);
        foreach (var info in targets["targetInfos"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (info["type"]?.GetValue<string>() != "page")
            {
                continue;
            }

            var targetId = info["targetId"]!.GetValue<string>();
            openPages.Add(targetId);
            if (attachedTargets.Add(targetId))
            {
                firstSession ??= await AttachAsync(targetId, null, waitingForDebugger: false);
            }
        }

        if (firstSession is null)
        {
            var created = await cdp.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" },
                cancellationToken: cancellationToken);
            var targetId = created["targetId"]!.GetValue<string>();
            openPages.Add(targetId);
            attachedTargets.Add(targetId);
            firstSession = await AttachAsync(targetId, null, waitingForDebugger: false);
        }

        await cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = options.IdpUrl.AbsoluteUri }, firstSession,
            cancellationToken);
        onStatus?.Invoke(options.Headless
            ? "Waiting for the existing browser session..."
            : "Complete the sign-in in the browser window.");

        while (true)
        {
            CdpEvent cdpEvent;
            try
            {
                cdpEvent = await cdp.Events.ReadAsync(cancellationToken);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                throw BrowserClosed();
            }

            switch (cdpEvent.Method)
            {
                case "Target.attachedToTarget":
                    {
                        var info = cdpEvent.Params["targetInfo"]!.AsObject();
                        var targetId = info["targetId"]!.GetValue<string>();
                        var sessionId = cdpEvent.Params["sessionId"]!.GetValue<string>();
                        var waiting = cdpEvent.Params["waitingForDebugger"]?.GetValue<bool>() == true;
                        if (info["type"]?.GetValue<string>() == "page" && attachedTargets.Add(targetId))
                        {
                            openPages.Add(targetId);
                            await AttachAsync(targetId, sessionId, waiting);
                        }
                        else if (waiting)
                        {
                            await cdp.SendAsync("Runtime.runIfWaitingForDebugger", sessionId: sessionId,
                                cancellationToken: cancellationToken);
                        }

                        break;
                    }

                case "Target.targetCreated":
                    {
                        var info = cdpEvent.Params["targetInfo"]!.AsObject();
                        if (info["type"]?.GetValue<string>() == "page")
                        {
                            openPages.Add(info["targetId"]!.GetValue<string>());
                        }

                        break;
                    }

                case "Target.targetDestroyed":
                    // On macOS the browser keeps running after its last window closes, so watch the pages
                    if (openPages.Remove(cdpEvent.Params["targetId"]!.GetValue<string>()) && openPages.Count == 0)
                    {
                        throw BrowserClosed();
                    }

                    break;

                case "Fetch.requestPaused":
                    if (await HandlePausedRequestAsync(cdp, cdpEvent, options.TargetUrl, cancellationToken) is { } saml)
                    {
                        return saml;
                    }

                    break;
            }
        }
    }

    /// <summary>Returns the SAMLResponse if this was the ACS POST; otherwise lets the request continue.</summary>
    private static async Task<string?> HandlePausedRequestAsync(
        CdpConnection cdp, CdpEvent paused, string? targetUrl, CancellationToken cancellationToken)
    {
        var requestId = paused.Params["requestId"]!.GetValue<string>();
        var request = paused.Params["request"]!.AsObject();
        var url = request["url"]?.GetValue<string>();
        var method = request["method"]?.GetValue<string>();

        var isAcsPost = method == "POST" && Uri.TryCreate(url, UriKind.Absolute, out var uri) && AcsEndpoints.IsAcs(uri, targetUrl);
        var samlResponse = isAcsPost ? FormField(ReadPostData(request), "SAMLResponse") : null;

        if (samlResponse is null && !isAcsPost)
        {
            try
            {
                await cdp.SendAsync("Fetch.continueRequest", new JsonObject { ["requestId"] = requestId },
                    paused.SessionId, cancellationToken);
            }
            catch (CdpException)
            {
                // The request was already cancelled (page navigated away) — nothing to continue
            }

            return null;
        }

        // Answer the ACS POST locally even when the assertion is unreadable: it must never reach AWS
        await cdp.SendAsync("Fetch.fulfillRequest", new JsonObject
        {
            ["requestId"] = requestId,
            ["responseCode"] = 200,
            ["responseHeaders"] = new JsonArray(new JsonObject
            {
                ["name"] = "Content-Type",
                ["value"] = "text/html; charset=utf-8",
            }),
            ["body"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(ResultPage(samlResponse is not null
                ? "Signed in. You can close this window and return to the terminal."
                : "samlgate could not read the SAML response. See the terminal for details."))),
        }, paused.SessionId, cancellationToken);

        return samlResponse ?? throw new SamlgateException(
            "The IdP posted to the AWS sign-in endpoint, but the request had no readable SAMLResponse.");
    }

    /// <summary>The form body is in postData, or base64 in postDataEntries on newer Chromium versions.</summary>
    internal static string? ReadPostData(JsonObject request)
    {
        if (request["postData"]?.GetValue<string>() is { Length: > 0 } postData)
        {
            return postData;
        }

        if (request["postDataEntries"] is JsonArray entries)
        {
            var bytes = entries.OfType<JsonObject>()
                .Select(e => e["bytes"]?.GetValue<string>())
                .OfType<string>()
                .SelectMany(Convert.FromBase64String)
                .ToArray();
            return bytes.Length > 0 ? Encoding.UTF8.GetString(bytes) : null;
        }

        return null;
    }

    /// <summary>Reads one field from an application/x-www-form-urlencoded body.</summary>
    internal static string? FormField(string? body, string name)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        foreach (var pair in body.Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0 && WebUtility.UrlDecode(pair[..separator]) == name)
            {
                return WebUtility.UrlDecode(pair[(separator + 1)..]);
            }
        }

        return null;
    }

    private static string ResultPage(string text)
    {
        var title = WebUtility.HtmlEncode("samlgate");
        var message = WebUtility.HtmlEncode(text);
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>{{title}}</title>
            <style>body{font-family:system-ui,sans-serif;display:grid;place-items:center;height:90vh;color:#222}</style>
            </head><body><p>{{message}}</p></body></html>
            """;
    }

    /// <summary>Browser.close lets the browser flush cookies to disk; killing it could lose the IdP session.</summary>
    private static async Task CloseGracefullyAsync(CdpConnection cdp, Process process)
    {
        try
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await cdp.SendAsync("Browser.close", cancellationToken: closeTimeout.Token);
        }
        catch (Exception e) when (e is CdpClosedException or CdpException or OperationCanceledException
                                      or System.Net.WebSockets.WebSocketException)
        {
            // Already closing or gone
        }

        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(exitTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Killed by the caller
        }
    }

    private static SamlgateException BrowserClosed() => new("The browser was closed before the sign-in finished.");
}
