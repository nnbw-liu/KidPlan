-- =============================================================================
-- 清掉 scripts/smoke.ps1 / smoke-data.sql 造出来的东西
--
-- 只需要删账号：Children / ChildAllergens / GrowthRecords / MealPlans
-- 对它的外键都是 ON DELETE CASCADE，会一起走。
-- =============================================================================

DELETE FROM AspNetUsers WHERE Email = 'e2e-verify@example.com';

SELECT
    (SELECT COUNT(*) FROM AspNetUsers WHERE Email = 'e2e-verify@example.com') AS 残留账号,
    (SELECT COUNT(*) FROM Children) AS 剩下的小孩,
    (SELECT COUNT(*) FROM ChildAllergens) AS 剩下的过敏原,
    (SELECT COUNT(*) FROM GrowthRecords) AS 剩下的身体记录,
    (SELECT COUNT(*) FROM MealPlans) AS 剩下的饮食安排;
