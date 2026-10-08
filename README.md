# LLM Autocorrect for Windows

A conservative Windows 11 tray application that uses a selectable local or paid AI provider to correct recently typed text and offer optional Tab-completion in ordinary Windows text fields. Ollama with `qwen3.5:4b` is the local-first default. The app is built with C#, .NET 8, WPF, UI Automation, and low-level Win32 hooks.

The safety rule is simple: when focus, caret position, application, password status, or input version is uncertain, the app does nothing.

## Implemented milestone

- One shared `WH_KEYBOARD_LL` input stream and `WH_MOUSE_LL` invalidation hook.
- In-memory typing buffer; ordinary typed text and full messages are not persisted.
- Foreground window, process, and focused UI Automation element tracking.
- Immediate hook-time version increments for stale-response rejection.
- Provider-neutral correction, regular autocomplete, and terminal autocomplete behind stable interfaces.
- Native Ollama structured-output requests plus OpenAI-compatible and Gemini protocols.
- Saved profiles for Ollama, LM Studio, Gemini, and a generic paid or local OpenAI-compatible service.
- Provider addresses may use localhost, a trusted LAN host, or HTTPS cloud APIs.
- API keys and tokens are stored in Windows Credential Manager and never in `settings.json`.
- Structured JSON responses, reusable `HttpClient`, cancellation, rate limits, and a two-minute cold-load timeout for local models.
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
- Configurable stale-safe insertion retries. Every retry reacquires the focused editor and rechecks the window, input version, caret context, original text, and released physical keys before changing anything.
- Autocomplete retries only failures proven to occur before insertion; ambiguous paste results stop and invalidate the tracked suffix to prevent duplicate text.
- Deliberate provider latency setting for stale-response testing.
- Physical-key release gating prevents accepted suggestions from being injected while Tab or a modifier is still held.

## Requirements

- Windows 11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- .NET 8 Windows Desktop Runtime to run a framework-dependent build
- Ollama for Windows and `qwen3.5:4b` for the default local configuration, or credentials for another configured provider

The app runs as the current user. It intentionally does not request administrator privileges and therefore will not interact with elevated applications or the Windows secure desktop.

## AI provider setup

The default provider is Ollama on this computer. After installing Ollama, download the model once:

```powershell
ollama pull qwen3.5:4b
```

The initial profile uses `http://127.0.0.1:11434`, model `qwen3.5:4b`, and a 4096-token context. Open **Settings > AI Provider** to test the connection or select another saved profile:

- **Ollama** uses the native `/api/chat` endpoint and can keep the model loaded.
- **LM Studio** uses its OpenAI-compatible endpoint, normally `http://127.0.0.1:1234/v1`.
- **Gemini** uses Google's cloud API and an API key.
- **OpenAICompatible** supports local or paid services that implement `/v1/chat/completions` and structured JSON output.

Each profile retains its own address, model, authentication type, HTTP header, optional prefix, context length, and keep-loaded preference. A blank credential box preserves the existing saved credential. Selecting **Clear saved credential** removes it from Windows Credential Manager.

The first Ollama correction may take substantially longer while Windows loads the model into memory. Leave **Keep model loaded** enabled for low-latency follow-up corrections; turn it off when you prefer to reclaim GPU memory between uses.

Gemini and generic OpenAI-compatible profiles can still read `GEMINI_API_KEY` and `OPENAI_API_KEY` respectively when no credential is saved. Avoid placing real keys in scripts, `.env` files, command history, screenshots, or issue reports. Rotate any credential that has been exposed.

For a provider on another computer, enter a LAN address such as `http://192.168.1.50:11434`. The remote server must listen on its LAN interface and its firewall must permit only the intended private network. Plain HTTP does not encrypt correction text or tokens; use HTTPS or a trusted VPN when authentication or sensitive text crosses the network. Never expose an unauthenticated model server directly to the internet.

**Private Mode** permits only a provider reached through this computer's loopback address. It blocks LAN and cloud endpoints both in the UI coordinators and immediately before a network request.

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

Ordinary settings live in `%APPDATA%\LLMAutocorrect\settings.json`. Provider secrets live separately in Windows Credential Manager under `LLMAutocorrect/AIProvider/...`. Dictionary terms live one-per-line in `%APPDATA%\LLMAutocorrect\dictionary.txt`. Personal memory lives in `%APPDATA%\LLMAutocorrect\memory.json`. Ranked spelling history is written to `%APPDATA%\LLMAutocorrect\spelling-history.xlsx`. Diagnostics are metadata-only at `%APPDATA%\LLMAutocorrect\logs\diagnostics.log`.

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
          │                       │                 └──► AiCorrectionProvider
          │                       │
          │                       └──► AutocompleteCoordinator
          │                                 └──► IAutocompleteProvider
          │                                         └──► AiAutocompleteProvider
          ▼
Freshness + focus + password + exclusion + output validation
          │
          ├──► Verified UI Automation replacement (automatic correction)
          └──► No-activate overlay → explicit acceptance → verified insertion
```

Provider protocol code knows nothing about hooks or replacement. `ProviderClient` routes structured requests to Ollama's native API, an OpenAI-compatible endpoint, or Gemini without changing the Windows input system, output validation, or stale-response protection.

## Reliability and privacy behavior

- Every physical key and mouse click increments a version at hook time.
- A result is discarded unless the version, process, foreground window, focused element (when available), and typed suffix still match.
- Navigation, selection-related shortcuts, paste, cut, Tab, Escape, and mouse clicks invalidate the editable suffix.
- Plain Backspace removes one character from the tracked suffix, so correction continues after self-edits. Ctrl+Backspace, Delete, navigation, selection changes, and mouse clicks conservatively invalidate pending work.
- Injected keys are ignored using both `LLKHF_INJECTED` and a private `dwExtraInfo` sentinel.
- Password fields clear the buffer and suppress both providers. Password managers and login-related processes are excluded by default.
- Context, requests, responses, unaccepted suggestions, and correction undo history remain in process memory. When personal memory is enabled, only the bounded, filtered learned entries described above are persisted.
- Spelling history persists individual word pairs and counts only when its separate Memory-tab option is enabled.
- Network/provider errors never block typing or show repeated dialogs. Permanent authentication, model, and billing errors are not retried automatically.
- HTTP redirects are disabled for provider requests so an authentication token cannot be forwarded to a different host.

## Manual verification checklist

1. In Notepad, type `i conected the vfd but it dose not seem to work`, pause, and verify a minimal correction.
2. Type `serverDataScreenGetList dose not update the a1VFD`; verify both identifiers remain byte-for-byte unchanged.
3. Set artificial latency to 2000 ms, type an error, then continue typing; verify the old response does nothing.
4. Repeat the latency test while clicking elsewhere and while switching from Brave to Notepad.
5. Verify no request/suggestion occurs in a password field, terminal, IDE, or password manager.
6. Show a suggestion, press Tab once, then press Tab again with no suggestion; verify normal application navigation on the second press.
7. Verify Esc dismisses and Ctrl+Right accepts one word at a time.
8. Verify a mouse click immediately hides a suggestion.
9. Stop Ollama or disconnect the network and verify typing remains uninterrupted with no dialog loop and an actionable provider status.
10. Use Ctrl+Alt+Z immediately after a correction and verify the original text returns only in the same control.
11. In **Settings → AI Provider**, test Ollama, then switch to another saved profile and back; verify each profile retains its own model and endpoint.

## Known limitations

- Browser caret rectangles and UI Automation runtime IDs vary by browser/site; the overlay may use a conservative fallback position.
- `SendInput` can affect only applications at the same or lower integrity level. Elevated targets are intentionally unsupported.
- The buffer tracks text typed after the latest safe synchronization point; it does not scrape an entire document or webpage.
- Complex IME composition, dead-key layouts, rich-editor custom selection models, and application-native autocomplete require additional compatibility testing.
- Live model quality and latency depend on the selected model, quantization, context length, GPU availability, and provider runtime.
- A remote Ollama or LM Studio server must be configured separately to listen on the LAN interface; the app does not change another server's bind address or firewall.

## Repository layout

- `src/LLMAutocorrect/Input` — hooks, version clock, buffer, event worker
- `src/LLMAutocorrect/Correction` — requests, validation, history, coordinator
- `src/LLMAutocorrect/Autocomplete` — requests, validation, state, coordinator, overlay
- `src/LLMAutocorrect/Providers` — Ollama, OpenAI-compatible, and Gemini protocol routing
- `src/LLMAutocorrect/Windows` — UI Automation, foreground/caret, Win32 interop, `SendInput`
- `src/LLMAutocorrect/Security` — exclusions, protected tokens, sensitive content
- `src/LLMAutocorrect/Configuration` — settings and technical dictionary
- `src/LLMAutocorrect/Memory` — bounded personal phrase and spelling memory
- `tests/LLMAutocorrect.Tests` — dependency-free executable safety tests
