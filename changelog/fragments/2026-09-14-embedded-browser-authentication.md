[Added] Added NavigationCancellation capability reporting for Windows and embedded macOS browsers.
[Added] Added cloneable JavaScript, password-saving, general autofill and diagnostic-redaction controller options with documented platform mappings.
[Fixed] Honored synchronous cancellation in both embedded WebKit navigation-policy callbacks, including subscriber failures and superseded requests, without duplicate page-start events or canceled-request replay.
[Fixed] Preserved embedded macOS URL/history state and the active document across presenter replacement and reattachment.
[Fixed] Completed embedded macOS bidirectional web messaging with listener cleanup, exact string payloads and JSON.parse-based delivery that preserves prototype-named properties as data.
[Fixed] Applied context-menu enablement to new and retained macOS native views and reported terminal Windows/macOS browser-process failures.
[Fixed] Applied finalized JavaScript policy on Windows, Linux and embedded macOS; rejected unsupported JavaScript disabling in macOS dialogs.
[Docs] Documented browser policies, cancellation, messaging, native verification requirements and current embedded macOS download support.
[Packaging] Bumped the package version to 12.0.4.9.
