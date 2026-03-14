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

        logger.info(
            "Loading model '%s' on %s with compute_type=%s ...",
            self._current_model_id, self._device, self._compute_type,
        )
        token = load_huggingface_token()
        if token:
            from huggingface_hub import login
            login(token=token, add_to_git_credential=False)
            logger.info("Logged in to HuggingFace with token from secrets.yaml")

        print(f"Downloading / loading model '{self._current_model_id}' — this may take a few minutes on first run...", flush=True)
        self.model = WhisperModel(
            self._current_model_id,
            device=self._device,
            compute_type=self._compute_type,
        )
        print("Model ready.", flush=True)
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
            text = " ".join(s.text for s in segments).strip()
        self.segment_id += 1
        self.audio_buffer = np.array([], dtype=np.float32)

        logger.debug("Final result [%d]: %r", self.segment_id, text)
        return {"type": "final", "text": text, "segment_id": self.segment_id}

    def transcribe_file(self, file_path: str) -> dict:
        """Transcribe an audio file directly (independent of the streaming buffer)."""
        vad_params = {"threshold": self.vad_threshold} if self.vad_enabled else {}

        with self._inference_lock:
            segments, _ = self.model.transcribe(
                file_path,
                language=self.language,
                beam_size=self.beam_size,
                vad_filter=self.vad_enabled,
                vad_parameters=vad_params or None,
                no_speech_threshold=0.6,
                repetition_penalty=1.3,
            )
            text = " ".join(s.text for s in segments).strip()
        self.segment_id += 1

        logger.info("File transcription [%d]: %r", self.segment_id, text)
        return {"type": "final", "text": text, "segment_id": self.segment_id}

    # ------------------------------------------------------------------
    # Model switching
    # ------------------------------------------------------------------

    @property
    def current_model_id(self) -> str:
        return self._current_model_id

    def switch_model(self, model_id: str) -> None:
        if model_id == self._current_model_id:
            return
        logger.info("Switching model: %s → %s", self._current_model_id, model_id)
        del self.model
        try:
            import torch
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except ImportError:
            pass
        gc.collect()

        self.model = WhisperModel(
            model_id,
            device=self._device,
            compute_type=self._compute_type,
        )
        self._current_model_id = model_id
        self.reset()
        logger.info("Model switched to '%s'", model_id)
