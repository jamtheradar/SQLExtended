DataByte for SSMS 2026.11.9.936

New
- Replication monitor: click a subscription, agent or error to open a detail pane under the tabs. It shows the full error (every message SQL Server logged for the failure, not just the first line), the agent's recent history, and the command the distribution agent stopped on, with the row's values and the source and destination tables. That is usually enough to see which value broke it. Copy details copies it all as text, and Open as query opens the T-SQL behind it. Nothing loads until you click, and nothing is written to the server.

Fixed
- Replication monitor: Load recent errors failed with "Input string was not in a correct format" when an error code contained letters (such as MSSQL_REPL20011), so the Errors tab never loaded.
