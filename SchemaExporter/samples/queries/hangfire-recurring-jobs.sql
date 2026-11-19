-- Hangfire recurring jobs (SQL Server storage), one row per job.
-- Use with: "mode": "filePerRow", "fileName": "{target}/{JobId}" (or "{JobId}" with one target).
-- Run against the database holding the app's [HangFire] schema; change the schema name if the app set its own.
--
-- Definition columns only, plus Health: a bucket that changes only when the job's state does, so a quiet night
-- commits nothing. Raw LastExecution/NextExecution change every run; they are deliberately left out.
-- Arguments are left out too: they can carry personal data or payloads.
--
-- Core Hangfire has no "disabled" flag: a removed job simply disappears (and shows as removed in git). An app
-- that keeps its own enable list in a table of its own needs its own query.
SET NOCOUNT ON;

WITH jobs AS
(
    SELECT SUBSTRING(h.[Key], LEN('recurring-job:') + 1, 400) AS JobId,
           MAX(CASE WHEN h.Field = 'Cron'          THEN h.Value END) AS Cron,
           MAX(CASE WHEN h.Field = 'TimeZoneId'    THEN h.Value END) AS TimeZoneId,
           MAX(CASE WHEN h.Field = 'Queue'         THEN h.Value END) AS Queue,
           MAX(CASE WHEN h.Field = 'Job'           THEN h.Value END) AS Job,
           -- Hangfire leaves an empty Error behind once a job recovers; treat it as no error.
           NULLIF(LTRIM(RTRIM(MAX(CASE WHEN h.Field = 'Error' THEN h.Value END))), '') AS Error,
           MAX(CASE WHEN h.Field = 'LastExecution' THEN h.Value END) AS LastExecution,
           MAX(CASE WHEN h.Field = 'LastJobId'     THEN h.Value END) AS LastJobId
    FROM [HangFire].[Hash] h
    WHERE h.[Key] LIKE 'recurring-job:%'
    GROUP BY h.[Key]
)
SELECT j.JobId,
       j.Cron,
       ISNULL(j.TimeZoneId, 'UTC')                                       AS TimeZoneId,
       ISNULL(j.Queue, 'default')                                        AS Queue,
       -- Invocation data is {"Type":..,"Method":..} (older) or {"t":..,"m":..} (1.7+ compact).
       COALESCE(JSON_VALUE(j.Job, '$.Type'), JSON_VALUE(j.Job, '$.t'))   AS Type,
       COALESCE(JSON_VALUE(j.Job, '$.Method'), JSON_VALUE(j.Job, '$.m')) AS Method,
       CASE
           WHEN j.Error IS NOT NULL                    THEN 'Error: ' + LEFT(j.Error, 200)
           WHEN j.LastExecution IS NULL                THEN 'NeverRun'
           WHEN s.Name = 'Failed'                      THEN 'Failing'
           WHEN TRY_CONVERT(datetime2, LEFT(j.LastExecution, 19), 126) < DATEADD(DAY, -7, SYSUTCDATETIME())
                                                       THEN 'NotRunIn7Days'
           ELSE 'OK'
       END                                                               AS Health
FROM jobs j
LEFT JOIN [HangFire].[Job] lj ON lj.Id = TRY_CONVERT(bigint, j.LastJobId)
LEFT JOIN [HangFire].[State] s ON s.Id = lj.StateId
ORDER BY j.JobId;
