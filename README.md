# LLM Autocorrect for Windows

A conservative Windows 11 tray application that uses Gemini to correct recently typed text and offer optional Tab-completion in ordinary Windows text fields. It is built with C#, .NET 8, WPF, UI Automation, and low-level Win32 hooks.

The safety rule is simple: when focus, caret position, application, password status, or input version is uncertain, the app does nothing.

## Implemented milestone

- One shared `WH_KEYBOARD_LL` input stream and `WH_MOUSE_LL` invalidation hook.
- In-memory typing buffer; ordinary typed text and full messages are not persisted.
- Foreground window, process, and focused UI Automation element tracking.
- Immediate hook-time version increments for stale-response rejection.
- Gemini correction provider behind `ICorrectionProvider`.
- Gemini autocomplete provider behind `IAutocompleteProvider`.
- Low-thinking Gemini requests for latency-sensitive correction and completion.
- Structured JSON responses, reusable `HttpClient`, cancellation, rate limits, and ten-second timeout.
- Protected URL, email, path, IP, number, unit, code identifier, and equipment identifier validation.
- Conservative edit-distance and length validation.
- Exact-suffix UI Automation replacement, with a unique marker on the limited injected-key fallback used by Chromium-style controls.
- Replacement first verifies and selects the exact suffix through UI Automation. Editable value controls are updated atomically; text-pattern controls receive a paste over the verified selection. Shared trailing newline markers are left untouched so an Enter correction cannot consume or duplicate a line break. If the focused control cannot expose a verifiable value/range, replacement is skipped. The previous clipboard is restored immediately and temporary text is excluded from clipboard-history processing.
- Ctrl+Alt+Z safe undo (up to 20 in-memory corrections).
- Non-activating autocomplete overlay with Tab accept, Esc dismiss, Ctrl+Right partial accept, and Alt+Down candidate cycling.
- Password-field, sensitive-topic, terminal, IDE, password-manager, and per-application exclusion gates.
- Private Mode, metadata-only diagnostics, editable technical dictionary, settings UI, tray controls, and startup registration.
- Global custom correction instructions plus a browser-address-bar spelling-only profile that preserves capitalization and punctuation.
- Privacy-conscious personal memory for explicitly accepted completions and recurring spelling corrections, with a disable switch and Clear Memory control.
- Technical dictionary terms are sent to autocomplete only when already present in the current context, so a saved term cannot steer an unrelated suggestion.
- Manual spelling correction for highlighted text or the word under a right-click, plus a `Ctrl+Alt+C` fallback.
- A ranked Excel spelling-history workbook that groups attempted misspellings under the corrected word.
- One automatic retry for an uncertain or temporarily failed correction, with an optional non-focus-stealing explanation popup.
- Deliberate provider latency setting for stale-response testing.
- Physical-key release gating prevents accepted suggestions from being injected while Tab or a modifier is still held.

## Requirements

- Windows 11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- .NET 8 Windows Desktop Runtime to run a framework-dependent build
- A Gemini API key

The app runs as the current user. It intentionally does not request administrator privileges and therefore will not interact with elevated applications or the Windows secure desktop.

## API key setup

The key is read only from the `GEMINI_API_KEY` environment variable. It is never saved in `settings.json`, source, or logs.

Recommended setup:

1. Open **Start → Edit environment variables for your account**.
2. Add a user variable named `GEMINI_API_KEY` with the key as its value.
3. Sign out and back in, or restart the terminal/Explorer process that launches the app.

For a temporary PowerShell session:

```powershell
$env:GEMINI_API_KEY = Read-Host 'Gemini API key'
```

Then start the application from that same session. Avoid placing a real key in scripts, `.env` files, command history, screenshots, or issue reports. If a key has been pasted into chat or another shared location, rotate it in Google AI Studio and configure the replacement.

The default model is `gemini-3.5-flash-lite` for lower-latency correction. It is editable under **Settings → AI Provider** in case the model name available to the account differs.

## Build and test

From PowerShell:

```powershell
.\scripts\build.ps1 -Configuration Release
```

Or with the .NET CLI:

```powershell
dotnet restore .\LLMAutocorrect.sln
dotnet build .\LLMAutocorrect.sln -c Release --no-restore
dotnet run --project .\tests\LLMAutocorrect.Tests\LLMAutocorrect.Tests.csproj -c Release --no-build
```

Run the framework-dependent app:

```powershell
.\src\LLMAutocorrect\bin\Release\net8.0-windows\win-x64\LLMAutocorrect.exe
```

Publish it to a clean folder:

```powershell
dotnet publish .\src\LLMAutocorrect\LLMAutocorrect.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\win-x64
```

## Usage

The app starts in the system tray. Autocorrect defaults to Conservative mode. Autocomplete displays a small overlay near the caret but never inserts anything without an explicit acceptance key.

| Action | Key |
|---|---|
| Accept full suggestion | Tab |
| Accept next predicted word | Ctrl+Right |
| Cycle candidates | Alt+Down |
| Dismiss suggestion | Esc |
| Undo latest autocorrection | Ctrl+Alt+Z |
| Correct selected text or the word at the caret | Ctrl+Alt+C |
| Toggle global enabled state | Ctrl+Alt+P |

Tab is intercepted only while a current, focus-matched suggestion is visible. Otherwise the target application receives Tab normally. When the overlay shows multiple options, press Alt+Down to move to the next option, then Tab to accept the displayed option.

Ordinary settings live in `%APPDATA%\LLMAutocorrect\settings.json`. Dictionary terms live one-per-line in `%APPDATA%\LLMAutocorrect\dictionary.txt`. Personal memory lives in `%APPDATA%\LLMAutocorrect\memory.json`. Ranked spelling history is written to `%APPDATA%\LLMAutocorrect\spelling-history.xlsx`. Diagnostics are metadata-only at `%APPDATA%\LLMAutocorrect\logs\diagnostics.log`.

Personal memory learns a short continuation only after you explicitly accept it with Tab or Ctrl+Right. It also learns recurring spelling corrections after the same correction succeeds more than once. Phrase prefixes and original misspellings are stored as SHA-256 hashes rather than readable text; only the short accepted continuation or corrected spelling is retained. Sensitive-looking text, text containing digits, and long passages are not saved. You can disable learning or permanently erase the memory under **Settings → Memory**.

The spelling-history workbook is separate from personal phrase memory. When enabled, it stores only individual misspelled and corrected words, their attempt counts, and the last-attempted time. It does not store complete sentences. Open or clear it under **Settings → Memory**.

Correction modes control how much the app may change. **Conservative** fixes spelling, capitalization, punctuation, and obvious grammar while preserving wording. **Normal** may also repair clearly awkward wording. **Clarity** permits a broader sentence cleanup while preserving meaning. Conservative remains the default.

For manual spelling correction, highlight text and right-click it, or right-click directly on a word. The app displays a small companion action next to the application's normal context menu. This is separate because Windows does not provide a safe universal API for inserting commands into every application's native text menu. `Ctrl+Alt+C` corrects the current selection or word at the caret directly.

Under **Settings → Correction**, the idle delay can be reduced to 200 ms, custom correction instructions can be entered, and the browser-address-bar spelling-only profile can be toggled. Lower delays feel faster but send requests during shorter typing pauses.

To enable launch at sign-in, open **Settings → General**, select **Start with Windows**, and save. This writes only the executable path to the current user's standard Windows Run key.

## Architecture

```text
Global keyboard/mouse hooks
          │
          ▼
InputVersionClock ──► TypingBuffer / focus snapshot
          │                       │
          │                       ├──► CorrectionCoordinator
          │                       │         └──► ICorrectionProvider
          │                       │                 └──► GeminiCorrectionProvider
          │                       │
          │                       └──► AutocompleteCoordinator
          │                                 └──► IAutocompleteProvider
          │                                         └──► GeminiAutocompleteProvider
          ▼
Freshness + focus + password + exclusion + output validation
          │
          ├──► Verified UI Automation replacement (automatic correction)
          └──► No-activate overlay → explicit acceptance → verified insertion
```

Gemini code knows nothing about hooks or replacement. A future Ollama, llama.cpp, LM Studio, vLLM, or OpenAI-compatible provider can implement the same correction/autocomplete interfaces and be selected during composition without changing the Windows input system.

## Reliability and privacy behavior

- Every physical key and mouse click increments a version at hook time.
- A result is discarded unless the version, process, foreground window, focused element (when available), and typed suffix still match.
- Navigation, selection-related shortcuts, paste, cut, Tab, Escape, and mouse clicks invalidate the editable suffix.
- Plain Backspace removes one character from the tracked suffix, so correction continues after self-edits. Ctrl+Backspace, Delete, navigation, selection changes, and mouse clicks conservatively invalidate pending work.
- Injected keys are ignored using both `LLKHF_INJECTED` and a private `dwExtraInfo` sentinel.
- Password fields clear the buffer and suppress both providers. Password managers and login-related processes are excluded by default.
- Context, requests, responses, unaccepted suggestions, and correction undo history remain in process memory. When personal memory is enabled, only the bounded, filtered learned entries described above are persisted.
- Spelling history persists individual word pairs and counts only when its separate Memory-tab option is enabled.
- Network/provider errors never block typing or show repeated dialogs.

## Manual verification checklist

1. In Notepad, type `i conected the vfd but it dose not seem to work`, pause, and verify a minimal correction.
2. Type `serverDataScreenGetList dose not update the a1VFD`; verify both identifiers remain byte-for-byte unchanged.
3. Set artificial latency to 2000 ms, type an error, then continue typing; verify the old response does nothing.
4. Repeat the latency test while clicking elsewhere and while switching from Brave to Notepad.
5. Verify no request/suggestion occurs in a password field, terminal, IDE, or password manager.
6. Show a suggestion, press Tab once, then press Tab again with no suggestion; verify normal application navigation on the second press.
7. Verify Esc dismisses and Ctrl+Right accepts one word at a time.
8. Verify a mouse click immediately hides a suggestion.
9. Disconnect the network and verify typing remains uninterrupted with no dialog loop.
10. Use Ctrl+Alt+Z immediately after a correction and verify the original text returns only in the same control.

## Known limitations

- Browser caret rectangles and UI Automation runtime IDs vary by browser/site; the overlay may use a conservative fallback position.
- `SendInput` can affect only applications at the same or lower integrity level. Elevated targets are intentionally unsupported.
- The buffer tracks text typed after the latest safe synchronization point; it does not scrape an entire document or webpage.
- Complex IME composition, dead-key layouts, rich-editor custom selection models, and application-native autocomplete require additional compatibility testing.
- Live Gemini behavior is account/model dependent and is not exercised by the offline test runner.
- Convenience features from the broader specification—notification toasts, timed pause menus, local-model UI, and fine-grained tray editing of application rules—are future work. Provider boundaries are already in place for local models.

## Repository layout

- `src/LLMAutocorrect/Input` — hooks, version clock, buffer, event worker
- `src/LLMAutocorrect/Correction` — requests, validation, history, coordinator
- `src/LLMAutocorrect/Autocomplete` — requests, validation, state, coordinator, overlay
- `src/LLMAutocorrect/Providers` — Gemini REST implementations
- `src/LLMAutocorrect/Windows` — UI Automation, foreground/caret, Win32 interop, `SendInput`
- `src/LLMAutocorrect/Security` — exclusions, protected tokens, sensitive content
- `src/LLMAutocorrect/Configuration` — settings and technical dictionary
- `src/LLMAutocorrect/Memory` — bounded personal phrase and spelling memory
- `tests/LLMAutocorrect.Tests` — dependency-free executable safety tests
