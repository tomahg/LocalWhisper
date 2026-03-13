"""
Simple test client — streams a WAV file to the server as PCM chunks.

Usage:
    python test_client.py                        # uses built-in test tone
    python test_client.py path/to/audio.wav      # streams a real WAV file
    python test_client.py --url ws://192.168.1.x:8765/ws/transcribe audio.wav
"""

import argparse
import asyncio
import json
import struct
import sys
import wave
from pathlib import Path

import websockets


CHUNK_DURATION_SEC = 0.5   # How much audio to send per chunk
SAMPLE_RATE = 16000
BYTES_PER_SAMPLE = 2       # int16
CHUNK_BYTES = int(SAMPLE_RATE * CHUNK_DURATION_SEC * BYTES_PER_SAMPLE)


def read_wav_as_pcm(path: Path) -> bytes:
    """Read a WAV file and return raw int16 mono 16kHz PCM bytes."""
    import numpy as np

    with wave.open(str(path), "rb") as wf:
        channels = wf.getnchannels()
        sampwidth = wf.getsampwidth()
        framerate = wf.getframerate()
        pcm = wf.readframes(wf.getnframes())

    if sampwidth != 2:
        print(f"WARNING: Expected 16-bit audio, got {sampwidth*8}-bit. Results may be poor.")

    # Decode to float32 for processing
    arr = np.frombuffer(pcm, dtype=np.int16).astype(np.float32)

    # Downmix to mono
    if channels != 1:
        arr = arr.reshape(-1, channels).mean(axis=1)
        print(f"INFO: Downmixed {channels} channels to mono.")

    # Resample to 16kHz if needed
    if framerate != SAMPLE_RATE:
        target_length = int(len(arr) * SAMPLE_RATE / framerate)
        arr = np.interp(
            np.linspace(0, len(arr) - 1, target_length),
            np.arange(len(arr)),
            arr,
        )
        print(f"INFO: Resampled from {framerate}Hz to {SAMPLE_RATE}Hz.")

    return arr.astype(np.int16).tobytes()


def generate_silence(duration_sec: float = 3.0) -> bytes:
    """Generate silent PCM for basic connectivity testing."""
    n_samples = int(SAMPLE_RATE * duration_sec)
    return struct.pack(f"<{n_samples}h", *([0] * n_samples))


async def stream(url: str, pcm: bytes) -> None:
    print(f"Connecting to {url} ...")
    async with websockets.connect(url) as ws:
        print(f"Connected. Streaming {len(pcm)} bytes in {CHUNK_BYTES}-byte chunks ...\n")

        offset = 0
        chunk_count = 0
        while offset < len(pcm):
            chunk = pcm[offset : offset + CHUNK_BYTES]
            offset += CHUNK_BYTES
            chunk_count += 1
            await ws.send(chunk)

            # Check for partial results (non-blocking)
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=0.05)
                result = json.loads(msg)
                print(f"[{result['type']:7}] {result['text']!r}  ({result.get('processing_time_ms', '?')}ms)")
            except asyncio.TimeoutError:
                pass

            # Simulate real-time pacing
            await asyncio.sleep(CHUNK_DURATION_SEC)

        # Signal end of audio
        print(f"\nSent {chunk_count} chunks. Sending audio_stop ...")
        await ws.send(json.dumps({"type": "audio_stop"}))

        # Wait for final result
        try:
            msg = await asyncio.wait_for(ws.recv(), timeout=30.0)
            result = json.loads(msg)
            print(f"\n[{result['type']:7}] {result['text']!r}  ({result.get('processing_time_ms', '?')}ms)")
        except asyncio.TimeoutError:
            print("Timed out waiting for final result.")

    print("\nDone.")


def main() -> None:
    parser = argparse.ArgumentParser(description="NorskTale test client")
    parser.add_argument("wav_file", nargs="?", help="WAV file to stream (optional)")
    parser.add_argument(
        "--url",
        default="ws://localhost:8765/ws/transcribe",
        help="WebSocket URL (default: ws://localhost:8765/ws/transcribe)",
    )
    args = parser.parse_args()

    if args.wav_file:
        path = Path(args.wav_file)
        if not path.exists():
            print(f"ERROR: File not found: {path}", file=sys.stderr)
            sys.exit(1)
        print(f"Reading {path} ...")
        pcm = read_wav_as_pcm(path)
    else:
        print("No WAV file given — streaming 3 seconds of silence (connectivity test).")
        pcm = generate_silence(3.0)

    asyncio.run(stream(args.url, pcm))


if __name__ == "__main__":
    main()
