# X Factor 1.1 stage music installation

## Reproduction and cause

Reproduced using Product's `D:\Games 3\X_Factor.rar` on 2026-10-03, with the Windows
installer from commit `21edb6e08b007ec6c174ebb8df46d71adb1d27f2`.
The RAR contains three loose files: `X Factor.def`, `X Factor.sff`, and
`X Factor.mp3`. Its unchanged DEF declares `spr = stages/X Factor.sff` and
`bgmusic = sound/X Factor.mp3`.

The existing install operation `20261004T002227-07b0891bc4e44dac828f3ab34b6daf6f`
reports a successful write to `stages/X Factor.mp3`. The MP3 was present and
uncorrupted, but its referenced `sound/X Factor.mp3` destination was missing.
This is an asset-resolution/layout defect, not an archive extraction failure.

On a clean root, the old installer also reported success while listing both
root-prefixed references as missing. StageAssetResolver checked literal paths
and ancestor archive roots but did not recognize a flattened root layout.
ExecuteFlatStage consequently copied all three loose files into `stages/`
without a music companion at the authored destination.

## Correction

After existing exact archive resolution, a narrow fallback recognizes an omitted
`sound/`, `data/`, or `stages/` prefix. It resolves only the exact remaining path
beneath the DEF directory, never arbitrary matching basenames or parent traversal.
Normal same-content/conflicting-shared-file checks remain authoritative.
The authored DEF remains byte-identical. Already-correct flat stage sprite
destinations are not planned twice. Companion preview wording now also covers
files whose source is inside the package but needs an additional destination.

Alternative considered: rewrite the DEF's music reference. Installing the
bundled file at the authored destination is preferred because it preserves
content and existing collision/rollback behavior.

## Evidence and regression

- New X Factor fixture failed before the fix with both references missing.
- Actual RAR clean install after the fix: one installed stage, no missing assets,
  `sound/X Factor.mp3` exists; source/destination SHA-256 both
  `E75F239B93803BCF5DCB0A6ED48CE0543693914882F68ECD686D7864C8DC6367`
  (4,666,277 bytes).
- Authored DEF, unrelated music/stage sentinels, select.def and config.ini unchanged.
- Targeted StageLayout/ContentInstallService/CharacterPackageInstall tests:
  **66 passed**. Coverage includes known prefixes, other extensions, subdirectories,
  exact-layout precedence, conflicting/identical existing music, invalid fallback
  paths, rollback and dry-run.
- Full Windows Core regression: **729 passed, zero failures**.
- Windows WPF app build: succeeded with zero warnings/errors.
- Existing game installation repaired by creating only the missing
  `sound/X Factor.mp3` through SafeMutationService, refusing any existing
  destination. Operation `20261004T005053-3534b2603ef34ea3b879e131dd783d5a`
  succeeded with the same SHA-256; the existing stages/ copy was retained.

The real archive is external test input, not committed third-party content.
The change is Windows installer logic only; no character, roster, config, or
macOS installer changes are included.
