"""Offline tests: the Anthropic client is replaced with a fake, no network or key needed."""

import importlib
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


class FakeMessages:
    def __init__(self, calls, stop_reason="end_turn"):
        self.calls, self.stop_reason = calls, stop_reason

    def create(self, **kw):
        self.calls.append(kw)
        return SimpleNamespace(
            content=[SimpleNamespace(type="text", text="Stay under the perichondrium.")],
            stop_reason=self.stop_reason,
            model=kw["model"],
        )


def make_app(monkeypatch, allow_frames=False, stop_reason="end_turn"):
    monkeypatch.setenv("MENTOR_TOKEN", "t1,t2")
    monkeypatch.setenv("RATE_PER_MIN", "3")
    if allow_frames:
        monkeypatch.setenv("ALLOW_FRAMES", "1")
    else:
        monkeypatch.delenv("ALLOW_FRAMES", raising=False)
    server = importlib.reload(importlib.import_module("server"))
    calls = []
    fake = FakeMessages(calls, stop_reason)
    server._client = SimpleNamespace(messages=fake, beta=SimpleNamespace(messages=fake))
    return server, TestClient(server.app), calls


H = {"Authorization": "Bearer t1"}


def test_auth_required(monkeypatch):
    _, c, _ = make_app(monkeypatch)
    assert c.post("/v1/coach", json={}).status_code == 401
    assert c.post("/v1/coach", json={}, headers={"Authorization": "Bearer nope"}).status_code == 401


def test_coach_uses_haiku_and_drops_frames_by_default(monkeypatch):
    _, c, calls = make_app(monkeypatch)
    r = c.post("/v1/coach", headers=H, json={"rule_id": "l_strut", "severity": "Warning", "frame_jpeg_b64": "AAAA"})
    assert r.status_code == 200 and r.json()["text"]
    kw = calls[0]
    assert kw["model"] == "claude-haiku-5-5"
    assert all(b["type"] != "image" for b in kw["messages"][0]["content"])
    assert "SeptoMentor" in kw["system"][0]["text"]


def test_frames_only_when_allowed(monkeypatch):
    _, c, calls = make_app(monkeypatch, allow_frames=True)
    c.post("/v1/coach", headers=H, json={"frame_jpeg_b64": "AAAA"})
    assert calls[0]["messages"][0]["content"][0]["type"] == "image"


def test_ask_uses_sonnet_with_alternating_history(monkeypatch):
    _, c, calls = make_app(monkeypatch)
    hist = [{"role": "assistant", "text": "x"}, {"role": "user", "text": "q1"},
            {"role": "assistant", "text": "a1"}, {"role": "user", "text": "dangling"}]
    r = c.post("/v1/ask", headers=H, json={"question": "How big is the L-strut?", "history": hist})
    assert r.status_code == 200
    kw = calls[0]
    assert kw["model"] == "claude-sonnet-5-5"
    roles = [m["role"] for m in kw["messages"]]
    assert roles == ["user", "assistant", "user"]
    assert kw["fallbacks"] == "default"


def test_refusal_is_reported(monkeypatch):
    _, c, _ = make_app(monkeypatch, stop_reason="refusal")
    r = c.post("/v1/ask", headers=H, json={"question": "?"})
    assert r.json()["refused"] is True


def test_rate_limit(monkeypatch):
    _, c, _ = make_app(monkeypatch)
    codes = [c.post("/v1/coach", headers=H, json={}).status_code for _ in range(4)]
    assert codes[:3] == [200, 200, 200] and codes[3] == 429


def test_empty_question_rejected(monkeypatch):
    _, c, _ = make_app(monkeypatch)
    assert c.post("/v1/ask", headers=H, json={"question": "  "}).status_code == 400
