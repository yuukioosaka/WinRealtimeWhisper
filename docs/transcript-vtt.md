# Live Transcript WebVTT Specification

Specification for the live transcript file emitted by WinRealtimeWhisper and consumed
by downstream agents (checklists, sentiment analysis, advice, ...).

- **Producer**: WinRealtimeWhisper
- **Consumers**: any process that tails the file
- **Status**: stable
- **Version**: 1.0

Agreed defaults: heartbeat interval 5 s; output directory
`%LOCALAPPDATA%\WinRealtimeWhisper\transcripts`; `chunk-seconds` is emitted in the
`NOTE session` block.

---

## 1. Design goals

| Goal | How |
| --- | --- |
| Standard format | WebVTT (W3C) — no private syntax |
| Append-only, safe to tail | Every block is written atomically and terminated by a blank line |
| Human readable | Any subtitle editor / player can open the finished file |
| Machine friendly | Cues carry absolute timestamps; notes carry structured metadata |
| No loss on rotation | Session-per-file plus a pointer file |
| Liveness detection | Periodic `NOTE heartbeat` |

Non-goals: partial (in-progress) text, input level, engine state, or derived
results such as sentiment. Those stay inside the consuming process.

---

## 2. Files and layout

```
%LOCALAPPDATA%\WinRealtimeWhisper\transcripts\
    2026-09-28_2149.live.vtt      <- being written (consumers tail this)
    2026-09-28_2149.vtt           <- finished (renamed on stop)
    2026-09-28_2310.live.vtt
    current.txt                   <- pointer: name of the active .live.vtt
```

The output directory is configurable. The pointer file `current.txt` contains the
bare file name (UTF-8, no BOM) of the active transcript, followed by a newline.

Using a `.live.vtt` prefix rather than `.vtt` prevents players and subtitle tools
from picking up a file that is still being written.

---

## 3. File structure

```
WEBVTT
Kind: captions
Language: ja
X-WINREALTIMEWHISPER-SESSION: 2026-09-28_2149

NOTE session
id: 2026-09-28_2149
started: 2026-09-28T21:49:17+09:00
source: WinRealtimeWhisper
model: ggml-small.bin
language: ja
chunk-seconds: 30

1
00:00:12.500 --> 00:00:14.200
<v Speaker1>今日は天気が良いですね。

2
00:00:14.200 --> 00:00:16.800
<v Speaker1>ところで明日の予定ですが、会議が三件入っています。

NOTE heartbeat 2026-09-28T21:49:31+09:00

3
00:00:18.000 --> 00:00:20.100
<v Speaker2>承知しました。資料は私が準備します。

NOTE session_end 2026-09-28T22:02:40+09:00
```

### 3.1 Header

The first line MUST be exactly `WEBVTT`. A UTF-8 BOM SHOULD NOT be written.

| Header field | Required | Meaning |
| --- | --- | --- |
| `Kind: captions` | RECOMMENDED | Constant. |
| `Language` | RECOMMENDED | BCP 47 tag of the recognition language. |
| `X-WINREALTIMEWHISPER-SESSION` | RECOMMENDED | Session id, equal to the base file name. |

### 3.2 `NOTE session`

Written once, immediately after the header, before the first cue.

```
NOTE session
id: <session id, same as base file name>
started: <RFC 3339 timestamp with offset>
source: WinRealtimeWhisper
model: <model file name>
language: <BCP 47 tag>
chunk-seconds: <number>
```

Consumers SHOULD read this block to establish context (model, language, session
wall-clock origin). Additional keys MAY be present and unknown keys MUST be ignored.

### 3.3 Cues

Standard WebVTT cues.

- The cue identifier is a **monotonically increasing integer starting at 1**
  (the sequence number). Consumers use it to detect gaps and duplicates.
- Timestamps are `HH:MM:SS.mmm` (WebVTT form), **relative to the start of the
  session**, not wall clock.
- Text is a single line. When speaker separation is enabled the text is
  `<v Name>text`; otherwise the `<v>` tag is omitted.
- No cue settings (position, alignment, ...) are emitted.

### 3.4 `NOTE heartbeat`

Written periodically (implementation defined; 5–10 s recommended) while
recording, **regardless of whether speech is detected**.

```
NOTE heartbeat <RFC 3339 timestamp>
```

Consecutive heartbeats MAY be merged (only the newest is meaningful), but a
consumer MUST tolerate seeing several.

If no heartbeat arrives within `heartbeat interval x 3`, a consumer MAY treat
the producer as stalled or terminated.

### 3.5 `NOTE session_end`

Written once, immediately before the file is closed.

```
NOTE session_end <RFC 3339 timestamp>
```

If a consumer observes a pointer switch to a new session **without** seeing
`session_end`, the previous session ended abnormally (crash). The remaining cues
are still valid and should be processed.

---

## 4. Producer requirements

1. **Atomic block writes.** Each block (cue or note, including its terminating
   blank line) MUST be written with a single `Write` call, or at least flushed
   as one unit. A partially written block MUST never be left on disk for longer
   than the duration of that write.
2. **Flush immediately.** Use `StreamWriter.AutoFlush = true` (or an explicit
   `Flush()` after every block).
3. **Share for read.** Open with `FileShare.ReadWrite` so consumers can read
   while the producer writes.
4. **Blank line termination.** Every block MUST end with an empty line. This is
   what makes the file safe to tail.
5. **Sequence numbers** MUST be unique and increasing within a session.
   The counter resets for each new session file.
6. **No partial text.** Only finalized segments are written.
7. **Ordering.** On stop, write `session_end` **before** updating `current.txt`.
   On start, create the new `.live.vtt`, write header and `session` note, then
   update `current.txt`.
8. **Pointer file.** `current.txt` MUST be updated atomically (write to a
   temporary file in the same directory, then replace/rename).
9. **Rename on stop.** After `session_end`, rename `*.live.vtt` to `*.vtt`.
   If the process is killed, a `.live.vtt` may remain; consumers MUST still
   process it.

---

## 5. Consumer algorithm

```
offset = 0
current = null

loop every 200 ms .. 1 s:
    name = read current.txt (trimmed, single line)
    if name != current:
        if current != null:
            read current file to EOF and process          # drain the old file
        current = name
        offset = 0
        open current file

    read from offset to EOF
    keep only content up to and including the last complete blank-line
    terminated block
    parse and process cues / notes
    advance offset to the end of the last processed block
```

Rules:

1. **Never process an unterminated trailing block.** Buffer it until the blank
   line arrives.
2. **Drain the old file before switching.** When the pointer changes, read the
   previous file to EOF, then switch. This guarantees no cue is lost.
3. **Deduplicate by session + cue id.** After a restart, re-read from the
   beginning of the current file and discard ids already seen.
4. **Tolerate unknown notes.** Ignore notes whose first token is not
   `session`, `heartbeat`, or `session_end`.
5. **Handle a missing pointer.** If `current.txt` does not exist, wait.
6. **Tolerate malformed blocks.** Skip a block that cannot be parsed and
   continue; do not abort the loop.

### 5.1 Timestamps

Cue timestamps are **session-relative**. To recover wall clock:

```
wall_clock = session.started + cue_start
```

`session.started` comes from the `NOTE session` block. This is exact as long as
the process did not pause/resume with a gap; producers that pause SHOULD start a
new session instead.

---

## 6. Consumer examples

### 6.1 Minimal tail loop (Python)

```python
import os, time, re, json

DIR = os.path.expandvars(r"%LOCALAPPDATA%\WinRealtimeWhisper\transcripts")
CUE_RE = re.compile(r"^(\d+)\s*$", re.M)
TIME_RE = re.compile(
    r"^(\d+:)?(\d{2}):(\d{2})\.(\d{3})\s+-->\s+(\d+:)?(\d{2}):(\d{2})\.(\d{3})")

def parse_block(block):
    lines = block.splitlines()
    if not lines:
        return None
    if lines[0].startswith("NOTE"):
        rest = lines[0][4:].strip().split(" ", 1)
        kind = rest[0] if rest else ""
        arg = rest[1] if len(rest) > 1 else ""
        return ("note", kind, arg, lines[1:])
    # cue: [id] time [text]
    i = 0
    cid = None
    if TIME_RE.match(lines[0]):
        pass
    else:
        cid = lines[0].strip()
        i = 1
    m = TIME_RE.match(lines[i])
    if not m:
        return None
    text = "\n".join(lines[i + 1:]).strip()
    return ("cue", cid, m.group(0), text)

def parse_partial(buf):
    """Return (blocks, remainder) where blocks end at the last blank line."""
    idx = buf.rfind("\n\n")
    if idx < 0:
        return [], buf
    head, tail = buf[: idx + 2], buf[idx + 2:]
    return [b for b in head.split("\n\n") if b.strip()], tail

offset = 0
current = None
rest = ""
while True:
    p = os.path.join(DIR, "current.txt")
    try:
        with open(p, "r", encoding="utf-8") as f:
            name = f.read().strip()
    except OSError:
        time.sleep(0.5)
        continue

    if name != current:
        if current:
            # drain old file fully
            old = os.path.join(DIR, current)
            with open(old, "r", encoding="utf-8", errors="replace") as f:
                f.seek(offset)
                data = f.read()
            blocks, _ = parse_partial(data + "\n\n")
            for b in blocks:
                handle(parse_block(b))
        current, offset, rest = name, 0, ""

    path = os.path.join(DIR, current)
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            f.seek(offset)
            data = f.read()
    except OSError:
        time.sleep(0.3)
        continue

    offset += len(data.encode("utf-8"))  # careful: use binary offsets; see note
    blocks, rest = parse_partial(rest + data)
    for b in blocks:
        handle(parse_block(b))

    time.sleep(0.3)
```

> Offsets: the snippet above is illustrative. For correctness, open the file in
> **binary** mode and decode incrementally so that byte offsets stay exact with
> multi-byte UTF-8. Track the offset of the last complete blank-line terminated
> block and re-seek from there.

### 6.2 Prompting an LLM from the stream

Give the model the session note once, then feed new cues as they appear:

```
System: You maintain a checklist for a meeting. You receive transcript
        segments as JSON: {"id":..,"start":..,"end":..,"speaker":..,"text":..}.
        Reply with JSON {"checks":[{"id":..,"done":bool,"evidence":..}]}.
User:   <new segments since last call>
```

Only the newly completed cues need to be sent. Because cue ids are monotonic, the
consumer can track the last processed id and send the delta.

---

## 7. Compatibility notes

- The file is valid WebVTT. Any WebVTT parser will happily read the cues and
  ignore the notes. `NOTE heartbeat` interleaved between cues is legal.
- The finished `.vtt` can be attached to a recording and played in VLC or burned
  into video with ffmpeg (`-i in.vtt`).
- Because timestamps are session-relative, a finished file is self-contained:
  `session.started` plus relative timestamps give absolute time.
- No private cue settings or tags are used other than the standard `<v>`.

---

## 8. Versioning

This document is version 1.0. Forward-compatible additions are limited to:

- new header fields (prefixed `X-WINREALTIMEWHISPER-`),
- new keys inside `NOTE session`,
- new note kinds (consumers ignore unknown kinds).

Producers MUST NOT change the cue syntax or the meaning of existing fields
within a major version.

---

## 9. Summary of the minimal contract

1. WebVTT with a header, a `NOTE session`, cues, `NOTE heartbeat`, `NOTE session_end`.
2. Every block terminated by a blank line and written atomically.
3. Monotonic cue ids starting at 1, per session.
4. Session-relative timestamps; wall clock via `NOTE session`.
5. One session per file, `.live.vtt` while writing, renamed to `.vtt` when done.
6. `current.txt` points at the active file and is updated atomically.
7. Consumers process only complete blocks and drain the old file on pointer change.
