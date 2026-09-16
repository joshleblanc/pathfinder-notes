# pathfinder-notes — live transcription via MiniMax ASR

A Windows .NET 8 console app that records the user's microphone **and** all
audio playing on the computer, runs it through the
[MiniMax ASR](https://platform.minimax.io/docs/api-reference/speech-to-text)
API in real time, and writes a rolling transcript to disk as it goes.

## How it works

1. Two NAudio captures pull 10 ms blocks from:
   - the **microphone** (WASAPI capture), and
   - the **default speaker** (WASAPI loopback — captures *whatever* is playing
     on the selected render device. No virtual audio cable, no "Stereo Mix" needed).
2. Each stream is mixed down to mono and resampled to 16 kHz, then written
   into a fixed-size 30-second ring buffer.
3. Every `CHUNK_SECONDS` (default 30 s — well under the API's 500 s / 50 MB limit)
   the main loop snapshots both buffers, sums them with simple clipping,
   encodes to 16-bit PCM mono WAV, and uploads to the ASR endpoint.
4. The returned `text` is appended to the session transcript file and printed
   to the console as it arrives.

Output files:

- `transcripts/transcript_<session-id>.txt` — clean rolling transcript (just text).
- `transcripts/transcript_<session-id>.log` — verbose log with chunk timestamps.

## Setup

```powershell
cd C:\source\pathfinder-notes
copy .env.example .env
notepad .env                                # paste MINIMAX_API_KEY
dotnet restore
dotnet build
dotnet run -- --list-devices               # confirm your mic + speaker are listed
dotnet run                                 # start the recorder
```

The first `dotnet run` will also restore the `NAudio` NuGet package.

## Run

```
session      2026-09-16_14-05-30
api url      https://api.minimax.io/v1/speech_to_text
model        asr-1.0
chunk        30s  @ 16000 Hz mono
mic device   Headset Microphone (CORSAIR HS80 RGB Wireless Gaming Headset)
loopback     Headset Earphone (CORSAIR HS80 RGB Wireless Gaming Headset)  (system audio)
transcripts  C:\source\pathfinder-notes\transcripts\

recording…  (Ctrl-C = graceful stop + final flush; Ctrl-C twice = force exit)

[14:06:00] chunk 1 (30.0s, 2 speakers) ✓ "[S1] Hello everyone, let's record a quick test…"
[14:06:32] chunk 2 (30.0s, 1 speaker)  ✓ "[S1] Second chunk of audio here…"
```

## Graceful shutdown — partial chunks are not lost

When you hit Ctrl-C mid-chunk, the program:

1. **First Ctrl-C** — soft stop. The main tick loop is cancelled but the captures
   keep streaming for a moment so the in-progress partial chunk can be rescued.
   Any audio that arrived **since the last regular tick** is sliced off the ring
   buffers, mixed, encoded, and uploaded as one final chunk (a few hundred ms
   to ~30s, depending on where in the chunk you were). If less than ~1 second
   of new audio is pending, the flush is skipped — not worth the API call.
2. **Second Ctrl-C** (during the final flush) — hard exit. The flush is in
   flight; you asked, so we bail. The session files already on disk are kept
   intact; only the final partial upload is cut short.

The mechanics:

- `_linkedCts` signal-cancels the loop *without* stopping the captures.
- `FinalFlushAsync` (inside `RecordingService.RunAsync`) stops the captures,
  drains 150 ms, computes the delta = last-snapshot → current, slices the
  trailing samples, then runs the same encode + upload path the regular tick
  uses.
- If the API upload itself hangs, `HttpClient.Timeout = 2 min` bounds it.
  Hard Ctrl-C remains the escape hatch.

**Notes**

- The *first* chunk after startup is always a full `chunk_seconds` window
  (no final-flush logic applies). Only the *last* partial chunk benefits from
  this — anything already transcribed is in the `.txt` / `.log` files.
- If the upload itself is what's slow (your internet, API latency), the
  graceful path adds the upload time to your Ctrl-C latency. Press Ctrl-C
  twice to skip it.

The clean transcript file looks like:

```
# Live transcription session started 2026-09-16_14-05-30
# MiniMax ASR model=asr-1.0 response_format=verbose_json
# speakers tracked per segment

[S1] Hello everyone, let's record a quick test.
[S2] Sure, go ahead.
[S1] Okay, here we go.
…
```

The matching log file appends per-segment start times plus the API `trace_id`
for support cases:

```
[14:06:00] chunk 1 (30.0s, 2 speakers, trace=021785…)
   @ 0.10s [S1] Hello everyone, let's record a quick test.
   @ 2.50s [S2] Sure, go ahead.
   @ 4.20s [S1] Okay, here we go.
```

## Pathfinder 2e session recap (opt-in)

After the last ASR call (regular tick or final flush), you can have the
program ask MiniMax's language model to fill in a Pathfinder 2e session recap
markdown. It's opt-in because every call costs tokens.

**Enable:** either `--summarize` on the CLI, or `ENABLE_SESSION_SUMMARY=true`
in `.env`.

The recap is generated from the **`.txt` transcript file** (the speaker-labeled
clean text) plus a markdown template that defines the structure. The default
template — `pf2e-session-template.md`, shipped next to the binary — has placeholders
for the typical PF2e GM bookkeeping: At-a-Glance, Summary, Encounters table,
Treasure, Story Beats, NPCs, Locations, Cliffhanger, XP, Notes. Open it in
any editor and tweak as you like; pass a custom path with
`--summary-template PATH`.

Output is saved to `transcripts/summary_<session>.md`. On failure the program
prints `[summary] FAILED — …` and exits cleanly — your transcripts are
unaffected.

**Validate before spending credits:** `--summarize-test <transcript.txt>` builds
the exact prompt that would go to the LLM and writes it to
`transcripts/_summarize_test/prompt_<hhmm>.txt` for inspection. No network call.

**Re-summarize an existing transcript on demand:**

```powershell
dotnet run -- --summarize-from transcripts\transcript_2026-09-16_11-50-04.txt
```

This reads an existing `.txt` transcript and writes `summary_<session-id>.md`
next to it (session id is taken from the filename). Useful when:

- you recorded without `--summarize` and want the recap now,
- you want to compare the recap from two different LLM models on the same audio,
- you want to swap out the template and re-cap an old session.

Optional flags: `--summary-model`, `--summary-api-url`, `--summary-template`,
`--summary-max-tokens`. They behave the same way as during a live run.

## Transcribe an existing audio file

```powershell
dotnet run -- --transcribe-file C:\path\to\meeting.mp3
```

Same pipeline as the live recorder, just driven from disk:

1. `MediaFoundationReader` (preferred — handles MP3 / M4A / AAC / OGG / FLAC)
   with `WaveFileReader` as a fallback for plain WAV.
2. Downmixes multi-channel files to mono and resamples to 16 kHz via
   `WdlResamplingSampleProvider`.
3. Streams the file through the same `chunk_seconds`-windowed upload path,
   appending to `transcripts/transcript_<session-id>.{txt,log}`.
4. Drains the tail (< 1 s is skipped, ≥ 1 s is one final chunk).
5. If `--summarize` is on (or `ENABLE_SESSION_SUMMARY=true`), generates the
   PF2e recap at the end.

Works on any file Windows can play in Media Player. The 500 s / 50 MB ASR
limits still apply, so default `chunk_seconds=30` keeps each upload
comfortably small.

## Speaker labels

Each chunk's `S1` / `S2` / `S3` labels are independent — the API does **not**
track a speaker's identity across chunks. Within one chunk the same label
always refers to the same voice. If you need cross-chunk identity (e.g. "S1 in
chunk 1 is the same person as S1 in chunk 7"), that work is on you — encode
the chunk's audio as the chunk's stable identifier, not the segment label.

## CLI flags

```
--list-devices                                show mics + render devices and exit
--mic "name"                                  override default mic
--loopback "name"                             override default render device for loopback
--no-loopback                                mic-only mode
--chunk-seconds N                            upload every N seconds (default 30, max 500)
--language "en"                              BCP-47 tag (en/zh/ja/…); omit for mixed
--response-format json|verbose_json          default verbose_json
--api-url URL                                override MiniMax endpoint
--max-minutes N                              auto-stop after N minutes (0 = off)
--summarize                                  generate a Pathfinder 2e session recap on shutdown
--summary-model NAME                         LLM model (default MiniMax-M3)
--summary-api-url URL                        chat endpoint (default the Anthropic-Messages URL)
--summary-template PATH                      path to your custom recap template
```

## Env vars (`.env` or process env)

| Variable | Default | Notes |
|---|---|---|
| `MINIMAX_API_KEY` | *(required)* | Bearer token |
| `MINIMAX_API_URL` | `https://api.minimax.io/v1/speech_to_text` | override for self-host / proxy |
| `ASR_MODEL` | `asr-1.0` | only model currently exposed by the API |
| `ASR_LANGUAGE` | *(unset)* | `en` / `zh` / `ja` / … leave unset for mixed/auto |
| `ASR_RESPONSE_FORMAT` | `verbose_json` | `json` / `verbose_json` — verbose_json adds per-segment `start`/`end`/`speaker`/`text`; cannot be combined with streaming |
| `CHUNK_SECONDS` | `30` | safe upper bound is `500`; default keeps API latency low |
| `TRANSCRIPT_DIR` | `transcripts` | where session files land |

CLI flags override env vars for the same setting.

## Known limits (from the API)

- Each request must be **≤ 500 s** and **≤ 50 MB**.
- WAV PCM16 mono 16 kHz ≈ **960 KB per 30 s chunk** — well inside both.
- `verbose_json` returns per-segment timestamps + speaker ids (`S1`/`S2`/`…`).
  Cannot be combined with `stream=true` (we don't use streaming).

## Troubleshooting

- **`loopback device not found`** — the speaker you named has no WASAPI loopback
  endpoint. Run `dotnet run -- --list-devices` to see valid render devices.
- **`401 authorized_error`** — `MINIMAX_API_KEY` missing or wrong.
- **`exceeds the limit of 500s`** — lower `CHUNK_SECONDS` (max valid is 500).
- **Noisy / both streams overlap** — pass `--no-loopback` to drop system audio,
  or speak louder than the playback.
