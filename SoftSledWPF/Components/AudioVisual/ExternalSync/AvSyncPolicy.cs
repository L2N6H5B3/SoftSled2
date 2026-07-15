using System;

namespace SoftSled.Components.AudioVisual.ExternalSync {

    /// <summary>
    /// Pure, side-effect-free A/V cross-stream offset logic — extracted from
    /// ExternalSyncMediaController so the "which offset do we trust?" decision
    /// is a testable function instead of tangled controller state.
    ///
    /// THE PROBLEM (proven with captures 2026-07-14): WMPNss gives us no RTCP
    /// Sender Reports, so the cross-stream A/V offset must be derived from
    /// weaker signals, and NO single signal is reliable across files:
    ///   - file A (H.264): RTP-Info was right (-1972), DIRECT diff was wrong (+12870)
    ///   - file B (H.264): DIRECT diff was right (~-11044), RTP-Info was wrong (-2930)
    /// Two H.264 files needing DIFFERENT sources, with no obvious per-file
    /// discriminator. This module computes ALL candidate offsets side-by-side,
    /// exposes them for logging, and centralises the (currently best-effort)
    /// selection so the discriminator can be developed against fixtures rather
    /// than by patching the live controller.
    ///
    /// Sign convention (matches PtsFramePacer): a MORE POSITIVE offset releases
    /// video EARLIER (video ahead); MORE NEGATIVE holds video LATER (video behind).
    /// So offset ≈ audioContentMs − videoContentMs at the shared play instant.
    /// </summary>
    internal static class AvSyncPolicy {

        /// <summary>Startup A/V skew beyond this (ms) is treated as implausible
        /// by the legacy RTP-Info policy (e.g. Live TV npt=now). NOTE: this clamp
        /// can be WRONG for recorded content whose genuine offset is large (file
        /// B's true offset likely exceeds it) — a known limitation the
        /// discriminator work must replace, not inherit blindly.</summary>
        public const long MaxPlausibleOffsetMs = 5000;

        /// <summary>One candidate cross-stream offset with provenance.</summary>
        public readonly struct Candidate {
            public readonly bool Valid;
            public readonly long OffsetMs;
            public readonly string Source;
            public Candidate(bool valid, long offsetMs, string source) {
                Valid = valid; OffsetMs = offsetMs; Source = source;
            }
            public static readonly Candidate None = new Candidate(false, 0, "n/a");
            public override string ToString() => Valid ? $"{OffsetMs}ms" : "-";
        }

        /// <summary>All candidate offsets for one media, computed from raw inputs.</summary>
        public readonly struct Candidates {
            public readonly Candidate Direct;         // raw first-MAU content diff
            public readonly Candidate RtpInfo;        // PLAY-response play-point diff (epoch-corrected)
            public readonly Candidate Correspondence; // steady-state embedded-SR pairing
            public Candidates(Candidate direct, Candidate rtpInfo, Candidate correspondence) {
                Direct = direct; RtpInfo = rtpInfo; Correspondence = correspondence;
            }
            public override string ToString() =>
                $"direct={Direct} rtpinfo={RtpInfo} corr={Correspondence}";
        }

        // ---- Pure candidate computations ------------------------------------

        /// <summary>DIRECT first-MAU content difference. Correct only when audio
        /// and video share an RTP epoch (always true for MPEG-2; sometimes for
        /// H.264 — file B). = audioFirstContentMs − videoFirstContentMs.</summary>
        public static Candidate ComputeDirect(long audioFirstRtp, int audioClockHz,
                                              long videoFirstRtp, int videoClockHz) {
            if (audioFirstRtp < 0 || videoFirstRtp < 0 || audioClockHz <= 0 || videoClockHz <= 0)
                return Candidate.None;
            long aMs = audioFirstRtp * 1000L / audioClockHz;
            long vMs = videoFirstRtp * 1000L / videoClockHz;
            return new Candidate(true, aMs - vMs, "direct");
        }

        /// <summary>RTP-Info play-point difference (epoch-corrected). Each stream's
        /// PLAY-response rtptime is its play point; the offset is the difference in
        /// how far past the play point each stream's first delivered MAU sits:
        ///   (videoInfo − videoFirst)/vClk − (audioInfo − audioFirst)/aClk.
        /// Correct when both play points denote the same content instant; corrupted
        /// by asymmetric preroll / independent epochs (file B).</summary>
        public static Candidate ComputeRtpInfo(long audioFirstRtp, long audioInfoRtp, int audioClockHz,
                                              long videoFirstRtp, long videoInfoRtp, int videoClockHz) {
            if (audioFirstRtp < 0 || videoFirstRtp < 0 || audioInfoRtp < 0 || videoInfoRtp < 0
                || audioClockHz <= 0 || videoClockHz <= 0)
                return Candidate.None;
            long vPad = (videoInfoRtp - videoFirstRtp) * 1000L / videoClockHz;
            long aPad = (audioInfoRtp - audioFirstRtp) * 1000L / audioClockHz;
            return new Candidate(true, vPad - aPad, "rtpinfo");
        }

        /// <summary>Steady-state Correspondence ("embedded SR") pairing. The
        /// depacketizers fit each stream's transmission-NTP↔header-ms line; at a
        /// common NTP the per-stream intercepts (Cv, Ca) plus the first-header diff
        /// give: offset = (Cv − Ca) − (videoFirstHdrMs − audioFirstHdrMs). Only
        /// meaningful once both slopes settle to ≈1.0 (real-time delivery).</summary>
        public static Candidate ComputeCorrespondence(bool haveFit,
                                                      double cvInterceptMs, double caInterceptMs,
                                                      long videoFirstHdrMs, long audioFirstHdrMs) {
            if (!haveFit) return Candidate.None;
            double off = (cvInterceptMs - caInterceptMs) - (videoFirstHdrMs - audioFirstHdrMs);
            return new Candidate(true, (long)Math.Round(off), "correspondence");
        }

        // ---- Selection policy ------------------------------------------------

        /// <summary>Result of choosing an offset from the candidates.</summary>
        public readonly struct Choice {
            public readonly bool Valid;
            public readonly long OffsetMs;
            public readonly string Source;   // which candidate won
            public readonly bool Clamped;    // legacy plausibility clamp fired
            public Choice(bool valid, long offsetMs, string source, bool clamped) {
                Valid = valid; OffsetMs = offsetMs; Source = source; Clamped = clamped;
            }
        }

        /// <summary>
        /// LEGACY policy — a faithful copy of the controller's current behaviour
        /// (RTP-Info + 5000ms plausibility clamp, RTP-Info→0 when implausible).
        /// Wiring the controller onto THIS is behaviour-preserving; the improved
        /// discriminator (see <see cref="ChooseExperimental"/>) is developed
        /// separately and validated against <see cref="Fixtures"/> before it
        /// replaces this.
        /// </summary>
        public static Choice ChooseLegacy(in Candidates c) {
            if (!c.RtpInfo.Valid) return new Choice(false, 0, "none", false);
            long off = c.RtpInfo.OffsetMs;
            bool clamped = false;
            if (Math.Abs(off) > MaxPlausibleOffsetMs) { off = 0; clamped = true; }
            return new Choice(true, off, "rtpinfo", clamped);
        }

        /// <summary>
        /// EXPERIMENTAL discriminator — the actual research target. Given all
        /// candidates, decide which reflects the true content-time offset. UNSOLVED:
        /// file A needs RtpInfo, file B needs Direct, and the distinguishing signal
        /// is not yet known (see <see cref="Fixtures"/>). Until a discriminator
        /// passes the fixtures, this defers to <see cref="ChooseLegacy"/> so it is
        /// safe to call. Do NOT make this the controller's source of truth until
        /// SelfTest passes for every fixture with a KnownGoodOffsetMs.
        /// </summary>
        public static Choice ChooseExperimental(in Candidates c) {
            // TODO(discriminator): populate from ground-truth captures. Candidate
            // signals to evaluate once fixtures have confirmed KnownGoodOffsetMs:
            //   - epoch magnitude (= Direct − RtpInfo): large for BOTH A and B, so
            //     not sufficient alone.
            //   - Correspondence agreement with Direct vs RtpInfo.
            //   - play-point geometry (audioInfo/videoInfo spacing vs first-MAU).
            return ChooseLegacy(in c);
        }

        // ---- Fixtures (regression / discriminator acceptance tests) ----------

        /// <summary>A real captured case: its candidate offsets (facts from logs)
        /// and the offset that actually achieves perfect sync (KnownGoodOffsetMs,
        /// null until confirmed by trimming to perfect lip-sync on hardware).</summary>
        public readonly struct Fixture {
            public readonly string Name;
            public readonly Candidates Candidates;
            public readonly long? KnownGoodOffsetMs; // null = ground truth not yet captured
            public readonly string Notes;
            public Fixture(string name, Candidates candidates, long? knownGood, string notes) {
                Name = name; Candidates = candidates; KnownGoodOffsetMs = knownGood; Notes = notes;
            }
        }

        /// <summary>
        /// Known cases from the 2026-07-14 captures. Populate KnownGoodOffsetMs by
        /// having the user trim each to perfect sync (applied offset at perfect
        /// lip-sync = the truth) and reading it from the [av-sync] log. The
        /// discriminator (ChooseExperimental) is correct when SelfTest passes for
        /// every fixture that has a KnownGoodOffsetMs.
        /// </summary>
        public static readonly Fixture[] Fixtures = new[] {
            // file A: RTP-Info was "acceptable" (-1972); DIRECT was clearly wrong (+12870).
            new Fixture("fileA_h264",
                new Candidates(
                    new Candidate(true, 12870, "direct"),
                    new Candidate(true, -1972, "rtpinfo"),
                    Candidate.None),               // correspondence not captured (rtcp-debug overwritten)
                -1972,                             // acceptable — treat as good pending finer trim
                "H.264; RTP-Info correct, direct wrong (independent epochs)"),

            // file B: CLEAN no-seek trim-to-perfect ground truth ≈ -4699ms
            // (2026-07-14 16:44 x64 run). All 3 sources FAIL: direct=-16140 (random
            // H.264 epoch), rtpinfo=-2759 (~1940ms too positive), correspondence
            // DRIFTED -443→-2422 and never settled. The ~-1940 residual (truth −
            // rtpinfo) is 0 for file A — same codec, different residual. LEADING
            // HYPOTHESIS: root cause is VIDEO UNDER-DELIVERY (corr vSlope=0.897 vs
            // healthy ~1.0) which BOTH breaks correspondence AND distorts rtpinfo;
            // fix delivery → both may resolve. So the fix is likely NOT
            // candidate-selection but delivery-rate + (converged) correspondence.
            new Fixture("fileB_h264",
                new Candidates(
                    new Candidate(true, -16140, "direct"),        // random epoch — ignore for H.264
                    new Candidate(true, -2759, "rtpinfo"),        // ~1940ms too positive (video under-delivery)
                    Candidate.None),                              // correspondence drifted, never converged
                -4699,                             // clean no-seek trim-to-perfect
                "H.264 50fps; truth -4699 is NEITHER candidate. Residual (truth-rtpinfo)=-1940ms " +
                "(fileA=0). Root-cause hypothesis: video under-delivery (vSlope 0.897) breaks corr + " +
                "skews rtpinfo. Next: fix delivery, re-check corr convergence & rtpinfo residual."),
        };

        /// <summary>Run the discriminator against every fixture that has a
        /// confirmed KnownGoodOffsetMs. Returns a human-readable report; the caller
        /// (a startup self-check or a future unit test) decides how loud to be.
        /// A fixture with null KnownGoodOffsetMs is reported as PENDING, not failed.</summary>
        public static string SelfTest(long toleranceMs = 200) {
            var sb = new System.Text.StringBuilder();
            int tested = 0, passed = 0;
            foreach (var f in Fixtures) {
                var choice = ChooseExperimental(in f.Candidates);
                if (f.KnownGoodOffsetMs == null) {
                    sb.AppendLine($"  [PENDING] {f.Name}: {f.Candidates} → chose {choice.Source}={choice.OffsetMs}ms (no ground truth)");
                    continue;
                }
                tested++;
                long err = Math.Abs(choice.OffsetMs - f.KnownGoodOffsetMs.Value);
                bool ok = choice.Valid && err <= toleranceMs;
                if (ok) passed++;
                sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {f.Name}: chose {choice.Source}={choice.OffsetMs}ms " +
                              $"vs truth {f.KnownGoodOffsetMs}ms (err {err}ms)");
            }
            sb.Insert(0, $"AvSyncPolicy.SelfTest: {passed}/{tested} confirmed fixtures pass" +
                         (tested == 0 ? " (all fixtures pending ground truth)" : "") + Environment.NewLine);
            return sb.ToString().TrimEnd();
        }
    }
}
