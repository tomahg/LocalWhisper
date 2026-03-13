# NorskTale — Norsk Tale-til-Tekst for Windows

## Hva er dette?

En Windows system tray-app (WinUI 3 / .NET 9) som gir tale-til-tekst i alle inputfelt i alle applikasjoner. Transkripsjonen kjøres av en Python-server (faster-whisper) som kan stå på en annen maskin på lokalnettet. Audio streames i sanntid over WebSocket.

## Prosjektstruktur

```
NorskTale/
├── CLAUDE.md                   # ← Du leser denne
├── PLAN.md                     # Detaljert arkitektur og implementeringsplan
├── client/                     # C# / WinUI 3 / .NET 9 Windows-klient
│   ├── NorskTale.sln
│   └── NorskTale/
│       ├── NorskTale.csproj
│       ├── App.xaml(.cs)
│       ├── MainWindow.xaml(.cs)
│       ├── Services/           # Kjernefunksjonalitet
│       ├── ViewModels/
│       ├── Models/
│       ├── Helpers/
│       └── Assets/
└── server/                     # Python / FastAPI transkripsjonsserver
    ├── server.py
    ├── transcriber.py
    ├── config.py
    ├── config.yaml
    └── requirements.txt
```

## Teknologivalg

### Klient
- **.NET 9** med `net9.0-windows10.0.22621.0`
- **WinUI 3** (Windows App SDK 1.6+) — Microsofts anbefalte desktop-rammeverk
- **NAudio 2.x** — Mikrofon-capture (16kHz, 16-bit, mono PCM)
- **H.NotifyIcon.WinUI** — System tray
- **CommunityToolkit.Mvvm** — MVVM
- **System.Net.WebSockets** — Innebygd WebSocket-klient

### Server
- **faster-whisper** — CTranslate2-basert Whisper, 4x raskere enn standard
- **FastAPI** + **uvicorn** — Asynkron WebSocket + REST
- **Standardmodell:** `NbAiLab/nb-whisper-medium`

## Viktige tekniske regler

### C# / WinUI 3
- WinUI 3 har IKKE tradisjonell WndProc. Bruk `SetWindowsHookEx` med `WH_KEYBOARD_LL` for global hotkey, IKKE `RegisterHotKey`.
- Packaged app (MSIX). Sett `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>` i csproj.
- Tekst-injeksjon: Bruk `SendInput` med `KEYEVENTF_UNICODE` for norske tegn (æøå). Clipboard+Ctrl+V som fallback for lange tekster.
- Audio sendes som rå binære WebSocket-frames. Ingen base64-encoding for audio.
- Partial-tekst oppdateres ved å sende backspace for forrige partial, deretter ny tekst.

### Python / Server
- Bruk `faster-whisper`, IKKE `openai-whisper` eller `transformers` pipeline.
- WebSocket-endpoint mottar binære PCM-frames og returnerer JSON med `{"type": "partial"|"final", "text": "..."}`.
- Sliding window-strategi: akkumuler audio, kjør inference hver ~2 sekunder, behold 0.5s overlapp.
- REST-endpoints for `/health`, `/models`, `/models/switch`.

### Kommunikasjon
- Klient → Server: Binære WebSocket-frames (PCM audio) + JSON kontrollmeldinger
- Server → Klient: JSON med transkripsjonsresultater
- `audio_stop`-melding trigger siste inference på gjenværende buffer

## Implementeringsrekkefølge

Følg fasene i PLAN.md:
1. **Server MVP** — faster-whisper + WebSocket, test med Python-klient
2. **C# Audio + WebSocket** — NAudio capture, send til server
3. **Tekst-injeksjon** — SendInput med Unicode, clipboard-fallback
4. **Global hotkey + orkestrering** — Bind alt sammen
5. **UI + System tray** — WinUI 3 innstillinger, tray-ikon
6. **Polish** — Reconnect, feilhåndtering, visuell feedback

## Kodestil

### C#
- Bruk `async/await` konsekvent. Ingen `.Result` eller `.Wait()`.
- Dependency injection via `Microsoft.Extensions.DependencyInjection`.
- Alle P/Invoke-deklarasjoner samlet i `Helpers/NativeMethods.cs`.
- `IDisposable` / `IAsyncDisposable` på services som holder unmanaged resources.

### Python
- Type hints på alle funksjoner.
- Pydantic for konfigurasjon og modeller.
- `async def` for alle WebSocket-handlers.
- Logging via `logging`-modulen, ikke `print()`.

## Kjente begrensninger å være klar over
- faster-whisper støtter ikke Metal/MPS direkte — på Mac brukes CPU med int8 (som likevel er rask).
- Whisper er ikke en ekte streaming-modell — vi simulerer streaming med sliding window.
- `SendInput` fungerer ikke i apper som kjører med høyere privilege-nivå enn klienten (f.eks. admin-apper). Dokumenter dette.
- WinUI 3 system tray er ikke like modent som WPF — forvent noen quirks med `H.NotifyIcon.WinUI`.
