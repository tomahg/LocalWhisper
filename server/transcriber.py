import gc
import glob
import logging
import os
import threading

# Python 3.8+ no longer searches PATH for DLL dependencies of extension modules.
# Explicitly register CUDA bin directories so ctranslate2 can find cublas etc.
for _cuda_bin in glob.glob(r"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v*\bin"):
    os.add_dll_directory(_cuda_bin)

import numpy as np
from faster_whisper import WhisperModel

# Ensure huggingface_hub shows download progress bars in the terminal
os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "0")

from config import AppConfig, TranscriptionConfig, load_huggingface_token

logger = logging.getLogger(__name__)

SAMPLE_RATE = 16000

_TOKENS_TO_STRIP = ["<|nocaptions|>"]


def _strip_tokens(text: str) -> str:
    for token in _TOKENS_TO_STRIP:
        text = text.replace(token, "")
    return text.strip()


def _resolve_device(device: str) -> str:
    """Resolve 'auto' to 'cuda' or 'cpu' based on availability."""
    if device != "auto":
        return device
    try:
        import torch
        if torch.cuda.is_available():
            logger.info("Auto-detected CUDA — using GPU")
            return "cuda"
    except ImportError:
        pass
    logger.info("Auto-detected no CUDA — using CPU")
    return "cpu"


class StreamingTranscriber:
    def __init__(self, config: AppConfig) -> None:
        tc: TranscriptionConfig = config.transcription

        self.language = tc.language
        self.beam_size = tc.beam_size
        self.vad_enabled = config.streaming.vad_enabled
        self.vad_threshold = config.streaming.vad_threshold

        self._device = _resolve_device(tc.device)
        self._compute_type = tc.compute_type
        self._current_model_id = tc.default_model

        self.audio_buffer = np.array([], dtype=np.float32)
        self.segment_id = 0
        self._inference_lock = threading.Lock()
        self._switch_lock    = threading.Lock()

        logger.info(
            "Loading model '%s' on %s with compute_type=%s ...",
            self._current_model_id, self._device, self._compute_type,
        )
        token = load_huggingface_token()
        if token:
            from huggingface_hub import login
            login(token=token, add_to_git_credential=False)
            logger.info("Logged in to HuggingFace with token from secrets.yaml")

        logger.info(
            "Downloading / loading model '%s' — this may take a few minutes on first run...",
            self._current_model_id,
        )
        self.model = WhisperModel(
            self._current_model_id,
            device=self._device,
            compute_type=self._compute_type,
        )
        logger.info("Model loaded.")

    # ------------------------------------------------------------------
    # Audio ingestion
    # ------------------------------------------------------------------

    def add_audio(self, pcm_bytes: bytes) -> None:
        """Accumulate raw int16 PCM chunks. Inference runs only on finalize()."""
        chunk = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.float32) / 32768.0
        self.audio_buffer = np.concatenate([self.audio_buffer, chunk])

    def finalize(self) -> dict | None:
        """Transcribe the complete session buffer in a single inference pass."""
        if len(self.audio_buffer) == 0:
            return None
        return self._run_inference()

    def reset(self) -> None:
        self.audio_buffer = np.array([], dtype=np.float32)
        self.segment_id = 0

    # ------------------------------------------------------------------
    # Inference
    # ------------------------------------------------------------------

    def _run_inference(self) -> dict:
        vad_params = {"threshold": self.vad_threshold} if self.vad_enabled else {}

        with self._inference_lock:
            segments, _ = self.model.transcribe(
                self.audio_buffer,
                language=self.language,
                beam_size=self.beam_size,
                vad_filter=self.vad_enabled,
                vad_parameters=vad_params or None,
                no_speech_threshold=0.6,
                repetition_penalty=1.3,
            )
            # IMPORTANT: consume generator before touching audio_buffer
            text = _strip_tokens(" ".join(s.text for s in segments))
        self.segment_id += 1
        self.audio_buffer = np.array([], dtype=np.float32)

        logger.debug("Final result [%d]: %r", self.segment_id, text)
        return {"type": "final", "text": text, "segment_id": self.segment_id}

    def set_vad_config(self, vad_enabled: bool, vad_threshold: float) -> None:
        """Update VAD settings at runtime (called by /config/streaming endpoint)."""
        self.vad_enabled = vad_enabled
        self.vad_threshold = max(0.0, min(1.0, vad_threshold))
        logger.info("VAD config: enabled=%s threshold=%.2f", self.vad_enabled, self.vad_threshold)

    def analyze_noise(self, pcm_bytes: bytes) -> dict:
        """Analyze ambient noise to recommend a VAD threshold.

        Sweeps Silero VAD thresholds from 0.90 down to 0.10, stopping at the first
        threshold where the noise is classified as speech. The recommended threshold
        is one step (0.10) above that boundary so real speech still passes comfortably.

        Uses only the public transcribe() API — no access to faster-whisper internals.
        When VAD finds no speech the generator is immediately empty and Whisper never
        runs, so only the single boundary threshold triggers an actual inference pass.
        """
        audio = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.float32) / 32768.0

        if len(audio) < SAMPLE_RATE // 2:
            logger.warning("Noise sample too short for calibration (< 0.5 s)")
            return {"recommended_threshold": 0.5}

        boundary: float | None = None
        with self._inference_lock:
            for threshold in [0.90, 0.80, 0.70, 0.60, 0.50, 0.40, 0.30, 0.20, 0.10]:
                segments_gen, _ = self.model.transcribe(
                    audio,
                    language=self.language,
                    beam_size=1,
                    vad_filter=True,
                    vad_parameters={"threshold": threshold},
                )
                # Empty generator → VAD found no speech → Whisper never ran (fast path)
                if any(True for _ in segments_gen):
                    boundary = threshold
                    break

        if boundary is None:
            # Noise so quiet it's suppressed even at the most permissive threshold
            logger.info("Calibration: noise suppressed at all thresholds → recommended 0.15")
            return {"recommended_threshold": 0.15}

        recommended = round(min(boundary + 0.10, 0.95), 2)
        logger.info("Calibration: noise detected at %.2f → recommended %.2f", boundary, recommended)
        return {"recommended_threshold": recommended, "noise_detected_at": boundary}

    def transcribe_file(self, file_path: str) -> dict:
        """Transcribe an audio file directly (independent of the streaming buffer)."""
        vad_params = {"threshold": self.vad_threshold} if self.vad_enabled else {}

        with self._inference_lock:
            segments, info = self.model.transcribe(
                file_path,
                language=self.language,
                beam_size=self.beam_size,
                vad_filter=self.vad_enabled,
                vad_parameters=vad_params or None,
                no_speech_threshold=0.6,
                repetition_penalty=1.3,
            )
            text = _strip_tokens(" ".join(s.text for s in segments))
            duration_ms = round(info.duration * 1000)
        self.segment_id += 1

        logger.info("File transcription [%d]: %r", self.segment_id, text)
        return {"type": "final", "text": text, "segment_id": self.segment_id,
                "audio_duration_ms": duration_ms}

    # ------------------------------------------------------------------
    # Model switching
    # ------------------------------------------------------------------

    @property
    def current_model_id(self) -> str:
        return self._current_model_id

    @property
    def device(self) -> str:
        return self._device

    @property
    def compute_type(self) -> str:
        return self._compute_type

    def switch_model(self, model_id: str) -> None:
        with self._switch_lock:
            if model_id == self._current_model_id:
                return
            logger.info("Switching model: %s → %s", self._current_model_id, model_id)

            # Load new model while holding switch lock so concurrent calls queue up
            # rather than downloading the same model twice.
            new_model = WhisperModel(
                model_id,
                device=self._device,
                compute_type=self._compute_type,
            )

            # Acquire inference lock so we never swap the model mid-transcription.
            with self._inference_lock:
                old_model = self.model
                self.model = new_model
                self._current_model_id = model_id
                self.reset()

        del old_model
        try:
            import torch
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except ImportError:
            pass
        gc.collect()
        logger.info("Model switched to '%s'", model_id)
