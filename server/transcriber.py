import gc
import logging
import os

import numpy as np
from faster_whisper import WhisperModel

# Ensure huggingface_hub shows download progress bars in the terminal
os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "0")

from config import AppConfig, TranscriptionConfig, StreamingConfig, load_huggingface_token

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
        sc: StreamingConfig = config.streaming

        self.language = tc.language
        self.beam_size = tc.beam_size
        self.chunk_duration = sc.chunk_duration_sec
        self.overlap_sec = sc.overlap_sec
        self.vad_enabled = sc.vad_enabled
        self.vad_threshold = sc.vad_threshold

        self._device = _resolve_device(tc.device)
        self._compute_type = tc.compute_type
        self._current_model_id = tc.default_model

        self.audio_buffer = np.array([], dtype=np.float32)
        self.segment_id = 0
        self._prompt = ""  # Accumulated text used as initial_prompt for next window

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

    def add_audio(self, pcm_bytes: bytes) -> dict | None:
        """Add a raw int16 PCM chunk. Returns a partial result if enough audio
        has accumulated, otherwise None."""
        chunk = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.float32) / 32768.0
        self.audio_buffer = np.concatenate([self.audio_buffer, chunk])

        if len(self.audio_buffer) / SAMPLE_RATE >= self.chunk_duration:
            return self._run_inference(is_final=False)
        return None

    def finalize(self) -> dict | None:
        """Return the complete session transcript.

        If partials have accumulated text, return that directly — the last
        overlap window was already captured by the most recent partial, so
        re-running inference on it would only cause hallucinations or
        duplicated words.

        For short utterances where no partial fired yet, run a single
        inference on the full buffer.
        """
        if self._prompt:
            # Normal case: partials already captured the session text.
            # The last overlap window is already included — don't re-run inference.
            self.audio_buffer = np.array([], dtype=np.float32)
            self.segment_id += 1
            text = self._prompt
            self._prompt = ""
            logger.debug("Final (from prompt) [%d]: %r", self.segment_id, text)
            return {"type": "final", "text": text, "segment_id": self.segment_id}

        # Short utterance: no partials fired yet — run a single inference.
        if len(self.audio_buffer) == 0:
            return None
        return self._run_inference(is_final=True)

    def reset(self) -> None:
        self.audio_buffer = np.array([], dtype=np.float32)
        self.segment_id = 0
        self._prompt = ""

    # ------------------------------------------------------------------
    # Inference
    # ------------------------------------------------------------------

    def _run_inference(self, is_final: bool) -> dict:
        vad_params = {"threshold": self.vad_threshold} if self.vad_enabled else {}

        # Pass previous text as initial_prompt so Whisper has context for the
        # current window — greatly reduces boundary errors and hallucinations.
        # Keep the prompt to the last ~224 tokens (Whisper's context limit).
        prompt = self._prompt[-200:] if self._prompt else None

        segments, _ = self.model.transcribe(
            self.audio_buffer,
            language=self.language,
            beam_size=self.beam_size,
            vad_filter=self.vad_enabled,
            vad_parameters=vad_params or None,
            initial_prompt=prompt,
            no_speech_threshold=0.6,
            repetition_penalty=1.3,
        )
        # IMPORTANT: consume generator before touching audio_buffer
        text = " ".join(s.text for s in segments).strip()
        self.segment_id += 1

        if is_final:
            self._prompt = ""
            self.audio_buffer = np.array([], dtype=np.float32)
            logger.debug("Final result [%d]: %r", self.segment_id, text)
            return {"type": "final", "text": text, "segment_id": self.segment_id}
        else:
            # Accumulate into the running session transcript.
            if text:
                self._prompt = (self._prompt + " " + text).strip()
                overlap_samples = int(self.overlap_sec * SAMPLE_RATE)
                self.audio_buffer = self.audio_buffer[-overlap_samples:]
            else:
                # VAD removed all audio — no speech to use as context.
                # Clearing the buffer breaks the silence feedback loop where
                # silence overlap fills the buffer → inference → VAD removes all
                # → keeps silence overlap → fills buffer again → repeat forever.
                self.audio_buffer = np.array([], dtype=np.float32)
                return None
            logger.debug("Partial result [%d]: %r", self.segment_id, self._prompt)
            return {"type": "partial", "text": self._prompt, "segment_id": self.segment_id}

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
