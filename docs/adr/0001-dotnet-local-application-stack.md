# Use .NET for the local application stack

GoalKeeper uses .NET 10 with an interactive-server Blazor Web App, EF Core migrations, and SQLite under the current user's local application-data directory. This replaces the Python domain and persistence path after behavioral parity because static domain modeling, integrated local UI, and forward schema migrations outweigh the one-time porting cost; the Python capture and Perception prototype remains the Phase 3 reference until those adapters are ported.
