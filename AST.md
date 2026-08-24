# AST — what the project IS

Companion to `README.md`, which introduces the project. This file records the constraints the
design answers to. Neither restates the other.

## Purpose
Support banking back-office operations — manage transactions and work records, cut manual
processing and operational error, and standardise data that is currently spread across separate
places.

Four user groups drive authorisation and screen design: **operations staff / controllers** ·
**leadership** · **coordinating departments** · **system administrators**.

## Tech stack
- **C# 14 / .NET 10.**
- **WPF + [WPF-UI](https://github.com/lepoco/wpfui)** (Fluent Design).
- **MySQL 9.7**, reached over the LAN. On a development machine the client and the server are the
  same box.
- **Dapper + MySqlConnector** for data access — no ORM, no migrations framework.
- **Prism** for modularity and navigation, **CommunityToolkit.Mvvm** patterns in the view models,
  **ErrorOr** for result types, **Serilog** for logging.
- Tests: **xUnit v3** + **FluentAssertions**, with integration tests on a real MySQL instance.

## Deployment model — the constraints that shape the design
Most of what looks unusual in this codebase follows from this section.

- **~30 concurrent users, realtime data.**
- **Runs from a network share.** Users open the application directly from the share; nothing is
  installed on a workstation and nothing is copied down.
- **On release the application notifies open clients and closes itself** after an
  administrator-set wait.
- **A release replaces the APPLICATION only. It NEVER touches the database and never resets
  data.** Migration scripts are applied by hand, version by version, before the new build runs.
  The application only *verifies* the schema version and blocks with a readable message on a
  mismatch — it must never migrate, create or drop anything itself.
- **A development machine deliberately shares one database between the application and the
  integration tests, which drop every table on each run.** That is safe only because it never
  holds real data. A release machine has its own database that tests never touch.
- Upgrades stay strictly inside the scope being handled — never disturb what already works.

## Mandatory data principles
Detail: `docs/design-effective-period.md` and `docs/design-temporality-classes.md`.

- **Soft delete.** Valid data already in the database is never hard-deleted. An edit inserts a new
  version and deactivates the old one; a delete deactivates (`isactive` 0/1).
- **Effective period.** A parameter is effective only inside its declared period; outside it the
  record has expired and cannot be used. When a related declaration shares the same period, data
  may be trimmed, overwritten or overlapped — an eight-case algebra governs which.
- **Strict temporal foreign keys.** A reference may not point outside the period its target is
  effective for. Declaring a child beyond its parent's period is blocked, not silently allowed.

## Feature areas
The list keeps growing. Only the first is built today; see the README for what runs and what does
not.

- **Declaration & authorisation** — database connection parameters; organisational units, roles
  and users; function authorisation plus data scoping by org unit and role; function identity
  (serving both authorisation and reporting); other business parameters.
- **Transaction processing** — input (`.csv` / `.xls` / `.xlsx` / `.txt` / clipboard) → validate
  against business rules → persist only if it passes → output (`.xls` / `.xlsx` / `.txt` /
  clipboard, forms, reports) → look up history and executed transactions.
- **Dashboard** — realtime, or on an administrator-set schedule. For an administrator: who is
  using the application, database connection status, transaction statistics by org unit. For a
  user: shortcuts to frequent operations and the five most recent transactions or lookups.
