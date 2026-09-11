using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }

    public class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public MockHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    public class ArchitectAndUxEnhancementTests
    {
        [Fact]
        public async Task ErrorBook_GetUnmasteredErrorsAsync_BoundParent_CanAccessChildErrors()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var parentId = Guid.NewGuid();
                var childId = Guid.NewGuid();

                var parent = new User { Id = parentId, Username = "张家长", Role = UserRole.Parent };
                var child = new User { Id = childId, Username = "张同学", Role = UserRole.Student };
                context.Users.AddRange(parent, child);

                // Add binding
                context.StudentParentBindings.Add(new StudentParentBinding
                {
                    ParentUserId = parentId,
                    StudentUserId = childId,
                    RelationType = "父亲"
                });

                var question = new Question { Stem = "光沿直线传播的条件是？", OptionsJson = "[]", CorrectAnswer = "同种均匀介质", Subject = "初中物理" };
                context.Questions.Add(question);

                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = childId,
                    Question = question,
                    UserWrongAnswer = "真空",
                    IsMastered = false,
                    ErrorReasonCategory = "概念模糊"
                };
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = parent };
                var fakeGamification = new FakeGamificationService();
                var service = new ErrorBookService(context, fakeGamification, fakeUserSession);

                // Act: Parent requests child's errors
                var result = await service.GetUnmasteredErrorsAsync(targetUserId: childId, requestorUserId: parentId);

                // Assert: Access granted
                Assert.Single(result);
                Assert.Equal(errorItem.Id, result[0].Id);

                // Verify count as well
                var count = await service.GetUnmasteredCountAsync(childId);
                Assert.Equal(1, count);
            }
        }

        [Fact]
        public async Task ErrorBook_GetUnmasteredErrorsAsync_UnauthorizedStranger_BlockedByIDORDefense()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var victimStudentId = Guid.NewGuid();
                var attackerParentId = Guid.NewGuid();

                var attacker = new User { Id = attackerParentId, Username = "陌生人", Role = UserRole.Parent };
                var victim = new User { Id = victimStudentId, Username = "受害者同学", Role = UserRole.Student };
                context.Users.AddRange(attacker, victim);

                var question = new Question { Stem = "力的作用是相互的吗？", OptionsJson = "[]", CorrectAnswer = "是" };
                context.Questions.Add(question);

                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = victimStudentId,
                    Question = question,
                    UserWrongAnswer = "否",
                    IsMastered = false
                };
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = attacker };
                var fakeGamification = new FakeGamificationService();
                var service = new ErrorBookService(context, fakeGamification, fakeUserSession);

                // Act: Attacker queries victim's error items
                var result = await service.GetUnmasteredErrorsAsync(targetUserId: victimStudentId, requestorUserId: attackerParentId);

                // Assert: Access denied, returns empty list
                Assert.Empty(result);

                var count = await service.GetUnmasteredCountAsync(victimStudentId);
                Assert.Equal(0, count);
            }
        }

        [Fact]
        public async Task QuestionImport_CsvDeduplication_And_DbDeduplication()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                // Pre-existing question in DB
                context.Questions.Add(new Question
                {
                    Stem = "已有题目：牛顿第一定律又称为什么定律？",
                    Subject = "初中物理",
                    Category = "力与运动",
                    GradeTarget = "八年级",
                    Type = QuestionType.SingleChoice,
                    CorrectAnswer = "惯性定律",
                    OptionsJson = "[\"A. 惯性定律\",\"B. 动量守恒定律\"]"
                });
                await context.SaveChangesAsync();

                var importService = new QuestionImportService(context);

                // CSV with:
                // 1. Duplicate of existing question in DB
                // 2. A valid new question
                // 3. Duplicate within the same batch (duplicate of 2)
                var csv = new StringBuilder();
                csv.AppendLine("学科,考点分类,适学年级,题型,题干,选项,正确答案,标准解析");
                csv.AppendLine("初中物理,力与运动,八年级,单选题,已有题目：牛顿第一定律又称为什么定律？,\"A. 惯性定律|B. 动量守恒\",A,解析");
                csv.AppendLine("初中物理,声现象,八年级,单选题,声音在真空中传播的速度是？,\"A. 340m/s|B. 0m/s\",B,真空中不能传声");
                csv.AppendLine("初中物理,声现象,八年级,单选题,声音在真空中传播的速度是？,\"A. 340m/s|B. 0m/s\",B,真空中不能传声");

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv.ToString()));
                var result = await importService.ParseAndImportQuestionsByFileFormatAsync(stream, "test.csv", Guid.NewGuid());

                Assert.Equal(1, result.SuccessCount);
                Assert.Equal(2, result.DuplicateCount);

                // Verify only 1 new question was added to DB (total 2)
                var totalQuestions = await context.Questions.CountAsync();
                Assert.Equal(2, totalQuestions);
            }
        }

        [Fact]
        public async Task QuestionImport_JsonValidation_MinOptionsCheck()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var importService = new QuestionImportService(context);

                // Single choice question with only 1 option (invalid!)
                var invalidQuestions = new List<object>
                {
                    new
                    {
                        stem = "单选题目但只有一个选项",
                        subject = "初中物理",
                        category = "测试",
                        type = "SingleChoice",
                        options = new List<string> { "A. 只有一项" },
                        correctAnswer = "A",
                        difficulty = 3
                    }
                };

                var jsonStr = JsonSerializer.Serialize(invalidQuestions);
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonStr));

                var result = await importService.ParseAndImportQuestionsByFileFormatAsync(stream, "test.json", Guid.NewGuid());

                Assert.Equal(0, result.SuccessCount);
                Assert.Equal(1, result.FailureCount);
                Assert.Contains("至少提供 2 个有效选项", result.ErrorMessages[0]);
            }
        }

        [Fact]
        public async Task AiTutorService_TracksLlmTokens_AndLogsAuditRecord()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var testUser = new User { Id = Guid.NewGuid(), Username = "测试学生", LlmApiKey = "sk-test123456" };
                context.Users.Add(testUser);

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "浸在液体中的物体受到的浮力大小取决于？",
                    Subject = "初中物理",
                    Category = "浮力",
                    OptionsJson = "[\"A. 液体密度与排开体积\",\"B. 物体深度\"]",
                    CorrectAnswer = "A"
                };
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var responseJson = @"{
                    ""id"": ""chatcmpl-test"",
                    ""choices"": [
                        {
                            ""message"": {
                                ""role"": ""assistant"",
                                ""content"": ""{\""summary\"": \""浮力原理\"", \""keyConcepts\"": [\""阿基米德原理\""], \""whyWrongAnalysis\"": \""混淆深度与体积\"", \""stepByStepReasoning\"": \""依据浮力定律\""}""
                            }
                        }
                    ],
                    ""usage"": {
                        ""prompt_tokens"": 42,
                        ""completion_tokens"": 88,
                        ""total_tokens"": 130
                    }
                }";

                var mockHandler = new MockHttpMessageHandler(request =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                    };
                    return Task.FromResult(response);
                });

                var httpClient = new HttpClient(mockHandler);
                var factory = new MockHttpClientFactory(httpClient);
                var fakeGamification = new FakeGamificationServiceWrapper(testUser);

                var aiService = new AiTutorService(fakeGamification, factory, context);

                var explanation = await aiService.GetExplanationAsync(question, "B");

                Assert.NotNull(explanation);

                // Verify audit log in DbContext
                var logs = await context.LlmGenerationLogs.ToListAsync();
                Assert.Single(logs);
                var log = logs[0];
                Assert.Equal(42, log.PromptTokens);
                Assert.Equal(88, log.CompletionTokens);
                Assert.Equal(130, log.TotalTokens);
                Assert.Equal(question.Category, log.Category);
                Assert.Equal("初中物理", log.Subject);
                Assert.Equal(question.Id, log.QuestionId);
            }
        }

        [Fact]
        public async Task StudentEvolutionService_AuditsDiagnosisTokens_InLlmGenerationLog()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var studentId = Guid.NewGuid();
                var student = new User { Id = studentId, Username = "测试学员", Role = UserRole.Student, Grade = "初中二年级", LlmApiKey = "sk-test123456" };
                context.Users.Add(student);

                // Add some practice records
                var q1 = new Question { Id = Guid.NewGuid(), Stem = "浮力题1", Subject = "初中物理", Category = "浮力", OptionsJson = "[]", CorrectAnswer = "A" };
                context.Questions.Add(q1);
                context.PracticeRecords.Add(new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = studentId,
                    QuestionId = q1.Id,
                    IsCorrect = true,
                    AnsweredAt = DateTime.UtcNow
                });
                await context.SaveChangesAsync();

                var responseJson = @"{
                    ""choices"": [
                        {
                            ""message"": {
                                ""role"": ""assistant"",
                                ""content"": ""该学员在浮力考点表现优秀，建议多攻坚压轴大题。""
                            }
                        }
                    ],
                    ""usage"": {
                        ""prompt_tokens"": 55,
                        ""completion_tokens"": 65,
                        ""total_tokens"": 120
                    }
                }";

                var mockHandler = new MockHttpMessageHandler(request =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                    };
                    return Task.FromResult(response);
                });

                var httpClient = new HttpClient(mockHandler);
                var factory = new MockHttpClientFactory(httpClient);
                var fakeGamification = new FakeGamificationServiceWrapper(student);

                var evolutionService = new StudentEvolutionService(context, fakeGamification, factory);

                var report = await evolutionService.GenerateDiagnosisReportAsync(studentId);

                Assert.NotNull(report);
                Assert.Contains("该学员在浮力考点表现优秀", report.AiGrowthAdvice);
                Assert.True(report.StarRating > 0);

                // Verify LLM token tracking log
                var logs = await context.LlmGenerationLogs.Where(l => l.Subject == "AI学情诊断").ToListAsync();
                Assert.Single(logs);
                var log = logs[0];
                Assert.Equal(55, log.PromptTokens);
                Assert.Equal(65, log.CompletionTokens);
                Assert.Equal(120, log.TotalTokens);
                Assert.Equal("认知报告评语", log.Category);
            }
        }
    }

    public class FakeGamificationServiceWrapper : FakeGamificationService
    {
        public FakeGamificationServiceWrapper(User user)
        {
            CurrentUser = user;
        }

        public override Task<User> GetCurrentUserAsync() => Task.FromResult(CurrentUser);
    }
}
