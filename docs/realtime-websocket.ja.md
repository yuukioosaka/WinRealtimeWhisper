# OpenAI Realtime 互換 WebSocket サーバー

Status: stable · Version 1.0

WinRealtimeWhisper は、確定した文字起こしを WebSocket で公開できます。別プロセスが
API キーなしに発話へリアルタイムに反応できるようにするための機能です。ワイヤ形式は
[OpenAI Realtime API](https://platform.openai.com/docs/api-reference/realtime) の
命名と封筒（エンベロープ）の流儀に合わせてあるので、既存のクライアント資産や
考え方をそのまま流用できます。

意図的に**一方向・テキストのみ**です。音声をクライアントから受け取ることはなく、
サーバーが音声を送ることもありません。クライアントから届いたメッセージは読み捨てます
（`ping` と `close` だけは処理します）。

## トランスポート

| 項目 | 値 |
| --- | --- |
| 待ち受けアドレス | `127.0.0.1` のみ（ネットワークには公開しない） |
| パス | `/v1/realtime`（ルート `/` も受け付けます） |
| プロトコル | WebSocket (RFC 6455) |
| 実装 | .NET の `HttpListener` + `System.Net.WebSockets`（外部依存なし） |
| 方向 | サーバー → クライアント の一方向 |
| ペイロード | テキストフレーム。1 フレーム = 1 イベント（JSON） |
| 既定ポート | `8765` |

```
ws://127.0.0.1:8765/v1/realtime
```

通常の HTTP で接続すると `400` と短い案内を返します。未知のパスは `404` です。

## 有効にする

- **設定 > 保存先** の「OpenAI Realtime 互換の WebSocket サーバーを立てる」と
  ポート。サーバーは録音と一緒に起動し、停止と一緒に終わります。
- コマンドライン: `--ws`（既定ポート）または `--ws-port <ポート>`（`--ws` も有効に
  なります）。

ポートは録音開始時に確保するため、2 つのインスタンスで同じポートは使えません。
ポートが埋まっていた場合は状態行とログに失敗を出し、録音自体は続行します。

## イベント

すべてのイベントは少なくとも `type` と `event_id` を持つ JSON オブジェクトです。

### `session.created`

接続ごとに 1 回、ハンドシェイク直後に送ります。

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

`session.id` は 1 回の録音セッション中は変わりません。途中で再接続しても同じ値が
届きます。`model` は ggml のファイル名であり、OpenAI のモデル ID ではありません。

### `input_audio_buffer.speech_started` / `input_audio_buffer.speech_stopped`

音量ベースの VAD が発話の開始・終了を判断したときに送出します。
`audio_start_ms` はセッション開始からの相対時間です。

```json
{
  "type": "input_audio_buffer.speech_started",
  "event_id": "evt_2_198f3c2a1b0",
  "item_id": null,
  "audio_start_ms": 4210
}
```

### `conversation.item.input_audio_transcription.completed`

確定した 1 区間につき 1 イベント。**これが主役のイベントです。** 途中経過は
設計上送りません。Whisper の中間出力は差分ではなく「その区間の全文の書き直し」で、
`delta` として流すと OpenAI の意味とずれるためです。

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

| フィールド | 意味 |
| --- | --- |
| `transcript` | 確定したテキスト |
| `item_id` | `item_<sequence>`。セッション内で増加します |
| `sequence` | `item_id` と同じ番号（整数） |
| `speaker` | `"Remote"`（ループバック）/ `"You"`（マイク）。片系統だけのときは付きません |
| `audio_start_ms` / `audio_end_ms` | セッション開始からの相対位置 |

`sequence` は WebVTT のキュー番号と一致するので、両方の出力を対応付けられます。
話者ラベルは両系統を認識しているときだけ意味を持ちます。片系統のときは VTT 出力と
同様に省略します。

### `error`

復帰可能なサーバー側のエラー。接続は維持します。

```json
{
  "type": "error",
  "event_id": "evt_4_198f3c2a1b0",
  "error": { "type": "server_error", "code": "server_error", "message": "..." }
}
```

## クライアントの例

最小限の Python クライアント。フレームを読んで `type` で分岐するだけです。

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

## OpenAI Realtime API との違い

| OpenAI | 本アプリ |
| --- | --- |
| 音声の双方向 | テキストの一方向出力 |
| サーバー VAD と応答生成 | VAD の通知のみ |
| 途中経過の `delta` イベント | 送りません。確定区間のみ |
| クラウドのモデル | whisper.cpp によるローカルの ggml モデル |
| `wss://` と API キー | ループバックの `ws://`、認証なし |

ループバックにしかバインドしないため認証は行いません。同じユーザーで動くプロセスは
接続できます。ローカルの他プロセスに対して秘匿したい情報を流さないでください。

## 関連

- [WebVTT ライブ文字起こし](transcript-vtt.ja.md) — ファイル版の等価な機能
- [README](../README.ja.md) — 設定とコマンドラインの一覧
