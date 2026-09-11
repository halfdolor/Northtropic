using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxPinnacleTranscendenceTests
    {
        [Fact]
        public void CurriculumSubjectConfig_DatabaseIndexes_ConfiguredProperly()
        {
            var (ctx, conn) = TestDbContextFactory.CreateInMemoryContext();
            using (conn)
            using (ctx)
            {
                var entityType = ctx.Model.FindEntityType(typeof(CurriculumSubjectConfig));
                Assert.NotNull(entityType);

                var indexes = entityType.GetIndexes().ToList();
                Assert.NotEmpty(indexes);

                // 验证 (Grade, Subject) 复合索引
                var gradeSubjectIndex = indexes.FirstOrDefault(i =>
                    i.Properties.Count == 2 &&
                    i.Properties.Any(p => p.Name == "Grade") &&
                    i.Properties.Any(p => p.Name == "Subject"));
                Assert.NotNull(gradeSubjectIndex);

                // 验证 Grade 单字段索引
                var gradeIndex = indexes.FirstOrDefault(i =>
                    i.Properties.Count == 1 &&
                    i.Properties.Any(p => p.Name == "Grade"));
                Assert.NotNull(gradeIndex);
            }
        }

        [Fact]
        public async Task PracticeService_CategoryCacheTelemetry_RecordsHitsAndMisses()
        {
            var (ctx, conn) = TestDbContextFactory.CreateInMemoryContext();
            using (conn)
            using (ctx)
            {
                var practiceService = new PracticeService(ctx, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                // 准备题库数据
                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "测试分类1",
                    Category = "平面几何与勾股定理",
                    Subject = "数学",
                    CorrectAnswer = "A",
                    Type = QuestionType.SingleChoice,
                    GradeTarget = "初中三年级",
                    OptionsJson = "[]"
                };
                ctx.Questions.Add(q1);
                await ctx.SaveChangesAsync();

                PracticeService.ResetCategoryCacheTelemetry();
                PracticeService.InvalidateCategoryCache();

                var initialMisses = PracticeService.CategoryCacheMissCount;
                var initialHits = PracticeService.CategoryCacheHitCount;

                // 第一次查询 -> 触发数据库查询与 Cache Miss
                var cats1 = await practiceService.GetCategoriesAsync();
                Assert.Contains("平面几何与勾股定理", cats1);
                Assert.True(PracticeService.CategoryCacheMissCount > initialMisses);

                // 第二次查询 -> 命中内存缓存 Cache Hit
                var hitsBefore = PracticeService.CategoryCacheHitCount;
                var cats2 = await practiceService.GetCategoriesAsync();
                Assert.Equal(cats1.Count, cats2.Count);
                Assert.Equal(hitsBefore + 1, PracticeService.CategoryCacheHitCount);
                Assert.True(PracticeService.CategoryCacheHitRatio > 0);
            }
        }

        [Fact]
        public async Task PracticeService_InvalidateCategoryCache_ThreadSafety()
        {
            var (ctx, conn) = TestDbContextFactory.CreateInMemoryContext();
            using (conn)
            using (ctx)
            {
                var practiceService = new PracticeService(ctx, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var q = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "并发测试题",
                    Category = "二次函数顶点式",
                    Subject = "数学",
                    CorrectAnswer = "B",
                    Type = QuestionType.SingleChoice,
                    GradeTarget = "初中三年级",
                    OptionsJson = "[]"
                };
                ctx.Questions.Add(q);
                await ctx.SaveChangesAsync();

                // 启动 10 个并发读写任务，校验在高频 invalidation 与读取下的线程安全性与无死锁
                var tasks = new List<Task>();
                for (int i = 0; i < 10; i++)
                {
                    int index = i;
                    tasks.Add(Task.Run(async () =>
                    {
                        if (index % 3 == 0)
                        {
                            PracticeService.InvalidateCategoryCache();
                        }
                        else
                        {
                            var cats = await practiceService.GetCategoriesAsync();
                            Assert.NotEmpty(cats);
                        }
                    }));
                }

                await Task.WhenAll(tasks);
            }
        }

        [Fact]
        public void ErrorBookService_CalculateRetentionHealthScore_ExponentialDecayModel()
        {
            var service = new ErrorBookService(null!, new FakeGamificationService(), new FakeUserSessionService());
            var now = DateTime.Now;

            // 1. 已掌握题目永久保持 100%
            var masteredItem = new ErrorItem
            {
                Id = Guid.NewGuid(),
                IsMastered = true,
                RevisionCount = 3,
                LastRevisedAt = now.AddDays(-100)
            };
            Assert.Equal(100.0, service.CalculateRetentionHealthScore(masteredItem, now));

            // 2. 刚复习完的题目接近 100% (elapsed = 0)
            var freshItem = new ErrorItem
            {
                Id = Guid.NewGuid(),
                IsMastered = false,
                RevisionCount = 0, // 推荐间隔约 24 小时
                LastRevisedAt = now
            };
            Assert.Equal(100.0, service.CalculateRetentionHealthScore(freshItem, now));

            // 3. 恰好经过一个半衰期周期的题目，记忆留存率理论值为 e^(-0.693) ~ 50%
            var interval = service.GetRecommendedReviewInterval(freshItem.RevisionCount);
            var halfwayItem = new ErrorItem
            {
                Id = Guid.NewGuid(),
                IsMastered = false,
                RevisionCount = 0,
                LastRevisedAt = now - interval
            };
            var halfwayScore = service.CalculateRetentionHealthScore(halfwayItem, now);
            Assert.InRange(halfwayScore, 48.0, 52.0);

            // 4. 经过 2 个半衰期周期的题目，记忆留存率理论值为 25% 左右
            var overdueItem = new ErrorItem
            {
                Id = Guid.NewGuid(),
                IsMastered = false,
                RevisionCount = 0,
                LastRevisedAt = now - (interval * 2)
            };
            var overdueScore = service.CalculateRetentionHealthScore(overdueItem, now);
            Assert.InRange(overdueScore, 23.0, 27.0);
        }
    }
}