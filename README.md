# Lintel

A dynamic island for the top of your Windows 11 screen. It rests as a small pill, shows what's playing, flashes
volume, brightness and power changes, keeps today's agenda one hover away, and holds files you drop on it.

## Features

- **Now playing**: album art, title, a live audio visualizer, and controls for any app in the Windows media
  controls (Spotify, browsers, Media Player, VLC…). Click the progress bar to seek.
- **Peeks**: short notices under the pill for track changes, volume, brightness, plugging in or unplugging,
  low battery, energy saver and finished downloads.
- **Today**: your agenda from any iCalendar (.ics) link: Google Calendar, Outlook or iCloud.
- **Shelf**: drop files, images, text or links on the island, then drag them out anywhere, or share them with the
  Windows share sheet.
- **Downloads**: shows when a browser download is in progress, and can put finished downloads on the shelf.
- **OLED care**: optional hide-when-idle, and a slow pixel shift so the island never burns in.
- **Multi-monitor**: main display, a specific display, every display, or whichever one your pointer is on.
- **Keeps out of the way**: hides in fullscreen apps, can stay out of screenshots and recordings, and scrolling over
  it changes the volume.

Open it by resting the pointer on it, clicking it, or pressing **Ctrl+Alt+L** (you can change this in Settings).
Right-click the tray icon for Settings and Quit.

## Lightweight

At rest Lintel uses about **0.5% of one CPU core and ~50 MB of RAM**. The window shrinks to hug the pill when
nothing is animating, the mouse is polled slowly when it's far away, hidden layers don't render, and audio capture
for the visualizer only runs while something is playing.

## Requirements

- Windows 11 (Windows 10 2004+ should also work)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) to run; .NET 8 SDK to build

## Build

```bash
dotnet build src/Lintel/Lintel.csproj -c Release
```

The app is written to `src/Lintel/bin/Release/net8.0-windows10.0.22621.0/Lintel.exe`.

To get a self-contained single file that needs no installed runtime:

```bash
dotnet publish src/Lintel/Lintel.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Privacy

Lintel doesn't collect anything. The only network requests it makes are to the calendar links you add.
Settings, the shelf list and a small log live in `%APPDATA%\Lintel`.

## Background

Lintel is inspired by the idea behind [Boring Notch](https://github.com/TheBoredTeam/boring.notch) for macOS.
It's an independent, clean-room Windows implementation: no code, assets or text were taken from that project.
See [docs/CLEAN_ROOM.md](docs/CLEAN_ROOM.md) and [docs/FINDINGS.md](docs/FINDINGS.md). Lintel isn't affiliated with
or endorsed by the Boring Notch authors.

## License

[MIT](LICENSE)
