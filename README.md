# WinRealtimeWhisper — Real-time transcription (Windows 11 / .NET Framework 4.8)

**English** | [日本語](README.ja.md)

A small tool that transcribes speaker output (WASAPI loopback) and microphone
audio with **local Whisper**, showing results in real time while saving them as
text and WAV.

**No API keys and no network are required.** All recognition runs on your PC.

## Layout

Source code lives under `src/`.

| Path | Role |
| --- | --- |
| `src/WinRealtimeWhisper.csproj` | .NET Framework 4.8 / WinForms / x64 project |
| `src/Program.cs` | Entry point (global exception dialog) |
| `src/MainForm.cs` | Main window (menu bar, recording controls, real-time view) |
| `src/SettingsForm.cs` | Settings dialog (General, Audio, Model, Storage) |
| `src/HistoryForm.cs` | History window (list and content preview) |
| `src/AppIcons.cs` | App icon drawn at runtime |
| `src/TranscriptionEngine.cs` | Capture devices, WAV writing, level display, start/stop |
| `src/WhisperRecognizer.cs` | Audio segmentation and Whisper inference (two threads) |
| `src/AudioPipeline.cs` | float samples → 16 kHz mono conversion |
| `src/WhisperModelStore.cs` | ggml model download and placement |
| `src/TranscriptionSession.cs` | Session line management, plain text, history saving |
| `src/AppSettings.cs` | Reads/writes `%LOCALAPPDATA%\WinRealtimeWhisper\settings.json` |
| `src/DiagLog.cs` | Diagnostic log output |
| `src/Loc.cs` | Japanese/English UI string table (`Loc.T("key")`) |
| `src/CommandLineOptions.cs` | Command-line argument parsing |
| `src/HeadlessRunner.cs` | GUI-less execution |
| `src/NativeConsole.cs` | Attaches the WinExe to the parent console |
| `Directory.Build.props` | Keeps `bin/` and `obj/` at the repository root |
| `tools/SmokeTest` | Tests for conversion, segmentation, inference, and dialogs (not part of the app build) |
| `installer/WinRealtimeWhisper.iss` | Inno Setup script for the installer |
| `installer/license.txt` | License shown by the installer |
| `.github/workflows/build.yml` | CI: build, CLI smoke tests, installer, release |

## Build

```powershell
dotnet build src/WinRealtimeWhisper.csproj -c Release
```

Output lands in `bin\WinRealtimeWhisper\Release\net48\` (test builds go to
`bin\SmokeTest\Release\net48\`). `Directory.Build.props` keeps all build output
tidy under the repository root.

`Whisper.net` and `Whisper.net.Runtime` are pinned to 1.9.1.
`Whisper.net.Runtime` contains the native `whisper.dll` / `ggml-*.dll`, which are
copied into `runtimes/win-x64/` at build time.
To keep the distribution small, `src/WinRealtimeWhisper.csproj` keeps only the win-x64 natives
(about 4.6 MB total instead of ~20 MB) and drops the macOS-only Metal shader.

### Installer

[Inno Setup 6](https://jrsoftware.org/isinfo.php) is required.

```powershell
dotnet build src/WinRealtimeWhisper.csproj -c Release
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" `
  -DAppVersion=1.0.0 installer\WinRealtimeWhisper.iss
```

The installer lands in `bin\installer\WinRealtimeWhisper-1.0.0-setup.exe`.
It installs per-user by default (`PrivilegesRequired=lowest`), so no admin rights
are needed; an elevated install is offered through the dialog if you want it.
The setup language is selectable between Japanese and English.

A silent install is useful for testing:

```powershell
.\bin\installer\WinRealtimeWhisper-1.0.0-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /DIR=C:\temp\winrealtimewhisper
C:\temp\winrealtimewhisper\unins000.exe /VERYSILENT
```

### Continuous integration

`.github/workflows/build.yml` runs on GitHub Actions (`windows-2022`):

| Job | What it does |
| --- | --- |
| `build` | Builds Release, asserts only win-x64 natives shipped, runs CLI smoke tests, uploads the build output |
| `installer` | Compiles the Inno Setup installer and uploads it |
| `release` | On a `v*` tag, attaches the setup and a portable ZIP to a draft GitHub Release |

The version comes from the tag on a tag build, and from `<Version>` in
`src/WinRealtimeWhisper.csproj` otherwise. To cut a release:

```powershell
dotnet build src/WinRealtimeWhisper.csproj -c Release   # confirm locally first
git tag v1.0.0
git push origin v1.0.0
```

## Models

The default is **`ggml-small.bin` (488 MB)**. Pick `tiny` / `base` / `small` /
`medium` on the **Model** tab of the settings dialog.

If a model has not been downloaded yet, a confirmation dialog appears at startup
or when you start recording, and the download runs automatically (progress is
shown in the status bar). To install manually, download from the URL below and
place it in `%LOCALAPPDATA%\WinRealtimeWhisper\models\`.

<https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin>

**On a PC without a GPU, `small` is the practical ceiling.** If it feels slow,
dropping to `base` or `tiny` makes a large difference.

## Usage

1. Launch `WinRealtimeWhisper.exe` (this is an x64 build).
2. On first run, a model download prompt appears. Choose **Yes** to fetch it.
3. If needed, open **Tools > Settings** to change the audio source and model.
4. Press **Start recording** (`F5`). Audio is recognized per segment, and
   finalized lines appear as they are produced.
5. Press **Stop recording** (`F6`). The app **transcribes all remaining audio**
   before finishing.

### Menu

| Menu | Item | Default shortcut |
| --- | --- | --- |
| File | Start recording | `F5` |
| File | Stop recording | `F6` |
| File | Exit | `Alt+F4` |
| Tools | History... | — |
| Tools | Settings... | `Ctrl+,` |
| Help | Open log / Open log folder / About | — |

### Settings dialog

| Tab | What you can set |
| --- | --- |
| General | Display language (Japanese / English) |
| Audio | Toggle loopback output and microphone capture, and choose the devices |
| Model | The ggml model to use, and download |
| Storage | Locations of text / WAV / models / logs |

Settings are saved only when you press **OK**.
Output and input devices can be chosen independently.

A display-language change takes effect **the next time you start the app**.
The recognition language (spoken language) stays Japanese.

Recognition waits for a speech pause (0.45 s of silence) before it runs, so
text appears roughly 1–3 seconds after you stop speaking. If no silence
arrives, a segment is cut at 6 seconds, so the delay never exceeds about that.

## Storage locations

| Kind | Location |
| --- | --- |
| Text (history) | `Documents\WinRealtimeWhisper\history\session_yyyyMMdd_HHmmss.txt` |
| WAV | `Documents\WinRealtimeWhisper\wav\rec_yyyyMMdd_HHmmss.wav` (not rewritten when input is a file via `-i`) |
| Models | `%LOCALAPPDATA%\WinRealtimeWhisper\models\` |
| Logs | `%LOCALAPPDATA%\WinRealtimeWhisper\logs\` |

- Text is auto-saved periodically while recording, then overwritten with the
  finalized content on stop.
- The format is plain text, one utterance per line (no timestamps).
- WAV is normalized to 44.1 kHz / 16-bit / stereo (Whisper receives 16 kHz / mono).
- History is available from **Tools > History**. Double-click an entry, or use
  **Open in editor**, to open the text file.

## Command line

Launching with no arguments opens the GUI as before. Specifying anything related
to recording or transcription runs the app **without a GUI** and writes results
to stdout, so you can redirect them to a file.

```powershell
WinRealtimeWhisper.exe --help
WinRealtimeWhisper.exe --ui-language ja --help
WinRealtimeWhisper.exe --list-devices
WinRealtimeWhisper.exe -t 60 -o interview.wav --text interview.txt
WinRealtimeWhisper.exe -t 0
WinRealtimeWhisper.exe -i speech.wav
```

| Option | Description |
| --- | --- |
| `-t`, `--seconds <sec>` | Recording length. `0` keeps going until you stop it |
| `-i`, `--input <file>` | Audio file to transcribe (no capture device is opened) |
| `-s`, `--source <both\|speakers\|mic>` | Audio source to capture |
| `-o`, `--output <file>` | Where to save the WAV |
| `--text <file>` | Where to save the transcript |
| `-m`, `--model <filename>` | ggml model to use |
| `--model-dir <folder>` | Override the model folder |
| `-l`, `--language <code>` | Recognition language (default `ja`) |
| `--ui-language <ja\|en>` | Display language for the UI and output |
| `--max-chunk <sec>` / `--silence <sec>` | Segmentation tuning |
| `--output-device <id\|name>` / `--input-device <id\|name>` | Devices to use |
| `--list-devices` | List available devices and exit |
| `-n`, `--headless` | Force GUI-less execution |
| `-h`, `--help` / `--version` | Help / version |

Console output (help, errors, progress, results) is **English by default**.
Pass `--ui-language ja` for Japanese. The GUI display language still follows the
setting (General tab).

When you pass a file with `-i`, the app reads it to the end, transcribes the
remainder, prints the result, and exits (no WAV is re-saved).

## How it works

### Recognition flow

```
WASAPI (48kHz / 2ch / 32bit float)
  ├─ Output (loopback) … settings.OutputDeviceId
  └─ Microphone        … settings.InputDeviceId
       └─ SampleConverter        … convert to 16kHz / mono / float
            └─ WhisperRecognizer
                 ├─ segment thread  … cut speech segments by silence and length
                 └─ inference thread … send segments to Whisper, emit final text
                      └─ TranscriptionEngine → MainForm (real-time view)
```

Segmentation and inference run on separate threads so that audio keeps being
buffered without loss while the CPU spends seconds on inference.

### Segmentation rules

These values are fixed.

| Rule | Default | Meaning |
| --- | --- | --- |
| Minimum segment | 3 s | Do not cut below this (too short means less context and lower accuracy) |
| Silence duration | 0.45 s | Cut when silence of this length occurs |
| Maximum segment | 6 s | Cut here even if no silence has arrived |
| Silence RMS threshold | 0.0022 | Anything below this is treated as silence |

The maximum segment length directly drives display latency. At 6 seconds,
"start of speech to finalized text" is at worst 6 s plus inference time, and it
appears sooner when a silence cuts the segment earlier.

Silence-only segments are discarded without inference (Whisper tends to
hallucinate phrases like "Thank you for watching" on silence).

### Real-time view

Whisper is batch processing per segment, not streaming recognition, so only
finalized lines accumulate in black text. Nothing appears until a speech
segment is complete.

## Troubleshooting

Logs are written here.

```
%LOCALAPPDATA%\WinRealtimeWhisper\logs\winrealtimewhisper-yyyyMMdd-HHmmss.log
```

### Check whether audio is arriving

The following line is recorded every 2 seconds.

```
[audio] cb=30 bytes=165120 fmt=48000Hz/2ch/32bit peak=0.4257(-7.4dB) rms=0.0816(-21.8dB) rtf=0.14 chunks=1 dropped=0
```

| Field | Meaning |
| --- | --- |
| `cb` | Callbacks in 2 seconds. `0` means the device is stopped |
| `peak` / `rms` | Peak amplitude / effective value. `-96dB` is silence |
| `fmt` | For loopback this is usually `48000Hz/2ch/32bit` |
| `rtf` | Inference time ÷ audio length. **Below 1 means keeping up with real time** |
| `chunks` | Number of recognized segments |
| `dropped` | Segments dropped because inference fell behind |

| Symptom | Possible cause |
| --- | --- |
| `cb=0` | Capture device could not be opened, or stopped |
| `rms` at -50 dB or lower | Zero volume, muted, or nothing playing |
| `rtf` above 1 | Insufficient CPU. Drop `small` → `base` / `tiny` |
| `dropped` increasing | Same as above |
| Text appears during silence | Hallucination. Check `text=` in the `[whisper]` line |

### Low accuracy

- Use a larger model (`tiny` → `base` → `small`).
- Check the source. Loopback only captures what the PC plays, so for microphone
  audio choose **Microphone** or **Speakers + microphone**.
- Make sure the language setting matches the audio.

## Implementation notes

- **16 kHz mono conversion**: Whisper requires 16 kHz mono float samples.
  The 48 kHz stereo float from loopback is resampled with linear interpolation,
  and channels are averaged to mono. Phase is carried across calls so audio does
  not break at buffer boundaries.
- **Segment cutting**: Leaving a little trailing silence prevents clipped word endings.
- **Avoiding duplication**: Cut samples are removed from the buffer and relative
  time is counted separately in `_baseSamples`. Segment length is taken from the
  buffer element count itself, with no separate counter (a mismatch would stop
  segments from ever being cut).
- **Stop handling**: On stop the app waits for the segment thread to finish and
  then transcribes the remainder, so speech right before stopping is not lost.

## Known limitations

- Recognition is per segment. No partial text is shown while speaking (only
  finalized sentences appear).
- WAV is not split per source; one session is mixed into a single file.
- The default microphone is used when **Microphone** is selected.
  **Speakers + microphone** opens loopback and the default microphone together.
- With **Speakers + microphone**, the same sound from both sources can be
  recognized twice.

## Appendix: other approaches considered

| Approach | Outcome |
| --- | --- |
| Azure Speech SDK | Cloud recognition. Requires an API key and network |
| `Microsoft.Windows.AI.Speech` | Local, key-free recognition, but the projection DLL ships only in Experimental builds of `Microsoft.WindowsAppSDK`. MSIX packaging is mandatory and an unpackaged EXE fails with `0x8007007E` |
| **Whisper.net** | **Adopted**. Works on net48 with no keys or network |
