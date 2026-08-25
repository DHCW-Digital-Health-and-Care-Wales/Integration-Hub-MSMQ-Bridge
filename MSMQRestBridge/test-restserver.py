import logging
from datetime import datetime
from pathlib import Path

from fastapi import FastAPI, Header, Request, HTTPException
from fastapi.responses import PlainTextResponse

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
)
log = logging.getLogger("test_receiver")

app = FastAPI(title="MSMQ Bridge Test Receiver")

# Optional: set to require the x-api-key header the bridge sends.
# Leave empty to skip validation during testing.
API_KEY = "my-secret-key"  # e.g. "my-secret-key"

SAVE_DIR = Path("received_messages")
SAVE_DIR.mkdir(exist_ok=True)

counter = 0


@app.post("/api/messages")
async def receive_message(
    request: Request,
    x_msmq_label: str | None = Header(default=None),
    x_msmq_arrivedtime: str | None = Header(default=None),
    x_api_key: str | None = Header(default=None),
):
    global counter

    # Optional API key check — matches what the C# bridge sends
    if API_KEY and x_api_key != API_KEY:
        log.warning("Rejected request: missing/invalid x-api-key")
        raise HTTPException(status_code=401, detail="Invalid API key")

    body_bytes = await request.body()

    try:
        body_text = body_bytes.decode("utf-8", errors="replace")
    except Exception:
        body_text = repr(body_bytes)

    counter += 1

    log.info("=" * 60)
    log.info(f"Message #{counter} received ({len(body_bytes)} bytes)")
    log.info(f"  X-MSMQ-Label      : {x_msmq_label}")
    log.info(f"  X-MSMQ-ArrivedTime: {x_msmq_arrivedtime}")
    log.info(f"  Body preview      : {body_text[:500]}")

    # Save full message to disk so you can inspect it
    timestamp = datetime.utcnow().strftime("%Y%m%d_%H%M%S_%f")
    safe_label = (x_msmq_label or "unlabeled").replace("\\", "_").replace("/", "_")
    out_file = SAVE_DIR / f"{timestamp}_{safe_label}.txt"
    out_file.write_text(
        f"label: {x_msmq_label}\n"
        f"arrivedTime: {x_msmq_arrivedtime}\n"
        f"---\n{body_text}\n",
        encoding="utf-8",
    )

    # Simulate failures for testing retry/dead-letter logic:
    # Uncomment to make the endpoint return 500 every 3rd message,
    # or reject messages containing "FAIL".
    #
    # if "FAIL" in body_text:
    #     raise HTTPException(status_code=500, detail="Simulated failure")
    #
    # if counter % 3 == 0:
    #     raise HTTPException(status_code=500, detail="Simulated transient failure")

    return PlainTextResponse(f"OK #{counter}", status_code=200)


@app.get("/health")
async def health():
    return {"status": "healthy", "messages_received": counter}


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)