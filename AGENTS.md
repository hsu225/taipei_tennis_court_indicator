# Repository Guidelines

## Project Structure & Module Organization
- Source lives under `src/` (e.g., `src/CourtFinder.Api`, `src/CourtFinder.Core`).
- Tests live under `tests/` (e.g., `tests/CourtFinder.Api.Tests`).
- Static assets belong in `assets/` or `wwwroot/` for web projects.
- Configuration sits in project roots or `config/` (e.g., `appsettings.json`, `appsettings.Development.json`, `appsettings.example.json`).

## Build, Test, and Development Commands
- Restore packages: `dotnet restore`.
- Build solution: `dotnet build CourtFinder.sln -c Debug`.
- Run API locally: `dotnet run --project src/CourtFinder.Api`.
- Run tests: `dotnet test tests --configuration Debug --no-build`.
- Optional client (if present): from the client directory `npm ci && npm run dev`.

## Coding Style & Naming Conventions
- C#: 4-space indent, UTF-8, newline at EOF; respect `.editorconfig`.
- Naming: PascalCase for types/methods; camelCase for locals/params; `UPPER_SNAKE_CASE` for constants; private fields `_camelCase`.
- Imports: remove unused; prefer explicit namespaces; use file-scoped namespaces for new code.
- Formatting: run `dotnet format` before committing; use ESLint/Prettier in client code when configured.

## Testing Guidelines
- Framework: xUnit with tests under `tests/` mirroring source namespaces.
- Naming: `MethodName_ShouldExpectedBehavior_WhenCondition`.
- Run: `dotnet test` (use `--filter` for focused runs). Aim for meaningful coverage on business logic; add/adjust tests with new features/fixes.

## Commit & Pull Request Guidelines
- Commits: use Conventional Commits (`feat:`, `fix:`, `docs:`, `test:`, `chore:`); keep changes small and scoped.
- PRs: include a clear description, link issues (e.g., `Closes #123`), screenshots for UI changes, and testing notes/steps. Keep CI green; avoid unrelated reformatting; update docs/tests alongside code.

## Security & Configuration Tips
- Do not commit secrets. Use `dotnet user-secrets` locally and environment variables in deployment.
- Keep `appsettings.Development.json` out of production. Provide `appsettings.example.json` when adding new settings.

## Agent-Specific Instructions
- This file’s scope covers the entire repo. Keep patches minimal, focused, and style-consistent.
- Avoid broad refactors outside task scope. Follow the structure and conventions above when adding or modifying code.

