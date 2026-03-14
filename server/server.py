import asyncio
import json
import logging
import time
from contextlib import asynccontextmanager

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse

from config import load_config, AppConfig
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


@asynccontextmanager
async def lifespan(app: FastAPI):
    global config, transcriber
    config = load_config()
    transcriber = StreamingTranscriber(config)
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

    loop = asyncio.get_event_loop()
    await loop.run_in_executor(None, transcriber.switch_model, model_id)
    return {"status": "ok", "model": transcriber.current_model_id}


@app.get("/config")
async def get_config():
    return config.model_dump()


# ---------------------------------------------------------------------------
# WebSocket endpoint
# ---------------------------------------------------------------------------


@app.websocket("/ws/transcribe")
async def websocket_transcribe(websocket: WebSocket):
    await websocket.accept()
    client = websocket.client
    logger.info("Client connected: %s", client)

    transcriber.reset()
    loop = asyncio.get_event_loop()

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
