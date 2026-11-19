-- Scheduler plans and their jobs, one row per job (a plan with no jobs still gets a row).
-- Use with: "mode": "filePerRow", "fileName": "{QueueName}/{PlanName}/{JobSequence} {JobName}".
-- Run against the Scheduler database.
--
-- Health is ScheduleHealth's own status (Healthy/Degraded/Unhealthy/Unknown), or Stale when the service hasn't
-- written it for 10 minutes (stopped or hung). Both change only with the plan's state. Times are left out.
-- Without a ScheduleHealth table (an older scheduler), drop the join and the Health column.
--
-- A job can be active here and still do nothing if the service switches its feature off elsewhere (a system setting,
-- for example). Export those switches with their own query.
SET NOCOUNT ON;

SELECT p.QueueName,
       p.Name                                               AS PlanName,
       p.GroupCode,
       p.Description                                        AS PlanDescription,
       p.ScheduleType,
       p.Cron,
       p.IsActive                                           AS PlanActive,
       p.RunOnStartup,
       CONVERT(date, p.FromDate)                            AS FromDate,
       CONVERT(date, p.ToDate)                              AS ToDate,
       ISNULL(j.Sequence, 0)                                AS JobSequence,
       ISNULL(j.Name, '(no jobs)')                          AS JobName,
       j.IsActive                                           AS JobActive,
       JSON_VALUE(j.TaskDetail, '$.Assembly')               AS Assembly,
       JSON_VALUE(j.TaskDetail, '$.Class')                  AS Class,
       JSON_VALUE(j.TaskDetail, '$.Method')                 AS Method,
       j.Params,
       CASE
           WHEN h.SchedulePlanId IS NULL                                 THEN NULL
           WHEN h.CheckedTime < DATEADD(MINUTE, -10, SYSDATETIME())      THEN 'Stale'
           ELSE h.Status
       END                                                  AS Health
FROM dbo.SchedulePlan p
LEFT JOIN dbo.ScheduleJob j ON j.SchedulePlanId = p.SchedulePlanId
LEFT JOIN dbo.ScheduleHealth h ON h.SchedulePlanId = p.SchedulePlanId
ORDER BY p.QueueName, p.Name, j.Sequence;
