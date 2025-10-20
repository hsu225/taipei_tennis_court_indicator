CourtFinder — Taipei Tennis Court Status

Overview
- Goal: a small cross-platform tool to query Taipei City tennis court availability. Works as a Windows CLI now; Android app can reuse the same core via .NET MAUI.

What’s included
- `CourtFinder.Core`: shared library with models, config, and provider interfaces.
- `CourtFinder.CLI`: Windows console tool using the core library.
- `sample_data`: mock courts and availability to run offline.

Quick start (Windows CLI)
- Requirements: .NET SDK 8.0+ installed (`dotnet --version`).
- Build: `dotnet build src/CourtFinder.CLI` (or build each project).
- Run examples:
  - List all courts: `dotnet run --project src/CourtFinder.CLI -- list`
  - Filter by district: `dotnet run --project src/CourtFinder.CLI -- list --district 大安區`
  - Search by name: `dotnet run --project src/CourtFinder.CLI -- search --name 大安`
  - Status (today): `dotnet run --project src/CourtFinder.CLI -- status --court 大安森林公園網球場`
  - Status by date: `dotnet run --project src/CourtFinder.CLI -- status --court 大安森林公園網球場 --date 2025-10-01`
  - Filter by time and only available: `dotnet run --project src/CourtFinder.CLI -- status --court 大安森林公園網球場 --date 2025-10-01 --time 06:00-09:00 --available`

Provider selection
- Default provider: `mock` (reads `sample_data`).
- Switch to an HTTP provider (to be wired to Taipei Sports Bureau / open data) by setting env var: `COURTFINDER_PROVIDER=taipei-open` and configuring `COURTFINDER_TAIPEI_BASEURL`.

Android path (via .NET MAUI)
- Create a .NET MAUI app and reference `CourtFinder.Core`.
- Use the `ITennisCourtProvider` interface and `ProviderFactory` to get data.
- Start with a simple page: list courts, tap to view day availability.

Notes
- This tool is query-only; no booking or login is performed.
- Use `COURTFINDER_PROVIDER=mock` to run offline with sample data.
- Real Taipei endpoints are wired in `TaipeiOpenDataProvider`; slot mapping will be refined with confirmed schema.
