"""IPC-01-only wrapper around the real Phase 0 AI Worker.

The production FastAPI app and contracts remain unchanged.  These authenticated
diagnostic routes provide deterministic, lightweight requests for correlation,
timeout, cancellation, and orderly shutdown tests.
"""

import asyncio
import time

import uvicorn
from fastapi import Depends

from photo_ai_worker.auth import require_token
from photo_ai_worker.contracts import AiError, AiRequest, AiResponse
from photo_ai_worker.main import app
from photo_ai_worker.settings import settings


_server: uvicorn.Server | None = None


@app.post(
    "/v1/ipc/echo",
    response_model=AiResponse,
    dependencies=[Depends(require_token)],
)
async def ipc_echo(req: AiRequest) -> AiResponse:
    start = time.perf_counter()
    return AiResponse(
        request_id=req.request_id,
        success=True,
        result={
            "job_id": req.job_id,
            "operation": req.operation,
            "input_paths": req.input_paths,
            "config": req.config,
        },
        timings={"total_ms": (time.perf_counter() - start) * 1000},
    )


@app.post(
    "/v1/ipc/delay",
    response_model=AiResponse,
    dependencies=[Depends(require_token)],
)
async def ipc_delay(req: AiRequest) -> AiResponse:
    start = time.perf_counter()
    try:
        delay_ms = int(req.config.get("delay_ms", 0))
    except (TypeError, ValueError):
        delay_ms = -1

    if delay_ms < 0 or delay_ms > 5_000:
        return AiResponse(
            request_id=req.request_id,
            success=False,
            error=AiError(
                code="INVALID_TEST_DELAY",
                category="validation",
                retryable=False,
                message="IPC diagnostic delay_ms must be between 0 and 5000",
            ),
            timings={"total_ms": (time.perf_counter() - start) * 1000},
        )

    await asyncio.sleep(delay_ms / 1000)
    return AiResponse(
        request_id=req.request_id,
        success=True,
        result={"delay_ms": delay_ms},
        timings={"total_ms": (time.perf_counter() - start) * 1000},
    )


@app.post(
    "/v1/ipc/shutdown",
    response_model=AiResponse,
    dependencies=[Depends(require_token)],
)
async def ipc_shutdown(req: AiRequest) -> AiResponse:
    loop = asyncio.get_running_loop()

    def request_shutdown() -> None:
        if _server is not None:
            _server.should_exit = True

    loop.call_later(0.1, request_shutdown)
    return AiResponse(
        request_id=req.request_id,
        success=True,
        result={"shutdown_requested": True},
        timings={"total_ms": 0.0},
    )


def run() -> None:
    global _server
    if not settings.token:
        raise SystemExit("PAF_AI_TOKEN must be set")
    if settings.host != "127.0.0.1":
        raise SystemExit("IPC-01 refuses non-loopback PAF_AI_HOST")

    config = uvicorn.Config(
        app,
        host=settings.host,
        port=settings.port,
        log_level="info",
        access_log=False,
    )
    _server = uvicorn.Server(config)
    _server.run()


if __name__ == "__main__":
    run()

