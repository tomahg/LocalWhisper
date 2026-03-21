# LocalWhisper — Norsk Tale-til-Tekst for Windows

## Prosjektoversikt

System-wide tale-til-tekst for Windows som injiserer transkribert norsk tale i ethvert inputfelt. Transkripsjonsserveren kjører på en vilkårlig maskin på lokalnettet (Mac/Windows/Linux) med GPU-akselerasjon.

---

## Arkitektur

```
┌──────────────────────────┐   WebSocket (audio chunks)   ┌───────────────────────────┐
│  Windows Client           │ ───────────────────────────► │  Transcription Server      │
│  C# / WinUI 3 / .NET 10   │                              │  Python / FastAPI           │
│                           │ ◄─────────────────────────── │                            │
│  • Global hotkey          │   WebSocket (partial text)   │  • faster-whisper           │
│  • NAudio mic capture     │                              │  • nb-whisper modeller      │
│  • Text injection via     │   REST (config, models)      │  • GPU/CPU inference        │
│    SendInput / Clipboard  │ ◄──────────────────────────► │  • Sliding window streaming │
│  • System tray (WinUI 3)  │                              │  • Modellbytte via REST     │
└──────────────────────────┘                               └───────────────────────────┘
```

---

## Del 1: Transcription Server (Python)

### Formål
Motta audio-chunks over WebSocket, kjøre inference med faster-whisper, og returnere partielle og endelige transkripsjoner.

### Teknologivalg
- **faster-whisper** (CTranslate2) — 4x raskere enn standard Whisper, lavere minnebruk, støtter VAD og segment-streaming
- **FastAPI** + **uvicorn** — asynkron web-server med WebSocket-støtte
- **NumPy** — audio-buffering og konvertering

### Prosjektstruktur

```
transcription-server/
├── server.py              # FastAPI app, WebSocket + REST endpoints
├── transcriber.py         # Modell-lasting, inference, sliding window-logikk
├── config.py              # Pydantic settings (les fra config.yaml)
├── config.yaml            # Konfigurasjon
├── requirements.txt
└── README.md
```

### config.yaml

```yaml
server:
  host: "0.0.0.0"
  port: 8765

transcription:
  default_model: "NbAiLab/nb-whisper-medium"
  device: "auto"            # "auto", "cuda", "cpu"
  compute_type: "float16"   # "float16", "int8", "float32"
  language: "no"
  beam_size: 5

streaming:
  chunk_duration_sec: 2.0   # Hvor mye audio som akkumuleres før inference
  overlap_sec: 0.5          # Overlapp mellom vinduer for kontekst
  vad_enabled: true         # Voice Activity Detection
  vad_threshold: 0.5

models:
  - id: "NbAiLab/nb-whisper-medium"
    name: "NB Whisper Medium (Norsk)"
  - id: "NbAiLab/nb-whisper-large"
    name: "NB Whisper Large (Norsk)"
  - id: "openai/whisper-large-v3"
    name: "Whisper Large v3 (Multilingual)"
```

### Nøkkellogikk: Streaming med sliding window

faster-whisper sin `transcribe()` returnerer segmenter iterativt. Strategien:

1. Klienten sender audio-chunks (binære frames, 16kHz 16-bit PCM mono) over WebSocket.
2. Serveren akkumulerer chunks i en buffer.
3. Hver `chunk_duration_sec` (f.eks. 2s) kjøres inference på bufferen.
4. Resultater sendes tilbake som `partial` (kan endres) eller `final` (stabil tekst).
5. Når klienten sender `audio_stop`, kjøres en siste inference på gjenværende buffer og markeres `final`.

```python
# Pseudokode for transcriber.py
class StreamingTranscriber:
    def __init__(self, model_id, device, compute_type):
        self.model = WhisperModel(model_id, device=device, compute_type=compute_type)
        self.audio_buffer = np.array([], dtype=np.float32)
        self.confirmed_text = ""
        self.segment_id = 0

    def add_audio(self, pcm_bytes: bytes):
        """Legg til audio-chunk i buffer. Returner None eller transkripsjonsresultat."""
        chunk = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.float32) / 32768.0
        self.audio_buffer = np.concatenate([self.audio_buffer, chunk])

        # Kjør inference når bufferen er lang nok
        if len(self.audio_buffer) / 16000 >= self.chunk_duration:
            return self._run_inference(is_final=False)
        return None

    def finalize(self):
        """Kjør siste inference på gjenværende buffer."""
        if len(self.audio_buffer) > 0:
            return self._run_inference(is_final=True)
        return None

    def _run_inference(self, is_final: bool):
        segments, info = self.model.transcribe(
            self.audio_buffer,
            language=self.language,
            beam_size=self.beam_size,
            vad_filter=self.vad_enabled,
        )
        text = " ".join(seg.text for seg in segments).strip()
        self.segment_id += 1

        if is_final:
            self.audio_buffer = np.array([], dtype=np.float32)
            return {"type": "final", "text": text, "segment_id": self.segment_id}
        else:
            # Behold overlapp for kontekst
            overlap_samples = int(self.overlap_sec * 16000)
            self.audio_buffer = self.audio_buffer[-overlap_samples:]
            return {"type": "partial", "text": text, "segment_id": self.segment_id}
```

### WebSocket-endpoint

```python
# server.py
@app.websocket("/ws/transcribe")
async def websocket_transcribe(websocket: WebSocket):
    await websocket.accept()
    transcriber = StreamingTranscriber(...)

    try:
        while True:
            message = await websocket.receive()

            if "bytes" in message:
                # Binær audio-data
                result = transcriber.add_audio(message["bytes"])
                if result:
                    await websocket.send_json(result)

            elif "text" in message:
                data = json.loads(message["text"])
                if data["type"] == "audio_stop":
                    result = transcriber.finalize()
                    if result:
                        await websocket.send_json(result)
                    break
    except WebSocketDisconnect:
        pass
```

### REST-endpoints

```
GET  /health                  → {"status": "ok", "model": "...", "device": "cuda"}
GET  /models                  → [{"id": "...", "name": "...", "loaded": true}, ...]
POST /models/switch           → {"model_id": "NbAiLab/nb-whisper-large"}  → 200 OK
GET  /config                  → Gjeldende konfigurasjon
```

### requirements.txt

```
faster-whisper>=1.0.0
fastapi>=0.110.0
uvicorn[standard]>=0.29.0
websockets>=12.0
numpy>=1.26.0
pyyaml>=6.0
```

### Kjøring

```bash
pip install -r requirements.txt
uvicorn server:app --host 0.0.0.0 --port 8765
```

---

## Del 2: Windows Client (C# / WinUI 3 / .NET 10)

### Teknologivalg
- **.NET 10** — Siste LTS-nær versjon
- **WinUI 3 (Windows App SDK 1.6+)** — Microsofts anbefalte UI-rammeverk
- **NAudio** — Audio capture
- **H.NotifyIcon.WinUI** — System tray-ikon for WinUI 3
- **CommunityToolkit.Mvvm** — MVVM-støtte

### Prosjektstruktur

```
LocalWhisper/
├── LocalWhisper.sln
├── LocalWhisper/
│   ├── LocalWhisper.csproj
│   ├── App.xaml / App.xaml.cs
│   ├── MainWindow.xaml / MainWindow.xaml.cs      # Innstillinger-vindu
│   ├── Package.appxmanifest
│   │
│   ├── Services/
│   │   ├── AudioCaptureService.cs        # NAudio mikrofon-capture
│   │   ├── WebSocketService.cs           # WebSocket-klient
│   │   ├── TextInjectionService.cs       # SendInput + Clipboard
│   │   ├── HotkeyService.cs             # Global hotkey
│   │   ├── TranscriptionService.cs       # Orkestrer alt
│   │   └── SettingsService.cs            # Les/skriv innstillinger
│   │
│   ├── ViewModels/
│   │   ├── MainViewModel.cs
│   │   └── SettingsViewModel.cs
│   │
│   ├── Models/
│   │   ├── TranscriptionResult.cs
│   │   ├── ServerConfig.cs
│   │   └── AppSettings.cs
│   │
│   ├── Helpers/
│   │   ├── NativeMethods.cs              # P/Invoke deklarasjoner
│   │   └── AudioConverter.cs             # PCM-konvertering
│   │
│   └── Assets/
│       ├── tray-idle.ico
│       ├── tray-listening.ico
│       └── tray-processing.ico
│
└── LocalWhisper.Tests/
    └── ...
```

### NuGet-pakker

```xml
<ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.6.*" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
    <PackageReference Include="NAudio" Version="2.*" />
    <PackageReference Include="H.NotifyIcon.WinUI" Version="2.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.*" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="9.*" />
    <PackageReference Include="System.Text.Json" Version="9.*" />
</ItemGroup>
```

### Komponent-detaljer

#### 1. HotkeyService

Registrer global hotkey som fungerer i alle apper via Win32 `RegisterHotKey`.

```csharp
// Nøkkelpunkter:
// - WinUI 3 bruker ikke tradisjonell WndProc. Bruk en usynlig HWND
//   via PInvoke.User32 CreateWindowEx for å motta WM_HOTKEY-meldinger.
// - Alternativt: bruk lavnivå keyboard hook (SetWindowsHookEx) med
//   WH_KEYBOARD_LL for mer fleksibilitet (hold-to-talk).
// - Konfigurer hotkey fra innstillinger (default: Ctrl+Shift+Space).
// - To moduser: "hold_to_talk" (hold nede = lytter) og "toggle" (trykk for å starte/stoppe).

public class HotkeyService : IDisposable
{
    // Bruk SetWindowsHookEx med WH_KEYBOARD_LL for hold-to-talk
    // Denne tilnærmingen gir key-down og key-up events
    public event Action? HotkeyPressed;
    public event Action? HotkeyReleased;
    // ...
}
```

#### 2. AudioCaptureService

```csharp
// NAudio WaveInEvent for mikrofon-capture
// Format: 16kHz, 16-bit, mono (PCM) — dette er hva Whisper forventer
// Sender chunks via callback hvert ~100ms (1600 samples = 3200 bytes)
// Chunks bufres og sendes over WebSocket i større batcher (f.eks. 500ms)

public class AudioCaptureService : IDisposable
{
    private WaveInEvent? _waveIn;
    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    public event Action<byte[]>? AudioDataAvailable;

    public void StartCapture(int deviceIndex = 0)
    {
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = 100,
            DeviceNumber = deviceIndex
        };
        _waveIn.DataAvailable += (s, e) =>
        {
            // Send kun bytes med faktisk data
            var buffer = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, buffer, e.BytesRecorded);
            AudioDataAvailable?.Invoke(buffer);
        };
        _waveIn.StartRecording();
    }

    public void StopCapture() => _waveIn?.StopRecording();
}
```

#### 3. WebSocketService

```csharp
// System.Net.WebSockets.ClientWebSocket
// Sender binære audio-frames direkte (ingen base64, ingen JSON-wrapping for audio)
// Mottar JSON for transkripsjonsresultater
// Automatisk reconnect med eksponentiell backoff

public class WebSocketService : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private readonly Uri _serverUri;
    private CancellationTokenSource? _cts;

    public event Action<TranscriptionResult>? TranscriptionReceived;

    public async Task ConnectAsync()
    {
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(_serverUri, CancellationToken.None);
        _ = Task.Run(ReceiveLoop); // Start lytting i bakgrunnen
    }

    public async Task SendAudioAsync(byte[] pcmData)
    {
        if (_ws?.State == WebSocketState.Open)
        {
            await _ws.SendAsync(pcmData, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
    }

    public async Task SendStopAsync()
    {
        var msg = JsonSerializer.SerializeToUtf8Bytes(new { type = "audio_stop" });
        await _ws.SendAsync(msg, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[4096];
        while (_ws?.State == WebSocketState.Open)
        {
            var result = await _ws.ReceiveAsync(buffer, _cts!.Token);
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var transcription = JsonSerializer.Deserialize<TranscriptionResult>(json);
                TranscriptionReceived?.Invoke(transcription!);
            }
        }
    }
}
```

#### 4. TextInjectionService

```csharp
// Injiser tekst i det aktive inputfeltet, uansett applikasjon.
//
// Strategi:
// 1. For kort tekst (<50 tegn): SendInput med KEYEVENTF_UNICODE
//    — Simulerer tastetrykk, fungerer i nesten alle apper
//    — Støtter æøå og andre Unicode-tegn
// 2. For lengre tekst: Clipboard + simulert Ctrl+V
//    — Raskere og mer pålitelig for lange strenger
//    — Krever at vi lagrer og gjenoppretter clipboard-innhold
//
// VIKTIG: Bruk KEYEVENTF_UNICODE-flagget for norske tegn.
//         Uten dette vil æøå ikke fungere korrekt.

public class TextInjectionService
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public void InjectText(string text)
    {
        if (text.Length < 50)
        {
            SendUnicodeKeystrokes(text);
        }
        else
        {
            PasteViaClipboard(text);
        }
    }

    private void SendUnicodeKeystrokes(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            // Key down
            inputs.Add(CreateUnicodeInput(c, isKeyUp: false));
            // Key up
            inputs.Add(CreateUnicodeInput(c, isKeyUp: true));
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private void PasteViaClipboard(string text)
    {
        // 1. Lagre eksisterende clipboard
        // 2. Sett ny tekst
        // 3. Simuler Ctrl+V
        // 4. Gjenopprett clipboard (med kort delay)
    }
}
```

#### 5. TranscriptionService (orkestrator)

```csharp
// Binder sammen alle services:
// HotkeyPressed → StartCapture + Connect WebSocket
// AudioDataAvailable → Send til WebSocket
// TranscriptionReceived → InjectText (for "partial": oppdater, for "final": bekreft)
// HotkeyReleased → StopCapture + Send audio_stop
//
// Partial-tekst-strategi:
// - Hold styr på hva som allerede er injisert
// - Når ny partial kommer: slett forrige partial (backspace), skriv ny
// - Når final kommer: bekreft teksten, nullstill

public class TranscriptionService
{
    private string _lastPartialText = "";

    private void OnTranscriptionReceived(TranscriptionResult result)
    {
        if (result.Type == "partial")
        {
            // Slett forrige partial
            _textInjection.SendBackspaces(_lastPartialText.Length);
            // Skriv ny partial
            _textInjection.InjectText(result.Text);
            _lastPartialText = result.Text;
        }
        else if (result.Type == "final")
        {
            // Slett partial, skriv final
            _textInjection.SendBackspaces(_lastPartialText.Length);
            _textInjection.InjectText(result.Text);
            _lastPartialText = "";
        }
    }
}
```

### WinUI 3 System Tray

```csharp
// H.NotifyIcon.WinUI gir system tray-støtte for WinUI 3.
// App.xaml.cs:
// - Ved oppstart: Vis tray-ikon, skjul hovedvindu
// - Tray-meny: "Innstillinger", "Status: Tilkoblet/Frakoblet", separator, "Avslutt"
// - Venstre-klikk på ikon: Vis/skjul innstillinger
// - Ikon endres basert på tilstand (idle/lytter/prosesserer)
```

### MainWindow (Innstillinger-UI)

Enkel WinUI 3 NavigationView med sider:

1. **Tilkobling** — Server-URL, tilkoblingsstatus, test-knapp
2. **Hotkey** — Velg tastekombinasjon, velg modus (hold/toggle)
3. **Modell** — Dropdown med tilgjengelige modeller fra serveren, bytt-knapp
4. **Lyd** — Velg mikrofon, volum-indikator
5. **Om** — Versjon, lenker

---

## Del 3: Kommunikasjonsprotokoll

### WebSocket-flyt

```
Klient                              Server
  │                                    │
  │──── [connect] ────────────────────►│
  │                                    │
  │──── [binary: PCM audio chunk] ───►│
  │──── [binary: PCM audio chunk] ───►│
  │──── [binary: PCM audio chunk] ───►│
  │                                    │── inference
  │◄─── {"type":"partial","text":"Hei"}│
  │                                    │
  │──── [binary: PCM audio chunk] ───►│
  │──── [binary: PCM audio chunk] ───►│
  │                                    │── inference
  │◄─── {"type":"partial","text":"Hei, jeg heter"}
  │                                    │
  │──── {"type":"audio_stop"} ────────►│
  │                                    │── final inference
  │◄─── {"type":"final","text":"Hei, jeg heter Ola."}
  │                                    │
  │──── [close] ──────────────────────►│
```

### Meldingsformat

**Server → Klient (JSON):**
```json
{
    "type": "partial | final | error",
    "text": "transkribert tekst",
    "segment_id": 1,
    "processing_time_ms": 245
}
```

---

## Del 4: Implementeringsrekkefølge

### Fase 1: Server MVP
1. Sett opp Python-prosjekt med dependencies
2. Implementer `transcriber.py` med faster-whisper, test med en WAV-fil
3. Implementer WebSocket-endpoint i `server.py`
4. Lag en enkel Python test-klient som sender en WAV-fil som chunks
5. Verifiser at nb-whisper-modellen gir korrekt norsk transkripsjon

### Fase 2: C# Client — Audio + WebSocket
1. Opprett WinUI 3-prosjekt (.NET 10, Windows App SDK 1.6)
2. Implementer `AudioCaptureService` med NAudio
3. Implementer `WebSocketService`
4. Test: Ta opp lyd, send til server, logg transkripsjoner i konsollen

### Fase 3: Tekst-injeksjon
1. Implementer `TextInjectionService` med SendInput (Unicode)
2. Implementer clipboard-fallback for lange tekster
3. Test i Notepad, Word, Chrome, Teams — verifiser æøå

### Fase 4: Global Hotkey + Orkestrering
1. Implementer `HotkeyService` med lavnivå keyboard hook
2. Implementer `TranscriptionService` som binder alt sammen
3. Test hold-to-talk og toggle-modus

### Fase 5: UI og System Tray
1. Sett opp H.NotifyIcon.WinUI for system tray
2. Bygg innstillinger-UI i MainWindow
3. Implementer modellbytte via REST
4. Ikon-bytte basert på tilstand

### Fase 6: Polish
1. Automatisk reconnect ved nettverksfeil
2. Audio device hotswap (mikrofon kobles til/fra)
3. Visuell feedback — liten overlay som viser hva som transkriberes
4. Logging og feilhåndtering
5. Installasjon/oppsett-guide

---

## Viktige hensyn

### WinUI 3 + .NET 10 spesifikt
- Bruk **packaged app** (MSIX) for enklere distribusjon og system tray-støtte
- Sett `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>` i csproj for å unngå runtime-avhengigheter
- Target `net10.0-windows10.0.22621.0` eller nyere
- WinUI 3 har ikke innebygd WndProc — for global hotkey, bruk enten `SetWindowsHookEx` (anbefalt) eller opprett en skjult Win32-vindu

### Nettverksoppdagelse
- Valgfritt: Implementer mDNS/Bonjour for automatisk server-oppdaging på lokalnettet
- Enklere alternativ: Bruk manuell IP-konfigurasjon i innstillinger

### Sikkerhet
- WebSocket-kommunikasjon er ukryptert på LAN. Vurder WSS (TLS) for produksjon
- Valgfritt: Enkel API-nøkkel for autentisering mellom klient og server

### Ytelse
- `faster-whisper` med `compute_type: "int8"` gir best ytelse/kvalitet-balanse
- På en Mac med Apple Silicon: Bruk `device: "cpu"` med `compute_type: "int8"` (Metal støttes ikke direkte av CTranslate2, men CPU-ytelsen er god)
- Forventet latens: 500ms–2s avhengig av chunk-størrelse og maskinvare
