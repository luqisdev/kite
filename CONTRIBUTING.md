# Contributing to KitBrowser

Thank you for considering contributing to **KitBrowser**!

## Core Principles
1. **Stay Pure Native:** No Node.js, Electron, or local web servers for the browser shell.
2. **Never Block the UI Thread:** Keep SQLite I/O, network requests, and heavy processing asynchronous.
3. **Protect Privacy & Low RAM:** No telemetry, no unnecessary background services, and lazy-load optional tools.

## Getting Started
```powershell
dotnet build KitBrowser.sln -c Release
dotnet run --project src\KitBrowser -c Release
```

## Pull Request Guidelines
- Keep changes focused and modular.
- Ensure `dotnet build KitBrowser.sln -c Release` passes with **0 errors and 0 warnings**.
