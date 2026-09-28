"""Compatibility test: feed events from the official OpenAI SDK into its own typed models.

The SDK parses each frame into a typed event object. If our payload shape were
wrong, the SDK would either raise or surface None for required fields. This
prints the typed fields so the values can be inspected.

Usage:
    python tools/ws_compat_test.py --port 8765 --seconds 30
"""

import argparse
import asyncio
import json
import sys
import traceback

try:
    from openai import AsyncOpenAI
except ImportError:
    sys.exit("The 'openai' package is required. Install it with: pip install openai")


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--seconds", type=float, default=30.0)
    args = parser.parse_args()

    url = "ws://127.0.0.1:%d/v1/realtime" % args.port
    print("connecting with the official OpenAI SDK: %s" % url)

    client = AsyncOpenAI(api_key="not-needed-local", websocket_base_url=url)
    typed_ok = 0
    parse_failures = 0

    async def run():
        nonlocal typed_ok, parse_failures

        async with client.realtime.connect(model="whisper-1") as connection:
            print("SDK: connected (handshake accepted)")

            async for event in connection:
                kind = getattr(event, "type", None)

                # SDK の型付きモデルとして中身が読めるかを確認する
                if kind == "session.created":
                    session = getattr(event, "session", None)
                    print("  session.created -> id=%s model=%s" % (
                        getattr(session, "id", None), getattr(session, "model", None)))

                elif kind == "conversation.item.input_audio_transcription.completed":
                    transcript = getattr(event, "transcript", None)
                    print("  completed -> seq=%s speaker=%s text=%r" % (
                        getattr(event, "sequence", None),
                        getattr(event, "speaker", None),
                        transcript))

                    # 必須フィールドが SDK の型で解決できているか
                    if transcript and getattr(event, "item_id", None):
                        typed_ok += 1
                    else:
                        parse_failures += 1
                        print("    WARNING: required fields missing")

                elif kind in ("input_audio_buffer.speech_started",
                              "input_audio_buffer.speech_stopped"):
                    print("  %s -> audio_start_ms=%s" % (
                        kind, getattr(event, "audio_start_ms", None)))

                elif kind == "error":
                    err = getattr(event, "error", None)
                    print("  error -> %s" % getattr(err, "message", err))

                else:
                    print("  %s" % kind)

                if typed_ok >= 4:
                    print("  (enough segments)")
                    break

    try:
        await asyncio.wait_for(run(), timeout=args.seconds)
    except asyncio.TimeoutError:
        print("SDK: timed out after %.0f s" % args.seconds)
    except Exception:
        print("SDK EXCEPTION:")
        traceback.print_exc()

    print()
    print("typed segments parsed by the SDK: %d" % typed_ok)
    print("segments with missing fields:    %d" % parse_failures)
    print("RESULT: %s" % ("COMPATIBLE" if typed_ok > 0 and parse_failures == 0 else "CHECK"))


asyncio.run(main())
