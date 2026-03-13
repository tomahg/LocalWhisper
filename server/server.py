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

    # Each connection gets its own transcriber state
    transcriber.reset()

    loop = asyncio.get_event_loop()

    try:
        while True:
            message = await websocket.receive()

            if "bytes" in message:
                t0 = time.perf_counter()
                result = await loop.run_in_executor(
                    None, transcriber.add_audio, message["bytes"]
                )
                if result:
                    result["processing_time_ms"] = round(
                        (time.perf_counter() - t0) * 1000
                    )
                    await websocket.send_json(result)

            elif "text" in message:
                try:
                    data = json.loads(message["text"])
                except json.JSONDecodeError:
                    logger.warning("Invalid JSON from client: %r", message["text"])
                    continue

                if data.get("type") == "audio_stop":
                    logger.info("audio_stop received — running final inference")
                    t0 = time.perf_counter()
                    result = await loop.run_in_executor(None, transcriber.finalize)
                    if result:
                        result["processing_time_ms"] = round(
                            (time.perf_counter() - t0) * 1000
                        )
                        await websocket.send_json(result)
                    break

    except WebSocketDisconnect:
        logger.info("Client disconnected: %s", client)
    except Exception:
        logger.exception("Unexpected error in WebSocket handler")
    finally:
        transcriber.reset()
