# Reverse-engineering findings: boringNotch.dmg

Target: `boringNotch.dmg` (SHA-256 `23786382548d33c59b309db558e513a92e3f95b63d611544dcddf5d196e46698`)

## How it was opened

1. The DMG is an Apple UDIF image (`koly` trailer) with zlib-compressed (`0x80000005`) chunks.
   A small UDIF reader decompressed partition 4 (`Apple_APFS`) into a raw image (`NXSB` magic confirmed).
2. 7-Zip read the APFS volume. Only framework symlinks failed to extract, which doesn't matter here.
3. REA opened the main executable (universal Mach-O, x86_64 + arm64,
   SHA-256 `f136e7334c4c197c9d0fbdcbb0d95649771ed7427bcf361b28aa76af03ed3900`).
   No deep decompiler provider is available for Mach-O on Windows, so the analysis stayed at the level of
   **metadata, type names, settings keys and UI strings**. No function bodies were decompiled or reused.
   That's on purpose: the rebuild is based on observed behavior, not on code.

## Bundle facts (Info.plist)

| Key | Value |
|---|---|
| Bundle ID | `theboringteam.boringnotch` |
| Version | 2.7.3 (build 271) |
| Minimum macOS | 14.0 |
| `LSUIElement` | true (no Dock icon, menu-bar app) |
| Privacy prompts | Apple Events (music control), Calendars, Reminders, Camera (mirror) |
| Updates | Sparkle, appcast on GitHub Pages |

Bundled frameworks: Lottie (animations), MediaRemoteAdapter (now-playing bridge), Sparkle (updates).
There's also an XPC helper (`BoringNotchXPCHelper.xpc`) and an audio file `boring.m4a`.

## Component inventory (Swift nominal types in module `boringNotch`)

Grouped by responsibility:

- **Window / shell**: `AppDelegate`, `BoringNotchWindow`, `BoringNotchSkyLightWindow`, `NotchSpaceManager`, `CGSSpace`,
  `NSScreenUUIDCache`, `SettingsWindowController`, `FullscreenMediaDetector`, `DragDetector`
- **State**: `BoringViewModel`, `BoringViewCoordinator`, `BoringAnimations`
- **Media**: `MusicManager`, `MediaControllerProtocol`, `NowPlayingController`, `AppleMusicController`, `SpotifyController`,
  `YouTubeMusicController` (+ HTTP / WebSocket / auth clients), `AppleScriptHelper`, `MediaChecker`, `MediaKeyInterceptor`,
  `AudioSpectrum`, `AudioPlayer`, `ImageService`, `ImageProcessingService`, `JSONLinesPipeHandler`
- **System HUDs**: `VolumeManager`, `BrightnessManager`, `KeyboardBacklightManager`
- **Battery**: `BatteryActivityManager`, `BatteryStatusViewModel`
- **Calendar**: `CalendarManager`, `CalendarService`, `CalendarServiceProviding`
- **Shelf (file tray)**: `ShelfStateViewModel`, `ShelfItemViewModel`, `ShelfSelectionModel`, `ShelfPersistenceService`,
  `TemporaryFileStorageService`, `ThumbnailService`, `QuickLookService`, `QuickShareService`, `ShareServiceFinder`,
  `SharingStateManager`, `DownloadWatcher`
- **Camera mirror**: `WebcamManager`
- **Updates / IPC**: `CheckForUpdatesViewModel`, `SparkleNSView`, `XPCHelperClient`

## Feature list (from the English UI strings and settings keys)

1. **Closed / open notch**: a black shape around the camera notch that expands on hover (`openNotchOnHover`,
   `minimumHoverDuration`, `extendedHoverPadding`), on click, or with a global shortcut ("Toggle Notch Open").
   It can close with a gesture (`closeGestureEnabled`, `gestureSensitivity`).
2. **Sizing**: match the real notch or the menu-bar height, a custom height on displays without a notch (`nonNotchHeightMode`),
   and corner-radius scaling.
3. **Displays**: preferred display, show on all displays, automatically switch displays, show on the lock screen.
4. **Media live activity**: album art plus a spectrum while music plays (`musicLiveActivityEnabled`, `useMusicVisualizer`,
   custom Lottie visualizers). Also a "sneak peek" with title and artist on track change (`sneakPeek`, `sneakPeekDuration`),
   an inactivity timeout, a progress bar style, lyrics, configurable control slots (`musicControlSlots`), and a music-source choice
   (Now Playing / Apple Music / Spotify / YouTube Music).
5. **HUD replacement**: custom volume, brightness and keyboard-backlight HUDs instead of the system ones
   (needs Accessibility access). Shown in the closed or open notch, with an optional percentage.
6. **Battery**: indicator, percentage, power-status icons, plug/unplug notifications, low-power mode, time to full charge.
7. **Calendar & reminders**: today's events, selected calendars, hide all-day events, full titles, auto-scroll, and
   reminders that can be completed.
8. **Shelf**: drop files on the notch to keep them, drag them out again (copy or remove after dragging),
   Quick Look, share through a chosen sharing service, and open the shelf by default when it has items.
9. **Mirror**: live webcam preview inside the notch.
10. **Appearance**: system or custom accent color, glow, blur behind album art, window shadow, idle "face" animation,
    settings icon in the notch, hide from screen recording.
11. **Fullscreen behavior**: hide for all apps or for media apps only.
12. **Onboarding, updates (Sparkle), menu-bar icon, haptic feedback**.

## What doesn't carry over to Windows

| macOS mechanism | Windows 11 situation |
|---|---|
| Physical camera notch | No notch, so the rebuild uses a floating "island" at the top center |
| MediaRemote / AppleScript per player | One system API (GSMTC) covers Spotify, browsers, Media Player, etc. |
| Replacing the system HUD (private APIs) | Windows 11's volume flyout can't be cleanly removed, so we add our own indicator |
| EventKit calendars / reminders | No public unpackaged API, so the rebuild subscribes to iCalendar (ICS) feeds |
| Lock-screen window (SkyLight) | Not possible (the secure desktop), so it's skipped |
| Two-finger swipe gestures | Mouse wheel over the island changes volume instead |
| Sparkle updates | Not included |
