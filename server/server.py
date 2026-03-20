import asyncio
import json
import logging
import os
import tempfile
import time
from contextlib import asynccontextmanager

from fastapi import FastAPI, File, Request, UploadFile, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse

from config import load_config, AppConfig
from corrector import apply, apply_full_sentence, load_corrections
from transcriber import StreamingTranscriber

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)-8s %(name)s — %(message)s",
)
logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# App startup / shutdown
# ---------------------------------------------------------------------------

config: AppConfig
transcriber: StreamingTranscriber
corrections: list
full_sentence_corrections: list


@asynccontextmanager
async def lifespan(app: FastAPI):
    global config, transcriber, corrections, full_sentence_corrections
    config = load_config()
    transcriber = StreamingTranscriber(config)
    corrections, full_sentence_corrections = load_corrections()
    yield
    logger.info("Server shutting down.")


app = FastAPI(title="LocalWhisperer Transcription Server", lifespan=lifespan)

# ---------------------------------------------------------------------------
# REST endpoints
# ---------------------------------------------------------------------------


@app.get("/health")
async def health():
    return {
        "status": "ok",
        "model": transcriber.current_model_id,
        "device": transcriber._device,
        "compute_type": transcriber._compute_type,
    }


@app.get("/models")
async def list_models():
    return [
        {
            "id": m.id,
            "name": m.name,
            "loaded": m.id == transcriber.current_model_id,
        }
        for m in config.models
    ]


@app.post("/models/switch")
async def switch_model(body: dict):
    model_id = body.get("model_id")
    if not model_id:
        return JSONResponse(status_code=400, content={"error": "model_id required"})

    known_ids = {m.id for m in config.models}
    if model_id not in known_ids:
        return JSONResponse(
            status_code=404,
            content={"error": f"Unknown model '{model_id}'. Known: {sorted(known_ids)}"},
        )

    loop = asyncio.get_running_loop()
    await loop.run_in_executor(None, transcriber.switch_model, model_id)
    return {"status": "ok", "model": transcriber.current_model_id}


@app.get("/config")
async def get_config():
    return config.model_dump()


@app.post("/config/streaming")
async def set_streaming_config(body: dict):
    vad_enabled = body.get("vad_enabled")
    vad_threshold = body.get("vad_threshold")

    if vad_enabled is None and vad_threshold is None:
        return JSONResponse(status_code=400, content={"error": "vad_enabled or vad_threshold required"})

    enabled = bool(vad_enabled) if vad_enabled is not None else transcriber.vad_enabled
    threshold = float(vad_threshold) if vad_threshold is not None else transcriber.vad_threshold

    if not 0.0 <= threshold <= 1.0:
        return JSONResponse(status_code=400, content={"error": "vad_threshold must be between 0.0 and 1.0"})

    transcriber.set_vad_config(enabled, threshold)
    return {"status": "ok", "vad_enabled": transcriber.vad_enabled, "vad_threshold": transcriber.vad_threshold}


@app.post("/config/calibrate")
async def calibrate_vad(request: Request):
    pcm_bytes = await request.body()
    if len(pcm_bytes) < 8000:  # ~0.25 s at 16 kHz 16-bit mono
        return JSONResponse(status_code=400, content={"error": "Audio too short (minimum ~0.5 s)"})

    loop = asyncio.get_running_loop()
    result = await loop.run_in_executor(None, transcriber.analyze_noise, pcm_bytes)
    return result


ALLOWED_EXTENSIONS = {".wav", ".mp3", ".m4a", ".ogg", ".flac", ".webm", ".wma", ".aac"}
MAX_FILE_SIZE = 100 * 1024 * 1024  # 100 MB


@app.post("/transcribe/file")
async def transcribe_file(file: UploadFile = File(...)):
    ext = os.path.splitext(file.filename or "")[1].lower()
    if ext not in ALLOWED_EXTENSIONS:
        return JSONResponse(
            status_code=400,
            content={"error": f"Unsupported file type '{ext}'. Allowed: {sorted(ALLOWED_EXTENSIONS)}"},
        )

    tmp_path = None
    try:
        data = await file.read()
        if len(data) > MAX_FILE_SIZE:
            return JSONResponse(status_code=400, content={"error": "File too large (max 100 MB)"})

        with tempfile.NamedTemporaryFile(delete=False, suffix=ext) as tmp:
            tmp.write(data)
            tmp_path = tmp.name

        loop = asyncio.get_running_loop()
        t0 = time.perf_counter()
        result = await loop.run_in_executor(None, transcriber.transcribe_file, tmp_path)
        elapsed_ms = round((time.perf_counter() - t0) * 1000)

        if result.get("text"):
            result["text"] = apply(result["text"], corrections)
            result["text"] = apply_full_sentence(result["text"], full_sentence_corrections)
        result["processing_time_ms"] = elapsed_ms
        return result
    finally:
        if tmp_path and os.path.exists(tmp_path):
            os.unlink(tmp_path)


# ---------------------------------------------------------------------------
# WebSocket endpoint
# ---------------------------------------------------------------------------


@app.websocket("/ws/transcribe")
async def websocket_transcribe(websocket: WebSocket):
    await websocket.accept()
    client = websocket.client
    logger.info("Client connected: %s", client)

    transcriber.reset()
    loop = asyncio.get_running_loop()

    # Queue decouples the receive loop from inference.
    # Audio chunks (bytes) and control strings ("audio_stop", "close") are enqueued
    # here and processed by the worker, so the receive loop is never blocked by
    # slow CPU inference.
    queue: asyncio.Queue[bytes | str] = asyncio.Queue()

    async def inference_worker() -> None:
        while True:
            item = await queue.get()

            if item == "close":
                break

            if item == "audio_stop":
                logger.info("audio_stop received — running final inference")
                t0 = time.perf_counter()
                try:
                    result = await loop.run_in_executor(None, transcriber.finalize)
                except Exception:
                    logger.exception("finalize() raised an exception")
                    result = None
                # Always send a final message so the client exits "processing" state,
                # even when VAD removes all audio and text is empty.
                final = result or {"type": "final", "text": ""}
                if final.get("text"):
                    final["text"] = apply(final["text"], corrections)
                    final["text"] = apply_full_sentence(final["text"], full_sentence_corrections)
                final["processing_time_ms"] = round((time.perf_counter() - t0) * 1000)
                try:
                    await websocket.send_json(final)
                except Exception:
                    break
                transcriber.reset()
                continue  # Stay alive for the next recording session

            # Binary audio chunk — just accumulate, no inference until audio_stop
            await loop.run_in_executor(None, transcriber.add_audio, item)

    worker_task = asyncio.create_task(inference_worker())

    try:
        while True:
            message = await websocket.receive()

            if message.get("type") == "websocket.disconnect":
                logger.info("Client disconnected: %s", client)
                break

            if "bytes" in message:
                await queue.put(message["bytes"])

            elif "text" in message:
                try:
                    data = json.loads(message["text"])
                except json.JSONDecodeError:
                    logger.warning("Invalid JSON from client: %r", message["text"])
                    continue

                if data.get("type") == "audio_stop":
                    await queue.put("audio_stop")

    except WebSocketDisconnect:
        logger.info("Client disconnected: %s", client)
    except Exception:
        logger.exception("Unexpected error in WebSocket handler")
    finally:
        await queue.put("close")
        worker_task.cancel()
        transcriber.reset()
