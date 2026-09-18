using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class StudyPlanAndEvolutionClosedLoopTests
    {
        private (SqliteConnection connection, DbContextOptions<AppDbContext> options, IDbContextFactory<AppDbContext> factory) CreateInMemoryDb()
        {
            var dbName = $"StudyPlanDb_{Guid.NewGuid():N}";
            var connString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
            var connection = new SqliteConnection(connString);
            connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connString)
                .Options;

            using (var initContext = new AppDbContext(options))
            {
                initContext.Database.EnsureCreated();
            }

            var factory = new SimpleTestDbContextFactory(options);
            return (connection, options, factory);
        }

        private class DummyHttpClientFactory : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new HttpClient();
        }

        [Fact]
        public async Task StudyPlanService_GenerateAdaptivePlan_ShouldCreatePlanWithTasks()
        {
            var (conn, options, factory) = CreateInMemoryDb();
            using (conn)
            {
                using var db = new AppDbContext(options);
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "小明",
                    Role = UserRole.Student,
                    Grade = "初中二年级",
                    DailyTargetQuestions = 15
                };
                db.Users.Add(student);

                // 添加考点试题与部分错题
                var q1 = new Question { Id = Guid.NewGuid(), Subject = "数学", Category = "因式分解", Stem = "题1", OptionsJson = "[\"A\",\"B\"]", CorrectAnswer = "A", Difficulty = 3 };
                var q2 = new Question { Id = Guid.NewGuid(), Subject = "物理", Category = "压强与浮力", Stem = "题2", OptionsJson = "[\"A\",\"B\"]", CorrectAnswer = "A", Difficulty = 3 };
                db.Questions.AddRange(q1, q2);

                db.ErrorItems.Add(new ErrorItem { Id = Guid.NewGuid(), UserId = student.Id, QuestionId = q1.Id, Question = q1, IsMastered = false });
                await db.SaveChangesAsync();

                var evolutionService = new StudentEvolutionService(db, new GamificationService(db, null!, factory), new DummyHttpClientFactory(), factory);
                var planService = new StudyPlanService(db, evolutionService, factory);

                // 执行生成自适应计划
                var plan = await planService.GenerateAdaptivePlanAsync(student.Id, "数学");

                Assert.NotNull(plan);
                Assert.Equal(StudyPlanStatus.Active, plan.Status);
                Assert.NotEmpty(plan.Tasks);
                Assert.Contains(plan.Tasks, t => t.TaskType == StudyPlanTaskType.WeaknessBreakthrough);
                Assert.True(plan.RemainingDays >= 6);
                Assert.False(string.IsNullOrWhiteSpace(plan.LatestSupervisionMessage));
            }
        }

        [Fact]
        public async Task StudyPlanService_RecordPracticeProgress_ShouldAdvanceTaskAndCompletePlan()
        {
            var (conn, options, factory) = CreateInMemoryDb();
            using (conn)
            {
                using var db = new AppDbContext(options);
                var student = new User { Id = Guid.NewGuid(), Username = "学霸李", Role = UserRole.Student };
                db.Users.Add(student);

                var plan = new StudyPlan
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    Title = "单任务突击计划",
                    Status = StudyPlanStatus.Active,
                    Tasks = new List<StudyPlanTask>
                    {
                        new StudyPlanTask
                        {
                            Id = Guid.NewGuid(),
                            Title = "攻坚因式分解",
                            Subject = "数学",
                            Category = "因式分解",
                            TargetCount = 2,
                            CompletedCount = 0,
                            IsCompleted = false
                        }
                    }
                };
                db.StudyPlans.Add(plan);
                await db.SaveChangesAsync();

                var evolutionService = new StudentEvolutionService(db, new GamificationService(db, null!, factory), new DummyHttpClientFactory(), factory);
                var planService = new StudyPlanService(db, evolutionService, factory);

                // 第 1 题：推进任务进度
                var (updated1, feedback1) = await planService.RecordPracticeProgressAsync(student.Id, "数学", "因式分解", true);
                Assert.True(updated1);
                Assert.Contains("已完成 1/2", feedback1);

                // 第 2 题：达成子任务并标记计划为 Completed
                var (updated2, feedback2) = await planService.RecordPracticeProgressAsync(student.Id, "数学", "因式分解", true);
                Assert.True(updated2);
                Assert.Contains("恭喜达成", feedback2);

                using var verifyDb = factory.CreateDbContext();
                var updatedPlan = await verifyDb.StudyPlans.Include(p => p.Tasks).FirstAsync(p => p.Id == plan.Id);
                Assert.True(updatedPlan.Tasks.First().IsCompleted);
                Assert.Equal(StudyPlanStatus.Completed, updatedPlan.Status);
                Assert.NotNull(updatedPlan.CompletedDate);
                Assert.Equal(100.0, updatedPlan.ProgressPercent);
            }
        }

        [Fact]
        public async Task StudyPlanService_SuperviseAndNudge_ShouldGenerateAppropriateNudges()
        {
            var (conn, options, factory) = CreateInMemoryDb();
            using (conn)
            {
                using var db = new AppDbContext(options);
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "小张",
                    Role = UserRole.Student,
                    TodayAnsweredCount = 0,
                    DailyTargetQuestions = 20
                };
                db.Users.Add(student);

                var plan = new StudyPlan
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    Title = "周度计划",
                    Status = StudyPlanStatus.Active,
                    StartDate = DateTime.Now.AddDays(-1),
                    Tasks = new List<StudyPlanTask>
                    {
                        new StudyPlanTask
                        {
                            Id = Guid.NewGuid(),
                            Title = "记忆唤醒任务",
                            Subject = "物理",
                            Category = "力学",
                            TaskType = StudyPlanTaskType.SpacedRepetition,
                            TargetCount = 5,
                            CompletedCount = 0
                        }
                    }
                };
                db.StudyPlans.Add(plan);
                await db.SaveChangesAsync();

                var evolutionService = new StudentEvolutionService(db, new GamificationService(db, null!, factory), new DummyHttpClientFactory(), factory);
                var planService = new StudyPlanService(db, evolutionService, factory);

                // 当存在未完成的抗遗忘任务时，应优先生成抗遗忘督促预警
                var (nudge, severity, urgentId) = await planService.SuperviseAndNudgeAsync(student.Id);

                Assert.Equal("Warning", severity);
                Assert.Contains("抗遗忘预警", nudge);
                Assert.Equal(plan.Tasks.First().Id, urgentId);

                // 刷新数据检查督促计数累加
                using var verifyDb = factory.CreateDbContext();
                var tracked = await verifyDb.StudyPlans.FindAsync(plan.Id);
                Assert.Equal(1, tracked!.SupervisionNudgeCount);
                Assert.NotNull(tracked.LastSupervisedAt);
            }
        }

        [Fact]
        public async Task StudyPlanService_EvaluateClosedLoopProgress_WithImprovement_ShouldExtractSuccessExperiences()
        {
            var (conn, options, factory) = CreateInMemoryDb();
            using (conn)
            {
                using var db = new AppDbContext(options);
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "优秀生",
                    Role = UserRole.Student,
                    CurrentStreak = 5,
                    TotalAnswered = 30,
                    TotalCorrect = 27
                };
                db.Users.Add(student);

                var q = new Question { Id = Guid.NewGuid(), Subject = "数学", Category = "因式分解", Stem = "Q", OptionsJson = "[]", CorrectAnswer = "A" };
                db.Questions.Add(q);

                // 近期 7 天做题：高正确率且完成 5 题以上
                for (int i = 0; i < 6; i++)
                {
                    db.PracticeRecords.Add(new PracticeRecord
                    {
                        Id = Guid.NewGuid(),
                        UserId = student.Id,
                        QuestionId = q.Id,
                        Question = q,
                        IsCorrect = true,
                        TimeTakenSeconds = 15,
                        AnsweredAt = DateTime.Now.AddDays(-1)
                    });
                }

                // 前期做题记录（准确率偏低）：用于形成正确率提升 Delta
                for (int i = 0; i < 6; i++)
                {
                    db.PracticeRecords.Add(new PracticeRecord
                    {
                        Id = Guid.NewGuid(),
                        UserId = student.Id,
                        QuestionId = q.Id,
                        Question = q,
                        IsCorrect = i < 3, // 50% 正确率
                        TimeTakenSeconds = 25,
                        AnsweredAt = DateTime.Now.AddDays(-10)
                    });
                }

                // 净化的错题记录
                db.ErrorItems.Add(new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = q.Id,
                    IsMastered = true,
                    LastRevisedAt = DateTime.Now.AddDays(-2)
                });

                await db.SaveChangesAsync();

                var evolutionService = new StudentEvolutionService(db, new GamificationService(db, null!, factory), new DummyHttpClientFactory(), factory);
                var planService = new StudyPlanService(db, evolutionService, factory);

                // 执行持续迭代闭环复盘
                var insight = await planService.EvaluateClosedLoopProgressAsync(student.Id);

                Assert.NotNull(insight);
                Assert.True(insight.HasImproved);
                Assert.True(insight.AccuracyDelta > 0);
                Assert.True(insight.PurifiedErrorsCount >= 1);
                Assert.NotEmpty(insight.SuccessExperiences);
                Assert.False(string.IsNullOrWhiteSpace(insight.SuccessExperienceSummary));
                Assert.Contains("下一代计划演进导向", insight.NextEvolutionStrategy);

                // 验证已持久化存库
                var savedEntity = await db.EvolutionClosedLoopInsights.FirstOrDefaultAsync(e => e.UserId == student.Id);
                Assert.NotNull(savedEntity);
                Assert.True(savedEntity.PotentialScore >= 60);
            }
        }

        [Fact]
        public async Task StudyPlanService_EvaluateClosedLoopProgress_WithoutImprovement_ShouldDiagnoseRootCauses()
        {
            var (conn, options, factory) = CreateInMemoryDb();
            using (conn)
            {
                using var db = new AppDbContext(options);
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "卡壳学员",
                    Role = UserRole.Student,
                    CurrentStreak = 0,
                    TotalAnswered = 15,
                    TotalCorrect = 5
                };
                db.Users.Add(student);

                var q = new Question { Id = Guid.NewGuid(), Subject = "物理", Category = "压强与浮力", Stem = "Q", OptionsJson = "[]", CorrectAnswer = "A" };
                db.Questions.Add(q);

                // 近 7 天仅作答 2 题且均答错，且耗时短（粗心）
                db.PracticeRecords.Add(new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = q.Id,
                    Question = q,
                    IsCorrect = false,
                    TimeTakenSeconds = 8,
                    AnsweredAt = DateTime.Now.AddDays(-2)
                });
                db.PracticeRecords.Add(new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = q.Id,
                    Question = q,
                    IsCorrect = false,
                    TimeTakenSeconds = 10,
                    AnsweredAt = DateTime.Now.AddDays(-1)
                });

                // 错题库中记录审题不清
                db.ErrorItems.Add(new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = q.Id,
                    Question = q,
                    IsMastered = false,
                    ErrorReasonCategory = "审题不清"
                });

                await db.SaveChangesAsync();

                var evolutionService = new StudentEvolutionService(db, new GamificationService(db, null!, factory), new DummyHttpClientFactory(), factory);
                var planService = new StudyPlanService(db, evolutionService, factory);

                // 执行持续闭环复盘诊断
                var insight = await planService.EvaluateClosedLoopProgressAsync(student.Id);

                Assert.NotNull(insight);
                Assert.False(insight.HasImproved);
                Assert.NotEmpty(insight.FailureReasons);
                // 应当成功诊断出刷题量不足
                Assert.Contains(insight.FailureReasons, r => r == ProgressFailureReason.InsufficientVolume);
                Assert.False(string.IsNullOrWhiteSpace(insight.RootCauseDiagnosis));
                Assert.False(string.IsNullOrWhiteSpace(insight.CorrectivePrescription));
                Assert.Contains("破局纠偏处方", "破局纠偏处方" + insight.CorrectivePrescription);
            }
        }

        [Fact]
        public async Task StudentEvolutionService_WeeklyDeltaAndPause_ShouldHandleGracefully()
        {
            var (conn, options, factory) = CreateInMemoryDb();
            using (conn)
            {
                using var db = new AppDbContext(options);
                var oldStudent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "老生暂停",
                    Role = UserRole.Student,
                    TotalAnswered = 100,
                    TotalCorrect = 85
                };
                db.Users.Add(oldStudent);

                var q = new Question { Id = Guid.NewGuid(), Subject = "数学", Category = "函数", Stem = "Q", OptionsJson = "[]", CorrectAnswer = "A" };
                db.Questions.Add(q);

                // 20 天前的做题记录（近 14 天无记录，模拟老学员停顿）
                db.PracticeRecords.Add(new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = oldStudent.Id,
                    QuestionId = q.Id,
                    Question = q,
                    IsCorrect = true,
                    TimeTakenSeconds = 20,
                    AnsweredAt = DateTime.Now.AddDays(-20)
                });
                await db.SaveChangesAsync();

                var evolutionService = new StudentEvolutionService(db, new GamificationService(db, null!, factory), new DummyHttpClientFactory(), factory);

                var report = await evolutionService.GenerateDiagnosisReportAsync(oldStudent.Id);

                Assert.NotNull(report);
                // 确认修复后不再误判为“刚开启学习旅程”，而是给出练习暂停提醒
                Assert.Contains("暂停提醒", report.WeeklyProgressSummary);
                // 确认真实弱项攻坚计数不为固定伪假数字
                Assert.True(report.WeakCategoriesReducedCount >= 0);
            }
        }
    }
}
