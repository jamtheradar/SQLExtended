-- SQL Server Agent jobs, one row per job step.
-- Use with: "mode": "filePerRow", "fileName": "{target}/{JobName}/{StepId} {StepName}", "database": "msdb".
-- The login needs SQLAgentReaderRole in msdb (or sysadmin).
--
-- Schedules are summarised in one column per step's job so a schedule change shows on every step of that job.
-- Health is the job's last outcome as a word; run times and durations are left out (they change every run).
SET NOCOUNT ON;

WITH sched AS
(
    -- Each schedule as words, e.g. "Nightly (on): weekly every 1 week on Mon,Tue,Wed,Thu,Fri at 02:00".
    SELECT s.schedule_id, s.name,
           CONCAT(s.name, ' (', CASE s.enabled WHEN 1 THEN 'on' ELSE 'off' END, '): ',
               CASE s.freq_type
                   WHEN 1   THEN CONCAT('once on ', s.active_start_date)
                   WHEN 4   THEN CONCAT('daily every ', s.freq_interval, ' day', CASE WHEN s.freq_interval > 1 THEN 's' END)
                   WHEN 8   THEN CONCAT('weekly every ', s.freq_recurrence_factor, ' week', CASE WHEN s.freq_recurrence_factor > 1 THEN 's' END, ' on ',
                                    STUFF(CONCAT(CASE WHEN s.freq_interval & 1  > 0 THEN ',Sun' END, CASE WHEN s.freq_interval & 2  > 0 THEN ',Mon' END,
                                                 CASE WHEN s.freq_interval & 4  > 0 THEN ',Tue' END, CASE WHEN s.freq_interval & 8  > 0 THEN ',Wed' END,
                                                 CASE WHEN s.freq_interval & 16 > 0 THEN ',Thu' END, CASE WHEN s.freq_interval & 32 > 0 THEN ',Fri' END,
                                                 CASE WHEN s.freq_interval & 64 > 0 THEN ',Sat' END), 1, 1, ''))
                   WHEN 16  THEN CONCAT('monthly on day ', s.freq_interval, ' every ', s.freq_recurrence_factor, ' month', CASE WHEN s.freq_recurrence_factor > 1 THEN 's' END)
                   WHEN 32  THEN CONCAT('monthly on the ',
                                    CASE s.freq_relative_interval WHEN 1 THEN 'first' WHEN 2 THEN 'second' WHEN 4 THEN 'third' WHEN 8 THEN 'fourth' WHEN 16 THEN 'last' END, ' ',
                                    CASE s.freq_interval WHEN 1 THEN 'Sun' WHEN 2 THEN 'Mon' WHEN 3 THEN 'Tue' WHEN 4 THEN 'Wed' WHEN 5 THEN 'Thu' WHEN 6 THEN 'Fri'
                                                         WHEN 7 THEN 'Sat' WHEN 8 THEN 'day' WHEN 9 THEN 'weekday' WHEN 10 THEN 'weekend day' END,
                                    ' every ', s.freq_recurrence_factor, ' month', CASE WHEN s.freq_recurrence_factor > 1 THEN 's' END)
                   WHEN 64  THEN 'when SQL Agent starts'
                   WHEN 128 THEN 'when the CPU is idle'
                   ELSE CONCAT('freq_type ', s.freq_type)
               END,
               CASE WHEN s.freq_type IN (64, 128) THEN ''
                    WHEN s.freq_subday_type IN (2, 4, 8) THEN CONCAT(', every ', s.freq_subday_interval,
                         CASE s.freq_subday_type WHEN 2 THEN ' sec' WHEN 4 THEN ' min' ELSE ' hr' END,
                         ' from ', STUFF(LEFT(RIGHT('000000' + CAST(s.active_start_time AS varchar(6)), 6), 4), 3, 0, ':'),
                         ' to ',   STUFF(LEFT(RIGHT('000000' + CAST(s.active_end_time   AS varchar(6)), 6), 4), 3, 0, ':'))
                    ELSE CONCAT(' at ', STUFF(LEFT(RIGHT('000000' + CAST(s.active_start_time AS varchar(6)), 6), 4), 3, 0, ':'))
               END) AS Description
    FROM dbo.sysschedules s
),
schedules AS
(
    SELECT js.job_id,
           STRING_AGG(sd.Description, '; ') WITHIN GROUP (ORDER BY sd.name, sd.schedule_id) AS Schedules
    FROM dbo.sysjobschedules js
    JOIN sched sd ON sd.schedule_id = js.schedule_id
    GROUP BY js.job_id
),
lastrun AS
(
    SELECT h.job_id, h.run_status,
           ROW_NUMBER() OVER (PARTITION BY h.job_id ORDER BY h.instance_id DESC) AS rn
    FROM dbo.sysjobhistory h
    WHERE h.step_id = 0
)
SELECT j.name                         AS JobName,
       j.enabled                      AS JobEnabled,
       c.name                         AS Category,
       j.description                  AS JobDescription,
       sc.Schedules,
       st.step_id                     AS StepId,
       st.step_name                   AS StepName,
       st.subsystem                   AS Subsystem,
       st.database_name               AS DatabaseName,
       st.command                     AS Command,
       CASE lr.run_status
           WHEN 0 THEN 'Failed' WHEN 1 THEN 'Succeeded' WHEN 2 THEN 'Retry' WHEN 3 THEN 'Cancelled'
           WHEN 4 THEN 'InProgress' ELSE 'NeverRun'
       END                            AS Health
FROM dbo.sysjobs j
LEFT JOIN dbo.syscategories c ON c.category_id = j.category_id
LEFT JOIN dbo.sysjobsteps st ON st.job_id = j.job_id
LEFT JOIN schedules sc ON sc.job_id = j.job_id
LEFT JOIN lastrun lr ON lr.job_id = j.job_id AND lr.rn = 1
-- SSRS makes one GUID-named job per subscription schedule (category 'Report Server'). Uncomment to leave them out.
-- WHERE ISNULL(c.name, '') <> N'Report Server'
ORDER BY j.name, st.step_id;
