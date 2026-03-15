import logging
import re
from pathlib import Path

import yaml

logger = logging.getLogger(__name__)

_CORRECTIONS_FILE = Path(__file__).parent / "corrections.yaml"


def load_corrections() -> tuple[list[tuple[re.Pattern, str]], list[tuple[str, str]]]:
    """Load word corrections from corrections.yaml.

    Returns a tuple of two lists:
    - regex_corrections: (pattern, replacement) pairs applied via regex substitution
    - full_segment_corrections: (wrong, correct) pairs applied only when the entire
      transcription text matches 'wrong' (case-insensitive, after stripping whitespace)
    """
    if not _CORRECTIONS_FILE.exists():
        return [], []
    try:
        with open(_CORRECTIONS_FILE, encoding="utf-8") as f:
            data = yaml.safe_load(f)
        regex_corrections: list[tuple[re.Pattern, str]] = []
        full_segment_corrections: list[tuple[str, str]] = []
        for item in data.get("corrections", []):
            wrong   = str(item.get("wrong",   "")).strip()
            correct = str(item.get("correct", ""))   # do NOT strip — value may be "\n" etc.
            if not wrong or correct == "":
                continue
            if item.get("full_segment"):
                full_segment_corrections.append((wrong, correct))
            else:
                pattern = re.compile(r"(?<!\w)" + re.escape(wrong) + r"(?!\w)", re.IGNORECASE)
                regex_corrections.append((pattern, correct))
        logger.info(
            "Loaded %d correction(s), %d full-segment correction(s) from corrections.yaml",
            len(regex_corrections), len(full_segment_corrections),
        )
        return regex_corrections, full_segment_corrections
    except Exception:
        logger.exception("Failed to load corrections.yaml")
        return [], []


def apply(text: str, corrections: list[tuple[re.Pattern, str]]) -> str:
    for pattern, replacement in corrections:
        text = pattern.sub(replacement, text)
    return text


def _normalize_for_matching(text: str) -> str:
    """Strip punctuation and normalize whitespace for full-segment matching."""
    text = re.sub(r"[,\.!?]+", "", text)
    text = re.sub(r"\s+", " ", text).strip()
    return text.lower()


def apply_full_segment(text: str, corrections: list[tuple[str, str]]) -> str:
    """Replace text wholesale if it matches a full-segment correction.

    Matching is case-insensitive and ignores punctuation (,.!?), so
    'Enter.' and 'Enter!' both match wrong='Enter', and 'Enter, enter.'
    matches wrong='Enter Enter'.
    """
    normalized = _normalize_for_matching(text)
    for wrong, correct in corrections:
        if normalized == _normalize_for_matching(wrong):
            return correct
    return text
