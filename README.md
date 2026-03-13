# NorskTale

Windows speech-to-text for any input field in any application. A system tray app (WinUI 3 / .NET 9) streams microphone audio over WebSocket to a transcription server running [faster-whisper](https://github.com/SYSTRAN/faster-whisper). The server can run locally or on another machine on the LAN.

Default model: **NbAiLab/nb-whisper-medium** — optimised for Norwegian.

---

## Project structure

```
NorskTale/
├── client/          # C# / WinUI 3 / .NET 9 Windows client (work in progress)
└── server/          # Python transcription server
    ├── server.py
    ├── transcriber.py
    ├── config.py
    ├── config.yaml          # Main configuration (committed)
    ├── secrets.yaml         # HuggingFace token — NOT committed (see below)
    ├── secrets.yaml.example # Template for secrets.yaml
    ├── requirements.txt
    └── test_client.py       # CLI test client
```

---

## Server

### Requirements

- Python 3.10+
- A virtual environment is recommended

### Install dependencies

```bash
cd server
python -m venv .venv
.venv\Scripts\activate      # Windows
pip install -r requirements.txt
```

### HuggingFace token (optional)

A token is not required for public models, but avoids rate-limiting and is needed for any gated models.

1. Get a read-only token at <https://huggingface.co/settings/tokens>
2. Copy the example file:
   ```bash
   cp secrets.yaml.example secrets.yaml
   ```
3. Open `secrets.yaml` and replace `hf_YOUR_TOKEN_HERE` with your token

`secrets.yaml` is listed in `.gitignore` and will never be committed. The server works fine without it.

### Configuration

Edit `config.yaml` to change model, device, compute type, etc.

```yaml
transcription:
  default_model: "NbAiLab/nb-whisper-medium"
  device: "cpu"        # "auto" | "cuda" | "cpu"
  compute_type: "int8" # "int8" recommended for CPU, "float16" for GPU
```

Available models (defined in `config.yaml`):

| Model | Notes |
|---|---|
| `NbAiLab/nb-whisper-small` | Fast, less accurate |
| `NbAiLab/nb-whisper-medium` | Recommended |
| `NbAiLab/nb-whisper-large` | Best quality, slow on CPU |
| `openai/whisper-large-v3` | Multilingual |

### Start the server

```bash
cd server
.venv\Scripts\activate
uvicorn server:app --host 0.0.0.0 --port 8765
```

On first run the selected model is downloaded from HuggingFace (~1–3 GB depending on model). Progress bars are shown in the terminal. Subsequent starts use the local cache and are fast.

Once ready you will see:

```
Model ready.
INFO:     Application startup complete.
```

### REST endpoints

| Endpoint | Method | Description |
|---|---|---|
| `/health` | GET | Server status, current model, device info |
| `/models` | GET | List available models |
| `/models/switch` | POST | Switch to a different model at runtime |
| `/config` | GET | Full configuration dump |

Example:
```bash
curl http://localhost:8765/health
```

---

## Test client

`test_client.py` lets you verify the server without the Windows client.

### Connectivity test (silence)

Streams 3 seconds of silence — confirms the WebSocket connection works:

```bash
python test_client.py
```

### Stream a WAV file

```bash
python test_client.py path\to\audio.wav
```

Any WAV file works — the test client automatically resamples to 16kHz and downmixes to mono if needed.

### Remote server

```bash
python test_client.py --url ws://192.168.1.x:8765/ws/transcribe path\to\audio.wav
```

### Expected output

```
Reading audio.wav ...
Connecting to ws://localhost:8765/ws/transcribe ...
Connected. Streaming 480000 bytes in 16000-byte chunks ...

[partial] 'Hei, dette er en test'  (312ms)

Sent 30 chunks. Sending audio_stop ...

[final  ] 'Hei, dette er en test av talestyring.'  (287ms)

Done.
```

---

## Known limitations

- `SendInput` does not work in applications running at a higher privilege level than the client (e.g. programs running as Administrator).
- faster-whisper does not support Metal/MPS — macOS uses CPU with int8.
- Whisper is not a true streaming model; real-time transcription is approximated with a sliding window.
