# LocalWhisperer

Windows speech-to-text for any input field in any application. A system tray app (WinUI 3 / .NET 10) streams microphone audio over WebSocket to a transcription server running [faster-whisper](https://github.com/SYSTRAN/faster-whisper). The server can run locally or on another machine on the LAN.

Default model: **NbAiLab/nb-whisper-medium** — optimised for Norwegian.

---

## How it works

1. Press **F9** (hold for hold-to-talk, or tap to toggle) — an overlay appears in the bottom-right corner
2. Speak — a live audio level indicator shows the microphone is active
3. Release / press again to stop — the server transcribes and the result appears with a **Kopier** button

**Optional: auto-send on silence** — enable in Lyd settings for continuous dictation. The server transcribes each pause automatically and text accumulates in the overlay while the microphone stays open. Press the hotkey to finish.

The client streams raw 16kHz PCM audio over WebSocket while recording. When stopped (or when a silence pause is detected), the server runs an inference pass on the buffered audio and returns the transcription.

---

## Project structure

```
LocalWhisperer/
├── client/                          # C# / WinUI 3 / .NET 10 Windows client
│   └── LocalWhisperer/
│       ├── App.xaml(.cs)            # Startup, system tray, global hotkey
│       ├── MainWindow.xaml(.cs)     # Settings window (NavigationView)
│       ├── Pages/                   # Settings pages (Connection, Hotkey, Model, Audio, About)
│       ├── Services/
│       │   ├── AudioCaptureService.cs
│       │   ├── WebSocketService.cs
│       │   ├── HotkeyService.cs
│       │   ├── TranscriptionOrchestrator.cs
│       │   ├── SettingsService.cs
│       │   └── ServerApiService.cs
│       └── Helpers/NativeMethods.cs # P/Invoke (SendInput, keyboard hook)
└── server/                          # Python transcription server
    ├── server.py
    ├── transcriber.py
    ├── config.py
    ├── config.yaml                  # Main configuration (committed)
    ├── secrets.yaml                 # HuggingFace token — NOT committed (see below)
    ├── secrets.yaml.example
    ├── requirements.txt
    └── test_client.py
```

---

## Client

### Requirements

- Windows 10 1809 (build 17763) or later
- .NET 10 SDK (for building from source)

### Build

```powershell
cd client
dotnet build LocalWhisperer/LocalWhisperer.csproj -r win-x64
```

### Publish (self-contained folder)

```powershell
cd client
dotnet publish LocalWhisperer/LocalWhisperer.csproj -r win-x64 -c Release --self-contained -o publish
```

Copy the `publish\` folder to the target machine and run `LocalWhisperer.exe`. No installation required.

### Usage

On first launch the app starts in the system tray (no window appears). The tray icon indicates state:

| Icon | State |
|---|---|
| Blue | Idle / connected |
| Red | Recording |
| Gray | Disconnected |

**Right-click** the tray icon for the context menu. **Left-click** opens the settings window.

#### Settings

| Page | Description |
|---|---|
| **Tilkobling** | Server URL, connect/disconnect |
| **Hurtigtast** | Active hotkey (F9), hold-to-talk toggle |
| **Modell** | Switch transcription model at runtime |
| **Lyd** | Select microphone, auto-copy to clipboard, auto-send on silence |
| **Om** | About |

Settings are persisted automatically between sessions.

#### Hotkey

Default: **F9**

Two modes (configurable in the Hurtigtast settings page):
- **Toggle** (default) — press once to start, press again to stop
- **Hold-to-talk** — hold key while speaking, release to stop

> Tip: Enable **auto-copy to clipboard** in the Lyd settings page to skip the manual copy step — the result is copied automatically when recording stops.

> Tip: Enable **auto-send ved stillhet** in the Lyd settings page for continuous dictation — transcribed text accumulates in the overlay as you speak and pause naturally.

---

## Server

### Requirements

- Python 3.10+

### Install

```bash
cd server
python -m venv .venv
.venv\Scripts\activate      # Windows
# source .venv/bin/activate   # macOS / Linux
pip install -r requirements.txt
```

### HuggingFace token (optional)

Required only for gated models. Avoids rate-limiting on first download.

1. Get a read-only token at <https://huggingface.co/settings/tokens>
2. `cp secrets.yaml.example secrets.yaml`
3. Replace `hf_YOUR_TOKEN_HERE` with your token

`secrets.yaml` is in `.gitignore` and will never be committed.

### Configuration

Edit `config.yaml`:

```yaml
transcription:
  default_model: "NbAiLab/nb-whisper-medium"
  device: "cpu"        # "auto" | "cuda" | "cpu"
  compute_type: "int8" # "int8" for CPU, "float16" for GPU
```

Available models:

| Model | Notes |
|---|---|
| `NbAiLab/nb-whisper-small` | Fast, less accurate |
| `NbAiLab/nb-whisper-medium` | Recommended |
| `NbAiLab/nb-whisper-large` | Best quality, slow on CPU |
| `openai/whisper-large-v3` | Multilingual |

### Start

```bash
cd server
.venv\Scripts\activate
uvicorn server:app --host 0.0.0.0 --port 8765
```

The selected model is downloaded from HuggingFace on first run (~1–3 GB). Subsequent starts use the local cache.

```
Model ready.
INFO:     Application startup complete.
```

### REST endpoints

| Endpoint | Method | Description |
|---|---|---|
| `/health` | GET | Status, current model, device |
| `/models` | GET | List available models |
| `/models/switch` | POST | Switch model at runtime |
| `/config` | GET | Full configuration |

```bash
curl http://localhost:8765/health
```

---

## Test client

Verify the server without the Windows client:

```bash
# Connectivity test (3 seconds of silence)
python test_client.py

# Stream a WAV file
python test_client.py path/to/audio.wav

# Remote server
python test_client.py --url ws://192.168.1.x:8765/ws/transcribe path/to/audio.wav
```

Expected output:
```
[final  ] 'Hei, dette er en test av talestyring.'  (1243ms)
```

---

## Known limitations

- Transcription runs after recording stops (or per silence pause with auto-send) — longer recordings between pauses mean a longer wait for each result.
- faster-whisper does not support Metal/MPS — macOS uses CPU with int8.
- The global keyboard hook (`WH_KEYBOARD_LL`) may be blocked in some enterprise environments.
