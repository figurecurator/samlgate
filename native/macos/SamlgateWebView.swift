import AppKit
import WebKit

// Private pipe protocol: one settings JSON line in; only the captured assertion out.
// Do not log URLs, page content, cookies, assertions, or underlying WebKit errors.
struct CaptureSettings: Decodable {
    let url: URL
    let profileId: UUID
    let targetUrl: URL?
}

// HTTP is restricted to loopback synthetic fixtures; real IdP navigation starts with HTTPS.
func isAllowedStartURL(_ url: URL) -> Bool {
    if ["https", "file"].contains(url.scheme?.lowercased() ?? "") { return true }
    return url.scheme?.lowercased() == "http" &&
        ["127.0.0.1", "localhost", "[::1]"].contains(url.host?.lowercased() ?? "")
}

func isACS(_ url: URL, custom: URL?) -> Bool {
    guard let host = url.host?.lowercased() else { return false }
    if let custom, host == custom.host?.lowercased(), url.path.lowercased().hasPrefix(custom.path.lowercased()) {
        return true
    }
    guard url.scheme?.lowercased() == "https", url.path.lowercased().hasPrefix("/saml") else { return false }
    return ["signin.aws.amazon.com", "signin.amazonaws.cn", "signin.amazonaws-us-gov.com"].contains {
        host == $0 || host.hasSuffix("." + $0)
    }
}

@MainActor
final class CaptureController: NSObject, NSWindowDelegate, WKNavigationDelegate, WKUIDelegate {
    private let settings: CaptureSettings
    private let configuration: WKWebViewConfiguration
    private var windows: [NSWindow] = []
    private var capturing = false
    private var finished = false
    var exitCode: Int32 = 1

    init(settings: CaptureSettings) {
        self.settings = settings
        configuration = WKWebViewConfiguration()
        // Never share the default WebKit/Safari data store with the user's everyday browsing.
        configuration.websiteDataStore = WKWebsiteDataStore(forIdentifier: settings.profileId)
        super.init()
    }

    func start() {
        let view = makeWindow(configuration: configuration)
        if settings.url.isFileURL {
            // Local pages are used only by synthetic capture tests.
            view.loadFileURL(settings.url, allowingReadAccessTo: settings.url.deletingLastPathComponent())
        } else {
            view.load(URLRequest(url: settings.url))
        }
        NSApp.activate(ignoringOtherApps: true)
    }

    private func makeWindow(configuration: WKWebViewConfiguration) -> WKWebView {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 760),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable],
                              backing: .buffered, defer: false)
        window.title = "samlgate — Sign in"
        window.isReleasedWhenClosed = false
        window.delegate = self
        let view = WKWebView(frame: window.contentLayoutRect, configuration: configuration)
        view.navigationDelegate = self
        view.uiDelegate = self
        window.contentView = view
        windows.append(window)
        window.center()
        window.makeKeyAndOrderFront(nil)
        return view
    }

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = action.request.url, isACS(url, custom: settings.targetUrl),
              action.request.httpMethod == "POST" else {
            decisionHandler(.allow)
            return
        }

        // Cancel before reading: even an unreadable assertion must never reach the ACS.
        decisionHandler(.cancel)
        guard !capturing, !finished else { return }
        capturing = true
        // Read only the submitting source frame and its matching form. A new-window POST
        // may have no target frame; the original source document still owns the assertion.
        webView.callAsyncJavaScript("""
            const form = Array.from(document.forms).find(f => f.action === acs);
            const field = form?.querySelector('[name="SAMLResponse"]');
            return field?.value || '';
            """, arguments: ["acs": url.absoluteString], in: action.sourceFrame, in: .page) { [weak self] result in
            guard let self else { return }
            switch result {
            case .success(let value):
                guard let assertion = value as? String, !assertion.isEmpty,
                      assertion.utf8.count <= 16 * 1024 * 1024,
                      Data(base64Encoded: assertion, options: .ignoreUnknownCharacters) != nil else {
                    self.finish(code: 4)
                    return
                }
                self.finish(code: 0, assertion: assertion)
            case .failure:
                self.finish(code: 4)
            }
        }
    }

    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for action: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        guard !finished else { return nil }
        // The requested configuration retains the same persistent data store and opener.
        return makeWindow(configuration: configuration)
    }

    func webViewDidClose(_ webView: WKWebView) {
        windows.first { $0.contentView === webView }?.close()
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        let failure = error as NSError
        // Cancellation is expected when intercepting an ACS POST or redirecting a popup.
        if failure.domain == NSURLErrorDomain && failure.code == NSURLErrorCancelled { return }
        if !capturing { finish(code: 5) }
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        if !capturing { finish(code: 5) }
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        finish(code: 5)
    }

    func windowWillClose(_ notification: Notification) {
        guard !finished, let window = notification.object as? NSWindow else { return }
        windows.removeAll { $0 === window }
        if windows.isEmpty { finish(code: 2) }
    }

    func finish(code: Int32, assertion: String? = nil) {
        guard !finished else { return }
        finished = true
        exitCode = code
        if let assertion {
            FileHandle.standardOutput.write(Data(assertion.utf8))
        }
        for window in windows { window.close() }
        windows.removeAll()
        // Exit through the AppKit run loop rather than forcibly killing WebKit subprocesses.
        NSApp.stop(nil)
        if let event = NSEvent.otherEvent(with: .applicationDefined, location: .zero,
                                         modifierFlags: [], timestamp: 0, windowNumber: 0,
                                         context: nil, subtype: 0, data1: 0, data2: 0) {
            NSApp.postEvent(event, atStart: true)
        }
    }
}

@main
struct Main {
    @MainActor
    static func main() {
        if CommandLine.arguments.count == 3, CommandLine.arguments[1] == "--remove-profile",
           let identifier = UUID(uuidString: CommandLine.arguments[2]) {
            // Explicit cleanup of one named store; never enumerate or touch other profiles.
            let application = NSApplication.shared
            // Initialize WebKit's main run loop before calling its standalone store API.
            let configuration = WKWebViewConfiguration()
            configuration.websiteDataStore = .nonPersistent()
            let bootstrap = WKWebView(frame: .zero, configuration: configuration)
            var code: Int32 = 1
            Task { @MainActor in
                do {
                    try await WKWebsiteDataStore.remove(forIdentifier: identifier)
                    code = 0
                } catch { }
                application.stop(nil)
                if let event = NSEvent.otherEvent(with: .applicationDefined, location: .zero,
                                                 modifierFlags: [], timestamp: 0, windowNumber: 0,
                                                 context: nil, subtype: 0, data1: 0, data2: 0) {
                    application.postEvent(event, atStart: true)
                }
            }
            application.run()
            withExtendedLifetime(bootstrap) { }
            exit(code)
        }
        guard let line = readLine(), let data = line.data(using: .utf8),
              let settings = try? JSONDecoder().decode(CaptureSettings.self, from: data),
              isAllowedStartURL(settings.url) else {
            exit(1)
        }
        let application = NSApplication.shared
        application.setActivationPolicy(.regular)
        let controller = CaptureController(settings: settings)
        signal(SIGTERM, SIG_IGN)
        signal(SIGINT, SIG_IGN)
        let termination = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        termination.setEventHandler { MainActor.assumeIsolated { controller.finish(code: 2) } }
        termination.resume()
        let interrupt = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
        interrupt.setEventHandler { MainActor.assumeIsolated { controller.finish(code: 2) } }
        interrupt.resume()
        DispatchQueue.main.async { controller.start() }
        application.run()
        exit(controller.exitCode)
    }
}
