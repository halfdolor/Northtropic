using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxNextLevelEvolutionTests
    {
        [Fact]
        public void CheckFillInBlankMatch_PurePowerOfTenScientificNotation_EvaluatesAndMatches()
        {
            // 1. 纯 10 次幂科学记数法（如大气压强 10^5 Pa、微观量级 10^-3、10^{5} 等）
            Assert.True(PracticeService.CheckFillInBlankMatch("10^5", "100000"));
            Assert.True(PracticeService.CheckFillInBlankMatch("100000", "10^5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10^5", "1*10^5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10^5", "1.0*10^5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10^{5}", "100000"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"10^{5}", "100000"));

            // 负指数与小数
            Assert.True(PracticeService.CheckFillInBlankMatch("10^-3", "0.001"));
            Assert.True(PracticeService.CheckFillInBlankMatch("0.001", "10^-3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10^{-3}", "0.001"));

            // 负符号基数与单位协同
            Assert.True(PracticeService.CheckFillInBlankMatch("-10^2", "-100"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10^5 Pa", "100000 帕"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10^5 Pa", "100000"));
        }

        [Fact]
        public void CheckFillInBlankMatch_ChineseOrdinalsAndSmallNumberCounts_NormalizedAndEquated()
        {
            // 2. 中文序数词（第一至第十）
            Assert.True(PracticeService.CheckFillInBlankMatch("第二周期", "第2周期"));
            Assert.True(PracticeService.CheckFillInBlankMatch("第2周期", "第二周期"));
            Assert.True(PracticeService.CheckFillInBlankMatch("第一宇宙速度", "第1宇宙速度"));
            Assert.True(PracticeService.CheckFillInBlankMatch("第三主族", "第3主族"));

            // 常用数量量词归一
            Assert.True(PracticeService.CheckFillInBlankMatch("两种", "2种"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2种", "两种"));
            Assert.True(PracticeService.CheckFillInBlankMatch("三个", "3个"));
            Assert.True(PracticeService.CheckFillInBlankMatch("一对", "1对"));
            Assert.True(PracticeService.CheckFillInBlankMatch("四条", "4条"));
            Assert.True(PracticeService.CheckFillInBlankMatch("4倍", "四倍"));

            // 单个中文数字
            Assert.True(PracticeService.CheckFillInBlankMatch("两", "2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("二", "2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("三", "3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("一", "1"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2", "两"));

            // 量词与纯数字容错（StripCommonUnits 剥离分类词）
            Assert.True(PracticeService.CheckFillInBlankMatch("2种", "2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("3个", "3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("两种", "2"));
        }

        [Fact]
        public async Task StudentEvolutionService_HyphenatedCategoryName_TracesPrerequisitesWithoutDropping()
        {
            // 3. 架构缺陷验证：包含多个连字符的复合考点（如 初中数学-一元二次方程-根的判别式）
            var (db, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (db)
            {
                var user = new User { Id = Guid.NewGuid(), Username = "hyphen_student", Role = UserRole.Student };
                db.Users.Add(user);

                // 前置知识点：一元一次方程
                var prereqQ = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "初中数学",
                    Category = "一元一次方程",
                    Stem = "解方程 2x = 4",
                    Type = QuestionType.SingleChoice,
                    CorrectAnswer = "A"
                };
                // 目标考点：带连字符的一元二次方程-根的判别式
                var targetQ = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "初中数学",
                    Category = "一元二次方程-根的判别式",
                    Stem = "判别式 Delta > 0",
                    Type = QuestionType.SingleChoice,
                    CorrectAnswer = "A"
                };
                db.Questions.AddRange(prereqQ, targetQ);
                await db.SaveChangesAsync();

                // 学生在目标考点答错，产生弱项记录
                db.PracticeRecords.Add(new PracticeRecord
                {
                    UserId = user.Id,
                    QuestionId = targetQ.Id,
                    IsCorrect = false,
                    TimeTakenSeconds = 15
                });
                await db.SaveChangesAsync();

                // 注册图谱依赖：初中数学 - 一元二次方程-根的判别式 依赖 初中数学 - 一元一次方程
                PrerequisiteKnowledgeGraph.RegisterDependency(
                    "初中数学", "一元二次方程-根的判别式",
                    "初中数学", "一元一次方程",
                    "一元二次方程根的性质以一元一次方程代数求解为根基"
                );

                var evolutionService = new StudentEvolutionService(db, new FakeGamificationService(), new SimpleHttpClientFactory());

                var report = await evolutionService.GenerateDiagnosisReportAsync(user.Id);

                // 验证 TopWeakCategories 包含复合考点
                Assert.Contains(report.TopWeakCategories, c => c.Contains("一元二次方程-根的判别式"));
                // 验证前置知识图谱未因连字符被误跳过，成功捕获先修考点预警
                Assert.Contains(report.PrerequisiteWarnings, w => w.PrerequisiteCategory == "一元一次方程");
            }
        }

        [Fact]
        public async Task PracticeService_GetSprintQuestionsFromErrorsAsync_ShufflesOptionsAndUsesAsNoTracking()
        {
            // 4. 认知科学与架构性能验证：错题专练时，选项被打乱且实体未被 ChangeTracker 污染
            var (db, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (db)
            {
                var user = new User { Id = Guid.NewGuid(), Username = "sprint_student", Role = UserRole.Student };
                db.Users.Add(user);

                var q = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "高中物理",
                    Category = "电磁感应",
                    Type = QuestionType.SingleChoice,
                    Stem = "关于法拉第电磁感应定律，下列说法正确的是？",
                    OptionsJson = "[\"A. 磁通量越大感应电动势越大\",\"B. 磁通量变化率越大感应电动势越大\",\"C. 磁通量为零时感应电动势必为零\",\"D. 电动势方向由欧姆定律决定\"]",
                    CorrectAnswer = "B"
                };
                db.Questions.Add(q);

                var err = new ErrorItem
                {
                    UserId = user.Id,
                    QuestionId = q.Id,
                    UserWrongAnswer = "A",
                    RevisionCount = 1,
                    IsMastered = false,
                    CreatedAt = DateTime.Now
                };
                db.ErrorItems.Add(err);
                await db.SaveChangesAsync();

                // 确保测试环境 ChangeTracker 为空
                db.ChangeTracker.Clear();

                var sessionService = new FakeUserSessionService { ActiveUser = user };
                var practiceService = new PracticeService(
                    db,
                    new FakeGamificationService(),
                    sessionService,
                    new FakeAiTutorService()
                );

                var sprintQuestions = await practiceService.GetSprintQuestionsFromErrorsAsync(user.Id, "高中物理", 1);

                Assert.Single(sprintQuestions);
                var sprintQ = sprintQuestions[0];
                Assert.Equal(q.Id, sprintQ.Id);

                // 验证 ChangeTracker 中没有追踪 Question 实体（验证 AsNoTracking 生效）
                Assert.DoesNotContain(db.ChangeTracker.Entries<Question>(), e => e.Entity.Id == q.Id);

                // 验证深拷贝选项语义依然保持一致：CorrectAnswer 对应的选项内容仍然是 'B. 磁通量变化率越大...' 的纯文本
                var optList = System.Text.Json.JsonSerializer.Deserialize<List<string>>(sprintQ.OptionsJson);
                Assert.NotNull(optList);
                var corrOpt = optList.FirstOrDefault(o => o.StartsWith(sprintQ.CorrectAnswer + "."));
                Assert.NotNull(corrOpt);
                Assert.Contains("磁通量变化率越大感应电动势越大", corrOpt);
            }
        }

        [Fact]
        public async Task GamificationService_UpdateStreakAsync_WithTargetUserId_UpdatesDesignatedUser()
        {
            // 5. 游戏化打卡系统架构灵活性验证：传入 targetUserId 正确更新目标学员 Streak
            var (db, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (db)
            {
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "streak_kid",
                    Role = UserRole.Student,
                    CurrentStreak = 2,
                    LastStudyDate = DateTime.Today.AddDays(-1)
                };
                db.Users.Add(student);
                await db.SaveChangesAsync();

                // 当前会话无活动用户（模拟后台服务或家长端代为结算场景）
                var sessionService = new FakeUserSessionService { ActiveUser = null };
                var gamificationService = new GamificationService(db, sessionService);

                await gamificationService.UpdateStreakAsync(student.Id);

                var updatedUser = await db.Users.FindAsync(student.Id);
                Assert.NotNull(updatedUser);
                Assert.Equal(3, updatedUser.CurrentStreak);
                Assert.Equal(DateTime.Today, updatedUser.LastStudyDate.Date);
            }
        }
    }
}
