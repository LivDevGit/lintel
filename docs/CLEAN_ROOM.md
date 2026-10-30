# Clean-room notes

Boring Notch is open source under the GPL-3.0. Copying its code (or translating it line by line) into another
program would make that program a derivative work under the GPL. Its name and artwork are also its authors' branding.
The Windows app in this repo, **Lintel**, was written to avoid both. This is an engineering record, not legal advice.
If you plan to sell the app, have a lawyer review it.

## What was used from the original

Only **ideas and behavior**, which copyright doesn't protect:

- the feature list (expanding top-of-screen panel, media controls, HUD, battery, calendar, file shelf)
- the names of settings and component types, used to understand *what* exists (see `FINDINGS.md`)

## What was *not* used

- No source code, from the GitHub repo or decompiled from the binary. The binary was never decompiled
  (only metadata, type names and UI strings were read).
- No assets: no icon, `Assets.car` images, `boring.m4a` sound, Lottie animations or idle-face animation.
- No UI text. Every label in Lintel was written from scratch.
- No name or branding. The app is called **Lintel** (the beam above a doorway), with its own visual design.

## Deliberate differences

| Area | Original (macOS) | Lintel (Windows 11) |
|---|---|---|
| Shape | Black shape fused to the camera notch | Floating rounded island, optionally attached to the top edge |
| Animation | SwiftUI spring animations | Its own spring physics driven by `CompositionTarget.Rendering` |
| Collapsed pill | Art + bars on either side of the notch | Art, scrolling **track title in the center**, a clock when idle |
| Visualizer | Animated (simulated) bars | **Real** spectrum from WASAPI loopback audio and FFT |
| Media sources | Per-player controllers | One Windows media session API for every player |
| Calendar | macOS EventKit + Reminders | **ICS feed subscriptions** (Google, Outlook, iCloud links) |
| HUD | Replaces the system HUD | A separate peek, shown next to the system flyout |
| Gestures | Trackpad swipes | Mouse wheel over the island changes volume |
| Downloads | Download watcher feeding the shelf | Download progress indicator plus optional auto-add to the shelf |
| Accent | System or custom | System, **from album art**, or custom |
| Icons | SF Symbols | Segoe Fluent Icons (ships with Windows 11) |

## Third-party code in Lintel

- [NAudio](https://github.com/naudio/NAudio): MIT. Core Audio volume and loopback capture.
- System.Management: MIT (.NET). WMI brightness events.
- Windows SDK projections: Microsoft.

Lintel itself is released under the MIT license (see `LICENSE`).
