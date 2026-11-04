These are synthetic test inputs, not captured speech or i7 acceptance evidence.
The exact product seam and audio receipt schema are documented at the top of
`../../NARRATION-AUDIO.cs`.

- `signal.wav`: RIFF PCM, mono, 16 kHz, signed 16-bit, 0.5 s. A 500 Hz square
  wave alternates +3277/-3277, giving independently known RMS 3277/32768.
- `silent.wav`: the same PCM format and duration, all zero samples (RMS 0).
- `non-pcm.wav`: RIFF IEEE float (format tag 3), mono, 16 kHz, 32-bit, 0.5 s.
  It fails the PCM check despite containing a non-silent waveform.
- `arguments.json`: real CONTROLS-PLAY labels plus the task's required quotes,
  semicolon, command substitution, leading option and Unicode argument cases.
- `shell-argument.json`: deliberately unsafe launch description, inspected only.
  It is never executed. It must fail NA-Arguments.

`self-check-only` validates the full 96-event committed CONTROLS-PLAY corpus
using the signal fixture, then checks each rejection independently in a fresh
temporary directory. Every temporary directory is removed in finally.
The fixture validator uses the current repository's real Git tree history.
`negative-fixture NAME` runs one invalid input directly and must exit 1 with:

| NAME | Expected rejection |
| --- | --- |
| silent-wav | NA-NonSilence |
| missing-utterance | NA-Coverage |
| tampered-hash | NA-Hash |
| non-pcm | NA-PCM |
| shell-argument | NA-Arguments |
| short-wav | NA-Duration |
| audible-control | NA-Silence |
| missing-speaking-binding | NA-NarrationBuild |
| forged-speaking-binding | NA-Hash |
| speaking-build-hash | NA-Hash |
| speaking-build-exit | NA-NarrationBuild |
| speaking-build-tree | NA-Binding |
| speaking-build-binary | NA-Binding |
| speaking-tree-unresolved | NA-Tree |

`short-wav` and `audible-control` verify the duration floor and disabled control.
The standalone default also exercises the real Narration model and the proposed
speech adapter through injected external process I/O, and requires the real
`evidence/production/NARRATION-AUDIO/audio.json`. Missing capabilities are red.
RMS above 0.001 (-60 dBFS) rejects digital silence; it proves neither speech
intelligibility nor that unrelated audio was absent from a monitor capture.
Capture logs and owner/maintainer listening remain necessary to assess provenance.

The 2026-10-30 owner amendment extends this same fixture builder with the real
speaking build's tree/binary identity, a synthetic hash-bound build receipt,
and capture speech argv `spd-say --wait -r -20 --`. Its speaking binding is
distinct from the unchanged CONTROLS-PLAY oracle. Missing/forged bindings and
each invalid build hash, exit, tree or binary are rejected independently.
The forged binding has zero tree/binary hashes and a nonexistent build REF.
All seven were accepted by the original validator in the recorded red run.

Product regressions use the real Narration action result for resident name
`Alex\0suffix`: text intent retains `ACT-FOOD-01 Accepted`, but speech returns
false without invoking the injected runner. Byte-bound cases use 4096 `é`
characters (8192 UTF-8 bytes, must launch verbatim), then append `x` (8193 bytes,
must return false without launch). A separate case requires exactly
`["-r", "-20", "--", text]`, preserving existing argument-oracle coverage.

Required cleanup seam: public static
`Blockparty.UI.NarrationAudioCapture.Run(Action capture, Action close)`.
The real capture failure path must use this seam. The test supplies a capture
action that opens a missing file in its own fresh directory, asserts cleanup
is invoked exactly once, and requires the original FileNotFoundException to
propagate. The missing product seam is intentional red, not a compiler failure.
This standalone contract does not establish the Unity caller wiring, recorder
process shutdown, or Python driver descendant cleanup; repaired-player checks
and independent review must verify those integration obligations.

The real receipt separately must record `-r -20` in `capture.speech` before
`--`; the old rate-0 receipt fails this case. Editing metadata alone does not
refresh WAVs or establish their audible rate. No fresh audio is recorded here.
