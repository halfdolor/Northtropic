using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxSymbiosisElevationTests
    {
        [Fact]
        public async Task UserSessionService_GenerateAndConsumeDownloadTicket_EnforcesSingleUseAndExpiry()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                using var httpClient = new HttpClient();
                var session = new UserSessionService(context, httpClient);
                var userId = Guid.NewGuid();

                // 1. 生成工单
                string ticket = await session.GenerateDownloadTicketAsync(userId, "backup_download", "Northtropic_Auto_20260911.bak");
                Assert.False(string.IsNullOrWhiteSpace(ticket));
                Assert.True(ticket.Length >= 32);

                // 2. 首次消费必须成功
                var (valid, resUserId, purpose, resource) = await session.ValidateAndConsumeDownloadTicketAsync(ticket);
                Assert.True(valid);
                Assert.Equal(userId, resUserId);
                Assert.Equal("backup_download", purpose);
                Assert.Equal("Northtropic_Auto_20260911.bak", resource);

                // 3. 重放攻击消费必须直接判定无效 (Single-Use Defense)
                var replayResult = await session.ValidateAndConsumeDownloadTicketAsync(ticket);
                Assert.False(replayResult.Valid);
                Assert.Equal(Guid.Empty, replayResult.UserId);

                // 4. 虚假票据判定无效
                var fakeResult = await session.ValidateAndConsumeDownloadTicketAsync("non_existent_ticket_token");
                Assert.False(fakeResult.Valid);
            }
        }

        [Fact]
        public void PrerequisiteKnowledgeGraph_NormalizeSubject_StripsGradePrefixes()
        {
            Assert.Equal("物理", PrerequisiteKnowledgeGraph.NormalizeSubject("初中物理"));
            Assert.Equal("数学", PrerequisiteKnowledgeGraph.NormalizeSubject("高中数学"));
            Assert.Equal("化学", PrerequisiteKnowledgeGraph.NormalizeSubject("小学化学"));
            Assert.Equal("生物", PrerequisiteKnowledgeGraph.NormalizeSubject("大学生物"));
            Assert.Equal("地理", PrerequisiteKnowledgeGraph.NormalizeSubject("地理"));
            Assert.Equal(string.Empty, PrerequisiteKnowledgeGraph.NormalizeSubject("   "));
        }

        [Fact]
        public void PrerequisiteKnowledgeGraph_MatchSubject_HandlesCrossStageSemantics()
        {
            Assert.True(PrerequisiteKnowledgeGraph.MatchSubject("物理", "初中物理"));
            Assert.True(PrerequisiteKnowledgeGraph.MatchSubject("初中物理", "高中物理"));
            Assert.True(PrerequisiteKnowledgeGraph.MatchSubject("数学", "初中数学（人教版）"));
            Assert.False(PrerequisiteKnowledgeGraph.MatchSubject("语文", "英语"));
        }

        [Fact]
        public void PrerequisiteKnowledgeGraph_MatchCategory_SupportsFuzzyContainment()
        {
            Assert.True(PrerequisiteKnowledgeGraph.MatchCategory("力学", "力学与牛顿定律"));
            Assert.True(PrerequisiteKnowledgeGraph.MatchCategory("牛顿运动定律综合应用", "牛顿定律"));
            Assert.False(PrerequisiteKnowledgeGraph.MatchCategory("电磁感应", "光学基础"));
        }

        [Fact]
        public void PrerequisiteKnowledgeGraph_GetPrerequisites_ResolvesPrefixTolerance()
        {
            // 在预设图谱中，“初中物理”应当能匹配到“物理”相关的“牛顿第二定律”或“浮力与阿基米德原理”
            var prereqsExact = PrerequisiteKnowledgeGraph.GetPrerequisites("物理", "浮力与阿基米德原理");
            var prereqsPrefixed = PrerequisiteKnowledgeGraph.GetPrerequisites("初中物理", "浮力与阿基米德原理");

            Assert.NotEmpty(prereqsExact);
            Assert.NotEmpty(prereqsPrefixed);
            Assert.Equal(prereqsExact.Count, prereqsPrefixed.Count);
        }

        [Fact]
        public async Task QuestionManagementService_ExportQuestions_SupportsSpecificQuestionSelection()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var service = new QuestionManagementService(context);
                var teacherId = Guid.NewGuid();
                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = teacherId,
                    Subject = "数学",
                    Category = "函数极值",
                    Stem = "求函数 f(x) = x^2 - 4x + 3 的极小值",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = JsonSerializer.Serialize(new List<string> { "A. -1", "B. 0", "C. 1", "D. 2" }),
                    CorrectAnswer = "A",
                    StandardAnalysis = "二次函数顶点坐标求解",
                    Difficulty = 3,
                    IsPublic = false,
                    CreatedAt = DateTime.UtcNow
                };
                var q2 = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = teacherId,
                    Subject = "数学",
                    Category = "平面几何",
                    Stem = "三角形内角和为多少度？",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = JsonSerializer.Serialize(new List<string> { "A. 90", "B. 180", "C. 270", "D. 360" }),
                    CorrectAnswer = "B",
                    StandardAnalysis = "平行线性质",
                    Difficulty = 1,
                    IsPublic = true,
                    CreatedAt = DateTime.UtcNow
                };
                var q3 = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = teacherId,
                    Subject = "物理",
                    Category = "牛顿力学",
                    Stem = "F = ma 是哪个定律？",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = JsonSerializer.Serialize(new List<string> { "A. 第一定律", "B. 第二定律", "C. 第三定律", "D. 万有引力" }),
                    CorrectAnswer = "B",
                    StandardAnalysis = "牛顿第二定律基本定义",
                    Difficulty = 2,
                    IsPublic = true,
                    CreatedAt = DateTime.UtcNow
                };

                context.Questions.AddRange(q1, q2, q3);
                await context.SaveChangesAsync();

                // 1. 测试指定只导出 q1 和 q3 (过滤掉 q2)
                var selectedIds = new List<Guid> { q1.Id, q3.Id };

                // JSON 导出
                string json = await service.ExportQuestionsJsonAsync(teacherId, specificQuestionIds: selectedIds);
                Assert.Contains("函数极值", json);
                Assert.Contains("牛顿力学", json);
                Assert.DoesNotContain("平面几何", json);

                // CSV 导出
                byte[] csvBytes = await service.ExportQuestionsCsvAsync(teacherId, specificQuestionIds: selectedIds);
                string csvText = Encoding.UTF8.GetString(csvBytes);
                Assert.Contains("函数极值", csvText);
                Assert.Contains("牛顿力学", csvText);
                Assert.DoesNotContain("平面几何", csvText);

                // XLSX 导出
                byte[] xlsxBytes = await service.ExportQuestionsXlsxAsync(teacherId, specificQuestionIds: selectedIds);
                Assert.NotNull(xlsxBytes);
                Assert.True(xlsxBytes.Length > 0);
                // OpenXML zip header PK (0x50, 0x4B)
                Assert.Equal(0x50, xlsxBytes[0]);
                Assert.Equal(0x4B, xlsxBytes[1]);
            }
        }

        [Fact]
        public async Task QuestionManagementService_ExportQuestions_EnforcesPrivacyIsolation()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var service = new QuestionManagementService(context);
                var teacherA = Guid.NewGuid();
                var teacherB = Guid.NewGuid();

                var privateQuestionOfB = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = teacherB,
                    Subject = "化学",
                    Category = "有机反应",
                    Stem = "B老师的私密独家密卷试题",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "保密",
                    Difficulty = 5,
                    IsPublic = false,
                    CreatedAt = DateTime.UtcNow
                };

                context.Questions.Add(privateQuestionOfB);
                await context.SaveChangesAsync();

                // 老师 A 尝试导出该特定 ID (非公开且不属于 A)
                string json = await service.ExportQuestionsJsonAsync(teacherA, specificQuestionIds: new[] { privateQuestionOfB.Id });
                var list = JsonSerializer.Deserialize<List<object>>(json);
                Assert.Empty(list!);
            }
        }

        [Fact]
        public async Task PracticeService_SubmitBatchPaper_TracksIndividualElapsedDwellTime()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var fakeGamification = new FakeGamificationService();
                var fakeSession = new FakeUserSessionService();
                var fakeAiTutor = new FakeAiTutorService();
                var practiceService = new PracticeService(context, fakeGamification, fakeSession, fakeAiTutor);

                var studentId = Guid.NewGuid();
                var student = new User { Id = studentId, Username = "TestStudent", Exp = 0, Coins = 100 };
                context.Users.Add(student);

                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "数学",
                    Category = "代数",
                    Stem = "1 + 1 = ?",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = JsonSerializer.Serialize(new List<string> { "A. 1", "B. 2" }),
                    CorrectAnswer = "B",
                    Difficulty = 1,
                    BaseExpReward = 10
                };
                var q2 = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "数学",
                    Category = "代数",
                    Stem = "2 * 3 = ?",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = JsonSerializer.Serialize(new List<string> { "A. 5", "B. 6" }),
                    CorrectAnswer = "B",
                    Difficulty = 1,
                    BaseExpReward = 10
                };
                context.Questions.AddRange(q1, q2);
                await context.SaveChangesAsync();

                // 模拟学生作答：第1题停留 45 秒，第2题停留 8 秒
                var submissions = new List<(Question Question, string UserAnswer, int TimeTakenSeconds)>
                {
                    (q1, "B", 45),
                    (q2, "B", 8)
                };

                var batchResult = await practiceService.SubmitBatchPaperAsync(submissions, 0, studentId);
                Assert.Equal(2, batchResult.CorrectCount);

                // 校验 PracticeRecord 中记录的真实作答用时是否精准入库
                var records = await context.PracticeRecords.Where(r => r.UserId == studentId).OrderBy(r => r.AnsweredAt).ToListAsync();
                Assert.Equal(2, records.Count);
                Assert.Contains(records, r => r.QuestionId == q1.Id && r.TimeTakenSeconds == 45);
                Assert.Contains(records, r => r.QuestionId == q2.Id && r.TimeTakenSeconds == 8);
            }
        }
    }
}
