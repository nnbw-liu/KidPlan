-- =============================================================================
-- 端到端冒烟验证用的测试数据
--
-- 这些数据只服务于本机的 scripts/smoke.ps1：用真实 MySQL 造出一个有
-- 过敏原、成长记录、固定三餐 + 不定时加餐的孩子，然后把每个页面都拉一遍。
-- 验证完执行 scripts/smoke-cleanup.sql 清干净。
--
-- **所有日期都相对 CURDATE() / NOW() 写**，不写死具体某天。
-- 踩过的坑：原来这里写死了「今天 = 2026-10-05」，跨过午夜之后「今天」变成了
-- 10-06，饮食安排页（默认看今天）就变成空的，冒烟脚本报「页面里缺：早餐 / 午餐 / 晚餐」——
-- 看上去像功能坏了，其实就是测试数据的日期过期了。相对日期可以在任何一天重跑。
-- =============================================================================

-- 从库里按邮箱取出冒烟测试账号的 Id，而不是写死一个 GUID：
-- 每次重跑冒烟脚本都会删掉旧账号、重新注册一个（Id 每次都不一样），
-- 写死 Id 的结果是数据挂在已删除的用户名下，页面全是空的。
SET @owner = (SELECT Id FROM AspNetUsers WHERE Email = 'e2e-verify@example.com');

SELECT @owner AS owner_id;

DELETE FROM Children WHERE OwnerId = @owner;

-- 生日也相对今天写：这个孩子永远是「刚满 4 岁」，年龄文案不会随着时间漂走。
INSERT INTO Children
    (OwnerId, Alias, BirthDate, Gender, BloodType, AvatarEmoji, Hobbies, FavoriteSports,
     Allergies, AllergicFoods, Notes, IsArchived, CreatedAt, UpdatedAt)
VALUES
    -- Gender 存的是枚举名（Unspecified / Boy / Girl），不是 'Female'。
    -- 写错的话 EF 读回来会直接抛「Cannot convert string value ... to the mapped enum」。
    (@owner, '冒烟娃', DATE_SUB(CURDATE(), INTERVAL 4 YEAR), 'Girl', 'A', '🐻', '搭积木', '游泳',
     NULL, NULL, '端到端验证用，稍后删除', 0, NOW(6), NOW(6));

SET @child = LAST_INSERT_ID();

-- 过敏原：五个等级各来一条，顺便看看分组显示对不对。
INSERT INTO ChildAllergens (ChildId, Name, Kind, Severity, Notes, NeedsReview, CreatedAt, UpdatedAt) VALUES
    (@child, '牛奶',   'Food',  'Intolerance', '喝了会胀气',              0, NOW(6), NOW(6)),
    (@child, '芒果',   'Food',  'Mild',        '嘴角有点红',              0, NOW(6), NOW(6)),
    (@child, '鸡蛋',   'Food',  'Moderate',    '吃多了会起疹子',          0, NOW(6), NOW(6)),
    (@child, '花生',   'Food',  'Severe',      'IgE 检测 3 级',           0, NOW(6), NOW(6)),
    (@child, '腰果',   'Food',  'Anaphylaxis', '有一次喉头水肿，进过急诊', 0, NOW(6), NOW(6)),
    (@child, '花粉',   'Other', 'Moderate',    NULL,                      1, NOW(6), NOW(6));

-- 成长记录：凑够所有可选指标的取值，让每张图都有数据。
-- 注意 Appetite / Mood 是 NOT NULL（Unknown 也是合法值，落库就是字符串 'Unknown'）。
--
-- 日期同样相对今天写，而且都落在**默认的「近 6 个月」**窗口里：曲线默认按 6 个月筛，
-- 写死一个会慢慢滑出窗口的日期，几个月后「图从 3 张变 4 张」这类断言就会莫名其妙地失败。
--
-- 那条**时段记录刻意不填身高/体重**：时段记录本来就更像「这一周的睡眠/食欲怎么样」，
-- 而不是一次量身高。真实数据里也是这样，于是「记录方式=只看时段记录」时
-- 身高/体重/BMI 三条线全都没数据——这正是用户报的「曲线出不来」，留着当回归用例。
INSERT INTO GrowthRecords
    (ChildId, Kind, RecordedAt, PeriodStart, PeriodEnd, HeightCm, WeightKg, TemperatureC, SleepHours,
     ExerciseMinutes, Appetite, Mood, Symptoms, Notes, CreatedAt)
VALUES
    (@child, 'Point', DATE_SUB(NOW(6), INTERVAL 150 DAY), NULL, NULL, 98.00, 15.20, 36.6, 11.0, 60, 'Good',   'Happy',   NULL, '五个月前的测量', NOW(6)),
    (@child, 'Point', DATE_SUB(NOW(6), INTERVAL 110 DAY), NULL, NULL, 101.50, 16.10, 36.5, 11.5, 75, 'Good',   'Happy',   NULL, NULL,         NOW(6)),
    (@child, 'Period', DATE_SUB(NOW(6), INTERVAL 78 DAY), DATE_SUB(CURDATE(), INTERVAL 78 DAY), DATE_SUB(CURDATE(), INTERVAL 73 DAY), NULL, NULL, 38.4, 9.0, 40, 'Poor', 'Low', '低烧两天，食欲差', '发烧那一周', NOW(6)),
    (@child, 'Point', DATE_SUB(NOW(6), INTERVAL 35 DAY), NULL, NULL, 105.20, 17.30, 36.4, 11.5, 90, 'Good',   'Happy',   NULL, NULL, NOW(6)),
    (@child, 'Point', DATE_SUB(NOW(6), INTERVAL 5 DAY), NULL, NULL, 106.40, 17.80, 36.7, 12.0, 100, 'Excellent', 'Happy', NULL, '最近一次', NOW(6));

-- 饮食：昨天和今天记了固定三餐，还记了不定时的点心零食（PlannedTime 为空），
-- 另外留几天完全没记，用来检查「漏记」提示。
INSERT INTO MealPlans
    (ChildId, CreatedByUserId, Date, MealType, PlannedTime, Menu, Foods, EstimatedCalories,
     Status, AllergyWarningAcknowledged, AllergyAcknowledgedMaxSeverity, AllergyAcknowledgedAt,
     CreatedFromAiSuggestion, Notes, CreatedAt, UpdatedAt, EatenAt, FinishNote, Appetite)
VALUES
    -- 今天：早（已吃完）、午（含过敏原且已确认，用来检查蓝色提示）、晚（还没到饭点）
    (@child, @owner, CURDATE(), 'Breakfast', '07:30:00', '小米南瓜粥 + 水煮蛋', '小米、南瓜、鸡蛋', 260,
     'Eaten', 0, NULL, NULL, 0, NULL, NOW(6), NOW(6), TIMESTAMP(CURDATE(), '07:45:00'), '吃了大半碗', 'Good'),
    (@child, @owner, CURDATE(), 'Lunch', '12:00:00', '番茄牛肉面', '番茄、牛肉、面条', 420,
     'Planned', 1, 'Moderate', TIMESTAMP(CURDATE(), '11:00:00'), 0, '知道含鸡蛋，还是安排了', NOW(6), NOW(6), NULL, NULL, 'Unknown'),
    (@child, @owner, CURDATE(), 'Dinner', '18:00:00', '清蒸鲈鱼 + 米饭 + 西兰花', '鲈鱼、大米、西兰花', 380,
     'Planned', 0, NULL, NULL, 0, NULL, NOW(6), NOW(6), NULL, NULL, 'Unknown'),
    -- 今天的加餐：一个定了时间，一个完全不定时
    (@child, @owner, CURDATE(), 'Snack', '15:30:00', '酸奶 + 蓝莓', '酸奶、蓝莓', 120,
     'Planned', 0, NULL, NULL, 0, NULL, NOW(6), NOW(6), NULL, NULL, 'Unknown'),
    (@child, @owner, CURDATE(), 'Treat', NULL, '两块动物饼干', '小麦粉、黄油', 90,
     'Planned', 0, NULL, NULL, 0, '不定时的零食', NOW(6), NOW(6), NULL, NULL, 'Unknown'),
    -- 昨天：三餐都记了，其中午餐跳过；加餐一条来自 AI 推荐
    (@child, @owner, DATE_SUB(CURDATE(), INTERVAL 1 DAY), 'Breakfast', '07:30:00', '牛奶燕麦粥', '燕麦、牛奶', 240,
     'Eaten', 1, 'Intolerance', TIMESTAMP(DATE_SUB(CURDATE(), INTERVAL 1 DAY), '07:00:00'), 0, NULL, NOW(6), NOW(6),
     TIMESTAMP(DATE_SUB(CURDATE(), INTERVAL 1 DAY), '07:40:00'), NULL, 'Good'),
    (@child, @owner, DATE_SUB(CURDATE(), INTERVAL 1 DAY), 'Lunch', '12:00:00', '幼儿园午餐', NULL, NULL,
     'Skipped', 0, NULL, NULL, 0, '在幼儿园吃过了', NOW(6), NOW(6), NULL, NULL, 'Unknown'),
    (@child, @owner, DATE_SUB(CURDATE(), INTERVAL 1 DAY), 'Dinner', '18:00:00', '虾仁蒸蛋 + 米饭', '虾仁、鸡蛋、大米', 350,
     'Eaten', 0, NULL, NULL, 0, NULL, NOW(6), NOW(6),
     TIMESTAMP(DATE_SUB(CURDATE(), INTERVAL 1 DAY), '18:20:00'), NULL, 'Good'),
    (@child, @owner, DATE_SUB(CURDATE(), INTERVAL 1 DAY), 'Snack', NULL, '一小把葡萄干', '葡萄干', 80,
     'Planned', 0, NULL, NULL, 1, 'AI 推荐写入的加餐', NOW(6), NOW(6), NULL, NULL, 'Unknown');

SELECT @child AS child_id, @owner AS owner_id;
