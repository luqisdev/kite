# KitBrowser

**KitBrowser** is a fast, lightweight, privacy-first, 100% native Windows web browser built with **C# + WPF (.NET 8)**, **Microsoft WebView2**, and **SQLite** — with zero Node.js, Electron, or bundled web servers.

---

## Key Features

### Performance & Low RAM
- **Pure Native Shell:** Hardware-accelerated WPF (.NET 8) UI sharing a single `CoreWebView2Environment`.
- **Two-Stage Smart Tab Sleeping:** Background tabs are softly suspended (`TrySuspendAsync` + `Low` memory target) and automatically discarded to **0 MB RAM** after inactivity.
- **Live Performance Center (`kite://performance`):** Monitor shell RAM, active engines, sleeping tabs, and optimize memory in one click.

### Privacy & Security
- **Built-in Ad & Tracker Blocker (`kite://adblock`):** Lock-free `.NET 8 FrozenSet` network-level blocking + cosmetic CSS hiding + per-site whitelist & custom domain rules.
- **URL Tracking Stripper:** Automatically strips `utm_*`, `fbclid`, `gclid`, and other tracking parameters on navigation and clean copy (`Ctrl+Shift+C`).
- **InPrivate Tabs (`Ctrl+Shift+N`):** Isolated WebView2 InPrivate sessions with zero history or session persistence.
- **Local DPAPI Password Vault (`kite://passwords`):** Passwords encrypted locally using Windows DPAPI (`ProtectedData`) with a built-in strong password generator.
- **Browser PIN Lock (`Ctrl+Shift+L`):** SHA-256 salted PIN lock with optional lock-on-startup.
- **Per-Site Permissions:** Click the address bar lock icon to manage Camera, Microphone, Location, Notifications, and clear site cookies/data.

### Productivity & Modern UI
- **Modern Horizontal Tabs & Hover Previews:** Minimalist horizontal tab bar with live thumbnail hover preview cards, drag-and-drop reordering, pinned tabs, audio/mute indicators, and optional colored tab groups.
- **Global Media Control Hub:** Control play/pause, ±10s seek, mute, PiP, and playback speed across all background audio/video tabs from a single toolbar button.
- **Built-in Page & Selection Translator:** One-click in-place Turkish page translation from the address bar and instant right-click selection translation popup.
- **Windows Default Browser & Delta Auto-Updates:** Register as Windows default browser (`http`/`https`/`.html`) with single-instance link handoff, plus Velopack Delta auto-updates.
- **Split View:** Use two tabs side-by-side in one window with a draggable divider.
- **Command Palette (`Ctrl+K`) & Omnibox Calculator:** Fuzzy search open tabs, bookmarks, history, `kite://` pages, commands, and evaluate math expressions inline.
- **Multi-Profile Support:** Run fully isolated profiles (Personal, Work, Custom) in parallel windows (`--profile=<id>`).
- **Built-in KitBrowser DevTools (`F12`):** Native Console, DOM/CSS Elements Inspector, Network Monitor, and Storage Viewer.
- **More Built-in Tools:** Reader Mode, Video Speed (`0.75x–3x`) & 200% Volume Booster, Picture-in-Picture (PiP), Mobile View Mode, Focus Mode (Pomodoro + Distraction Blocker), Reading List, Quick Notes, Screenshot (`Ctrl+Shift+S`), Save as PDF (`Ctrl+P`), Chrome/Edge Bookmark Import & Export, and Custom New Tab Wallpapers.

---

## Internal Pages

| URL | Description |
| :--- | :--- |
| `kit://newtab` | Native New Tab Page with Speed Dial, clock & efficiency stats |
| `kite://settings` | Full-page Settings (`Ctrl+,`) |
| `kite://performance` | Live RAM, tab sleep & memory optimization dashboard |
| `kite://adblock` | Custom block rules & per-site whitelist manager |
| `kite://passwords` | Local Windows DPAPI encrypted password vault |
| `kite://bookmarks` | Full-page Bookmarks + Chrome/Edge import & export (`Ctrl+Shift+B`) |
| `kite://history` | Searchable full-page History (`Ctrl+H`) |
| `kite://downloads` | Full-page Downloads manager (`Ctrl+J`) |

---

## Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| `Ctrl+T` / `Ctrl+W` | New tab / Close tab *(or Middle-Click tab)* |
| `Ctrl+Shift+T` | Reopen last closed tab |
| `Ctrl+Shift+N` | New InPrivate tab |
| `Ctrl+K` / `Ctrl+L` | Command Palette / Focus Address Bar |
| `Ctrl+1..8` / `Ctrl+9` | Jump to tab 1–8 / Last tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / Previous tab |
| `Ctrl+F` | Find in page |
| `Ctrl +` / `Ctrl -` / `Ctrl+0` | Zoom in / out / reset |
| `Ctrl+Shift+O` | Toggle Bookmarks Bar |
| `Ctrl+Shift+C` | Copy clean URL (tracking stripped) |
| `Ctrl+Shift+S` | Capture page screenshot (PNG) |
| `Ctrl+P` | Save page as PDF |
| `Ctrl+Shift+Del` | Clear browsing data |
| `Ctrl+Shift+L` | Lock browser with PIN |
| `F11` / `F12` / `Ctrl+U` | Fullscreen / KitBrowser DevTools / View Source |

---

## Build & Run

### Prerequisites
- Windows 10/11 (x64)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) *(pre-installed on Windows 11)*

### Commands
```powershell
# Build Release
dotnet build KitBrowser.sln -c Release

# Run KitBrowser
dotnet run --project src\KitBrowser -c Release

# Build Velopack Installer & Delta Release Package (dist\releases\KitBrowser-win-Setup.exe)
.\build-release.ps1 -Version 1.0.0
```

---

## Project Structure

```text
KitBrowser/
├── .github/workflows/ci.yml
├── src/KitBrowser/
│   ├── Core/
│   │   ├── Models/BrowserModels.cs
│   │   └── Services/
│   │       ├── BrowserUtilities.cs
│   │       ├── SecurityAndFocusServices.cs
│   │       ├── StorageService.cs
│   │       └── TabHostController.cs
│   ├── Resources/
│   │   ├── blocklist.txt
│   │   └── kitbrowser.ico
│   ├── UI/Themes/
│   │   ├── Controls.xaml
│   │   ├── DarkTheme.xaml
│   │   └── LightTheme.xaml
│   ├── App.xaml / App.xaml.cs
│   └── MainWindow.xaml / MainWindow.xaml.cs
├── build-release.ps1
├── LICENSE
├── THIRD-PARTY-NOTICES.txt
└── README.md
```

---

## License

Licensed under the [GNU General Public License v3.0 (GPLv3)](LICENSE).
Third-party component licenses are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
