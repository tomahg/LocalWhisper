---
description: faster-whisper API reference — model loading, transcribe(), VAD, segment iteration, compute types, streaming strategy for NorskTale server
---

# faster-whisper Reference

faster-whisper uses CTranslate2 under the hood. API differs from `openai-whisper` in important ways.

## Model Loading

```python
from faster_whisper import WhisperModel

# Basic
model = WhisperModel("NbAiLab/nb-whisper-medium", device="auto", compute_type="float16")

# device options: "auto", "cuda", "cpu"
# compute_type options (by speed/accuracy):
#   "float16"  — GPU only, best quality, uses most VRAM
#   "int8_float16" — GPU, good balance
#   "int8"     — CPU or GPU, 4x smaller, minimal quality loss for Whisper
#   "float32"  — CPU, full precision, slowest

# For CPU (Mac Apple Silicon, no GPU):
model = WhisperModel("NbAiLab/nb-whisper-medium", device="cpu", compute_type="int8")
# CTranslate2 does NOT support Metal/MPS. CPU+int8 is the fastest option on Mac.
```

### Hugging Face model IDs for NorskTale:
- `NbAiLab/nb-whisper-small`   — fast, lower accuracy
- `NbAiLab/nb-whisper-medium`  — recommended default
- `NbAiLab/nb-whisper-large`   — best accuracy, slowest
- `openai/whisper-large-v3`    — multilingual fallback

---

## transcribe() — Key Parameters

```python
segments, info = model.transcribe(
    audio,                  # np.ndarray (float32, 16kHz) OR file path
    language="no",          # "no" for Norwegian. None = auto-detect (slower)
    beam_size=5,            # Higher = more accurate but slower. 1 = greedy
    best_of=5,              # Candidates for beam search
    vad_filter=True,        # Voice Activity Detection — filters silence
    vad_parameters=dict(    # Optional VAD tuning
        threshold=0.5,
        min_silence_duration_ms=500,
    ),
    word_timestamps=False,  # Set True if you need word-level timing
    condition_on_previous_text=True,  # Uses previous segments as context
    temperature=0.0,        # 0.0 = deterministic. Increase if getting loops
    no_speech_threshold=0.6, # Segments below this probability are skipped
)
```

### ⚠️ `segments` is a **generator**, not a list:

```python
# WRONG — does nothing until iterated
segments, info = model.transcribe(audio)
print(segments)  # Generator object

# CORRECT — iterate to get results
segments, info = model.transcribe(audio)
text = " ".join(segment.text for segment in segments).strip()

# Or collect all segments first (forces full inference):
segment_list = list(segments)
text = " ".join(s.text for s in segment_list).strip()
```

### Segment object fields:
```python
segment.text       # str — transcribed text for this segment
segment.start      # float — start time in seconds
segment.end        # float — end time in seconds
segment.avg_logprob  # float — confidence (-1.0 to 0.0, higher is better)
segment.no_speech_prob  # float — probability that segment contains no speech
```

### `info` object:
```python
info.language               # Detected language code ("no", "en", etc.)
info.language_probability   # Confidence of language detection
info.duration               # Total audio duration in seconds
```

---

## Audio Format Requirements

```python
import numpy as np

# faster-whisper expects: float32, mono, 16kHz
# Input from client: int16 PCM bytes (from NAudio at 16kHz mono)

def pcm_bytes_to_float32(pcm_bytes: bytes) -> np.ndarray:
    """Convert raw int16 PCM bytes to float32 array for faster-whisper."""
    audio = np.frombuffer(pcm_bytes, dtype=np.int16)
    return audio.astype(np.float32) / 32768.0

# Concatenating audio chunks:
buffer = np.array([], dtype=np.float32)
buffer = np.concatenate([buffer, pcm_bytes_to_float32(new_chunk)])

# Duration of buffer in seconds:
duration_sec = len(buffer) / 16000
```

---

## Sliding Window Streaming Strategy

faster-whisper is **not a streaming model** — it processes a fixed audio buffer. We simulate streaming:

```
buffer grows → inference every chunk_duration_sec → return "partial"
                                                   → trim buffer, keep overlap
hotkey released → final inference on remainder → return "final"
```

```python
class StreamingTranscriber:
    def __init__(self, model_id: str, device: str, compute_type: str,
                 language: str = "no", beam_size: int = 5,
                 chunk_duration: float = 2.0, overlap_sec: float = 0.5,
                 vad_enabled: bool = True):
        self.model = WhisperModel(model_id, device=device, compute_type=compute_type)
        self.audio_buffer = np.array([], dtype=np.float32)
        self.chunk_duration = chunk_duration
        self.overlap_sec = overlap_sec
        self.language = language
        self.beam_size = beam_size
        self.vad_enabled = vad_enabled
        self.segment_id = 0

    def add_audio(self, pcm_bytes: bytes) -> dict | None:
        chunk = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.float32) / 32768.0
        self.audio_buffer = np.concatenate([self.audio_buffer, chunk])

        if len(self.audio_buffer) / 16000 >= self.chunk_duration:
            return self._run_inference(is_final=False)
        return None

    def finalize(self) -> dict | None:
        if len(self.audio_buffer) > 0:
            return self._run_inference(is_final=True)
        return None

    def reset(self):
        self.audio_buffer = np.array([], dtype=np.float32)
        self.segment_id = 0

    def _run_inference(self, is_final: bool) -> dict:
        segments, _ = self.model.transcribe(
            self.audio_buffer,
            language=self.language,
            beam_size=self.beam_size,
            vad_filter=self.vad_enabled,
        )
        # MUST consume generator before trimming buffer
        text = " ".join(s.text for s in segments).strip()
        self.segment_id += 1

        if is_final:
            self.audio_buffer = np.array([], dtype=np.float32)
            return {"type": "final", "text": text, "segment_id": self.segment_id}
        else:
            # Keep overlap for context
            overlap_samples = int(self.overlap_sec * 16000)
            self.audio_buffer = self.audio_buffer[-overlap_samples:]
            return {"type": "partial", "text": text, "segment_id": self.segment_id}
```

---

## VAD (Voice Activity Detection)

```python
# Built-in Silero VAD
segments, info = model.transcribe(
    audio,
    vad_filter=True,
    vad_parameters=dict(
        threshold=0.5,             # Speech probability threshold (0-1)
        min_speech_duration_ms=250, # Minimum speech segment
        max_speech_duration_s=30,  # Max segment before forced split
        min_silence_duration_ms=500, # Silence duration to split segments
        speech_pad_ms=400,         # Padding added around speech
    )
)
```

VAD removes silent audio before inference — dramatically reduces hallucinations on quiet input.

---

## Model Switching at Runtime

```python
import gc
import torch  # only if using CUDA

def switch_model(self, model_id: str):
    """Switch to a different Whisper model."""
    # 1. Release old model
    del self.model
    if torch.cuda.is_available():
        torch.cuda.empty_cache()
    gc.collect()

    # 2. Load new model
    self.model = WhisperModel(model_id, device=self.device, compute_type=self.compute_type)
    self.current_model_id = model_id
```

---

## Common Pitfalls

| Pitfall | Fix |
|---------|-----|
| `segments` never iterated | Always consume the generator: `list(segments)` or `for s in segments` |
| Audio not float32 | Convert: `arr.astype(np.float32) / 32768.0` |
| Hallucinations on silence | Enable `vad_filter=True` |
| Repeated text loops | Set `temperature=0.2` or `condition_on_previous_text=False` |
| Metal/MPS on Mac | Not supported — use `device="cpu", compute_type="int8"` |
| VRAM OOM | Use `compute_type="int8"` or smaller model |
| Slow first inference | Model loads lazily — first call initializes CUDA kernels, warm up on startup |

---

## FastAPI WebSocket Integration

```python
@app.websocket("/ws/transcribe")
async def websocket_transcribe(websocket: WebSocket):
    await websocket.accept()
    transcriber = StreamingTranscriber(...)

    try:
        while True:
            message = await websocket.receive()

            if "bytes" in message:
                # Run inference in thread pool to avoid blocking event loop
                result = await asyncio.get_event_loop().run_in_executor(
                    None, transcriber.add_audio, message["bytes"]
                )
                if result:
                    await websocket.send_json(result)

            elif "text" in message:
                data = json.loads(message["text"])
                if data.get("type") == "audio_stop":
                    result = await asyncio.get_event_loop().run_in_executor(
                        None, transcriber.finalize
                    )
                    if result:
                        await websocket.send_json(result)
                    break

    except WebSocketDisconnect:
        pass
```

**Important:** Run `transcribe()` in `run_in_executor` — it's CPU-bound and will block the asyncio event loop if called directly.
