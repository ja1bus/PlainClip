# PlainClip

English | [日本語](README.md)

PlainClip (shown as Plain Clip in the Microsoft Store) is a lightweight clipboard history app for Windows 11. It stays in the notification area (system tray), keeps only recent plain text in memory, and lets you paste it quickly without formatting using just your left hand.

This repository contains the source code of the publicly released PlainClip.

## Features

- Press Left Shift twice to paste the text currently on the clipboard without formatting (fonts, colors, HTML, etc.). The key can be changed to Right Shift, Right Ctrl, Left Ctrl, or a regular shortcut, or turned off
- Watches text copies and keeps only plain text in the history (10 items by default, 5–50). The contents of the Windows clipboard are not changed
- Click the tray icon (or press a shortcut you register) to show the history list; paste with a click, `↑↓`+`Enter`, or `1`–`9`, and delete with `Delete`
- Number shortcuts (optional, e.g. `Ctrl+Alt+1`–`9`) paste a history item by its number without opening the list
- Copies that password managers and other apps mark as "do not keep in history" are not added to the history (the same convention as Windows clipboard history)
- Optional start when you sign in to Windows
- English and Japanese display languages (follow the Windows display language or choose one in Settings)

There is no image or file history, no saved snippets, no search, and no sync. The history is kept only in memory and is never saved to a file (it is cleared when the app exits). PlainClip does not send anything to the internet (see the privacy policy below).

## Supported OS and runtime

- Windows 11 (64-bit, x64)
- The Microsoft Store version includes .NET, so nothing else needs to be installed.
- To build and run from the source in this repository, you need the [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0).

## Installation

The official distribution channel is the Microsoft Store. Please get the app there (free).

- Microsoft Store: https://apps.microsoft.com/detail/9MVCBZVNBJW2

This GitHub repository does not distribute executable files (binaries).

## Basic usage

1. When started, the app stays in the notification area. The "How to use PlainClip" window appears the first time.
2. Copy text as usual.
3. Where you want to paste, press Left Shift twice to paste the current clipboard text without formatting.
4. To reuse text you copied earlier, click the tray icon and choose it from the history list.
5. Key assignments, history size, auto start, display language, and more can be changed from the tray icon's right-click menu → "Settings...". To exit, choose "Exit" in the same menu.

For details, see the user manual.

- User manual: https://www.iwanaga.net/plainclip/en/manual/

## Building

### Requirements

- Windows 11 x64
- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0) (verified: 8.0.425)
- Builds with the `dotnet` CLI alone (Visual Studio is not required).
- The app does not depend on NuGet packages. Only the tests use the NuGet packages `Microsoft.NET.Test.Sdk`, `xunit`, and `xunit.runner.visualstudio` (downloaded from nuget.org on the first build).

### Commands

Run from the repository root.

```powershell
dotnet build PlainClip.csproj
dotnet test Tests/PlainClip.Tests.csproj
dotnet run --project PlainClip.csproj
```

`dotnet run` starts the app in the notification area. To exit, choose "Exit" in the tray menu.

To create a folder for distribution (framework-dependent, win-x64), run the following. The output goes to `publish\FrameworkDependent\` and requires the .NET 8 Desktop Runtime (x64).

```powershell
dotnet publish PlainClip.csproj -c Release -p:PublishProfile=FrameworkDependent
```

The output also includes `LICENSE` and `THIRD-PARTY-NOTICES.md` (shown from "MIT License" and "Third-party notices" in the app's Settings).

### Structure

| Path | Contents |
|---|---|
| `PlainClip.csproj` | The app (WPF, `net8.0-windows`). Only the tray icon uses the WinForms `NotifyIcon` |
| `Core/` | Logic that does not depend on the UI or Win32 (history, normalization to plain text, double-tap detection, shortcuts, settings) and the UI strings (English `Strings.resx`, Japanese `Strings.ja.resx`) |
| `Tests/` | Unit tests for `Core` (xUnit) |
| `Services/`, `Views/`, `Interop/` | Clipboard watching and reading/writing, keyboard hook, shortcuts, pasting, tray, and so on; windows; Win32 API declarations |
| `icon.png`, `Resources/icon.ico` | App icon (`pwsh -File tools/draw-icon.ps1` draws `icon.png`; `pwsh -File tools/make-icon.ps1` creates `icon.ico`) |

The tooling that builds the Microsoft Store package (MSIX) is not included in this repository. The `PlainClipMsix` condition in `PlainClip.csproj` and `Services/AutoStartService.Packaged.cs` are for the Store version.

Comments in the source code are mostly in Japanese. "PROJECT.md" and "§N" in comments refer to the development specification (and its section numbers), which is not included in this repository.

## License

Released under the [MIT License](LICENSE).

Copyright (c) 2026 ja1bus (© ja1bus)

For third-party software, see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## No warranty

This software is provided "as is", without warranty of any kind, express or implied. In no event shall the author be liable for any damages arising from the use of this software (see [LICENSE](LICENSE) for details).

## Links

- Website: https://www.iwanaga.net/plainclip/en/
- User manual: https://www.iwanaga.net/plainclip/en/manual/
- Privacy policy: https://www.iwanaga.net/plainclip/en/privacy/
- Microsoft Store: https://apps.microsoft.com/detail/9MVCBZVNBJW2
- Contact: support@iwanaga.net
