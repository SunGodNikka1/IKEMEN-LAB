# IKEMEN Lab — Windows (WIN-1)

C# / .NET 8 / WPF foundation for browsing and launching an existing IKEMEN GO install.

## Projects

- `IKEMENLab.Core` — DEF parsing, read-only indexing, settings, launcher
- `IKEMENLab.App` — WPF shell (Dashboard, Characters, Settings)
- `IKEMENLab.Tests` — xUnit

## Safety

Every write beneath the selected IKEMEN root goes through `SafeMutationService` (verified backup first,
manifest under `%LOCALAPPDATA%\IKEMEN Lab`, rollback on failure). Settings live in
`%LOCALAPPDATA%\IKEMEN Lab\settings.json`.

## Refresh

The sidebar **Refresh** button (or **F5**) re-reads the selected installation from disk through the same
library index as startup: characters, stages, `select.def` roster status and order, `config.ini`
(motif, Quick Settings), Date Added, Dashboard counts / Recently Installed, screenpacks, collections and
Content Health. The current page stays open, and the selected character/stage stays selected while it
still exists (otherwise the selection is cleared).

## Delete Character

Characters → row **…** menu or the inspector → **Delete Character…**. After a confirmation showing the
name, the `chars/` folder and the `select.def` lines affected:

1. every `[Characters]` line of `select.def` that belongs to the package (any of its DEFs, active or
   commented out) is removed completely — no placeholder or blank slot, so later characters move up;
   everything else in the file (comments, stages, options, line endings, encoding) is kept byte-for-byte;
2. the whole top-level folder under `chars/` is deleted (`SafeMutation` DeleteDirectory: verified backup
   in app data, then one atomic rename out of `chars/`);
3. if the folder cannot be deleted (e.g. a file inside is open), `select.def` is rolled back, so nothing
   is left half-done;
4. only then are the package's Date Added record and saved primary-DEF choice retired, and the library
   refreshes.

`scripts\Run-RefreshDeleteFixtures.ps1` runs these scenarios against disposable fixture roots.

## Sprite Inspector

Select a character and choose **Inspect Sprites…**. The window lists every sprite in the character's SFF
(group,number, size, axis, encoding, stored bytes) for SFF v1 and v2, with a filter (`9000`, `0,0`, `RLE8`),
a zoomable checkerboard preview with the sprite's axis drawn on it, a palette picker (the character's ACT
files and embedded palettes) and **Export PNG…**. Problems are listed under the table: duplicate sprite
numbers, sprites that cannot be decoded, broken links, very large sprites, and (for characters) a missing
`0,0` standing frame or `9000,0` select portrait. It is read-only; nothing in the IKEMEN folder is written.

## Size & Stats

**Edit Size & Stats…** edits the values in the character's CNS: `[Data]` life, power, attack, defence,
fall.defence_up, liedown.time, airjuggle, and `[Size]` xscale, yscale, ground/air push widths, height and
AI distances. The **Scale by** buttons multiply Width and Height scale together. Values are validated
against sane ranges before anything is written. Only the edited lines change: comments, spacing, line
endings and the file's text encoding are kept, a repeated key is edited where the engine reads it (the
last one), and a missing key or section is added in the right place. The write goes through the safe
mutation service (backup, hash check, read-back verification), and **Undo last save** restores the file
byte for byte. If the CNS changed on disk after the editor opened, the save is refused.

## Build

```powershell
dotnet restore
dotnet build -c Debug
dotnet test
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -o .\publish\win-x64
```

## Run

```powershell
.\IKEMENLab.App\bin\Release\net8.0-windows\IKEMENLab.exe
```

Or the published copy under `publish\win-x64\IKEMENLab.exe`.
