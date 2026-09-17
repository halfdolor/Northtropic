using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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
    public class GlobalLlmInheritancePlanATests
    {
        private class TestHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

            public TestHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_handler(request));
            }
        }

        private class SimpleHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;
            public SimpleHttpClientFactory(HttpClient client) => _client = client;
            public HttpClient CreateClient(string name) => _client;
        }

        [Fact]
        public async Task StudentWithoutKey_ShouldInheritSuperAdminLlmAndOcrConfig()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using var _ = connection;
            using var __ = context;

            var admin = new User
            {
                Id = Guid.NewGuid(),
                Username = "全局管理员",
                Role = UserRole.SuperAdmin,
                LlmApiKey = "sk-admin-deepseek-key",
                LlmBaseUrl = "https://api.deepseek.com",
                LlmModelName = "deepseek-chat",
                BaiduApiKey = "baidu-admin-client-id",
                BaiduSecretKey = "baidu-admin-secret"
            };
            var student = new User
            {
                Id = Guid.NewGuid(),
                Username = "小明同学",
                Role = UserRole.Student,
                Grade = "初中三年级",
                LlmApiKey = "",
                BaiduApiKey = ""
            };

            context.Users.AddRange(admin, student);
            await context.SaveChangesAsync();

            var userSessionService = new UserSessionService(context, new HttpClient());
            var effectiveUser = await userSessionService.ResolveEffectiveUserLlmConfigAsync(student);

            Assert.Equal(student.Id, effectiveUser.Id);
            Assert.Equal("小明同学", effectiveUser.Username);
            Assert.Equal("初中三年级", effectiveUser.Grade);
            Assert.Equal("sk-admin-deepseek-key", effectiveUser.LlmApiKey);
            Assert.Equal("https://api.deepseek.com", effectiveUser.LlmBaseUrl);
            Assert.Equal("deepseek-chat", effectiveUser.LlmModelName);
            Assert.Equal("baidu-admin-client-id", effectiveUser.BaiduApiKey);
            Assert.Equal("baidu-admin-secret", effectiveUser.BaiduSecretKey);
        }

        [Fact]
        public async Task StudentWithPrivateKey_ShouldPreserveOwnPrivateKey()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using var _ = connection;
            using var __ = context;

            var admin = new User
            {
                Id = Guid.NewGuid(),
                Username = "超级管理员",
                Role = UserRole.SuperAdmin,
                LlmApiKey = "sk-admin-key",
                LlmModelName = "deepseek-chat"
            };
            var student = new User
            {
                Id = Guid.NewGuid(),
                Username = "极客学员",
                Role = UserRole.Student,
                LlmApiKey = "sk-my-own-private-key",
                LlmBaseUrl = "https://api.openai.com/v1",
                LlmModelName = "gpt-4o"
            };

            context.Users.AddRange(admin, student);
            await context.SaveChangesAsync();

            var userSessionService = new UserSessionService(context, new HttpClient());
            var effectiveUser = await userSessionService.ResolveEffectiveUserLlmConfigAsync(student);

            Assert.Equal("sk-my-own-private-key", effectiveUser.LlmApiKey);
            Assert.Equal("gpt-4o", effectiveUser.LlmModelName);
        }

        [Fact]
        public async Task NeitherHasKey_ShouldFallbackToHeuristicEngine()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using var _ = connection;
            using var __ = context;

            var admin = new User
            {
                Id = Guid.NewGuid(),
                Username = "超级管理员",
                Role = UserRole.SuperAdmin,
                LlmApiKey = ""
            };
            var student = new User
            {
                Id = Guid.NewGuid(),
                Username = "普通学员",
                Role = UserRole.Student,
                Grade = "初中二年级",
                LlmApiKey = ""
            };

            context.Users.AddRange(admin, student);
            await context.SaveChangesAsync();

            var userSessionService = new UserSessionService(context, new HttpClient());
            var fakeGamification = new FakeGamificationService { CurrentUser = student };
            var generator = new AiQuestionGeneratorService(
                fakeGamification,
                context,
                new SimpleHttpClientFactory(new HttpClient()),
                userSessionService: userSessionService);

            var questions = await generator.GenerateBatchQuestionsAsync("初中二年级", "初中数学", "几何图形", 2);

            Assert.NotEmpty(questions);
            // 验证日志中记录为本地离线启发式引擎
            var log = await context.LlmGenerationLogs.FirstOrDefaultAsync(l => l.UserId == student.Id);
            Assert.NotNull(log);
            Assert.Equal("Heuristic-Engine-v3", log.ModelName);
        }

        [Fact]
        public async Task StudentGeneratesQuestions_WithInheritedAdminKey_CallsRealLlmApi()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using var _ = connection;
            using var __ = context;

            var admin = new User
            {
                Id = Guid.NewGuid(),
                Username = "超级管理员",
                Role = UserRole.SuperAdmin,
                LlmApiKey = "sk-admin-key-real",
                LlmBaseUrl = "https://api.deepseek.com",
                LlmModelName = "deepseek-chat"
            };
            var student = new User
            {
                Id = Guid.NewGuid(),
                Username = "做题学生",
                Role = UserRole.Student,
                Grade = "初中二年级",
                LlmApiKey = ""
            };

            context.Users.AddRange(admin, student);
            await context.SaveChangesAsync();

            var responseJson = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            content = "{\"questions\": [{\"subject\": \"初中数学\", \"category\": \"一次函数\", \"type\": \"单选题\", \"stem\": \"已知函数 y=2x+1，当 x=2 时 y 的值为？\", \"options\": [\"A. 5\", \"B. 4\", \"C. 3\", \"D. 2\"], \"correctAnswer\": \"A\", \"standardAnalysis\": \"代入计算得到 2*2+1=5。\", \"difficulty\": 2}]}"
                        }
                    }
                },
                usage = new
                {
                    prompt_tokens = 150,
                    completion_tokens = 80,
                    total_tokens = 230
                }
            });

            string? interceptedAuthHeader = null;
            var testHandler = new TestHttpHandler(req =>
            {
                interceptedAuthHeader = req.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson)
                };
            });

            var httpClient = new HttpClient(testHandler);
            var userSessionService = new UserSessionService(context, httpClient);
            var fakeGamification = new FakeGamificationService { CurrentUser = student };
            var generator = new AiQuestionGeneratorService(
                fakeGamification,
                context,
                new SimpleHttpClientFactory(httpClient),
                userSessionService: userSessionService);

            var questions = await generator.GenerateBatchQuestionsAsync("初中二年级", "初中数学", "一次函数", 1);

            Assert.Single(questions);
            Assert.Contains("y=2x+1", questions[0].Stem);
            Assert.Equal("Bearer sk-admin-key-real", interceptedAuthHeader);

            // 验证日志中归属于学生自身，且模型名称为 deepseek-chat
            var log = await context.LlmGenerationLogs.FirstOrDefaultAsync(l => l.UserId == student.Id);
            Assert.NotNull(log);
            Assert.Equal("deepseek-chat", log.ModelName);
            Assert.Equal(230, log.TotalTokens);
        }

        [Fact]
        public async Task AiTutor_WithInheritedAdminKey_CallsRealLlmExplanation()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using var _ = connection;
            using var __ = context;

            var admin = new User
            {
                Id = Guid.NewGuid(),
                Username = "超级管理员",
                Role = UserRole.SuperAdmin,
                LlmApiKey = "sk-admin-tutor-key",
                LlmBaseUrl = "https://api.deepseek.com",
                LlmModelName = "deepseek-chat"
            };
            var student = new User
            {
                Id = Guid.NewGuid(),
                Username = "辅导学生",
                Role = UserRole.Student,
                LlmApiKey = ""
            };

            context.Users.AddRange(admin, student);
            await context.SaveChangesAsync();

            var responseJson = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            content = "{\"summary\": \"核心概念考查\", \"whyWrongAnalysis\": \"考点理解偏离\", \"stepByStepReasoning\": \"步骤1：观察斜率；步骤2：代入计算\", \"keyConcepts\": [\"一次函数性质\"]}"
                        }
                    }
                }
            });

            string? interceptedAuthHeader = null;
            var testHandler = new TestHttpHandler(req =>
            {
                interceptedAuthHeader = req.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson)
                };
            });

            var httpClient = new HttpClient(testHandler);
            var userSessionService = new UserSessionService(context, httpClient);
            var fakeGamification = new FakeGamificationService { CurrentUser = student };
            var tutor = new AiTutorService(
                fakeGamification,
                new SimpleHttpClientFactory(httpClient),
                dbContext: context,
                userSessionService: userSessionService);

            var testQuestion = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "测试题目",
                Subject = "初中数学",
                Category = "一次函数",
                CorrectAnswer = "A"
            };

            var explanation = await tutor.GetExplanationAsync(testQuestion, "B");

            Assert.NotNull(explanation);
            Assert.Equal("考点理解偏离", explanation.WhyWrongAnalysis);
            Assert.Equal("Bearer sk-admin-tutor-key", interceptedAuthHeader);
        }
    }
}
