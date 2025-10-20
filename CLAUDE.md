# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

CourtFinder is a cross-platform tool to query Taipei City tennis court availability. Currently implemented as a Windows CLI with a shared core library designed for reuse in future .NET MAUI Android apps.

**Key Architecture:**
- `CourtFinder.Core`: Shared library with models, providers, and interfaces
- `CourtFinder.CLI`: Windows console application
- Provider pattern for data sources (mock, Taipei web scraping, Taipei open data)

## Build and Run Commands

### Building
```bash
# Build entire solution
dotnet build src/CourtFinder.CLI/CourtFinder.CLI.sln

# Build specific project
dotnet build src/CourtFinder.Core
dotnet build src/CourtFinder.CLI

# Restore packages
dotnet restore
```

### Running the CLI
```bash
# List all courts
dotnet run --project src/CourtFinder.CLI -- list

# Filter by district
dotnet run --project src/CourtFinder.CLI -- list --district 大安區

# Search by name
dotnet run --project src/CourtFinder.CLI -- search --name 大安

# Check status (today)
dotnet run --project src/CourtFinder.CLI -- status --court 大安森林公園網球場

# Check status by date
dotnet run --project src/CourtFinder.CLI -- status --court 大安森林公園網球場 --date 2025-10-01

# Filter by time and availability
dotnet run --project src/CourtFinder.CLI -- status --court 大安森林公園網球場 --date 2025-10-01 --time 06:00-09:00 --available

# Health check with connectivity probe
dotnet run --project src/CourtFinder.CLI -- health
```

### Testing
```bash
# Run all tests
dotnet test

# Run tests without rebuilding
dotnet test --no-build

# Run specific test
dotnet test --filter "FullyQualifiedName~TestMethodName"

# Run with configuration
dotnet test tests --configuration Debug
```

## Architecture Patterns

### Provider Pattern
The core abstraction is `ITennisCourtProvider` with three implementations:

1. **MockProvider**: Reads from `sample_data/` for offline testing
2. **TaipeiWebProvider**: Web scraping implementation using HttpClient
3. **TaipeiOpenDataProvider**: Official open data API implementation

Provider selection via `ProviderFactory.CreateDefault()` reads `COURTFINDER_PROVIDER` environment variable:
- `mock`: Use MockProvider (default if not set)
- `taipei-web`: Use TaipeiWebProvider
- Other values: Use TaipeiOpenDataProvider

### Core Models
- **Court**: Represents a tennis court venue (ID, name, district, address, phone)
- **Availability**: Daily court availability with time slots
- **TimeSlot**: Individual time slot with start/end times and availability status

### CLI Command Pattern
The CLI uses a simple switch-based command dispatcher in `Program.cs` with local functions for each command (ListCourts, SearchCourts, ShowStatus).

## Configuration

### Environment Variables
- `COURTFINDER_PROVIDER`: Provider selection (`mock`, `taipei-web`, or defaults to taipei-open)
- `COURTFINDER_TAIPEI_BASEURL`: Base URL for Taipei provider APIs (optional override)

### Sample Data
Mock provider reads from `sample_data/`:
- `courts.json`: List of courts
- `availability/*.json`: Daily availability data by court ID

## Development Notes

### Console Encoding
The CLI sets UTF-8 encoding explicitly for Chinese character support:
```csharp
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
```

### Target Framework
All projects use .NET 8.0 with nullable reference types enabled.

### Test Framework
Uses xUnit with standard Microsoft test SDK. Test project includes common global usings for System namespaces.

### Coding Conventions
Follow guidelines in AGENTS.md:
- 4-space indentation
- PascalCase for types/methods, camelCase for locals, `_camelCase` for private fields
- File-scoped namespaces for new code
- Run `dotnet format` before commits
- Conventional Commits for commit messages (`feat:`, `fix:`, `test:`, etc.)
