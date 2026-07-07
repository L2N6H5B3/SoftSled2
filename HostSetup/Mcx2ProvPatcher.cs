using System.IO;

namespace SoftSled.HostSetup {

    /// <summary>
    /// In-place, single-byte patch for the Windows 7 MCX2 provisioning
    /// binary (<c>%WINDIR%\ehome\Mcx2Prov.exe</c>).
    ///
    /// <para>During extender certificate-chain validation the stock binary
    /// enforces a revocation (CRL) check. SoftSled's device certificates are
    /// issued by a self-signed CA with no CRL distribution point, so that
    /// check fails and pairing is refused. In the provisioning code the
    /// validation call is set up with a strictness argument of 7; lowering it
    /// to 1 drops the revocation enforcement while leaving every genuine
    /// extender working.</para>
    ///
    /// <para>On the wire this is a single byte — the <c>disp8</c> of
    /// <c>lea ecx,[rax+7]</c> (<c>8D 48 07</c>), which sits immediately
    /// before <c>mov rdx,rdi; mov ebx,800B0109h</c>
    /// (<c>CERT_E_UNTRUSTEDROOT</c>). We find it by a signature that is unique
    /// in the file rather than an absolute offset, then flip <c>07 → 01</c>.
    /// The patch is therefore applied to the host's OWN binary in place: no
    /// Microsoft binary is redistributed, and a host whose build differs is
    /// detected and refused rather than silently overwritten with the wrong
    /// file.</para>
    ///
    /// <para><b>Cross-version (verified 2026-06-20 / x86 added 2026-07-07).</b>
    /// The patch byte is always the strictness argument (<c>07 → 01</c>), but
    /// the compiler emits it differently per architecture, so two signatures
    /// are tried and exactly one must match uniquely:</para>
    ///
    /// <list type="bullet">
    /// <item><b>x64</b> — the arg is <c>lea ecx,[rax+7]</c> BEFORE the error
    /// constant: <c>8D 48 &lt;07&gt; | 48 8B D7 BB 09 01 0B 80</c>
    /// (<c>… ; mov rdx,rdi; mov ebx,800B0109h</c>). Matches uniquely across
    /// Win7 RTM/SP1 x64 (all editions; target @0x21D15) and Win8.1 Pro w/ Media
    /// Center x64 (target @0x1EC17; site inferred from the identical pattern —
    /// no diff-verified patched reference yet, so worth a live test).</item>
    /// <item><b>x86</b> — the arg is a <c>push 7</c> (<c>6A 07</c>) AFTER the
    /// error constant: <c>C7 45 fc 09 01 0B 80 6A &lt;07&gt;</c>
    /// (<c>mov [ebp-4],800B0109h; push 7</c>). The 5-byte anchor
    /// <c>09 01 0B 80 6A</c> is unique per file (the other 800B0109h occurrence
    /// is followed by <c>39</c>, not <c>6A</c>). Verified byte-identical on
    /// Win7 SP1 x86 6.1.7601.17514 (target @0x1DFD2) and x86 Embedded
    /// 6.1.7601.17631 (target @0x1DDD2).</item>
    /// </list>
    ///
    /// <para>Only the surrounding stack/frame addressing differs between builds,
    /// not this validation-setup core.</para>
    /// </summary>
    internal static class Mcx2ProvPatcher {

        /// <summary>A prefix/suffix pair bracketing the single patch byte.</summary>
        private sealed class Signature {
            public readonly byte[] Prefix;
            public readonly byte[] Suffix;
            public Signature(byte[] prefix, byte[] suffix) { Prefix = prefix; Suffix = suffix; }
        }

        // Each entry brackets the one target byte; whichever build this binary
        // is, exactly one entry matches (uniquely) across the whole file.
        private static readonly Signature[] Signatures = {
            // x64: lea ecx,[rax+<07>] ; mov rdx,rdi ; mov ebx,800B0109h.
            new Signature(
                prefix: new byte[] { 0x8D, 0x48 },
                suffix: new byte[] { 0x48, 0x8B, 0xD7, 0xBB, 0x09, 0x01, 0x0B, 0x80 }),
            // x86: mov [ebp-4],800B0109h ; push <07>. Target is the byte right
            // after the push opcode (6A), so the suffix is empty.
            new Signature(
                prefix: new byte[] { 0x09, 0x01, 0x0B, 0x80, 0x6A },
                suffix: new byte[0]),
        };

        private const byte OriginalByte = 0x07; // lea ecx,[rax+7]  — CRL check enforced
        private const byte PatchedByte  = 0x01; // lea ecx,[rax+1]  — CRL check skipped

        public enum PatchState {
            NotFound,    // signature absent — unknown / unsupported build
            Ambiguous,   // signature matched more than once — refuse to guess
            Original,    // located, currently un-patched
            Patched,     // located, already patched
            Unexpected,  // located, but the target byte is neither known value
        }

        /// <summary>Index of the single patch byte, or -1 if not uniquely found.</summary>
        private static int Locate(byte[] bytes, out bool ambiguous) {
            ambiguous = false;
            int found = -1;
            foreach (Signature sig in Signatures) {
                int limit = bytes.Length - (sig.Prefix.Length + 1 + sig.Suffix.Length);
                for (int i = 0; i <= limit; i++) {
                    if (!Match(bytes, i, sig.Prefix)) continue;
                    int target = i + sig.Prefix.Length;
                    if (!Match(bytes, target + 1, sig.Suffix)) continue;
                    if (found != -1) { ambiguous = true; return -1; }
                    found = target;
                }
            }
            return found;
        }

        private static bool Match(byte[] b, int at, byte[] pat) {
            if (at + pat.Length > b.Length) return false;
            for (int k = 0; k < pat.Length; k++)
                if (b[at + k] != pat[k]) return false;
            return true;
        }

        /// <summary>Classify the patch site in an in-memory image.</summary>
        public static PatchState Inspect(byte[] bytes) {
            int t = Locate(bytes, out bool ambiguous);
            if (t == -1) return ambiguous ? PatchState.Ambiguous : PatchState.NotFound;
            byte v = bytes[t];
            if (v == PatchedByte)  return PatchState.Patched;
            if (v == OriginalByte) return PatchState.Original;
            return PatchState.Unexpected;
        }

        /// <summary>Classify the patch site of a file on disk (read-only).</summary>
        public static PatchState InspectFile(string path) {
            try {
                if (!File.Exists(path)) return PatchState.NotFound;
                return Inspect(File.ReadAllBytes(path));
            } catch {
                return PatchState.NotFound;
            }
        }

        /// <summary>
        /// Apply (<paramref name="patch"/>=true) or revert (false) the single
        /// byte, mutating <paramref name="bytes"/> in place when a change is
        /// made. Returns the resulting / blocking state — only
        /// <see cref="PatchState.Patched"/> (when patching) or
        /// <see cref="PatchState.Original"/> (when reverting) means success.
        /// </summary>
        public static PatchState Apply(byte[] bytes, bool patch) {
            int t = Locate(bytes, out bool ambiguous);
            if (t == -1) return ambiguous ? PatchState.Ambiguous : PatchState.NotFound;
            byte v    = bytes[t];
            byte want = patch ? PatchedByte  : OriginalByte;
            byte from = patch ? OriginalByte : PatchedByte;
            if (v == want) return patch ? PatchState.Patched : PatchState.Original; // already done
            if (v != from) return PatchState.Unexpected;
            bytes[t] = want;
            return patch ? PatchState.Patched : PatchState.Original;
        }
    }
}
