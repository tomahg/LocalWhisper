import logging
from pathlib import Path
from typing import Literal

import yaml
from pydantic import BaseModel, field_validator

logger = logging.getLogger(__name__)

CONFIG_PATH = Path(__file__).parent / "config.yaml"
SECRETS_PATH = Path(__file__).parent / "secrets.yaml"


class ServerConfig(BaseModel):
    host: str = "0.0.0.0"
    port: int = 8765


class TranscriptionConfig(BaseModel):
    default_model: str = "NbAiLab/nb-whisper-medium"
    language: str = "no"
    beam_size: int = 5
    device: Literal["auto", "cuda", "cpu"] = "cpu"
    compute_type: Literal["int8", "float16", "int8_float16", "float32"] = "int8"

    @field_validator("device")
    @classmethod
    def validate_device(cls, v: str) -> str:
        if v not in ("auto", "cuda", "cpu"):
            raise ValueError(f"device must be 'auto', 'cuda', or 'cpu', got '{v}'")
        return v

    @field_validator("compute_type")
    @classmethod
    def validate_compute_type(cls, v: str) -> str:
        valid = ("int8", "float16", "int8_float16", "float32")
        if v not in valid:
            raise ValueError(f"compute_type must be one of {valid}, got '{v}'")
        return v


class StreamingConfig(BaseModel):
    vad_enabled: bool = True
    vad_threshold: float = 0.5


class ModelInfo(BaseModel):
    id: str
    name: str


class AppConfig(BaseModel):
    server: ServerConfig = ServerConfig()
    transcription: TranscriptionConfig = TranscriptionConfig()
    streaming: StreamingConfig = StreamingConfig()
    models: list[ModelInfo] = []


def load_huggingface_token() -> str | None:
    """Load HuggingFace token from secrets.yaml, if present."""
    if not SECRETS_PATH.exists():
        return None
    with open(SECRETS_PATH, encoding="utf-8") as f:
        secrets = yaml.safe_load(f) or {}
    token = secrets.get("huggingface_token")
    if token and not token.startswith("hf_YOUR"):
        return token
    return None


def load_config(path: Path = CONFIG_PATH) -> AppConfig:
    if not path.exists():
        logger.warning("config.yaml not found, using defaults")
        return AppConfig()

    with open(path, encoding="utf-8") as f:
        raw = yaml.safe_load(f)

    config = AppConfig(**raw)
    logger.info(
        "Config loaded: device=%s compute_type=%s model=%s",
        config.transcription.device,
        config.transcription.compute_type,
        config.transcription.default_model,
    )
    return config
