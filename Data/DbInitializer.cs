using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Northtropic.Models;

namespace Northtropic.Data
{
    public static class DbInitializer
    {
        public static void Initialize(AppDbContext context)
        {
            context.Database.EnsureCreated();

            // 优化 SQLite 并发读写与锁等待配置 (WAL 预写日志模式，支持读写并发，避免 "database is locked" 异常)
            try
            {
                context.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");
                context.Database.ExecuteSqlRaw("PRAGMA busy_timeout = 5000;");
                context.Database.ExecuteSqlRaw("PRAGMA synchronous = NORMAL;");
            }
            catch { }

            // 确保 SQLite 中存在 LlmGenerationLogs 数据表
            context.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""LlmGenerationLogs"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_LlmGenerationLogs"" PRIMARY KEY,
                    ""UserId"" TEXT NOT NULL,
                    ""QuestionId"" TEXT NULL,
                    ""ModelName"" TEXT NOT NULL,
                    ""Subject"" TEXT NOT NULL,
                    ""Category"" TEXT NOT NULL,
                    ""PromptTokens"" INTEGER NOT NULL,
                    ""CompletionTokens"" INTEGER NOT NULL,
                    ""TotalTokens"" INTEGER NOT NULL,
                    ""GeneratedAt"" TEXT NOT NULL
                );
            ");

            // 确保 SQLite 中存在 StudentParentBindings 数据表
            context.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""StudentParentBindings"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_StudentParentBindings"" PRIMARY KEY,
                    ""ParentUserId"" TEXT NOT NULL,
                    ""StudentUserId"" TEXT NOT NULL,
                    ""RelationType"" TEXT NOT NULL DEFAULT '监护人',
                    ""CreatedAt"" TEXT NOT NULL
                );
            ");

            // 确保 SQLite 中存在 UserFavorites 数据表
            context.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""UserFavorites"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_UserFavorites"" PRIMARY KEY,
                    ""UserId"" TEXT NOT NULL,
                    ""QuestionId"" TEXT NOT NULL,
                    ""Note"" TEXT NOT NULL DEFAULT '',
                    ""CreatedAt"" TEXT NOT NULL
                );
            ");

            // 确保 SQLite 中存在 CurriculumSubjectConfigs 数据表
            context.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""CurriculumSubjectConfigs"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_CurriculumSubjectConfigs"" PRIMARY KEY,
                    ""Grade"" TEXT NOT NULL,
                    ""Subject"" TEXT NOT NULL,
                    ""TopicsJson"" TEXT NOT NULL DEFAULT '[]',
                    ""SortOrder"" INTEGER NOT NULL DEFAULT 0,
                    ""IsBuiltIn"" INTEGER NOT NULL DEFAULT 1,
                    ""UpdatedAt"" TEXT NOT NULL
                );
            ");

            // 确保 SQLite 中存在 HomeworkAssignments 数据表
            context.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""HomeworkAssignments"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_HomeworkAssignments"" PRIMARY KEY,
                    ""CreatorUserId"" TEXT NOT NULL,
                    ""StudentUserId"" TEXT NOT NULL,
                    ""Title"" TEXT NOT NULL,
                    ""Subject"" TEXT NOT NULL,
                    ""Category"" TEXT NOT NULL DEFAULT '全部分类',
                    ""QuestionCount"" INTEGER NOT NULL DEFAULT 5,
                    ""TargetDifficulty"" INTEGER NOT NULL DEFAULT 3,
                    ""Deadline"" TEXT NULL,
                    ""ParentNote"" TEXT NOT NULL DEFAULT '',
                    ""IsCompleted"" INTEGER NOT NULL DEFAULT 0,
                    ""Score"" INTEGER NOT NULL DEFAULT 0,
                    ""AccuracyRate"" INTEGER NOT NULL DEFAULT 0,
                    ""CorrectCount"" INTEGER NOT NULL DEFAULT 0,
                    ""TotalAnswered"" INTEGER NOT NULL DEFAULT 0,
                    ""CompletedAt"" TEXT NULL,
                    ""CreatedAt"" TEXT NOT NULL
                );
            ");

            // 安全补全 Users 表的所有列
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""PhoneNumber"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""Role"" INTEGER NOT NULL DEFAULT 3;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""BindingCode"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""TodayAnsweredCount"" INTEGER NOT NULL DEFAULT 0;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""TodayCountDate"" TEXT NOT NULL DEFAULT '2026-01-01';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""ActiveTitle"" TEXT NOT NULL DEFAULT '青铜学童';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""SoundEffectsEnabled"" INTEGER NOT NULL DEFAULT 1;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""ConfettiEnabled"" INTEGER NOT NULL DEFAULT 1;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""ExpBoostUntil"" TEXT NULL;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""GoldBoostUntil"" TEXT NULL;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""ComboShieldCount"" INTEGER NOT NULL DEFAULT 0;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""DailyTargetQuestions"" INTEGER NOT NULL DEFAULT 20;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""AutoVoiceGuidance"" INTEGER NOT NULL DEFAULT 1;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""OcrProvider"" TEXT NOT NULL DEFAULT 'PaddleOcr';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""BaiduApiKey"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""BaiduSecretKey"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""BaiduOcrEndpoint"" TEXT NOT NULL DEFAULT 'https://aip.baidubce.com/rest/2.0/ocr/v1/accurate_basic';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""Password"" TEXT NOT NULL DEFAULT '123456';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""MustChangePassword"" INTEGER NOT NULL DEFAULT 1;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""SmsToken"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""SmsApiEndpoint"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"UPDATE ""Users"" SET ""SmsToken"" = '', ""SmsApiEndpoint"" = '' WHERE ""SmsApiEndpoint"" LIKE '%iorai%' OR ""SmsToken"" LIKE '77C69%';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""SmsTemplate"" TEXT NOT NULL DEFAULT '您的登录验证码为{0}';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""SessionTimeoutMinutes"" INTEGER NOT NULL DEFAULT 30;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""Email"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""AccountStatus"" INTEGER NOT NULL DEFAULT 0;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""RejectReason"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""RegisteredAt"" TEXT NOT NULL DEFAULT '2026-01-01';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""ApprovedAt"" TEXT NULL;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""AliyunAccessKeyId"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""AliyunAccessKeySecret"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""AliyunSmsSignName"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""AliyunSmsTemplateCode"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""AliyunSmsTemplateParam"" TEXT NOT NULL DEFAULT 'code';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""AliyunSmsEndpoint"" TEXT NOT NULL DEFAULT 'dysmsapi.aliyuncs.com';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""AliyunSmsRegionId"" TEXT NOT NULL DEFAULT 'cn-hangzhou';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""Avatar"" TEXT NOT NULL DEFAULT '🎓';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""LastDailyRewardClaimDate"" TEXT NULL;");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""ParentEncouragementNote"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""Users"" ADD COLUMN ""ParentNoteUpdatedAt"" TEXT NULL;");

            // 确保超级管理员默认手机号为 13800000000
            SafeExecuteSql(context, @"UPDATE ""Users"" SET ""PhoneNumber"" = '13800000000' WHERE ""Role"" = 0;");

            // 安全补全 Questions 表的新增公私有列
            SafeExecuteSql(context, @"ALTER TABLE ""Questions"" ADD COLUMN ""CreatedByUserId"" TEXT NULL;");
            SafeExecuteSql(context, @"ALTER TABLE ""Questions"" ADD COLUMN ""IsPublic"" INTEGER NOT NULL DEFAULT 0;");
            SafeExecuteSql(context, @"ALTER TABLE ""Questions"" ADD COLUMN ""PublishStatus"" INTEGER NOT NULL DEFAULT 0;");
            SafeExecuteSql(context, @"ALTER TABLE ""Questions"" ADD COLUMN ""CreatedAt"" TEXT NOT NULL DEFAULT '2026-01-01';");

            // 安全补全 ErrorItems 表的新增列
            SafeExecuteSql(context, @"ALTER TABLE ""ErrorItems"" ADD COLUMN ""ErrorReasonCategory"" TEXT NOT NULL DEFAULT '未分类';");
            SafeExecuteSql(context, @"ALTER TABLE ""ErrorItems"" ADD COLUMN ""AiCustomAdvice"" TEXT NOT NULL DEFAULT '';");
            SafeExecuteSql(context, @"ALTER TABLE ""ErrorItems"" ADD COLUMN ""LastRevisedAt"" TEXT NULL;");

            // 确保系统中存在 4 大体验账号 (管理员、学员李小明、家长李大强、名师张老师)
            var adminId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var studentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var parentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            var teacherId = Guid.Parse("44444444-4444-4444-4444-444444444444");

            var adminUser = context.Users.FirstOrDefault(u => u.Id == adminId || u.Role == UserRole.SuperAdmin || u.PhoneNumber == "13800000000");
            if (adminUser == null)
            {
                adminUser = new User
                {
                    Id = adminId,
                    Username = "超级管理员",
                    PhoneNumber = "13800000000",
                    Role = UserRole.SuperAdmin,
                    Level = 5,
                    Exp = 450,
                    Coins = 500,
                    CurrentStreak = 7,
                    Grade = "大学/职业软件工程",
                    Avatar = "🛡️",
                    AccountStatus = UserAccountStatus.Approved,
                    Password = "123456",
                    MustChangePassword = false
                };
                context.Users.Add(adminUser);
            }

            var studentUser = context.Users.FirstOrDefault(u => u.Id == studentId || u.PhoneNumber == "13800000001" || (u.Role == UserRole.Student && u.Username == "李小明"));
            if (studentUser == null)
            {
                studentUser = new User
                {
                    Id = studentId,
                    Username = "李小明",
                    PhoneNumber = "13800000001",
                    Role = UserRole.Student,
                    Level = 3,
                    Exp = 260,
                    Coins = 320,
                    CurrentStreak = 5,
                    Grade = "初中二年级",
                    BindingCode = "STU2026",
                    Avatar = "🎒",
                    TotalAnswered = 48,
                    TotalCorrect = 39,
                    TodayAnsweredCount = 12,
                    DailyTargetQuestions = 20,
                    AccountStatus = UserAccountStatus.Approved,
                    Password = "123456",
                    MustChangePassword = false
                };
                context.Users.Add(studentUser);
            }

            var parentUser = context.Users.FirstOrDefault(u => u.Id == parentId || u.PhoneNumber == "13800000002" || (u.Role == UserRole.Parent && u.Username == "李大强"));
            if (parentUser == null)
            {
                parentUser = new User
                {
                    Id = parentId,
                    Username = "李大强",
                    PhoneNumber = "13800000002",
                    Role = UserRole.Parent,
                    Level = 1,
                    Exp = 100,
                    Coins = 200,
                    CurrentStreak = 3,
                    Grade = "初中二年级",
                    Avatar = "👨‍👩‍👧",
                    AccountStatus = UserAccountStatus.Approved,
                    Password = "123456",
                    MustChangePassword = false
                };
                context.Users.Add(parentUser);
            }

            var teacherUser = context.Users.FirstOrDefault(u => u.Id == teacherId || u.PhoneNumber == "13800000003" || (u.Role == UserRole.Teacher && u.Username == "张老师"));
            if (teacherUser == null)
            {
                teacherUser = new User
                {
                    Id = teacherId,
                    Username = "张老师",
                    PhoneNumber = "13800000003",
                    Role = UserRole.Teacher,
                    Level = 4,
                    Exp = 380,
                    Coins = 400,
                    CurrentStreak = 6,
                    Grade = "初中二年级",
                    Avatar = "👩‍🏫",
                    AccountStatus = UserAccountStatus.Approved,
                    Password = "123456",
                    MustChangePassword = false
                };
                context.Users.Add(teacherUser);
            }
            context.SaveChanges();

            // 确保家长李大强与学员李小明建立监护人关联
            if (parentUser != null && studentUser != null)
            {
                var binding = context.StudentParentBindings.FirstOrDefault(b => b.ParentUserId == parentUser.Id && b.StudentUserId == studentUser.Id);
                if (binding == null)
                {
                    context.StudentParentBindings.Add(new StudentParentBinding
                    {
                        Id = Guid.NewGuid(),
                        ParentUserId = parentUser.Id,
                        StudentUserId = studentUser.Id,
                        RelationType = "父亲/监护人",
                        CreatedAt = DateTime.Now
                    });
                    context.SaveChanges();
                }
            }

            // 预置精品样题库（增量填充，确保全学科、全学段拥有充足标准题源）
            var sampleQuestions = new List<Question>
            {
                // ==================== 初中数学 ====================
                new Question
                {
                    Id = Guid.Parse("a1111111-1111-1111-1111-111111111111"),
                    Subject = "数学",
                    Category = "勾股定理与二次根式",
                    GradeTarget = "初中二年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "在直角三角形 $\\triangle ABC$ 中，已知两直角边长分别为 $a = 6$ 和 $b = 8$，则斜边 $c$ 的长度为多少？",
                    OptionsJson = "[\"A. 10\",\"B. 14\",\"C. 12\",\"D. 4.8\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "根据勾股定理 $c^2 = a^2 + b^2$。代入数据得 $c^2 = 6^2 + 8^2 = 36 + 64 = 100$，因此斜边 $c = 10$。故正确答案选 A。",
                    Difficulty = 2,
                    BaseExpReward = 15,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },
                new Question
                {
                    Id = Guid.Parse("a3333333-3333-3333-3333-333333333333"),
                    Subject = "数学",
                    Category = "一元二次方程根与系数关系",
                    GradeTarget = "初中二年级",
                    Type = QuestionType.FillInBlank,
                    Stem = "已知一元二次方程 $x^2 - 5x + 6 = 0$ 的两个实数根分别为 $x_1, x_2$，则两根之积 $x_1 \\cdot x_2$ 的值为______。",
                    OptionsJson = "[]",
                    CorrectAnswer = "6",
                    StandardAnalysis = "根据一元二次方程根与系数的关系（韦达定理）：对于方程 $ax^2 + bx + c = 0$，有 $x_1 x_2 = \\frac{c}{a}$。本题中 $a=1, c=6$，因此 $x_1 x_2 = 6$。",
                    Difficulty = 2,
                    BaseExpReward = 15,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },
                new Question
                {
                    Id = Guid.Parse("b1010000-0000-0000-0000-000000000001"),
                    Subject = "数学",
                    Category = "二次函数性质与最值",
                    GradeTarget = "初中三年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "已知二次函数 $y = -(x - 1)^2 + 4$，关于该函数图象的性质，下列说法正确的是：",
                    OptionsJson = "[\"A. 图象开口向下，当 x = 1 时取得最大值 4\",\"B. 图象开口向上，当 x = 1 时取得最小值 4\",\"C. 图象与 y 轴交点为 (0, 4)\",\"D. 对称轴为直线 x = -1\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "由顶点式 $y = a(x-h)^2 + k$ 可知，$a = -1 < 0$，抛物线开口向下，对称轴为 $x=1$，顶点为 $(1, 4)$。当 $x=1$ 时取得最大值 4。当 $x=0$ 时 $y=3$，与 y 轴交于 $(0, 3)$。故选 A。",
                    Difficulty = 3,
                    BaseExpReward = 20,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },
                new Question
                {
                    Id = Guid.Parse("b1020000-0000-0000-0000-000000000002"),
                    Subject = "数学",
                    Category = "三角形全等判定与角平分线",
                    GradeTarget = "初中一年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "在 $\\triangle ABC$ 中，已知 $\\angle A = 50^\\circ$，$\\angle B = 60^\\circ$，则第三个角 $\\angle C$ 的度数是：",
                    OptionsJson = "[\"A. 70°\",\"B. 80°\",\"C. 60°\",\"D. 90°\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "三角形内角和定理：任意三角形的三个内角之和等于 $180^\\circ$。因此 $\\angle C = 180^\\circ - 50^\\circ - 60^\\circ = 70^\\circ$。故选 A。",
                    Difficulty = 1,
                    BaseExpReward = 10,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },

                // ==================== 初中物理 ====================
                new Question
                {
                    Id = Guid.Parse("a2222222-2222-2222-2222-222222222222"),
                    Subject = "物理",
                    Category = "牛顿运动定律与浮力",
                    GradeTarget = "初中二年级",
                    Type = QuestionType.MultipleChoice,
                    Stem = "下列关于物体受力与浮力的说法中，**正确**的有（多选）：",
                    OptionsJson = "[\"A. 浸在液体中的物体所受浮力大小等于它排开液体所受的重力\",\"B. 悬浮在水中的物体，其密度等于水的密度\",\"C. 物体的重力加速度在地球各处严格恒定不变\",\"D. 漂浮在水面的轮船，受到的浮力大于其自身重力\"]",
                    CorrectAnswer = "A, B",
                    StandardAnalysis = "根据阿基米德原理，浮力等于排开液体受到的重力（A正确）；物体悬浮时 $\\rho_{物} = \\rho_{液}$ 且 $F_浮 = G$（B正确）；漂浮时 $F_浮 = G$ 而非大于（D错误）；地球不同纬度重力加速度略有差异（C错误）。故正确选项为 A, B。",
                    Difficulty = 3,
                    BaseExpReward = 20,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },
                new Question
                {
                    Id = Guid.Parse("b2010000-0000-0000-0000-000000000001"),
                    Subject = "物理",
                    Category = "凸透镜成像规律与光学",
                    GradeTarget = "初中二年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "在探究凸透镜成像规律的实验中，当物体到凸透镜的距离大于 2 倍焦距 ($u > 2f$) 时，光屏上所成的像是：",
                    OptionsJson = "[\"A. 倒立、缩小的实像\",\"B. 倒立、放大的实像\",\"C. 正立、放大的虚像\",\"D. 倒立、等大的实像\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "根据凸透镜成像规律：当物距 $u > 2f$ 时，成倒立、缩小的实像，像距 $f < v < 2f$，其实际应用是照相机。故选 A。",
                    Difficulty = 2,
                    BaseExpReward = 15,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },
                new Question
                {
                    Id = Guid.Parse("b2020000-0000-0000-0000-000000000002"),
                    Subject = "物理",
                    Category = "欧姆定律与电功率",
                    GradeTarget = "初中三年级",
                    Type = QuestionType.FillInBlank,
                    Stem = "一个标有“$6\\text{V}\\ 3\\text{W}$”字样的小灯泡正常发光时，通过它的电流为______ $\\text{A}$。",
                    OptionsJson = "[]",
                    CorrectAnswer = "0.5",
                    StandardAnalysis = "由电功率公式 $P = UI$ 可得：$I = \\frac{P}{U} = \\frac{3\\text{W}}{6\\text{V}} = 0.5\\text{A}$。故答案为 0.5。",
                    Difficulty = 2,
                    BaseExpReward = 15,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },

                // ==================== 初中化学 ====================
                new Question
                {
                    Id = Guid.Parse("a4444444-4444-4444-4444-444444444444"),
                    Subject = "化学",
                    Category = "质量守恒定律与化学方程式",
                    GradeTarget = "初中三年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "在密闭容器内发生反应 $2\\text{H}_2 + \\text{O}_2 \\xrightarrow{\\text{点燃}} 2\\text{H}_2\\text{O}$，若有 $4\\text{g}$ 氢气与 $32\\text{g}$ 氧气恰好完全反应，则生成水的质量为多少？",
                    OptionsJson = "[\"A. 18g\",\"B. 36g\",\"C. 32g\",\"D. 40g\"]",
                    CorrectAnswer = "B",
                    StandardAnalysis = "根据质量守恒定律，参加化学反应的各物质质量总和等于反应后生成的各物质质量总和。$m(\\text{H}_2\\text{O}) = m(\\text{H}_2) + m(\\text{O}_2) = 4\\text{g} + 32\\text{g} = 36\\text{g}$。故正确答案为 B。",
                    Difficulty = 2,
                    BaseExpReward = 15,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },
                new Question
                {
                    Id = Guid.Parse("b3010000-0000-0000-0000-000000000001"),
                    Subject = "化学",
                    Category = "金属活动性顺序与置换反应",
                    GradeTarget = "初中三年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "下列各组物质间，**不能**发生置换反应的是：",
                    OptionsJson = "[\"A. 铁 (Fe) 与硫酸锌 (ZnSO4) 溶液\",\"B. 铁 (Fe) 与硫酸铜 (CuSO4) 溶液\",\"C. 锌 (Zn) 与稀硫酸 (H2SO4)\",\"D. 铜 (Cu) 与硝酸银 (AgNO3) 溶液\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "根据金属活动性顺序：K, Ca, Na, Mg, Al, Zn, Fe, Sn, Pb, (H), Cu, Hg, Ag, Pt, Au。排在前面的金属能把排在后面的金属从其盐溶液中置换出来。锌的金属活动性强于铁，因此铁不能置换出硫酸锌溶液中的锌。故选 A。",
                    Difficulty = 2,
                    BaseExpReward = 15,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },
                new Question
                {
                    Id = Guid.Parse("b3020000-0000-0000-0000-000000000002"),
                    Subject = "化学",
                    Category = "常见酸碱盐与溶液pH",
                    GradeTarget = "初中三年级",
                    Type = QuestionType.FillInBlank,
                    Stem = "常温（$25^\\circ\\text{C}$）下，纯水和中性溶液的 $\\text{pH}$ 值等于______。",
                    OptionsJson = "[]",
                    CorrectAnswer = "7",
                    StandardAnalysis = "常温下，中性溶液的 $\\text{pH} = 7$；酸性溶液 $\\text{pH} < 7$；碱性溶液 $\\text{pH} > 7$。故填 7。",
                    Difficulty = 1,
                    BaseExpReward = 10,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },

                // ==================== 初中英语 ====================
                new Question
                {
                    Id = Guid.Parse("a5555555-5555-5555-5555-555555555555"),
                    Subject = "英语",
                    Category = "定语从句与时态语法",
                    GradeTarget = "初中三年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "The girl ________ won the first prize in the English speech contest is my sister.",
                    OptionsJson = "[\"A. which\",\"B. who\",\"C. whose\",\"D. where\"]",
                    CorrectAnswer = "B",
                    StandardAnalysis = "先行词是 The girl（指人），在定语从句中作主语，关系代词应使用 who（或 that）。which 指物，whose 表所属，where 表地点。故正确答案为 B。",
                    Difficulty = 1,
                    BaseExpReward = 10,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },
                new Question
                {
                    Id = Guid.Parse("b4010000-0000-0000-0000-000000000001"),
                    Subject = "英语",
                    Category = "宾语从句陈述语序与时态",
                    GradeTarget = "初中三年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "Excuse me, could you tell me ________ yesterday afternoon?",
                    OptionsJson = "[\"A. where did you buy the book\",\"B. where you bought the book\",\"C. why did you buy the book\",\"D. when will the train leave\"]",
                    CorrectAnswer = "B",
                    StandardAnalysis = "宾语从句有两大核心考点：陈述语序与时态呼应。宾语从句必须使用陈述语序（主语在前，谓语在后），排除 A、C 疑问语序；结合时间状语 yesterday afternoon，从句须用一般过去时 bought。故选 B。",
                    Difficulty = 2,
                    BaseExpReward = 15,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },
                new Question
                {
                    Id = Guid.Parse("b4020000-0000-0000-0000-000000000002"),
                    Subject = "英语",
                    Category = "非谓语动词与固定搭配",
                    GradeTarget = "初中二年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "Our English teacher always encourages us ________ English aloud every morning.",
                    OptionsJson = "[\"A. to read\",\"B. reading\",\"C. read\",\"D. reads\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "固定搭配 encourage sb. to do sth. 表示“鼓励某人做某事”，应用动词不定式 to read。故选 A。",
                    Difficulty = 1,
                    BaseExpReward = 10,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },

                // ==================== 语文 ====================
                new Question
                {
                    Id = Guid.Parse("b5010000-0000-0000-0000-000000000001"),
                    Subject = "语文",
                    Category = "文言文实词与通假字",
                    GradeTarget = "初中二年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "下列各组文言文语句中，加点字**不含通假字**的一项是：",
                    OptionsJson = "[\"A. 学而时习之，不亦说乎（《论语》）\",\"B. 便要还家，设酒杀鸡作食（《桃花源记》）\",\"C. 荡胸生曾云，决眦入归鸟（《望岳》）\",\"D. 落霞与孤鹜齐飞，秋水共长天一色（《滕王阁序》）\"]",
                    CorrectAnswer = "D",
                    StandardAnalysis = "A 项“说”通“悦”，愉快；B 项“要”通“邀”，邀请；C 项“曾”通“层”，重叠。D 项各字均为本义，无通假字。故选 D。",
                    Difficulty = 2,
                    BaseExpReward = 15,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },
                new Question
                {
                    Id = Guid.Parse("b5020000-0000-0000-0000-000000000002"),
                    Subject = "语文",
                    Category = "古诗名篇名句默写",
                    GradeTarget = "初中二年级",
                    Type = QuestionType.FillInBlank,
                    Stem = "王维在《使至塞上》中描绘塞外奇特壮丽风光的千古名句是：“______，长河落日圆。”",
                    OptionsJson = "[]",
                    CorrectAnswer = "大漠孤烟直",
                    StandardAnalysis = "唐代诗人王维《使至塞上》经典名句：“大漠孤烟直，长河落日圆。”字迹须准确无误，故填“大漠孤烟直”。",
                    Difficulty = 1,
                    BaseExpReward = 10,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },
                new Question
                {
                    Id = Guid.Parse("b5030000-0000-0000-0000-000000000003"),
                    Subject = "语文",
                    Category = "现代文修辞手法与表达效果",
                    GradeTarget = "初中一年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "“盼望着，盼望着，东风来了，春天的脚步近了。”（朱自清《春》）这句话运用的主要修辞手法是：",
                    OptionsJson = "[\"A. 反复与拟人\",\"B. 比喻与夸张\",\"C. 排比与对偶\",\"D. 设问与借代\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "“盼望着，盼望着”连续出现两次，是典型的反复修辞，生动表达出对春天的殷切期盼；“春天的脚步近了”把春天人格化，赋予其人的脚步动作，属于拟人修辞。故选 A。",
                    Difficulty = 1,
                    BaseExpReward = 10,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },

                // ==================== 生物 ====================
                new Question
                {
                    Id = Guid.Parse("b6010000-0000-0000-0000-000000000001"),
                    Subject = "生物",
                    Category = "显微镜使用与细胞结构",
                    GradeTarget = "初中一年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "在显微镜下观察洋葱表皮临时装片时，若物像位于视野的左上方，欲将其移到视野正中央，玻片标本应向哪个方向移动？",
                    OptionsJson = "[\"A. 左上方\",\"B. 右下方\",\"C. 右上方\",\"D. 左下方\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "显微镜下所成的像是倒立的虚像（即上下颠倒、左右颠倒）。物像偏向哪个方向，标本就向哪个方向移动。物像在左上方，故玻片标本应向左上方移动。故选 A。",
                    Difficulty = 2,
                    BaseExpReward = 15,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },

                // ==================== 历史 ====================
                new Question
                {
                    Id = Guid.Parse("b7010000-0000-0000-0000-000000000001"),
                    Subject = "历史",
                    Category = "中国古代史与丝绸之路",
                    GradeTarget = "初中一年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "西汉汉武帝时期，为联络大月氏夹击匈奴，开辟了通往西域的丝绸之路的关键历史人物是：",
                    OptionsJson = "[\"A. 张骞\",\"B. 班超\",\"C. 卫青\",\"D. 玄奘\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "西汉建元二年（公元前138年），张骞奉汉武帝之命出使西域，历经艰险开辟了连接亚欧大陆的丝绸之路，被誉为“凿空”之举。故选 A。",
                    Difficulty = 1,
                    BaseExpReward = 10,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },

                // ==================== 地理 ====================
                new Question
                {
                    Id = Guid.Parse("b8010000-0000-0000-0000-000000000001"),
                    Subject = "地理",
                    Category = "地球自转公转与昼夜交替",
                    GradeTarget = "初中一年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "下列地理现象中，主要是由于地球自转引起的是：",
                    OptionsJson = "[\"A. 昼夜交替与日月星辰东升西落\",\"B. 春夏秋冬四季的更替\",\"C. 五带的划分\",\"D. 极昼与极夜现象\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "地球绕地轴自西向东自转，产生了昼夜交替、时间差异和天体东升西落现象；而四季更替、五带划分与极昼极夜现象是由地球公转引起的。故选 A。",
                    Difficulty = 1,
                    BaseExpReward = 10,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },

                // ==================== 小学与高中学段补充 ====================
                new Question
                {
                    Id = Guid.Parse("b9010000-0000-0000-0000-000000000001"),
                    Subject = "数学",
                    Category = "小学分数乘除运算",
                    GradeTarget = "小学六年级",
                    Type = QuestionType.FillInBlank,
                    Stem = "计算：$\\frac{3}{4} \\times \\frac{8}{9} = $______。（填最简分数）",
                    OptionsJson = "[]",
                    CorrectAnswer = "2/3",
                    StandardAnalysis = "分数乘法交叉约分：$\\frac{3 \\times 8}{4 \\times 9} = \\frac{1 \\times 2}{1 \\times 3} = \\frac{2}{3}$。故填 2/3。",
                    Difficulty = 1,
                    BaseExpReward = 10,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                },
                new Question
                {
                    Id = Guid.Parse("b9020000-0000-0000-0000-000000000002"),
                    Subject = "数学",
                    Category = "高中集合交并补与区间表示",
                    GradeTarget = "高中一年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "设集合 $A = \\{x \\mid -1 < x < 3\\}$，$B = \\{x \\mid 0 \\le x \\le 4\\}$，则交集 $A \\cap B$ 为：",
                    OptionsJson = "[\"A. [0, 3)\",\"B. (-1, 4]\",\"C. (0, 3]\",\"D. [-1, 3)\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "求交集即取同时属于集合 A 与集合 B 的实数：$-1 < x < 3$ 且 $0 \\le x \\le 4$，即 $0 \\le x < 3$。写成区间形式为 $[0, 3)$。故选 A。",
                    Difficulty = 2,
                    BaseExpReward = 15,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = teacherUser?.Id
                }
            };

            bool hasNewQuestion = false;
            foreach (var sq in sampleQuestions)
            {
                if (!context.Questions.Any(q => q.Id == sq.Id))
                {
                    context.Questions.Add(sq);
                    hasNewQuestion = true;
                }
            }
            if (hasNewQuestion)
            {
                context.SaveChanges();
            }

            // 为学员李小明预置待净化错题（保持充足的错题本数据）
            if (studentUser != null && context.ErrorItems.Count(e => e.UserId == studentUser.Id) < 3)
            {
                var q1 = context.Questions.FirstOrDefault(q => q.Subject == "数学");
                var q2 = context.Questions.FirstOrDefault(q => q.Subject == "物理");
                var q3 = context.Questions.FirstOrDefault(q => q.Subject == "化学");
                if (q1 != null && !context.ErrorItems.Any(e => e.UserId == studentUser.Id && e.QuestionId == q1.Id))
                {
                    context.ErrorItems.Add(new ErrorItem
                    {
                        Id = Guid.NewGuid(),
                        UserId = studentUser.Id,
                        QuestionId = q1.Id,
                        UserWrongAnswer = "B",
                        ErrorReasonCategory = "概念模糊",
                        RevisionCount = 0,
                        IsMastered = false,
                        CreatedAt = DateTime.Now.AddDays(-2),
                        AiCustomAdvice = "勾股定理牢记斜边平方等于直角边平方和，不要直接相加直角边！"
                    });
                }
                if (q2 != null && !context.ErrorItems.Any(e => e.UserId == studentUser.Id && e.QuestionId == q2.Id))
                {
                    context.ErrorItems.Add(new ErrorItem
                    {
                        Id = Guid.NewGuid(),
                        UserId = studentUser.Id,
                        QuestionId = q2.Id,
                        UserWrongAnswer = "A",
                        ErrorReasonCategory = "审题粗心",
                        RevisionCount = 1,
                        IsMastered = false,
                        CreatedAt = DateTime.Now.AddDays(-1),
                        AiCustomAdvice = "此题为多项选择题，阿基米德原理与浮沉条件需同时关注！"
                    });
                }
                if (q3 != null && !context.ErrorItems.Any(e => e.UserId == studentUser.Id && e.QuestionId == q3.Id))
                {
                    context.ErrorItems.Add(new ErrorItem
                    {
                        Id = Guid.NewGuid(),
                        UserId = studentUser.Id,
                        QuestionId = q3.Id,
                        UserWrongAnswer = "C",
                        ErrorReasonCategory = "计算失误",
                        RevisionCount = 0,
                        IsMastered = false,
                        CreatedAt = DateTime.Now.AddDays(-3),
                        AiCustomAdvice = "注意质量守恒定律：反应前各反应物质量和严格等于生成物质量和！"
                    });
                }
                context.SaveChanges();
            }

            // 检查成就数据并补全缺失的成就
            var allDefaultAchievements = new List<Achievement>
            {
                new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "FIRST_BLOOD",
                    Title = "首刷突破",
                    Description = "完成第一次 AI 刷题提交",
                    Icon = "EmojiEvents",
                    RewardExp = 50,
                    RewardCoins = 20
                },
                new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "COMBO_5",
                    Title = "五连绝世",
                    Description = "在单次刷题中达成 5 连击 Combo",
                    Icon = "Whatshot",
                    RewardExp = 100,
                    RewardCoins = 50
                },
                new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "COMBO_10",
                    Title = "十连超神",
                    Description = "在单次刷题中达成 10 连击 Combo 巅峰",
                    Icon = "LocalFireDepartment",
                    RewardExp = 250,
                    RewardCoins = 120
                },
                new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "ERROR_KILLER_5",
                    Title = "错题克星",
                    Description = "成功净化消灭 5 道错题",
                    Icon = "AutoFixHigh",
                    RewardExp = 150,
                    RewardCoins = 80
                },
                new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "AI_EXPLORER",
                    Title = "求知若渴",
                    Description = "累计向 AI 导师发起 5 次深度解析或对话",
                    Icon = "Psychology",
                    RewardExp = 80,
                    RewardCoins = 30
                },
                new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "STREAK_7",
                    Title = "持之以恒",
                    Description = "累计连续打卡学习达到 7 天",
                    Icon = "CalendarToday",
                    RewardExp = 200,
                    RewardCoins = 100
                },
                new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "SCHOLAR_50",
                    Title = "百炼成钢",
                    Description = "累计完成 50 道题目练习挑战",
                    Icon = "School",
                    RewardExp = 300,
                    RewardCoins = 150
                },
                new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "FAVORITE_MASTER",
                    Title = "博闻强记",
                    Description = "累计收藏标记 3 道经典难题",
                    Icon = "Star",
                    RewardExp = 60,
                    RewardCoins = 25
                }
            };

            foreach (var ach in allDefaultAchievements)
            {
                if (!context.Achievements.Any(a => a.Code == ach.Code))
                {
                    context.Achievements.Add(ach);
                }
            }
            context.SaveChanges();
        }

        private static void SafeExecuteSql(AppDbContext context, string sql)
        {
            try
            {
                var conn = context.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    conn.Open();
                }
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }
}
