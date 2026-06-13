# External-Sync Playback Pipeline

An alternative `IMediaController` implementation that takes audio out of FFME's
hands and owns A/V sync explicitly via NAudio + a feedback loop on FFME's
`SpeedRatio`. Selected by `SoftSledConfig.UseExternalSyncMode = true`.

## Why this exists

FFME's `MediaElement` runs an internal sync engine that locks the video clock
to its audio buffer. Two pre-existing pains motivated bypassing it:

1. **Server-side trick play freezes video.** WMPNss / DLNA servers stop
   sending audio at any rate ≠ 1×. FFME's audio buffer drains, the renderer
   enters `SYNC-BUFFER`, video freezes despite plenty of decoded frames
   queued. Workaround #1 (`AudioSilenceInjector`) papered over this by
   synthesising audio MAUs while the wire was muted — works, but is fragile
   (mistakes normal buffering for trick play and produces a rapid-repeat
   artefact when the wire resumes).

2. **First-MAU offset shows as lip-sync skew.** Recorded TV servers send the
   prior IDR (typically ~1.5 s before the play position) so the decoder has a
   valid reference, while audio starts at the play position. FFME with
   `IsTimeSyncDisabled = true` (needed to survive trick play) just plays both
   at their own clocks → permanent skew. With `IsTimeSyncDisabled = false`
   audio-locking works at 1× but reintroduces problem #1.

External-sync mode sidesteps both. We decode audio ourselves into NAudio (the
device's actual playback position is the authoritative master clock), feed
FFME video-only raw bytes (no muxer roundtrip, no audio to sync to), and
nudge `Media.SpeedRatio` in a feedback loop to keep the two in step.

## Data flow

```
   ┌─────────────┐                              ┌──────────────────┐
   │  RTSPClient │ ── audio MAU + rtpTs ──────► │ External-audio   │
   │             │      (bypass muxers)         │ consumer hook    │
   │             │                              └────────┬─────────┘
   │             │                                       │
   │             │ ── first vid MAU ──┐                  ▼
   │             │   timestamp        │      ┌──────────────────────┐
   │             │   captured         │      │ LibAvAudioDecoder    │
   │             │                    │      │ (libav avcodec       │
   │             │                    │      │  + swr_convert       │
   │             │                    │      │  → 48 kHz s16 stereo)│
   │             │                    │      └──────────┬───────────┘
   │             │                    │                 │ PCM + ptsMs
   │             │                    │                 ▼
   │             │                    │      ┌──────────────────────┐
   │             │                    │      │ NAudioMasterRenderer │
   │             │                    │      │ (BufferedWaveProvider│
   │             │                    │      │  + WaveOutEvent)     │
   │             │                    │      │  ── ms position ─────┼──┐
   │             │                    │      └──────────────────────┘  │
   │             │                    │                                │
   │             │                    │      ┌──────────────────────┐  │
   │             │                    │      │ ExternalSync         │  │
   │             │                    └─────►│ MediaController      │◄─┘
   │             │                           │ - holds renderer to  │
   │             │                           │   align A/V at start │
   │             │                           │ - sync timer (250ms) │
   │             │                           │ - SpeedRatio nudges  │
   │             │                           │ - IMediaController   │
   │             │                           │   surface for AvCtrl │
   │             │                           └──────────┬───────────┘
   │             │                                      │
   │             │ ── video MAU (raw NAL/MPEG VS) ──┐   │ SpeedRatio
   │             │   via TrySetupMpvVideoProducer / │   │ nudges
   │             │   TrySetupH264VideoProducer →    ▼   ▼
   │             │   AsfStreamProducer →     ┌──────────────────────┐
   │             │   AsfFfmeInputStream  ───►│ FFME MediaElement    │
   │             │   (ForcedInputFormat=     │ - elementary-stream  │
   └─────────────┘   "mpegvideo"/"h264")     │   demuxer, no muxer  │
                                             │   roundtrip          │
                                             │ - video-only input   │
                                             │   (audio bypassed)   │
                                             │ - Position read by   │
                                             │   sync controller    │
                                             └──────────────────────┘
```

## Components

### `LibAvAudioDecoder`
Wraps `avcodec_send_packet` / `avcodec_receive_frame` for a given
`AVCodecID`, plus `swr_convert` to a canonical 48 kHz int16 stereo output.
One background thread; bounded packet queue; events `OnFormatReady` (fires
once the decoder reveals its native format) and `OnPcm(bytes, len, ptsMs)`
per decoded frame.

Codec ID chosen by `ExternalSyncMediaController.OnAudioCodecCommitted` based
on the wire codec + SDP fmtp layer hint (e.g. `VND.MS.WM-MPA` with `layer=2`
→ `AV_CODEC_ID_MP2`).

### `NAudioMasterRenderer`
`BufferedWaveProvider` + `WaveOutEvent`. Two key responsibilities:

- **Authoritative position.** `GetMediaTimeMs()` returns
  `device.GetPosition() × 1000 / averageBytesPerSecond` — i.e. the number of
  milliseconds the audio device has *actually* played. 0-relative so it
  matches WMC's expected "elapsed since stream start" semantics and FFME's
  `MediaElement.Position` for direct comparison.

- **Pre-roll hold.** Optional `startHeld = true` queues incoming PCM in
  `_holdQueue` instead of writing to the provider; `ReleaseHold(skipMs)`
  discards the first `skipMs` of held audio, drains the rest, starts the
  device. Unsatisfied skip carries over via `_pendingSkipBytes` to the next
  `WritePcm` calls.

### `ExternalSyncMediaController`
Implements `IMediaController`. Owns the decoder + renderer + sync timer.
Subscribes to `RTSPClient.SetExternalAudioConsumer` for audio MAU routing
(bypasses the FFME muxer cascade entirely) and to the FFME `MediaElement`'s
`MediaOpened` / `MediaClosed` for video sync events.

### `ExternalAudioFormat` (DTO)
Wire codec + MPEG layer + sample-rate / channel hints, parsed from SDP fmtp
at codec-commit time. Lets the controller pick the right libav decoder ID
without having to wait for the first decoded frame.

## Sync algorithm

### Wire-PTS anchoring
At first sync tick:
```
wireOffset = audioFirstWirePtsMs - videoFirstWirePtsMs
```
Stored as `_wireOffsetMs`. Encodes the server's *intended* sync — e.g. when
the server sends prior IDR ~1.5 s before the play position, video's first
wire-PTS is 1.5 s earlier than audio's, so `wireOffset = +1500 ms`. Adding
this to the drift formula targets the server's truth rather than "whatever
skew the streams happened to have when video opened".

### Drift formula
```
drift = audioElapsed - videoElapsed + wireOffset - AudioSyncOffsetMs
```
Where `audioElapsed` / `videoElapsed` are deltas from the baselines captured
at the first valid sync tick. `drift > 0` → audio is ahead of where it
should be relative to video → speed video up. Inverse for negative drift.

### Deadband hysteresis
- Enter deadband when `|drift| < 35 ms`.
- Exit deadband when `|drift| > 75 ms`.
Prevents the controller oscillating between "neutral" and "edge-of-nudge"
when drift hovers around a single threshold.

### Proportional control
```
frac = drift / 1000 ms                     (1 s drift = ±0.1 fractional speed)
frac = clamp(frac, -0.05, +0.05)           (±5 % rate ceiling)
SpeedRatio = 1.0 + frac
```
1 s of drift closes in ~1 wall-second at the rate ceiling. Larger drifts
saturate at ±5 %.

### Pre-roll hold (Phase 2.6)
When a video element is attached, the renderer is constructed with
`startHeld = true`. Audio MAUs flow into the decoder and the decoder's PCM
output accumulates in the renderer's `_holdQueue` — but NAudio isn't
started, so the speakers are silent.

When `MediaOpened` fires on the video element, the controller computes
`skipMs = max(0, videoFirstWirePtsMs - audioFirstWirePtsMs)` and calls
`renderer.ReleaseHold(skipMs)`. Result: NAudio's first audible sample is at
the wire-PTS that matches video's first visible frame. No 20-second sync
ramp at session start.

Watchdog: if `MediaOpened` doesn't fire within 3 s (audio-only sessions, or
video failed to open), the renderer releases with `skipMs = 0` to avoid
sitting silent forever.

## RTSPClient hooks

External-sync mode plugs into RTSPClient via three additions:

| API | Purpose |
|---|---|
| `SetExternalAudioConsumer(codecCommit, mauArrived)` | Hand audio MAU routing to the controller. When set, audio MAUs bypass the muxer cascade entirely. `codecCommit` fires once with an `ExternalAudioFormat`; `mauArrived` fires per MAU. |
| `FirstVideoMauWirePtsMs` (property) | Captured on the first video NAL via `Interlocked.CompareExchange` in `videoDepacketizer.NalUnitReady`. -1 until set. Used by the controller to compute `wireOffset`. |
| `FinalizePipelineSetup` early branch | When external-sync is active (`IsExternalAudioActive`), skips combined PS/TS pipelines and dual audio FFME pipelines — only sets up the video-only producer so FFME decodes/renders video without trying to consume audio. |

## Configuration

| Key | Type | Default | Effect |
|---|---|---|---|
| `UseExternalSyncMode` | bool | `false` | Selects `ExternalSyncMediaController` instead of `FfmeMediaController` at session start. |
| `AudioSyncOffsetMs` | int | `0` | User-tunable A/V trim, clamped to ±500 ms. **Current pacer semantics (post-FFME-removal):** added to the cross-stream offset (`offset = rtpInfoOffsetMs + AudioSyncOffsetMs`), and the pacer releases a video frame when `framePts-pts0 ≤ master + offset`, so a **larger (more positive) value releases video EARLIER → reduces video-lags-audio**. (The old "positive ⇒ video lags more" note described the removed FFME SpeedRatio path and was inverted for the pacer.) Adjustable live in-session via **Ctrl+] / Ctrl+[** (20 ms steps; ] = video earlier), which persists back here, or in Settings → Audio. |

## Supported wire codecs

| Wire codec | SDP fmtp | Libav decoder | Status |
|---|---|---|---|
| `MPA` (RFC 2250 MPEG audio) | `layer=3` | `AV_CODEC_ID_MP3` | ✅ Phase 1 |
| `VND.MS.WM-MPA` (WMRTP MPEG audio) | `layer=1\|2` | `AV_CODEC_ID_MP2` | ✅ Phase 2 |
| `VND.MS.WM-MPA` (WMRTP MPEG audio) | `layer=3` | `AV_CODEC_ID_MP3` | ✅ Phase 2 |
| `VND.MS.WM-MPV` (MPEG-1/2 video) | — | FFME `mpegvideo` demuxer | ✅ Phase 2 (video via FFME, sync controller drives `SpeedRatio`) |
| `X-WMF-PF` w/ PCM fmtp | `audio/vnd.wave` | `AV_CODEC_ID_PCM_S16LE` etc. | 🚧 Phase 3 |
| `X-WMF-PF` w/ H.264 fmtp | `audio/x-ms-…` | FFME `h264` demuxer | 🚧 Phase 3 |
| `VND.MS.WM-AC3` | — | `AV_CODEC_ID_AC3` | 🚧 Phase 3 |

## Phase history

- **Phase 1** — MP3 audio-only via libav + NAudio. Validated decoder +
  renderer + `IMediaController` surface. No video, no sync controller.
- **Phase 2** — added MP2 / VND.MS.WM-MPA, video-via-FFME, sync controller
  with proportional `SpeedRatio` nudges and a 250 ms tick.
- **Phase 2.5** — wire-PTS anchoring (controller targets server's intended
  sync), tighter recovery time (2000 → 1000 ms), deadband hysteresis (±35 /
  ±75), SpeedRatio-honour diagnostic in the per-tick log.
- **Phase 2.6** — pre-roll audio hold eliminates the ~25 s visible re-sync
  ramp at session start. Audio decoder runs, output queues until video opens,
  then drops the wire-PTS-skew worth of held audio so first audible sample
  matches first visible frame.
- **Phase 3** (next) — codec coverage: PCM (X-WMF-PF), AC3, H.264 video.
  Retire the muxer paths once we're confident external-sync covers every
  codec combination we see in the captured corpus.

## Operator notes

- When debugging sync issues, the `[ext-sync] sync:` log line is the
  authoritative source. Each tick logs `audE`, `vidE`, `drift`, `req`, and
  `actual` (measured video advance / wall advance). If `actual ≠ req` for an
  extended window, FFME isn't honouring `SpeedRatio` on the current stream
  (worth investigating).
- `[naudio-master] hold released:` shows the pre-roll skip in bytes and any
  carry-over. If `pendingSkipCarryover` is non-zero the renderer is still
  consuming skip from subsequent `WritePcm` calls — normal when video opens
  before enough audio has been decoded to absorb the full skip.
- The legacy FFME path (`UseExternalSyncMode = false`) remains intact and
  uses `AudioSilenceInjector` for trick-play survival. Both paths share
  `RTSPClient` and the muxers; only the controller selection + audio MAU
  routing differs.
