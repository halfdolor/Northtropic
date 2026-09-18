using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Helpers;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxSovereignEvolutionTests
    {
        public class SqliteDbContextFactory : IDbContextFactory<AppDbContext>
        {
            private readonly DbContextOptions<AppDbContext> _options;
            public SqliteDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
            public AppDbContext CreateDbContext() => new AppDbContext(_options);
        }

        private static (SqliteConnection Connection, DbContextOptions<AppDbContext> Options) CreateSharedInMemoryDb()
        {
            var dbName = $"memdb_{Guid.NewGuid():N}";
            var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
            var connection = new SqliteConnection(connectionString);
            connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;

            using (var ctx = new AppDbContext(options))
            {
                ctx.Database.EnsureCreated();
            }

            return (connection, options);
        }

        #region 1. 系统架构师：用户注销级联试题治理与防外键冲突测试

        [Fact]
        public async Task DeleteUserAsync_WithPrivateAndPublicQuestions_CleansPrivateAndDisassociatesPublic()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);

            var superAdminId = Guid.NewGuid();
            var teacherId = Guid.NewGuid();
            var studentId = Guid.NewGuid();

            var superAdmin = new User
            {
                Id = superAdminId,
                Username = "SuperAdminUser",
                Password = PasswordHasher.HashPassword("admin123"),
                Role = UserRole.SuperAdmin,
                Grade = "高中三年级"
            };

            var teacher = new User
            {
                Id = teacherId,
                Username = "TeacherAuthor",
                Password = PasswordHasher.HashPassword("teacher123"),
                Role = UserRole.Teacher,
                Grade = "初中三年级"
            };

            var student = new User
            {
                Id = studentId,
                Username = "StudentLearner",
                Password = PasswordHasher.HashPassword("student123"),
                Role = UserRole.Student,
                Grade = "初中三年级"
            };

            // 教师创建一道公开题与一道私有题
            var publicQuestionId = Guid.NewGuid();
            var publicQuestion = new Question
            {
                Id = publicQuestionId,
                Subject = "数学",
                Category = "一元二次方程",
                Stem = "求方程 x^2 - 4 = 0 的解",
                CorrectAnswer = "x = ±2",
                IsPublic = true,
                CreatedByUserId = teacherId,
                Type = QuestionType.SingleChoice,
                OptionsJson = "[\"A. x=2\", \"B. x=±2\", \"C. x=-2\", \"D. 无解\"]"
            };

            var privateQuestionId = Guid.NewGuid();
            var privateQuestion = new Question
            {
                Id = privateQuestionId,
                Subject = "物理",
                Category = "欧姆定律",
                Stem = "电路中电阻与电流的关系实验探究",
                CorrectAnswer = "反比",
                IsPublic = false,
                CreatedByUserId = teacherId,
                Type = QuestionType.ShortAnswer
            };

            // 学生在此两道题上产生了答题记录和错题
            var practiceRecord = new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = studentId,
                QuestionId = privateQuestionId,
                UserAnswer = "正比",
                IsCorrect = false,
                AnsweredAt = DateTime.UtcNow
            };

            var errorItem = new ErrorItem
            {
                Id = Guid.NewGuid(),
                UserId = studentId,
                QuestionId = privateQuestionId,
                UserWrongAnswer = "正比",
                ErrorReasonCategory = "概念模糊",
                CreatedAt = DateTime.UtcNow
            };

            var favorite = new UserFavorite
            {
                Id = Guid.NewGuid(),
                UserId = studentId,
                QuestionId = privateQuestionId,
                CreatedAt = DateTime.UtcNow
            };

            var llmLog = new LlmGenerationLog
            {
                Id = Guid.NewGuid(),
                UserId = studentId,
                QuestionId = privateQuestionId,
                ModelName = "gpt-4",
                Subject = "物理",
                Category = "欧姆定律",
                PromptTokens = 50,
                CompletionTokens = 50,
                TotalTokens = 100,
                GeneratedAt = DateTime.UtcNow
            };

            using (var initCtx = factory.CreateDbContext())
            {
                initCtx.Users.AddRange(superAdmin, teacher, student);
                initCtx.Questions.AddRange(publicQuestion, privateQuestion);
                initCtx.PracticeRecords.Add(practiceRecord);
                initCtx.ErrorItems.Add(errorItem);
                initCtx.UserFavorites.Add(favorite);
                initCtx.LlmGenerationLogs.Add(llmLog);
                await initCtx.SaveChangesAsync();
            }

            using var httpClient = new HttpClient();
            using var sessionCtx = factory.CreateDbContext();
            var sessionService = new UserSessionService(sessionCtx, httpClient, factory);

            // 超级管理员先登录
            var loginRes = await sessionService.LoginWithPasswordAsync("SuperAdminUser", "admin123");
            Assert.True(loginRes.Success);

            // 超级管理员删除教师账号
            var deleteResult = await sessionService.DeleteUserAsync(teacherId);
            Assert.True(deleteResult);

            // 验证数据库状态
            using (var verifyCtx = factory.CreateDbContext())
            {
                // 1. 教师已被删除
                var deletedTeacher = await verifyCtx.Users.FindAsync(teacherId);
                Assert.Null(deletedTeacher);

                // 2. 公开试题仍然存在，但其创作者已被脱敏解除 (CreatedByUserId == null)
                var keptPublicQuestion = await verifyCtx.Questions.FindAsync(publicQuestionId);
                Assert.NotNull(keptPublicQuestion);
                Assert.True(keptPublicQuestion.IsPublic);
                Assert.Null(keptPublicQuestion.CreatedByUserId);

                // 3. 私有试题及其级联从属数据已彻底清理
                var deletedPrivateQuestion = await verifyCtx.Questions.FindAsync(privateQuestionId);
                Assert.Null(deletedPrivateQuestion);

                var relRecords = await verifyCtx.PracticeRecords.Where(r => r.QuestionId == privateQuestionId).ToListAsync();
                Assert.Empty(relRecords);

                var relErrors = await verifyCtx.ErrorItems.Where(e => e.QuestionId == privateQuestionId).ToListAsync();
                Assert.Empty(relErrors);

                var relFavs = await verifyCtx.UserFavorites.Where(f => f.QuestionId == privateQuestionId).ToListAsync();
                Assert.Empty(relFavs);

                // 4. LlmGenerationLog 的 QuestionId 已解除引用
                var updatedLlmLog = await verifyCtx.LlmGenerationLogs.FindAsync(llmLog.Id);
                Assert.NotNull(updatedLlmLog);
                Assert.Null(updatedLlmLog.QuestionId);
            }
        }

        [Fact]
        public async Task DeleteUserAsync_NonSuperAdmin_ForbiddenAndPreservesData()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);

            var studentId1 = Guid.NewGuid();
            var studentId2 = Guid.NewGuid();

            var student1 = new User { Id = studentId1, Username = "Student1", Password = PasswordHasher.HashPassword("123456"), Role = UserRole.Student, Grade = "初中一年级" };
            var student2 = new User { Id = studentId2, Username = "Student2", Password = PasswordHasher.HashPassword("123456"), Role = UserRole.Student, Grade = "初中一年级" };

            using (var initCtx = factory.CreateDbContext())
            {
                initCtx.Users.AddRange(student1, student2);
                await initCtx.SaveChangesAsync();
            }

            using var httpClient = new HttpClient();
            using var sessionCtx = factory.CreateDbContext();
            var sessionService = new UserSessionService(sessionCtx, httpClient, factory);

            // 学生 1 登录
            var loginRes = await sessionService.LoginWithPasswordAsync("Student1", "123456");
            Assert.True(loginRes.Success);

            // 学生 1 尝试越权删除学生 2
            var deleteResult = await sessionService.DeleteUserAsync(studentId2);
            Assert.False(deleteResult);

            using (var verifyCtx = factory.CreateDbContext())
            {
                var student2StillExists = await verifyCtx.Users.FindAsync(studentId2);
                Assert.NotNull(student2StillExists);
            }
        }

        #endregion

        #region 2. 系统架构师：分类考点缓存遥测与并发失效测试

        [Fact]
        public async Task PracticeService_CategoryCache_HitMissRatioAndInvalidation_CalculatesAccurately()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);

            using (var initCtx = factory.CreateDbContext())
            {
                initCtx.Questions.Add(new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "数学",
                    Category = "一元一次不等式",
                    Stem = "测试题干",
                    CorrectAnswer = "A",
                    IsPublic = true
                });
                await initCtx.SaveChangesAsync();
            }

            PracticeService.ResetCategoryCacheTelemetry();
            PracticeService.InvalidateCategoryCache();

            using var practiceCtx = factory.CreateDbContext();
            var practiceService = new PracticeService(
                practiceCtx,
                new FakeGamificationService(),
                new FakeUserSessionService(),
                new FakeAiTutorService(),
                factory);

            // 1. 初次查询，强制刷新或初次加载，应触发 Miss
            var cats1 = await practiceService.GetCategoriesAsync(forceRefresh: true);
            Assert.NotEmpty(cats1);
            Assert.Contains("一元一次不等式", cats1);
            Assert.True(PracticeService.CategoryCacheMissCount >= 1);

            // 2. 二次非强制查询，应命中内存快照 Cache Hit
            var cats2 = await practiceService.GetCategoriesAsync(forceRefresh: false);
            Assert.NotEmpty(cats2);
            Assert.True(PracticeService.CategoryCacheHitCount >= 1);

            // 3. 命中率计算应大于 0% 且不超过 100%
            double ratio = PracticeService.CategoryCacheHitRatio;
            Assert.InRange(ratio, 1.0, 100.0);

            // 4. 主动失效后再次读取，应再次触发 Miss
            long missBefore = PracticeService.CategoryCacheMissCount;
            PracticeService.InvalidateCategoryCache();
            await practiceService.GetCategoriesAsync(forceRefresh: false);
            Assert.True(PracticeService.CategoryCacheMissCount > missBefore);
        }

        #endregion

        #region 3. 系统架构师：系统健康遥测与自适应自愈维护计划评估测试

        [Fact]
        public async Task SystemHealthService_AdaptiveMaintenance_EvaluatesHealthyAndRecordsEvents()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);

            using var healthCtx = factory.CreateDbContext();
            var healthService = new SystemHealthService(healthCtx, factory);

            // 1. 记录架构事件遥测
            healthService.RecordArchitectureEvent("SelfHealing", "Success", "自愈测试事件记录成功", 15.5);
            var recent = healthService.GetRecentArchitectureEvents(category: "SelfHealing");
            Assert.NotEmpty(recent);
            Assert.Contains(recent, e => e.Message.Contains("自愈测试事件记录成功"));

            // 2. 评估自适应维护计划
            var plan = await healthService.EvaluateAdaptiveMaintenancePlanAsync();
            Assert.NotNull(plan);
            Assert.NotNull(plan.UrgencyLevel);
            Assert.NotNull(plan.ActionReasons);

            // 3. 系统健康 DTO 获取
            var dto = await healthService.GetSystemHealthAsync();
            Assert.NotNull(dto);
            Assert.True(dto.HealthScore >= 0 && dto.HealthScore <= 100);
            Assert.NotNull(dto.HealthRating);
        }

        #endregion

        #region 4. 用户体验与数据一致性：单题级联删除完整性

        [Fact]
        public async Task QuestionManagementService_DeleteQuestion_CascadeCleanup_PreservesDatabaseIntegrity()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);

            var superAdminId = Guid.NewGuid();
            var studentId = Guid.NewGuid();
            var qId = Guid.NewGuid();

            var admin = new User { Id = superAdminId, Username = "SuperAdminQ", Role = UserRole.SuperAdmin };
            var student = new User { Id = studentId, Username = "StudentQ", Role = UserRole.Student };

            var question = new Question
            {
                Id = qId,
                Subject = "化学",
                Category = "质量守恒定律",
                Stem = "化学反应前后，各物质的质量总和是否改变？",
                CorrectAnswer = "不变",
                IsPublic = true,
                Type = QuestionType.FillInBlank
            };

            var error = new ErrorItem
            {
                Id = Guid.NewGuid(),
                UserId = studentId,
                QuestionId = qId,
                UserWrongAnswer = "改变",
                ErrorReasonCategory = "概念模糊",
                CreatedAt = DateTime.UtcNow
            };

            var fav = new UserFavorite
            {
                Id = Guid.NewGuid(),
                UserId = studentId,
                QuestionId = qId,
                CreatedAt = DateTime.UtcNow
            };

            var record = new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = studentId,
                QuestionId = qId,
                UserAnswer = "改变",
                IsCorrect = false,
                AnsweredAt = DateTime.UtcNow
            };

            var log = new LlmGenerationLog
            {
                Id = Guid.NewGuid(),
                UserId = superAdminId,
                QuestionId = qId,
                ModelName = "qwen-max",
                Subject = "化学",
                Category = "质量守恒定律",
                PromptTokens = 30,
                CompletionTokens = 30,
                TotalTokens = 60,
                GeneratedAt = DateTime.UtcNow
            };

            using (var initCtx = factory.CreateDbContext())
            {
                initCtx.Users.AddRange(admin, student);
                initCtx.Questions.Add(question);
                initCtx.ErrorItems.Add(error);
                initCtx.UserFavorites.Add(fav);
                initCtx.PracticeRecords.Add(record);
                initCtx.LlmGenerationLogs.Add(log);
                await initCtx.SaveChangesAsync();
            }

            using var qmCtx = factory.CreateDbContext();
            var qmService = new QuestionManagementService(qmCtx, factory);

            // 超级管理员删除该试题
            var deleted = await qmService.DeleteQuestionAsync(qId, superAdminId);
            Assert.True(deleted);

            using (var verifyCtx = factory.CreateDbContext())
            {
                // 试题已不存在
                Assert.Null(await verifyCtx.Questions.FindAsync(qId));
                // 关联收藏、错题、练习记录已被级联清理
                Assert.Empty(await verifyCtx.ErrorItems.Where(e => e.QuestionId == qId).ToListAsync());
                Assert.Empty(await verifyCtx.UserFavorites.Where(f => f.QuestionId == qId).ToListAsync());
                Assert.Empty(await verifyCtx.PracticeRecords.Where(r => r.QuestionId == qId).ToListAsync());

                // LlmGenerationLog 的外键被安全置空
                var updatedLog = await verifyCtx.LlmGenerationLogs.FindAsync(log.Id);
                Assert.NotNull(updatedLog);
                Assert.Null(updatedLog.QuestionId);
            }
        }

        #endregion
    }
}
