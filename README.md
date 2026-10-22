# MediaWidget

A lightweight media widget that sits on the Windows 11 taskbar and shows what's playing in any app that supports the Windows media controls (Spotify, browsers, media players, …).

Built to replace heavier third-party widgets: it's event-driven, uses ~0% CPU when idle and around 20 MB of memory.

## Features

- Album art, title (scrolls if too long), artist and a seekable progress bar
- Previous / play-pause / next, optional shuffle and repeat
- Volume flyout and mouse-wheel volume – controls the playing app's volume, falling back to system volume
- Optional synced lyrics from [LRCLIB](https://lrclib.net): a scrolling line on the widget, and a card with surrounding lines (click a line to seek)
- Optional colours from the album art, with readable-contrast text
- Light/dark mode follows Windows; works on bottom, top, left and right taskbars (vertical layout for side taskbars)
- Avoids covering the Start button, Widgets button and system tray; hides during fullscreen apps
- Start with Windows option

Right-click the widget for settings. Drag it to move it.

## Requirements

- Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run, or the .NET 10 SDK to build

## Build and run

```powershell
dotnet run -c Release
```

Or publish a single exe:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
.\publish\MediaWidget.exe
```

Only one instance runs at a time. Settings are stored in `HKCU\Software\MediaWidget`; errors are logged to `%LOCALAPPDATA%\MediaWidget\errors.log`.
