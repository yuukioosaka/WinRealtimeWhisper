#!/usr/bin/env python
"""Minimal client for the WinRealtimeWhisper OpenAI Realtime compatible server.

Specification: docs/realtime-websocket.md

Usage:
    python tools/ws_client.py
    python tools/ws_client.py --port 8765 --count 10
    python tools/ws_client.py --url ws://127.0.0.1:9000/v1/realtime

Start a recording with the server enabled first, for example:

    WinRealtimeWhisper.exe -t 60 -s speakers --ws

The client waits for the port to open, then prints one line per event until it
is closed or reaches --count finalized segments.
"""

import argparse
import asyncio
import json
import sys

try:
    import websockets
except ImportError:
    sys.exit(
        "The 'websockets' package is required. Install it with:\n"
        "    pip install websockets"
    )


async def read_events(uri, count, connect_timeout, idle_timeout, show_all):
    """Connect, then print events until enough segments arrive or the link closes."""

    # サーバーは録音と同時にポートを開くので、開くまで待つ。
    loop = asyncio.get_event_loop()
    deadline = loop.time() + connect_timeout
    socket = None

    while loop.time() < deadline:
        try:
            socket = await websockets.connect(uri)
            break
        except (OSError, ConnectionRefusedError):
            await asyncio.sleep(0.5)

    if socket is None:
        print("Could not connect to %s within %.0f s." % (uri, connect_timeout), file=sys.stderr)
        print("Is a recording running with --ws (or the setting enabled)?", file=sys.stderr)
        return 1

    async with socket:
        print("connected: %s" % uri)
        segments = 0

        while segments < count:
            try:
                raw = await asyncio.wait_for(socket.recv(), timeout=idle_timeout)
            except asyncio.TimeoutError:
                print("no events for %.0f s; giving up." % idle_timeout, file=sys.stderr)
                return 0
            except websockets.ConnectionClosed:
                print("server closed the connection (recording stopped).")
                return 0

            try:
                event = json.loads(raw)
            except ValueError:
                print("unparsable frame: %r" % raw, file=sys.stderr)
                continue

            kind = event.get("type", "")

            if kind == "session.created":
                session = event.get("session", {})
                transcription = session.get("input_audio_transcription", {})
                print("session id=%s model=%s language=%s" % (
                    session.get("id", "-"),
                    session.get("model", "-"),
                    transcription.get("language", "-")))
            elif kind.endswith("transcription.completed"):
                segments += 1
                speaker = event.get("speaker")
                print("[%2d] %s%s  (%s-%s ms)" % (
                    event.get("sequence", segments),
                    "[%s] " % speaker if speaker else "",
                    event.get("transcript", ""),
                    event.get("audio_start_ms", "?"),
                    event.get("audio_end_ms", "?")))
            elif kind == "error":
                print("error: %s" % event.get("error", {}).get("message", "?"), file=sys.stderr)
            elif show_all:
                print("event: %s" % kind)

    print("done: %d segment(s)." % segments)
    return 0


def main():
    parser = argparse.ArgumentParser(
        description="Print events from the WinRealtimeWhisper WebSocket server.")
    parser.add_argument("--host", default="127.0.0.1",
                        help="server host (default: 127.0.0.1)")
    parser.add_argument("--port", type=int, default=8765,
                        help="server port (default: 8765)")
    parser.add_argument("--url", default=None,
                        help="full WebSocket URL; overrides --host and --port")
    parser.add_argument("--count", type=int, default=5,
                        help="stop after this many finalized segments (default: 5)")
    parser.add_argument("--connect-timeout", type=float, default=40.0,
                        help="seconds to wait for the port to open (default: 40)")
    parser.add_argument("--idle-timeout", type=float, default=30.0,
                        help="seconds of silence before giving up (default: 30)")
    parser.add_argument("--all", action="store_true",
                        help="also print speech_started / speech_stopped events")
    args = parser.parse_args()

    uri = args.url or "ws://%s:%d/v1/realtime" % (args.host, args.port)

    try:
        return asyncio.run(read_events(
            uri, args.count, args.connect_timeout, args.idle_timeout, args.all))
    except KeyboardInterrupt:
        return 0


if __name__ == "__main__":
    sys.exit(main())
