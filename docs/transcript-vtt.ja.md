# ライブ文字起こし WebVTT 仕様

WinRealtimeWhisper が出力し、後段のエージェント（チェックリスト、感情分析、助言など）が
消費するライブ文字起こしファイルの仕様。

- **生成側**: WinRealtimeWhisper
- **消費側**: ファイルを tail する任意のプロセス
- **状態**: 確定
- **バージョン**: 1.0

確定した既定値: heartbeat 間隔 5 秒、出力先
`%LOCALAPPDATA%\WinRealtimeWhisper\transcripts`、`NOTE session` に
`chunk-seconds` を出力する。

---

## 1. 設計目標

| 目標 | 手段 |
| --- | --- |
| 標準フォーマット | WebVTT (W3C)。独自構文を使わない |
| 追記専用・tail 安全 | 各ブロックをアトミックに書き、空行で終端する |
| 人間が読める | 完成ファイルは任意の字幕エディタ／プレイヤーで開ける |
| 機械に優しい | キューは絶対時刻を、NOTE は構造化メタ情報を持つ |
| ローテーションで欠落しない | セッション単位ファイル + ポインタファイル |
| 生存確認 | 定期 `NOTE heartbeat` |

対象外: 途中結果（partial）、入力レベル、エンジン状態、感情などの派生結果。
それらは消費側プロセスの中に留める。

---

## 2. ファイルと配置

```
%LOCALAPPDATA%\WinRealtimeWhisper\transcripts\
    2026-09-28_2149.live.vtt      <- 書き込み中（消費側はこれを tail）
    2026-09-28_2149.vtt           <- 完成品（停止時にリネーム）
    2026-09-28_2310.live.vtt
    current.txt                   <- ポインタ: 現在の .live.vtt の名前
```

出力ディレクトリは設定可能。ポインタファイル `current.txt` には、現在の
文字起こしのファイル名（UTF-8、BOM なし）と改行のみを書く。

拡張子を `.vtt` ではなく `.live.vtt` にすることで、書き込み中のファイルを
プレイヤーや字幕ツールが拾うのを防ぐ。

---

## 3. ファイル構造

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

### 3.1 ヘッダ

先頭行は必ず `WEBVTT`。UTF-8 BOM は書かない（SHOULD NOT）。

| ヘッダフィールド | 必須 | 意味 |
| --- | --- | --- |
| `Kind: captions` | 推奨 | 固定。 |
| `Language` | 推奨 | 認識言語の BCP 47 タグ。 |
| `X-WINREALTIMEWHISPER-SESSION` | 推奨 | セッション ID。ベースファイル名と同一。 |

### 3.2 `NOTE session`

ヘッダ直後、最初のキューより前に 1 回だけ書く。

```
NOTE session
id: <セッション ID。ベースファイル名と同一>
started: <RFC 3339（オフセット付き）タイムスタンプ>
source: WinRealtimeWhisper
model: <モデルファイル名>
language: <BCP 47 タグ>
chunk-seconds: <数値>
```

消費側はこのブロックを読み、文脈（モデル、言語、セッションの実時刻原点）を把握する
（SHOULD）。追加キーが存在してもよく、未知のキーは無視する（MUST）。

### 3.3 キュー

標準の WebVTT キュー。

- キュー識別子は **1 から始まる単調増加の整数**（シーケンス番号）。
  消費側は欠落・重複の検知に使う。
- タイムスタンプは `HH:MM:SS.mmm`（WebVTT 形式）で、**セッション開始からの相対時刻**。
  実時刻ではない。
- テキストは 1 行。話者分離が有効なときは `<v 名前>テキスト`、
  無効なときは `<v>` タグを付けない。
- キュー設定（position, alignment など）は出力しない。

### 3.4 `NOTE heartbeat`

録音中、**発話の有無に関わらず**定期的に書く（間隔は実装依存。5〜10 秒を推奨）。

```
NOTE heartbeat <RFC 3339 タイムスタンプ>
```

連続する heartbeat は省略してもよい（最新のみ意味を持つ）が、消費側は複数見ても
許容しなければならない（MUST）。

`heartbeat 間隔 x 3` の間に heartbeat が来なければ、消費側は生成側が停止した
可能性があると見なしてよい（MAY）。

### 3.5 `NOTE session_end`

ファイルを閉じる直前に 1 回だけ書く。

```
NOTE session_end <RFC 3339 タイムスタンプ>
```

消費側が `session_end` を見ずにポインタの新セッションへの切り替えを観測した場合、
前セッションは異常終了（クラッシュ）している。残っているキューは有効であり、
処理すべきである。

---

## 4. 生成側の要件

1. **ブロック単位のアトミック書き込み。** 各ブロック（キューまたは NOTE、終端の
   空行を含む）は 1 回の `Write` で書くか、少なくとも 1 単位としてフラッシュする。
   書きかけのブロックを長く残してはならない。
2. **即時フラッシュ。** `StreamWriter.AutoFlush = true`（または各ブロック後に明示的な `Flush()`）を使う。
3. **読み取り共有。** `FileShare.ReadWrite` で開き、書き込み中でも消費側が読めるようにする。
4. **空行終端。** 各ブロックは必ず空行で終わる。これが tail 安全性の根拠。
5. **シーケンス番号** はセッション内で一意かつ増加。カウンタはセッションごとにリセット。
6. **途中結果を書かない。** 確定セグメントのみ書く。
7. **順序。** 停止時は `session_end` を書いてから `current.txt` を更新する。
   開始時は新しい `.live.vtt` を作り、ヘッダと `session` NOTE を書いてから
   `current.txt` を更新する。
8. **ポインタファイル。** `current.txt` はアトミックに更新する
   （同一ディレクトリの一時ファイルに書いてから置換／リネーム）。
9. **停止時リネーム。** `session_end` の後、`*.live.vtt` を `*.vtt` にリネームする。
   プロセス強制終了時は `.live.vtt` が残りうるが、消費側はそれを処理できなければ
   ならない。

---

## 5. 消費側アルゴリズム

```
offset = 0
current = null

200 ms 〜 1 秒ごとにループ:
    name = current.txt を読む（trim して 1 行）
    if name != current:
        if current != null:
            旧ファイルを EOF まで読み切って処理する     # 旧ファイルの drain
        current = name
        offset = 0
        現ファイルを開く

    offset から EOF まで読む
    最後の「空行で終端された完全なブロック」までを対象にする
    キュー / NOTE をパースして処理
    offset を最後に処理したブロックの末尾へ進める
```

規則:

1. **終端していない末尾ブロックを処理しない。** 空行が来るまでバッファする。
2. **旧ファイルを drain してから切り替える。** ポインタが変わったら、前のファイルを
   EOF まで読んでから切り替える。これでキューの欠落を防ぐ。
3. **セッション + キュー ID で重複排除。** 再起動時は現ファイルを先頭から読み直し、
   既に見た ID を捨てる。
4. **未知の NOTE を許容する。** 先頭トークンが `session` / `heartbeat` /
   `session_end` 以外の NOTE は無視する。
5. **ポインタ不在を扱う。** `current.txt` が無ければ待つ。
6. **壊れたブロックを許容する。** パースできないブロックはスキップして継続する。
   ループを中止しない。

### 5.1 タイムスタンプ

キューのタイムスタンプは**セッション相対**。実時刻を得るには:

```
実時刻 = session.started + キュー開始時刻
```

`session.started` は `NOTE session` から得る。プロセスが間を空けて pause/resume
しなければ正確。pause する生成側は、再開時ではなく新セッションを開始すべき。

---

## 6. 消費側の実装例

### 6.1 最小 tail ループ（Python）

```python
import os, time, re, json

DIR = os.path.expandvars(r"%LOCALAPPDATA%\WinRealtimeWhisper\transcripts")
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
    i = 0
    cid = None
    if not TIME_RE.match(lines[0]):
        cid = lines[0].strip()
        i = 1
    m = TIME_RE.match(lines[i])
    if not m:
        return None
    text = "\n".join(lines[i + 1:]).strip()
    return ("cue", cid, m.group(0), text)

def parse_partial(buf):
    """(blocks, remainder) を返す。blocks は最後の空行まで。"""
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

    offset += len(data.encode("utf-8"))  # 注意: 下記の注記を参照
    blocks, rest = parse_partial(rest + data)
    for b in blocks:
        handle(parse_block(b))

    time.sleep(0.3)
```

> オフセットについて: 上記は説明用。正確には **バイナリモード**で開いて逐次デコードし、
> マルチバイト UTF-8 でもバイトオフセットを厳密に保つこと。最後の完全なブロックの
> 末尾オフセットを保持し、そこから `seek` し直す。

### 6.2 LLM への入力

セッション NOTE を最初に 1 回渡し、以降は新しいキューを流す:

```
System: 会議のチェックリストを管理する。文字起こしセグメントを
        JSON {"id":..,"start":..,"end":..,"speaker":..,"text":..} で受け取る。
        {"checks":[{"id":..,"done":bool,"evidence":..}]} の JSON で返す。
User:   <前回以降の新しいセグメント>
```

新しく確定したキューのみ送ればよい。キュー ID が単調増加なので、消費側は
最後に処理した ID を保持して差分を送れる。

---

## 7. 互換性の注記

- 本ファイルは正しい WebVTT。任意の WebVTT パーサはキューを読み、NOTE を無視する。
  キュー間に挟まる `NOTE heartbeat` は合法。
- 完成した `.vtt` は録音に添付して VLC で再生でき、ffmpeg（`-i in.vtt`）で動画に
  焼き込める。
- タイムスタンプがセッション相対なので、完成ファイルは自己完結している。
  `session.started` と相対時刻から絶対時刻が得られる。
- 標準の `<v>` 以外に独自のキュー設定・タグを使わない。

---

## 8. バージョニング

本ドキュメントはバージョン 1.0。前方互換の追加は以下に限る:

- 新しいヘッダフィールド（`X-WINREALTIMEWHISPER-` 接頭辞付き）、
- `NOTE session` 内の新しいキー、
- 新しい NOTE 種別（消費側は未知の種別を無視する）。

生成側は同一メジャーバージョン内でキューの構文や既存フィールドの意味を
変更してはならない（MUST NOT）。

---

## 9. 最小契約のまとめ

1. WebVTT。ヘッダ、`NOTE session`、キュー、`NOTE heartbeat`、`NOTE session_end`。
2. 各ブロックは空行で終端し、アトミックに書く。
3. キュー ID はセッションごとに 1 から単調増加。
4. タイムスタンプはセッション相対。実時刻は `NOTE session` から。
5. 1 セッション 1 ファイル。書き込み中は `.live.vtt`、完了時に `.vtt` へリネーム。
6. `current.txt` が現ファイルを指し、アトミックに更新される。
7. 消費側は完全なブロックのみ処理し、ポインタ切り替え時に旧ファイルを drain する。
