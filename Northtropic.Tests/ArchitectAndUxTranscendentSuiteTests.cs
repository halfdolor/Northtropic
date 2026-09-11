using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxTranscendentSuiteTests
    {
        private class TestDbContextFactoryImpl : IDbContextFactory<AppDbContext>
        {
            private readonly DbContextOptions<AppDbContext> _options;

            public TestDbContextFactoryImpl(DbContextOptions<AppDbContext> options)
            {
                _options = options;
            }

            public AppDbContext CreateDbContext()
            {
                return new AppDbContext(_options);
            }
        }

        private static (AppDbContext Context, SqliteConnection Connection, IDbContextFactory<AppDbContext> Factory) CreateFactoryContext()
        {
            var dbName = $"TranscendentDb_{Guid.NewGuid():N}";
            var connString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
            var connection = new SqliteConnection(connString);
            connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connString)
                .Options;

            var context = new AppDbContext(options);
            context.Database.EnsureCreated();

            var factory = new TestDbContextFactoryImpl(options);
            return (context, connection, factory);
        }

        #region 1. 架构级并发安全与 DbScope 隔离测试

        [Fact]
        public async Task QuestionManagementService_ConcurrentQueriesAndMutations_ZeroCircuitCollisions()
        {
            var (seedContext, connection, factory) = CreateFactoryContext();
            try
            {
                var adminUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "admin_architect",
                    Password = "hashed_pw",
                    Role = UserRole.SuperAdmin,
                    Grade = "高中一年级"
                };
                seedContext.Users.Add(adminUser);

                var teacherUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "teacher_architect",
                    Password = "hashed_pw",
                    Role = UserRole.Teacher,
                    Grade = "高中二年级"
                };
                seedContext.Users.Add(teacherUser);

                for (int i = 0; i < 8; i++)
                {
                    seedContext.Questions.Add(new Question
                    {
                        Id = Guid.NewGuid(),
                        Stem = $"并发安全测试试题 #{i}",
                        Subject = "化学",
                        Category = "化学反应原理",
                        Type = QuestionType.FillInBlank,
                        CorrectAnswer = "Na2O2",
                        IsPublic = true,
                        Difficulty = 3,
                        CreatedByUserId = teacherUser.Id
                    });
                }
                await seedContext.SaveChangesAsync();

                var service = new QuestionManagementService(seedContext, factory);

                // 启动 10 个并发异步任务同时查询、统计、新增与审核试题
                var tasks = new List<Task>();
                for (int t = 0; t < 10; t++)
                {
                    int threadIndex = t;
                    tasks.Add(Task.Run(async () =>
                    {
                        if (threadIndex % 3 == 0)
                        {
                            var stats = await service.GetQuestionStatisticsAsync();
                            Assert.True(stats.TotalQuestions >= 8);
                        }
                        else if (threadIndex % 3 == 1)
                        {
                            var list = await service.GetQuestionsForManagementAsync(adminUser.Id, "化学");
                            Assert.NotEmpty(list);
                        }
                        else
                        {
                            var newQ = new Question
                            {
                                Stem = $"并发动态插入题目 {threadIndex}",
                                Subject = "物理",
                                Category = "电磁学",
                                Type = QuestionType.SingleChoice,
                                OptionsJson = "[\"A. 1\",\"B. 2\"]",
                                CorrectAnswer = "A",
                                Difficulty = 2,
                                IsPublic = false
                            };
                            var addResult = await service.AddQuestionAsync(newQ, teacherUser.Id);
                            Assert.True(addResult.Success);
                        }
                    }));
                }

                // 所有并发任务应全部平稳完成，绝不产生 InvalidOperationException DbContext 线程冲突
                await Task.WhenAll(tasks);

                var finalStats = await service.GetQuestionStatisticsAsync();
                Assert.True(finalStats.TotalQuestions > 8);
            }
            finally
            {
                seedContext.Dispose();
                connection.Close();
                connection.Dispose();
            }
        }

        [Fact]
        public async Task PracticeService_ConcurrentSubmissionsAndBatch_SafeAndIsolated()
        {
            var (seedContext, connection, factory) = CreateFactoryContext();
            try
            {
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "transcendent_student",
                    Password = "hashed_pw",
                    Role = UserRole.Student,
                    Grade = "高中一年级"
                };
                seedContext.Users.Add(student);

                var questionA = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "测试题目 A: $2H_2 + O_2 = 2H_2O$",
                    Subject = "化学",
                    Category = "物质变化",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "2H2O",
                    IsPublic = true,
                    Difficulty = 2
                };
                var questionB = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "测试题目 B: 求不等式解集",
                    Subject = "数学",
                    Category = "不等式",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "(-inf, -1) u (1, +inf)",
                    IsPublic = true,
                    Difficulty = 3
                };
                seedContext.Questions.AddRange(questionA, questionB);
                await seedContext.SaveChangesAsync();

                var fakeSession = new FakeUserSessionService { ActiveUser = student };
                var fakeGamification = new FakeGamificationService { CurrentUser = student };
                var fakeAiTutor = new FakeAiTutorService();

                var practiceService = new PracticeService(seedContext, fakeGamification, fakeSession, fakeAiTutor, factory);

                // 测试整卷提交原子事务 (SubmitBatchPaperAsync)
                var submissions = new List<(Question Question, string UserAnswer, int TimeTakenSeconds)>
                {
                    (questionA, "2H_2O", 15),
                    (questionB, "x < -1 或 x > 1", 20)
                };

                var batchResult = await practiceService.SubmitBatchPaperAsync(submissions, 0, student.Id);
                Assert.Equal(2, batchResult.TotalQuestions);
                Assert.Equal(2, batchResult.CorrectCount);
                Assert.Equal(2, batchResult.ItemResults.Count);
                Assert.True(batchResult.ItemResults[1].IsCorrect);
                Assert.True(batchResult.ItemResults[2].IsCorrect);

                // 验证并发单独答题
                var concurrentTasks = new List<Task<AnswerCheckResult>>();
                for (int i = 0; i < 6; i++)
                {
                    concurrentTasks.Add(Task.Run(async () =>
                    {
                        return await practiceService.SubmitAnswerAsync(questionA, "2H2O", 10, 1, student.Id);
                    }));
                }

                var results = await Task.WhenAll(concurrentTasks);
                Assert.All(results, r => Assert.True(r.IsCorrect));
            }
            finally
            {
                seedContext.Dispose();
                connection.Close();
                connection.Dispose();
            }
        }

        #endregion

        #region 2. 用户体验专家与学科等价智能引擎测试

        [Fact]
        public void CheckFillInBlankMatch_OversetUndersetConditions_EquivalenceAccepted()
        {
            // 1. \overset{点燃}{=} 与 \underset{加热}{=} 条件标注识别与等价消除
            Assert.True(PracticeService.CheckFillInBlankMatch(@"2H_2 + O_2 \overset{点燃}{=} 2H_2O", "2H2+O2=2H2O"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"2KClO_3 \underset{加热}{=} 2KCl + 3O_2", "2KClO3=2KCl+3O2"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"CaCO_3 \overset{\Delta}{=} CaO + CO_2", "CaCO3=CaO+CO2"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"CH_4 + 2O_2 \overset{点燃}{=} CO_2 + 2H_2O", "CH4+2O2=CO2+2H2O"));

            // 2. 反应条件反馈说明生成
            var qChem = new Question
            {
                Stem = "写出氢气与氧气燃烧的化学方程式",
                Subject = "化学",
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "2H2+O2=2H2O"
            };
            var reason = PracticeService.GenerateEquivalentMatchReason(qChem, @"2H_2 + O_2 \overset{点燃}{=} 2H_2O");
            Assert.Contains("化学反应方程式条件等价", reason);
        }

        [Fact]
        public void CheckFillInBlankMatch_DisjunctiveInequalities_EquivalenceAccepted()
        {
            // 1. 不等式 "或" / "或者" 分隔的多区间并集转化: x < -1 或 x > 1 <=> (-inf, -1) u (1, +inf)
            Assert.True(PracticeService.CheckFillInBlankMatch("x < -1 或 x > 1", "(-inf, -1) u (1, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x < -1 或者 x > 1", @"(-\infty, -1) \cup (1, +\infty)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x > 1 或 x < -1", "(-∞, -1) ∪ (1, +∞)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x <= 2 或 x >= 5", "(-inf, 2] u [5, +inf)"));

            // 2. 反馈说明生成
            var qMath = new Question
            {
                Stem = "求不等式的解集",
                Subject = "数学",
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "(-inf, -1) u (1, +inf)"
            };
            var reason = PracticeService.GenerateEquivalentMatchReason(qMath, "x < -1 或 x > 1");
            Assert.Contains("不等式或关系与区间并集等价", reason);
        }

        [Fact]
        public void CheckFillInBlankMatch_SetBuilderNotation_EquivalenceAccepted()
        {
            // 1. 集合描述法 {x | x > 2} <=> (2, +inf) <=> x > 2
            Assert.True(PracticeService.CheckFillInBlankMatch("{x | x > 2}", "(2, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\{x \mid x > 2\}", "(2, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("{x | x <= 5}", "(-inf, 5]"));
            Assert.True(PracticeService.CheckFillInBlankMatch("{x | 1 < x < 4}", "(1, 4)"));

            // 2. 反馈说明生成
            var qMath = new Question
            {
                Stem = "求集合解集",
                Subject = "数学",
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "(2, +inf)"
            };
            var reason = PracticeService.GenerateEquivalentMatchReason(qMath, "{x | x > 2}");
            Assert.Contains("集合描述法与解集等价", reason);
        }

        [Fact]
        public void CheckFillInBlankMatch_ExpandedHighSchoolChemicalSynonyms_EquivalenceAccepted()
        {
            // 1. 过氧化钠 (Na2O2)
            Assert.True(PracticeService.CheckFillInBlankMatch("过氧化钠", "Na2O2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("Na_2O_2", "过氧化钠"));

            // 2. 次氯酸 (HClO) 与 次氯酸钠 (NaClO)
            Assert.True(PracticeService.CheckFillInBlankMatch("次氯酸", "HClO"));
            Assert.True(PracticeService.CheckFillInBlankMatch("次氯酸钠", "NaClO"));

            // 3. 氨气 (NH3)
            Assert.True(PracticeService.CheckFillInBlankMatch("氨气", "NH3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("NH_3", "氨气"));

            // 4. 硫酸氢钠 (NaHSO4) 与 碳酸氢钙 (Ca(HCO3)2)
            Assert.True(PracticeService.CheckFillInBlankMatch("硫酸氢钠", "NaHSO4"));
            Assert.True(PracticeService.CheckFillInBlankMatch("碳酸氢钙", "Ca(HCO3)2"));

            // 5. 苯 (C6H6) 与 乙酸乙酯 (CH3COOCH2CH3)
            Assert.True(PracticeService.CheckFillInBlankMatch("苯", "C6H6"));
            Assert.True(PracticeService.CheckFillInBlankMatch("乙酸乙酯", "CH3COOCH2CH3"));
        }

        [Fact]
        public void CheckFillInBlankMatch_GreekSymbolsInPhysics_EquivalenceAccepted()
        {
            // 希腊物理量符号等价: \eta vs η (效率), \nu vs ν (频率), \Delta vs Δ
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\eta = 80%", "η=80%"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\Delta E", "ΔE"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\nu = 50Hz", "ν=50Hz"));
        }

        #endregion
    }
}
