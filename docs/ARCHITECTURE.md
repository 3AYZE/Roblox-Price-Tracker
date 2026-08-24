# Architecture

The application remains split into Core, Infrastructure, and WPF GUI projects.

- Core: domain model, alert state machine, price validation, polling planner, rate-limit governor.
- Infrastructure: Roblox catalog provider, JSON repository, tracker coordinator, notification outbox, logging.
- GUI: WPF presentation, settings, thumbnail presentation service, reports, backup/export workflows.

Persistent user state remains under `%LOCALAPPDATA%\RobloxPriceTracker` and is not stored beside the EXE.
The v0.3.0 UI changes do not change the repository schema.
