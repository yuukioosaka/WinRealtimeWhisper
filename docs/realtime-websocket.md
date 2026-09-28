# OpenAI Realtime compatible WebSocket server

Status: stable · Version 1.0

WinRealtimeWhisper can expose its finalized transcript over a WebSocket so that
other processes can react to speech in real time without an API key. The wire
format follows the naming and envelope conventions of the
[OpenAI Realtime API](https://platform.openai.com/docs/api-reference/realtime)
so that existing client code and mental models carry over.

It is deliberately **one-way and text-only**. Audio is never accepted from the
client, and the server never sends audio. Messages sent by the client are read
and discarded, except for `ping` and `close`.

## Transport

| Item | Value |
| --- | --- |
| Bind address | `127.0.0.1` only (never exposed to the network) |
| Path | `/v1/realtime` (the root `/` is also accepted) |
| Protocol | WebSocket, RFC 6455 |
| Implementation | .NET `HttpListener` + `System.Net.WebSockets` (no third-party dependency) |
| Direction | Server → client only |
| Payload | Text frames, one JSON event per frame |
| Default port | `8765` |

```
ws://127.0.0.1:8765/v1/realtime
```

Connecting with plain HTTP returns `400` with a short hint; an unknown path
returns `404`.

## Enabling

- **Settings > Storage** → "Run an OpenAI Realtime compatible WebSocket server",
  plus the port. The server starts with recording and stops with it.
- Command line: `--ws` (default port) or `--ws-port <port>` (implies `--ws`).

The port is bound when recording starts, so two instances cannot share a port.
If the port is taken, the failure is reported in the status line and the log;
recording continues.

## Events

Every event is a JSON object with at least `type` and `event_id`.

### `session.created`

Sent once per connection, immediately after the handshake.

```json
{
  "type": "session.created",
  "event_id": "evt_1_198f3c2a1b0",
  "session": {
    "id": "2026-09-28_2149",
    "object": "realtime.session",
    "model": "ggml-small.bin",
    "input_audio_format": "pcm16",
    "input_audio_transcription": { "model": "whisper-1", "language": "ja" },
    "turn_detection": { "type": "server_vad" },
    "source": "WinRealtimeWhisper",
    "started_at": "2026-09-28T21:49:17+09:00"
  }
}
```

`session.id` is stable for one recording session, so a client that reconnects
mid-session receives the same value. `model` is the ggml file name, not an
OpenAI model id.

### `input_audio_buffer.speech_started` / `input_audio_buffer.speech_stopped`

Emitted when the level-based voice activity detector decides speech has begun or
ended. `audio_start_ms` is relative to the start of the session.

```json
{
  "type": "input_audio_buffer.speech_started",
  "event_id": "evt_2_198f3c2a1b0",
  "item_id": null,
  "audio_start_ms": 4210
}
```

### `conversation.item.input_audio_transcription.completed`

One event per finalized segment. **This is the main event.** Partial text is
never sent, by design: Whisper's intermediate output is a full rewrite of the
segment rather than a delta, so streaming it as `delta` would misrepresent the
OpenAI semantics.

```json
{
  "type": "conversation.item.input_audio_transcription.completed",
  "event_id": "evt_3_198f3c2a1b0",
  "item_id": "item_1",
  "content_index": 0,
  "transcript": "今日は天気が良いですね。",
  "speaker": "Remote",
  "audio_start_ms": 12500,
  "audio_end_ms": 14200,
  "sequence": 1
}
```

| Field | Meaning |
| --- | --- |
| `transcript` | The finalized text |
| `item_id` | `item_<sequence>`, increasing within a session |
| `sequence` | Same number as `item_id`, as an integer |
| `speaker` | `"Remote"` (loopback) or `"You"` (microphone); absent when only one source is captured |
| `audio_start_ms` / `audio_end_ms` | Segment position relative to the session start |

`sequence` matches the cue id in the WebVTT transcript, so the two outputs can be
correlated. The speaker label is only meaningful when both sources are being
recognized; with a single source it is omitted, exactly as in the VTT output.

### `error`

A recoverable server-side problem. The connection stays open.

```json
{
  "type": "error",
  "event_id": "evt_4_198f3c2a1b0",
  "error": { "type": "server_error", "code": "server_error", "message": "..." }
}
```

## Client example

A minimal Python client. It only has to read frames and dispatch on `type`.

```python
import asyncio, json, websockets

async def main():
    async with websockets.connect("ws://127.0.0.1:8765/v1/realtime") as ws:
        async for raw in ws:
            event = json.loads(raw)
            kind = event["type"]
            if kind.endswith("transcription.completed"):
                print(f'[{event.get("speaker", "-")}] {event["transcript"]}')
            elif kind == "session.created":
                print("session", event["session"]["id"])

asyncio.run(main())
```

## Differences from the OpenAI Realtime API

| OpenAI | Here |
| --- | --- |
| Bidirectional audio in/out | One-way text out |
| Server-side VAD with response generation | VAD notification only |
| `delta` events for in-progress text | Not sent; completed segments only |
| Cloud models | Local ggml model via whisper.cpp |
| `wss://` with an API key | `ws://` on loopback, no authentication |

Because the bind address is loopback, no authentication is performed. Anything
running as the same user can connect; do not treat the stream as confidential
from local processes.

## See also

- [WebVTT live transcript](transcript-vtt.md) — the file-based equivalent
- [README](../README.md) — settings and command line reference
