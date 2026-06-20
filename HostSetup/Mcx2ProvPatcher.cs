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
    /// <para><b>Cross-version (verified 2026-06-20).</b> The signature anchors
    /// on the bytes AFTER the target — <c>48 8B D7 BB 09 01 0B 80</c>
    /// (<c>mov rdx,rdi; mov ebx,800B0109h</c>) — preceded by the
    /// <c>lea ecx,[rax+disp8]</c> opcode (<c>8D 48</c>). Only the surrounding
    /// stack/frame addressing differs between builds, not this validation-setup
    /// core, so the one signature matches uniquely across every Media Center
    /// build checked: Win7 RTM/SP1 x64 (all editions; target @0x21D15) and
    /// Win8.1 Pro w/ Media Center x64 (target @0x1EC17). Win8.1's site is
    /// inferred from the identical instruction pattern (no diff-verified
    /// patched reference for it yet), so Win8.1 pairing is worth a live test.</para>
    /// </summary>
    internal static class Mcx2ProvPatcher {

        // Anchor: lea ecx,[rax+disp8]  (8D 48 <target>) immediately followed by
        //   mov rdx,rdi ; mov ebx,800B0109h  (CERT_E_UNTRUSTEDROOT).
        // The 8-byte suffix carries that distinctive error constant, so the
        // whole pattern occurs exactly once per binary (verified across Win7
        // RTM/SP1 and Win8.1). Prefix is just the lea opcode+modrm.
        private static readonly byte[] Prefix =
            { 0x8D, 0x48 };
        private static readonly byte[] Suffix =
            { 0x48, 0x8B, 0xD7, 0xBB, 0x09, 0x01, 0x0B, 0x80 };

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
            int limit = bytes.Length - (Prefix.Length + 1 + Suffix.Length);
            for (int i = 0; i <= limit; i++) {
                if (!Match(bytes, i, Prefix)) continue;
                int target = i + Prefix.Length;
                if (!Match(bytes, target + 1, Suffix)) continue;
                if (found != -1) { ambiguous = true; return -1; }
                found = target;
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
