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
