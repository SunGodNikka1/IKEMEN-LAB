# IKEMEN Lab — Windows (WIN-1)

C# / .NET 8 / WPF foundation for browsing and launching an existing IKEMEN GO install.

## Projects

- `IKEMENLab.Core` — DEF parsing, read-only indexing, settings, launcher
- `IKEMENLab.App` — WPF shell (Dashboard, Characters, Settings)
- `IKEMENLab.Tests` — xUnit

## Safety

WIN-1 never writes beneath the selected IKEMEN root. Settings live in `%LOCALAPPDATA%\IKEMEN Lab\settings.json`.

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
