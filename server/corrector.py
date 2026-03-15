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
    - full_sentence_corrections: (wrong, correct) pairs matched against either the
      entire text or the last sentence of the text (case-insensitive, punctuation-ignored)
    """
    if not _CORRECTIONS_FILE.exists():
        return [], []
    try:
        with open(_CORRECTIONS_FILE, encoding="utf-8") as f:
            data = yaml.safe_load(f)
        regex_corrections: list[tuple[re.Pattern, str]] = []
        full_sentence_corrections: list[tuple[str, str]] = []
        for item in data.get("corrections", []):
            wrong   = str(item.get("wrong",   "")).strip()
            correct = str(item.get("correct", ""))   # do NOT strip — value may be "\n" etc.
            if not wrong or correct == "":
                continue
            if item.get("full_sentence"):
                full_sentence_corrections.append((wrong, correct))
            else:
                pattern = re.compile(r"(?<!\w)" + re.escape(wrong) + r"(?!\w)", re.IGNORECASE)
                regex_corrections.append((pattern, correct))
        logger.info(
            "Loaded %d correction(s), %d full-sentence correction(s) from corrections.yaml",
            len(regex_corrections), len(full_sentence_corrections),
        )
        return regex_corrections, full_sentence_corrections
    except Exception:
        logger.exception("Failed to load corrections.yaml")
        return [], []


def apply(text: str, corrections: list[tuple[re.Pattern, str]]) -> str:
    for pattern, replacement in corrections:
        text = pattern.sub(replacement, text)
    return text


def _normalize_for_matching(text: str) -> str:
    """Strip punctuation and normalize whitespace for full-sentence matching."""
    text = re.sub(r"[,\.!?]+", "", text)
    text = re.sub(r"\s+", " ", text).strip()
    return text.lower()


# Splits on sentence-ending punctuation followed by whitespace OR end of string.
# Produces alternating [sentence, delimiter, sentence, delimiter, ..., sentence].
# Using (?:\s+|$) ensures trailing punctuation (e.g. the "." in "Enter.") is always
# captured as a delimiter rather than left attached to the sentence content.
_SENTENCE_SPLIT_RE = re.compile(r"([.!?]+(?:\s+|$))")


def apply_full_sentence(text: str, corrections: list[tuple[str, str]]) -> str:
    """Apply full-sentence corrections to any sentence within the text.

    Splits the text on sentence boundaries and checks each sentence independently.
    Matching is case-insensitive and ignores punctuation (,.!?).

    Examples with wrong='Enter', correct='\\n':
      "Enter."                                          → "\\n"
      "Dette fungerte jo fint. Enter."                  → "Dette fungerte jo fint.\\n"
      "Dette er første avsnitt. Enter. Andre avsnitt."  → "Dette er første avsnitt.\\nAndre avsnitt."
    """
    parts = _SENTENCE_SPLIT_RE.split(text.strip())
    # parts alternates: sentence, delimiter, sentence, delimiter, ..., sentence

    result: list[str] = []
    changed = False
    i = 0
    while i < len(parts):
        sentence  = parts[i]
        delimiter = parts[i + 1] if i + 1 < len(parts) else ""
        normalized = _normalize_for_matching(sentence)

        matched_correct = None
        for wrong, correct in corrections:
            if normalized == _normalize_for_matching(wrong):
                matched_correct = correct
                break

        if matched_correct is not None:
            # Strip trailing space from the preceding delimiter so we don't get
            # "first sentence. \n" with a stray space before the correction.
            if result and result[-1].endswith(" "):
                result[-1] = result[-1].rstrip()
            result.append(matched_correct)
            # Drop the delimiter that trails the matched sentence
            changed = True
        else:
            result.append(sentence)
            if delimiter:
                result.append(delimiter)

        i += 2

    return "".join(result) if changed else text
