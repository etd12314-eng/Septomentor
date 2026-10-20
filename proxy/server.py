"""SeptoMentor AI proxy.

Holds the Anthropic API key and the system prompt; the Quest APK only knows this
proxy's URL and a revocable MENTOR_TOKEN.

  POST /v1/coach  -> short automatic coaching on warnings / phase changes (Haiku)
  POST /v1/ask    -> resident Q&A (Sonnet)
  GET  /health

Env:
  ANTHROPIC_API_KEY   required (or any credential the SDK resolves)
  MENTOR_TOKEN        required bearer token for the headset (comma-separate to allow several)
  ALLOW_FRAMES=1      accept video frames; default OFF - frames are dropped unread.
                      Patient video leaving the device needs institutional approval.
  COACH_MODEL / ASK_MODEL   override models
  RATE_PER_MIN        per-token request limit (default 20)

Run:  uvicorn server:app --host 0.0.0.0 --port 8080   (put TLS in front of it)
"""

from __future__ import annotations

import logging
import os
import time
from collections import defaultdict, deque
from typing import Optional

import anthropic
from fastapi import Depends, FastAPI, Header, HTTPException
from pydantic import BaseModel, Field

log = logging.getLogger("septomentor.proxy")
logging.basicConfig(level=logging.INFO)

COACH_MODEL = os.environ.get("COACH_MODEL", "claude-haiku-5-5")
ASK_MODEL = os.environ.get("ASK_MODEL", "claude-sonnet-5-5")
ALLOW_FRAMES = os.environ.get("ALLOW_FRAMES") == "1"
RATE_PER_MIN = int(os.environ.get("RATE_PER_MIN", "20"))
MAX_FRAME_B64 = 1_500_000  # ~1.1 MB JPEG

SYSTEM_PROMPT = """You are SeptoMentor, a calm senior rhinologist mentoring an ENT resident \
during endoscopic septoplasty training. You speak through a headset, so be brief.

Ground rules:
- This is a training aid, not a medical device. The attending surgeon makes every decision; \
say so if the resident asks you to decide something clinically binding.
- Scene information comes from an automatic segmentation. Unless labels_validated is true, \
treat structure names as unconfirmed guesses, and never state them as fact. If is_simulation \
is true, the scene is a synthetic simulation.
- Teach the standard principles: subperichondrial/subperiosteal plane (bluish-white cartilage \
under the flap), preserve a dorsal and caudal L-strut of about 10 mm or more, remove only the \
deviated portion, cut rather than twist at the perpendicular plate (skull-base risk), avoid \
opposing bilateral mucosal tears, protect the turbinates, keep the view clear with suction.
- If something sounds unsafe or uncertain, tell the resident to pause and involve the attending.
- No patient identifiers. Do not invent measurements; quote only numbers given to you."""

COACH_STYLE = (
    "Give one or two short spoken sentences (max 35 words) the resident can act on now. "
    "No preamble, no lists."
)
ASK_STYLE = (
    "Answer the resident's question in at most 90 words, plain spoken English, "
    "most important point first. No markdown."
)

app = FastAPI(title="SeptoMentor proxy", docs_url=None, redoc_url=None)
_client: Optional[anthropic.Anthropic] = None
_hits: dict[str, deque] = defaultdict(deque)


def client() -> anthropic.Anthropic:
    global _client
    if _client is None:
        _client = anthropic.Anthropic(timeout=30.0, max_retries=2)
    return _client


def _tokens() -> set[str]:
    return {t.strip() for t in os.environ.get("MENTOR_TOKEN", "").split(",") if t.strip()}


def auth(authorization: str = Header(default="")) -> str:
    tokens = _tokens()
    if not tokens:
        raise HTTPException(503, "proxy not configured (MENTOR_TOKEN unset)")
    token = authorization.removeprefix("Bearer ").strip()
    if token not in tokens:
        raise HTTPException(401, "bad token")
    now = time.monotonic()
    q = _hits[token]
    while q and now - q[0] > 60:
        q.popleft()
    if len(q) >= RATE_PER_MIN:
        raise HTTPException(429, "rate limited")
    q.append(now)
    return token


class Turn(BaseModel):
    role: str
    text: str


class Context(BaseModel):
    session_id: str = ""
    phase: str = "Setup"
    instrument: str = "none"
    visible_structures: list[str] = Field(default_factory=list)
    recent_events: list[str] = Field(default_factory=list)
    is_simulation: bool = True
    labels_validated: bool = False
    frame_jpeg_b64: str = ""


class CoachRequest(Context):
    trigger: str = "warning"
    rule_id: str = ""
    severity: str = "Info"
    message: str = ""


class AskRequest(Context):
    question: str
    history: list[Turn] = Field(default_factory=list)


class Reply(BaseModel):
    text: str
    model: str = ""
    refused: bool = False


def _scene(ctx: Context) -> str:
    return (
        f"phase={ctx.phase}; instrument={ctx.instrument}; "
        f"visible_structures={', '.join(ctx.visible_structures[:12]) or 'none'}; "
        f"recent_events={' | '.join(ctx.recent_events[-6:]) or 'none'}; "
        f"is_simulation={ctx.is_simulation}; labels_validated={ctx.labels_validated}"
    )


def _content(text: str, ctx: Context) -> list[dict]:
    blocks: list[dict] = []
    frame = ctx.frame_jpeg_b64
    if frame and ALLOW_FRAMES and len(frame) <= MAX_FRAME_B64:
        blocks.append({"type": "image", "source": {"type": "base64", "media_type": "image/jpeg", "data": frame}})
    blocks.append({"type": "text", "text": text})
    return blocks


def _system(style: str) -> list[dict]:
    return [
        {"type": "text", "text": SYSTEM_PROMPT, "cache_control": {"type": "ephemeral"}},
        {"type": "text", "text": style},
    ]


def _text_of(resp) -> str:
    return " ".join(b.text for b in resp.content if b.type == "text").strip()


def _call(fn, **kwargs):
    try:
        return fn(**kwargs)
    except anthropic.RateLimitError:
        raise HTTPException(429, "upstream rate limited")
    except anthropic.APIStatusError as e:
        log.warning("anthropic status %s (request %s)", e.status_code, getattr(e, "request_id", None))
        raise HTTPException(502, "upstream error")
    except anthropic.APIConnectionError:
        raise HTTPException(504, "upstream unreachable")


REFUSAL_TEXT = "I can't help with that one. Please ask your attending."


@app.get("/health")
def health() -> dict:
    return {"ok": True, "frames_allowed": ALLOW_FRAMES, "coach_model": COACH_MODEL, "ask_model": ASK_MODEL}


@app.post("/v1/coach", response_model=Reply)
def coach(req: CoachRequest, _: str = Depends(auth)) -> Reply:
    prompt = (
        f"Trigger: {req.trigger}. Rule: {req.rule_id} ({req.severity}). "
        f"Rule message shown: \"{req.message[:300]}\".\nScene: {_scene(req)}"
    )
    resp = _call(
        client().messages.create,
        model=COACH_MODEL,
        max_tokens=300,
        system=_system(COACH_STYLE),
        output_config={"effort": "low"},
        messages=[{"role": "user", "content": _content(prompt, req)}],
    )
    if resp.stop_reason == "refusal":
        return Reply(text="", model=resp.model, refused=True)
    return Reply(text=_text_of(resp), model=resp.model)


@app.post("/v1/ask", response_model=Reply)
def ask(req: AskRequest, _: str = Depends(auth)) -> Reply:
    question = req.question.strip()[:800]
    if not question:
        raise HTTPException(400, "empty question")
    messages: list[dict] = []
    for t in req.history[-8:]:
        role = "assistant" if t.role == "assistant" else "user"
        if messages and messages[-1]["role"] == role:
            continue  # keep strict alternation
        messages.append({"role": role, "content": t.text[:1500]})
    if messages and messages[0]["role"] != "user":
        messages.pop(0)
    if messages and messages[-1]["role"] == "user":
        messages.pop()
    messages.append({"role": "user", "content": _content(f"Scene: {_scene(req)}\nQuestion: {question}", req)})

    resp = _call(
        client().beta.messages.create,
        model=ASK_MODEL,
        max_tokens=1024,
        system=_system(ASK_STYLE),
        output_config={"effort": "medium"},
        betas=["server-side-fallback-2026-07-01"],
        fallbacks="default",
        messages=messages,
    )
    if resp.stop_reason == "refusal":
        return Reply(text=REFUSAL_TEXT, model=resp.model, refused=True)
    return Reply(text=_text_of(resp) or REFUSAL_TEXT, model=resp.model)
