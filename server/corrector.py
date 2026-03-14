import logging
import re
from pathlib import Path

import yaml

logger = logging.getLogger(__name__)

_CORRECTIONS_FILE = Path(__file__).parent / "corrections.yaml"


def load_corrections() -> list[tuple[re.Pattern, str]]:
    """Load word corrections from corrections.yaml.
    Returns compiled (pattern, replacement) pairs, or empty list if file is absent."""
    if not _CORRECTIONS_FILE.exists():
        return []
    try:
        with open(_CORRECTIONS_FILE, encoding="utf-8") as f:
            data = yaml.safe_load(f)
        result = []
        for item in data.get("corrections", []):
            wrong   = str(item.get("wrong",   "")).strip()
            correct = str(item.get("correct", "")).strip()
            if not wrong or not correct:
                continue
            # Match whole words, case-insensitive
            pattern = re.compile(r"(?<!\w)" + re.escape(wrong) + r"(?!\w)", re.IGNORECASE)
            result.append((pattern, correct))
        logger.info("Loaded %d correction(s) from corrections.yaml", len(result))
        return result
    except Exception:
        logger.exception("Failed to load corrections.yaml")
        return []


def apply(text: str, corrections: list[tuple[re.Pattern, str]]) -> str:
    for pattern, replacement in corrections:
        text = pattern.sub(replacement, text)
    return text
