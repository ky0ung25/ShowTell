# ShowTell

Record your screen and voice with a double-tap, then drop it straight into Claude, Codex or Grok. Available for Windows and macOS.

1. Press **F1 + F2 together, twice** → recording starts (a small red "REC" pill appears; it is not captured in the frames).
2. Do it again → recording stops and the **Send recording** window opens.
3. Pick the **AI app**, the **chat thread** (New chat, the currently open chat, or one from the app's sidebar), and type your **instructions**.
4. Press **Enter** → ShowTell switches to that app, opens the thread, and pastes the key frames plus your instructions and voice transcript into the message box. **It never presses send.** Review it, then send it yourself.

## Download

Get the latest build from the [Releases](../../releases) page. Builds from every push to `main` are also attached to its run on the [Actions](../../actions) tab.

| | File | Runs on |
|---|---|---|
| Windows | `ShowTell-windows-x64.zip`: one portable `ShowTell.exe` (no install, no .NET needed) | Windows 10/11, 64-bit |
| macOS | `ShowTell-macos.dmg` (or `.zip`) | macOS 14 Sonoma or later, Apple silicon or Intel |

**Windows:** unzip, then run `ShowTell.exe`. Windows SmartScreen may warn about an unrecognized app: click **More info → Run anyway**. ShowTell lives in the system tray.

**macOS:** open the .dmg and drag ShowTell to Applications. The app isn't notarized by Apple, so the first time **right-click it → Open → Open** (or run `xattr -dr com.apple.quarantine /Applications/ShowTell.app`). ShowTell lives in the menu bar. On first launch it asks for these permissions and lists them in Settings:

| Permission | Why |
|---|---|
| Screen Recording | capture the frames (quit and reopen ShowTell after turning it on) |
| Accessibility | open the chat and paste into it |
| Input Monitoring | hear the F1 + F2 shortcut |
| Microphone | record your voice |

## What gets pasted

These apps don't accept video, so ShowTell turns the recording into:

- **Key frames**: screenshots taken whenever the screen visibly changes (plus one every 8 s), trimmed to the 12 most significant. You can click thumbnails to leave frames out.
- **Text**: your instructions, the frame timestamps, and a timestamped transcript of what you said.

Transcription runs on your computer with NVIDIA **Parakeet TDT 0.6B v2** (English, with punctuation). The model downloads once from Hugging Face the first time ShowTell starts: about 630 MB on Windows (ONNX via sherpa-onnx), roughly 500 MB on macOS (Core ML via FluidAudio, running on the Neural Engine). Audio never leaves your computer and uses no tokens. In Settings you can switch to Parakeet v3 (25 European languages); Windows also offers Whisper models (smaller downloads, any language).

Recordings (frames, `audio.wav`, `transcript.txt`, `prompt.txt`) are deleted once pasted; ones you close without pasting are kept (newest 20).

| | Windows | macOS |
|---|---|---|
| Settings, recordings, log | `%LOCALAPPDATA%\ShowTell` | `~/Library/Application Support/ShowTell` |
| Open Settings | Settings button in the Send window, tray icon menu, or launch ShowTell again | Settings button in the Send window, menu bar icon, or open ShowTell again |

## Settings

You can change the two shortcut keys (click a key, then press the new one), the double-tap window, max frames, max length, speech model, the start/stop sound (off by default), deleting recordings after they're pasted (on by default), and start at login. Windows also lets you pick the screen and microphone; macOS records the screen under the mouse and the input device chosen in System Settings → Sound.

On Apple keyboards F1/F2 are brightness keys by default. ShowTell recognizes them either way, so you don't need to hold fn.

`settings.json` also holds the AI app definitions (`targets`): app names, Windows process names or macOS bundle IDs, the "new chat" button labels, and a fallback shortcut. Edit it to add another app, then use the tray / menu bar → **Reload settings.json**.

## How it works

| Piece | Windows | macOS |
|---|---|---|
| Hotkey | Low-level keyboard hook; a lone F1/F2 is passed through so F1 Help and F2 Rename still work | Listen-only event tap (also sees brightness/media keys) |
| Screen | GDI capture every 0.5 s, change detection on a 128×72 thumbnail | ScreenCaptureKit, same change detection; ShowTell's own windows are excluded |
| Mic | NAudio WASAPI → 16 kHz mono WAV | AVAudioRecorder → 16 kHz mono WAV |
| Speech-to-text | Parakeet (sherpa-onnx, CPU) or Whisper.net | Parakeet (FluidAudio, Core ML / Neural Engine) |
| Threads, new chat, message box | UI Automation on the app's sidebar and input box | Accessibility (AX) API, same approach |
| Paste | Clipboard + Ctrl+V, one frame at a time | Clipboard + ⌘V, one frame at a time |

- The thread list is read from the app's **visible sidebar**. If the sidebar is collapsed you'll only see New chat / Currently open. An app redesign can break this; the fallback is "Currently open chat".
- Don't click around while it pastes (about 1 s per frame). If the target app loses focus, ShowTell stops and leaves the text on your clipboard.
- If no microphone is available it records the screen only and says so in the prompt.

## Build from source

**Windows** (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```powershell
cd windows
powershell -ExecutionPolicy Bypass -File build-exe.ps1   # portable dist\ShowTell.exe
powershell -ExecutionPolicy Bypass -File install.ps1     # or install to %LOCALAPPDATA%\Programs\ShowTell with Start Menu shortcuts
```

**macOS** (needs Xcode 16 or later):

```bash
cd macos
./build.sh                  # universal dist/ShowTell.app, .zip and .dmg
ARCHS=arm64 ./build.sh      # Apple silicon only, faster
```

**Releases:** push a tag like `v0.1.0` and GitHub Actions builds both apps and attaches them to a new release.

**Self-test:** `ShowTell.exe --selftest [transcribe=C:\path\speech.wav] [model=base.en]` writes a report to `%LOCALAPPDATA%\ShowTell\selftest.txt`. On macOS, `ShowTell.app/Contents/MacOS/ShowTell --selftest [transcribe=/path/speech.wav]` prints one.

## Repository layout

```
windows/   WPF/.NET 8 app (C#)
macos/     AppKit/SwiftUI app (Swift package)
.github/   CI: builds both apps on every push, publishes releases for v* tags
```

## License

MIT. See [LICENSE](LICENSE).
