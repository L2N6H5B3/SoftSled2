# Content-Clock Migration — removing the derived A/V offset

**Status:** Stage A (instrumentation) in progress.
**Started:** 2026-07-26.

## The problem this solves

A/V sync today rests on a **cross-stream offset derived once per position**
(`UpdateSyncOffset` → `_baseOffsetMs` → `PtsFramePacer._syncOffsetMs`). One
sample pair — the first audio MAU and the first decoded video frame — fixes
the relationship for the whole position.

That is fragile by construction: any discontinuity forces a re-derivation, and
a re-derivation from a bad origin pair is unrecoverable. Log
`softsled-20260725-193615` is the worked example. WMC issued an unattended
mid-stream `Start(StartTime=194360)` (a live-buffer reposition, not a user
seek). A stale pre-seek frame latched the video origin (`ptsMs=231776`) while
the pacer anchored a post-seek `pts0=238851` — a 7075 ms origin mismatch that
produced `CONTENT offset = 10251ms` instead of ~0 and desynced playback until
the media was stopped.

The two ordering bugs behind that specific failure are fixed (see
`ArmAnchorGate`), but the *class* of bug remains as long as sync depends on a
derived quantity that must be recomputed whenever a stream breaks and resumes.

## The insight

The wire RTP timestamp is the ASF **Send Time** — a transmission clock, not a
presentation clock (MS-RTSP §2.2.1.2). It keeps advancing through stalls,
pauses and repositions regardless of content. Three separate compensations
exist today purely to work around that: `_resumePtsShiftMs`, the
`(content, wire)` reference-pair re-expression in `UpdateSyncOffset`, and the
offset derivation itself.

But the B57532D6 payload extension carries a 10 MHz **file-global content
presentation clock**, and we already parse it per MAU (`EventData.ContentMs`).
Both streams sit on that one timeline. So the offset is not a property of the
streams at all — it is an artifact of the pacer reasoning in wire-PTS +
audio-byte space instead of content space.

## Evidence (measured, not assumed)

Gathered via the `[content-clock]` survey added for this purpose.

| | H.264 recorded | H.264 live | MPEG-2 |
|---|---|---|---|
| log | `20260726-070048` | `20260726-092354` | `20260726-093946` |
| audio MAUs | 142,591 — **0 absent** | 1,775 — **0 absent** | 13,549 — **0 absent** |
| video MAUs | 150,567 — **0 absent** | 1,909 — **0 absent** | 7,940 — **0 absent** |
| `contentAdv` vs `wireAdv` | exact (5055/5055) | exact | exact (5016/5016) |
| `content/real` | ~1.00 | ~1.00 | 1.003 / 1.017 |
| drift steps | 3 (all genuine) | 1 (media change) | **0** |

Coverage is **100 % on every MAU of both streams, both codecs, both wire
clocks** (1 kHz x-wmf-pf and 90 kHz MPEG-2), recorded and live.

**The decisive property** — the content clock does *not* jump when the wire
does:

```
07:03:27  audio: wire +2024ms, content +42ms
07:48:29  audio: wire +3316ms, content +43ms   (video identical)
```

Those are delivery stalls. Send Time ran on; content correctly did not. That
is exactly the behaviour the redesign depends on.

**Expected non-faults:**

* MPEG-2 video `backwards = 25 %` of MAUs. `has_b_frames=1` and
  `decodeOrderRtp` is non-monotonic — MAUs arrive in *decode* order carrying
  *presentation* content times. This confirms `ContentMs` is a presentation
  clock, and is why libav must do the reordering (see Risk 2).
* MPEG-2 audio/video drift sit 24 ms apart (near-common epoch); H.264 ~25 s
  apart (independent epochs). Both are fine — only the *content* values are
  ever compared.

## Target rule

```
release video frame when:   frame.ContentMs <= audibleContentMs + trim
```

`trim` (`_liveTrimMs`) survives unchanged as the device-latency knob. No
cross-stream quantity is derived, so there is nothing to re-derive when a
stream drops out and returns.

### Why this is resilient by construction

| event | today | after |
|---|---|---|
| audio stalls | master clock stalls (works) | audible content stalls — same |
| video stalls | pacer starves, drift accumulates | frames resume at true content time |
| WMC reposition | offset re-derived from one origin pair — **the failure mode** | each stream re-enters at its own content time |
| stale pre-seek frame | latches origin → permanent desync | stale content → past-due dropped, self-correcting |

A stale frame can no longer cause a permanent fault, only a few discarded
frames.

## Staging

Each stage is independently testable and revertable.

### Stage A — instrument only, zero behaviour change

Maintain a per-stream wire→content drift (proven constant within a position,
stepping only at genuine discontinuities). Expose audible wire-pts on the
renderer and the last released frame's pts on the pacer, then log per second
what the content rule *would* decide versus what actually happened.

**Success criterion:** `shadowDelta` stays near zero through steady play, a
seek, and a stall. If it diverges, the model is wrong and we stop here having
changed nothing.

Stage A also *measures* Risk 3 rather than assuming it away.

### Stage B — plumb real content time

Split into B1 (audio) and B2 (video) because the plan's original video approach
collided with the still-live offset rule (see B2).

**B1 — audio (DONE 2026-07-26).** `OnAudioMau → SubmitPacket(data, …, contentMs)
→ OnPcm(…, contentMs) → WritePcm` fed content time (falling back to wire only
for a stream with no content clock). The renderer's gap tracker + reference pair
now run in content units; `AudibleWirePtsMs` → `AudibleContentMs`, exact and
drift-free. The byte master clock (`GetMediaTimeMs`) is byte-derived and
untouched, so the offset rule and WMC `Position` are unaffected. This also fixed
a latent shadow bug: the old reconstruction mixed resume-shifted wire
(`audibleWire`) with unshifted-wire drift (`aDrift`) — correct only while
`_resumePtsShiftMs == 0`. Confirmed: 144/160 shadow samples ≤50 ms, axisSkew 0.

**B2 — video (DONE 2026-07-26).** *Deviation from the original plan:* NOT
`pkt->pts = contentMs`. That breaks the offset rule, which captures
`_firstVideoMauRtpRaw` / `_firstVideoMauWirePtsMs` from the decoded frame's pts
for its RTP-Info path and content-ref re-expression — both assume WIRE units.
Reorder can only track one timeline via pts, and the offset rule needs it to be
wire until Stage D. So instead the pacer reconstructs each frame's content as
`framePts + videoDrift`, where `videoDrift` (content − wire) is measured
continuously by the content-clock survey and is constant within a position
(exact reconstruction). `PtsFramePacer.SetVideoContentDrift` + `LastReleasedContentMs`.
No decoder or offset-rule change.

*Resume-shift interaction (FOUND + FIXED 2026-07-26, log 20260726-211912).*
A 15.7 s pause produced a −15725 ms `_resumePtsShiftMs`. In content mode that was
catastrophic: the shift cancels the real Send-Time jump in the DECODER path (so
`vidPts` stays continuous) while the survey samples UNSHIFTED wire (so `vDrift`
steps −15725), and `frameContent = vidPts + vDrift` collapsed ~13 s → every frame
past-due → video raced at 150 fps while audio was fine.

Fixed two ways together (Stage C content path only; offset path untouched):
1. **Content mode skips the resume-shift** — the content clock doesn't jump across
   a pause, and the survey tracks the real wire step, so cancelling it is both
   unnecessary and harmful.
2. **Frame content is baked at EMIT time**, not release: the `OnFrame` handler
   submits `wirePts + drift` as the pacer's timestamp. This makes each frame
   immune to a mixed pre/post-pause queue — a single release-time drift would
   mis-value one side of the pause. The pacer's content branch then compares the
   stored content directly (no drift), and the buffer-overflow check runs on the
   continuous content span rather than the jumping wire span.

Audio never had this (content fed directly since B1). The offset path still uses
the shift and is unchanged.

### Stage C — flip the pacer (DONE 2026-07-26, behind flag, awaiting A/B)

Config flag `SoftSledConfig.UseContentReleaseMode` (default **false** = the proven
offset rule). When true, `PtsFramePacer` releases each frame when
`framePts + videoDrift <= audibleContent + trim`, where `audibleContent` is the
renderer's `AudibleContentMs` through a dedicated smoother
(`SmoothedAudibleContentMs`, same jitter-rejection as the master smoother). No
`_pts0`, `_masterAtAnchor`, or `_syncOffsetMs` in the release path.

The offset computation (`UpdateSyncOffset`) still runs and still calls
`SetSyncOffsetMs` — but ONLY to size the jitter buffer (`|offset|` = the
video-lead the buffer must hold). The pacer ignores it for timing. This reuse
means content mode inherits the proven buffer-sizing with no new tuning.

Seek handling needs no re-derivation: `ResetMasterSmoothing` clears the content
smoother too, `AudibleContentMs` returns `MinValue` until the post-clear write
re-establishes the reference (pacer holds meanwhile), then the smoother re-seeds
to the new content position. That is the whole resilience win — a seek/reposition
is just the audible content clock jumping, which video follows automatically.

**Measured prediction to verify:** H.264 steady-state should drop from the offset
rule's ~+60 ms residual to ~0 (release is *at* audibleContent + trim). MPEG-2 was
already ~0. The `[content-shadow] shadowDelta` should sit near 0 by construction
in this mode (it *is* the release condition), so the real test is the eye + the
seek transients (positive-offset speed-through should become a clean skip).

To A/B: set `UseContentReleaseMode` true in config, restart playback; the log
prints `[ext-sync] CONTENT release mode ENABLED`.

### Stage D — delete the dead machinery

`_pts0`, `_masterAtAnchor`, `_anchored`, `_reanchorMasterOverride`,
`Reanchor()`, `_syncOffsetMs`/`_targetOffsetMs`/slew, `UpdateSyncOffset`,
`_baseOffsetMs`, `_videoCodecTrimMs`, the content-ref pairs,
`MaxPlausibleOffsetMs`/`MaxPlausibleContentOffsetMs`, `_resumePtsShiftMs`,
`_pauseStartMs`.

**Keep:** `_liveTrimMs`; `SmoothedMasterMs` (now smoothing content time);
`SilenceCountingProvider` + gap-fill/overlap-trim (now in content units — more
correct); decoder flush/backpressure; the anchor gate, demoted from *sync
correctness* to *pipeline hygiene*.

Hold the Stage C flag for a while before deleting — the cost is a little dead
code, the benefit is a one-line rollback if something surfaces on untested
content.

## Risks

1. **`Position` must stay 0-relative.** WMC's `GetPosition` adds
   `StartPayloadStartTime`; leaking absolute content time double-counts.
   *Mitigation:* leave `GetMediaTimeMs()` exactly as-is and add a separate
   property for sync. Keeps the blast radius off the WMC-facing surface.
2. **The yadif 2× trap.** On the deinterlaced path `frame->pts` is *double*
   the true value (`firstDisplayPts=266264 (bestEffort; rawPts=532528)`).
   `best_effort_timestamp` is correct and must be used exclusively; any
   fallback to raw pts doubles every MPEG-2 timestamp. Set the buffersrc
   `time_base` to match the new units.
3. **Two byte axes in the renderer.** `_authoredBytes` (includes gap fills,
   counted during hold) and `_bytesWritten` (post-hold, post-skip) diverge
   while the preroll hold drains, and `ClearBuffer` re-seeds the gap reference
   on the authored axis while the clock runs on the played axis. Any
   audible-content mapping must be defined against one axis consistently.
   Stage A logs the divergence so this is measured, not guessed.
4. **Coverage holes.** None seen in ~300 k MAUs, but keep a hold-last +
   interpolate-from-wire-delta guard per stream.
5. **Live TV.** The content clock is file-global for recordings; for live it is
   the buffer timeline. It worked, but only ~45 s of live was sampled — get a
   longer live run before Stage D.

## Related history

See `memory/project_softsled_av_sync.md` (#59 Send Time, #61 B57 discovery,
#63–#72 the offset saga and its resolution). This migration supersedes the
offset-derivation approach entirely rather than refining it further.
