using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class StudentEvolutionService : IStudentEvolutionService
    {
        private readonly AppDbContext _dbContext;
        private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;
        private readonly IGamificationService _gamificationService;
        private readonly HttpClient _httpClient;

        public StudentEvolutionService(
            AppDbContext dbContext,
            IGamificationService gamificationService,
            IHttpClientFactory httpClientFactory,
            IDbContextFactory<AppDbContext>? dbContextFactory = null)
        {
            _dbContext = dbContext;
            _dbContextFactory = dbContextFactory;
            _gamificationService = gamificationService;
            _httpClient = httpClientFactory.CreateClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(20);
        }

        private ValueTask<AsyncDbScope> CreateDbScopeAsync()
        {
            return AsyncDbScope.CreateAsync(_dbContextFactory, _dbContext);
        }

        public async Task<List<KnowledgePointMasteryDto>> GetMasteryOverviewAsync(Guid userId, string? subject = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId);
            string userGrade = user?.Grade ?? "初中二年级";

            // 1. 获取答题记录 (只读投影优化，消除大实体全量物化)
            var query = ctx.PracticeRecords
                .AsNoTracking()
                .Where(r => r.UserId == userId && r.Question != null);

            if (!string.IsNullOrWhiteSpace(subject) && subject != "全部分科" && subject != "通用学科")
            {
                query = query.Where(r => r.Question!.Subject == subject);
            }

            var records = await query
                .Select(r => new
                {
                    Subject = r.Question!.Subject,
                    Category = r.Question!.Category,
                    r.IsCorrect,
                    r.TimeTakenSeconds,
                    r.AnsweredAt
                })
                .ToListAsync();

            // 2. 获取错题记录 (只读投影优化)
            var errors = await ctx.ErrorItems
                .AsNoTracking()
                .Where(e => e.UserId == userId && e.Question != null)
                .Select(e => new
                {
                    Subject = e.Question!.Subject,
                    Category = e.Question!.Category,
                    e.IsMastered
                })
                .ToListAsync();

            // 按 Subject + Category 分组统计
            var grouped = records
                .GroupBy(r => (Subject: r.Subject, Category: r.Category))
                .ToDictionary(g => g.Key, g => g.ToList());

            var result = new List<KnowledgePointMasteryDto>();

            // 3. 拉取该年级全量标配学科与知识点阵列
            var gradeSubjects = GradeSubjectProvider.GetSubjectsByGrade(userGrade);
            var categoryPool = new List<(string Subject, string Category)>();

            foreach (var sub in gradeSubjects)
            {
                if (!string.IsNullOrWhiteSpace(subject) && subject != "全部分科" && subject != "通用学科" && sub != subject)
                    continue;

                var cats = GradeSubjectProvider.GetCategoriesBySubject(sub);
                foreach (var cat in cats)
                {
                    categoryPool.Add((sub, cat));
                }
            }

            // 合并已有答题记录但不在预设模板中的分类
            foreach (var key in grouped.Keys)
            {
                if (!categoryPool.Contains(key))
                {
                    categoryPool.Add(key);
                }
            }

            // 4. 逐个知识点计算演进指数
            foreach (var (subName, catName) in categoryPool)
            {
                var key = (Subject: subName, Category: catName);
                var subRecords = grouped.ContainsKey(key) ? grouped[key] : null;
                var subErrors = errors.Where(e => e.Subject == subName && e.Category == catName).ToList();

                int total = subRecords?.Count ?? 0;
                int correct = subRecords?.Count(r => r.IsCorrect) ?? 0;
                double accuracy = total > 0 ? (double)correct / total * 100 : 0;
                double avgSpeed = total > 0 && subRecords != null ? subRecords.Average(r => r.TimeTakenSeconds) : 0;
                DateTime? lastDate = total > 0 && subRecords != null ? subRecords.Max(r => r.AnsweredAt) : null;

                // 掌握度算法 = 基础正确率 + 错题净化提成 - 速度过慢惩罚
                int errorBonus = subErrors.Count > 0 ? (int)((double)subErrors.Count(e => e.IsMastered) / subErrors.Count * 15) : 10;
                int masteryScore = 0;

                if (total == 0)
                {
                    masteryScore = 0;
                }
                else
                {
                    masteryScore = (int)Math.Round(accuracy * 0.85 + errorBonus);
                    if (avgSpeed > 60) masteryScore = Math.Max(0, masteryScore - 5);
                    masteryScore = Math.Clamp(masteryScore, 0, 100);
                }

                string level = "🔒 待探索";
                if (total > 0)
                {
                    if (masteryScore >= 85) level = "🔥 融会贯通";
                    else if (masteryScore >= 65) level = "📈 演进提升中";
                    else level = "⚠️ 攻坚弱项";
                }

                result.Add(new KnowledgePointMasteryDto
                {
                    Subject = subName,
                    Category = catName,
                    TotalAnswered = total,
                    TotalCorrect = correct,
                    MasteryScore = masteryScore,
                    AverageSpeedSeconds = Math.Round(avgSpeed, 1),
                    MasteryLevel = level,
                    LastPracticedAt = lastDate
                });
            }

            return result.OrderByDescending(r => r.TotalAnswered).ThenByDescending(r => r.MasteryScore).ToList();
        }

        public async Task<EvolutionDiagnosisReportDto> GenerateDiagnosisReportAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            var masteryList = await GetMasteryOverviewAsync(userId);
            var errors = await ctx.ErrorItems.AsNoTracking().Where(e => e.UserId == userId).ToListAsync();

            var practiced = masteryList.Where(m => m.TotalAnswered > 0).ToList();
            double overallAccuracy = practiced.Count > 0 ? practiced.Average(m => m.AccuracyRate) : 0.0;

            var report = new EvolutionDiagnosisReportDto
            {
                UserId = userId,
                GeneratedAt = DateTime.Now,
                OverallMasteryRate = Math.Round(overallAccuracy, 1)
            };

            // 星级评估
            if (overallAccuracy >= 90) report.StarRating = 5;
            else if (overallAccuracy >= 80) report.StarRating = 4;
            else if (overallAccuracy >= 65) report.StarRating = 3;
            else if (overallAccuracy >= 50) report.StarRating = 2;
            else report.StarRating = 1;

            // 掌握最佳与弱项考点
            report.TopMasteredCategories = practiced
                .Where(m => m.MasteryScore >= 75)
                .OrderByDescending(m => m.MasteryScore)
                .Take(4)
                .Select(m => $"{m.Subject}-{m.Category}")
                .ToList();

            report.TopWeakCategories = masteryList
                .Where(m => m.TotalAnswered > 0 && m.MasteryScore < 70)
                .OrderBy(m => m.MasteryScore)
                .Take(4)
                .Select(m => $"{m.Subject}-{m.Category}")
                .ToList();

            // 错因归因统计
            foreach (var err in errors)
            {
                var cat = string.IsNullOrWhiteSpace(err.ErrorReasonCategory) ? "未分类" : err.ErrorReasonCategory;
                if (!report.ErrorReasonBreakdown.ContainsKey(cat))
                    report.ErrorReasonBreakdown[cat] = 0;
                report.ErrorReasonBreakdown[cat]++;
            }

            // 推荐下一次演进练习点：优先推荐已有作答但掌握度偏低（<70）的薄弱攻坚考点；若无可练考点再推荐待探索新考点
            var weakest = practiced.Where(m => m.MasteryScore < 70).OrderBy(m => m.MasteryScore).FirstOrDefault()
                ?? practiced.OrderBy(m => m.MasteryScore).FirstOrDefault()
                ?? masteryList.OrderBy(m => m.MasteryScore).FirstOrDefault();

            if (weakest != null)
            {
                report.RecommendedSubject = weakest.Subject;
                report.RecommendedCategory = weakest.Category;
                report.RecommendedDifficulty = weakest.MasteryScore < 40 ? 2 : (weakest.MasteryScore > 80 ? 4 : 3);
            }

            // 艾宾浩斯复习临界知识点统计
            var pendingSpacedNodes = masteryList.Where(m => m.NeedsSpacedReview).ToList();
            report.PendingSpacedReviewCount = pendingSpacedNodes.Count;

            // 周度进步跨越对比 (近7天 vs 前7天)
            var now = DateTime.Now;
            var sevenDaysAgo = now.AddDays(-7);
            var fourteenDaysAgo = now.AddDays(-14);

            var recentRecords = await ctx.PracticeRecords
                .AsNoTracking()
                .Where(r => r.UserId == userId && r.AnsweredAt >= sevenDaysAgo)
                .Select(r => new { r.IsCorrect, r.TimeTakenSeconds })
                .ToListAsync();

            var prevRecords = await ctx.PracticeRecords
                .AsNoTracking()
                .Where(r => r.UserId == userId && r.AnsweredAt >= fourteenDaysAgo && r.AnsweredAt < sevenDaysAgo)
                .Select(r => new { r.IsCorrect, r.TimeTakenSeconds })
                .ToListAsync();

            double recentAcc = recentRecords.Count > 0 ? (double)recentRecords.Count(r => r.IsCorrect) / recentRecords.Count * 100.0 : overallAccuracy;
            double prevAcc = prevRecords.Count > 0 ? (double)prevRecords.Count(r => r.IsCorrect) / prevRecords.Count * 100.0 : recentAcc;

            report.WeeklyAccuracyDelta = prevRecords.Count > 0 ? Math.Round(recentAcc - prevAcc, 1) : 0.0;

            double recentSpeed = recentRecords.Count > 0 ? recentRecords.Average(r => r.TimeTakenSeconds) : 0;
            double prevSpeed = prevRecords.Count > 0 ? prevRecords.Average(r => r.TimeTakenSeconds) : recentSpeed;
            report.SpeedImprovementSeconds = prevRecords.Count > 0 ? Math.Round(Math.Max(0, prevSpeed - recentSpeed), 1) : 0.0;

            report.WeakCategoriesReducedCount = Math.Max(0, 5 - report.TopWeakCategories.Count);

            if (recentRecords.Count == 0 && prevRecords.Count == 0)
            {
                report.WeeklyProgressSummary = "🌱 刚开启学习旅程，完成更多刷题后将展现更精细的周度增长跨越图谱！";
            }
            else if (prevRecords.Count == 0)
            {
                report.WeeklyProgressSummary = $"🌱 **首周练习基准已建立**：近 7 天共完成 {recentRecords.Count} 道练习，正确率达 **{recentAcc:F1}%**，平均解题用时 **{recentSpeed:F1} 秒**！下周将为您生成周度纵向跨越对比图谱。";
            }
            else
            {
                report.WeeklyProgressSummary = $"📈 **真实能力跨越轨迹**：近 7 天做题正确率变动 **{(report.WeeklyAccuracyDelta >= 0 ? "+" : "")}{report.WeeklyAccuracyDelta}%**，平均单题解题提速 **{report.SpeedImprovementSeconds} 秒**！当前有 **{report.PendingSpacedReviewCount} 个考点** 处于艾宾浩斯最佳复习巩固窗口。";
            }

            // 针对待突破弱项考点，进行前置知识图谱溯源评估
            foreach (var weakCat in report.TopWeakCategories)
            {
                var firstDash = weakCat.IndexOf('-');
                if (firstDash > 0 && firstDash < weakCat.Length - 1)
                {
                    var subjectPart = weakCat.Substring(0, firstDash);
                    var categoryPart = weakCat.Substring(firstDash + 1);
                    var warnings = await PrerequisiteKnowledgeGraph.TraceWeakPrerequisitesAsync(userId, subjectPart, categoryPart, ctx);
                    report.PrerequisiteWarnings.AddRange(warnings);
                }
            }

            // AI 演进诊断评语
            if (user != null && !string.IsNullOrWhiteSpace(user.LlmApiKey))
            {
                try
                {
                    report.AiGrowthAdvice = await CallLlmDiagnosisAdviceAsync(user, report, practiced);
                    return report;
                }
                catch (Exception)
                {
                    // 降级评语
                }
            }

            report.AiGrowthAdvice = GenerateFallbackAdvice(user?.Username ?? "学员", overallAccuracy, report.TopMasteredCategories, report.TopWeakCategories);
            return report;
        }

        public async Task<List<KnowledgePointMasteryDto>> GetPendingSpacedReviewNodesAsync(Guid userId)
        {
            var masteryList = await GetMasteryOverviewAsync(userId);
            return masteryList
                .Where(m => m.NeedsSpacedReview)
                .OrderBy(m => m.MemoryRetentionRate)
                .ToList();
        }

        public async Task<List<PrerequisiteTraceWarningDto>> TraceWeakPrerequisitesAsync(Guid userId, string subject, string category)
        {
            await using var dbScope = await CreateDbScopeAsync();
            return await PrerequisiteKnowledgeGraph.TraceWeakPrerequisitesAsync(userId, subject, category, dbScope.Context);
        }

        private async Task<string> CallLlmDiagnosisAdviceAsync(User user, EvolutionDiagnosisReportDto report, List<KnowledgePointMasteryDto> practiced)
        {
            var systemPrompt = "你是一位极具洞察力与激励性的名师 AI 顾问。请根据学员的答题掌握度数据，撰写一份 200 字左右的【学情演进与破壁诊断报告】。要求文字精炼、正向鼓励、提出极具针对性的突破方案。支持 Markdown 排版。";

            var userPrompt = $"学员姓名：{user.Username}\n年级：{user.Grade}\n综合正确率：{report.OverallMasteryRate}%\n星级评估：{report.StarRating} 星\n优势掌握考点：{string.Join(", ", report.TopMasteredCategories)}\n待突破弱项考点：{string.Join(", ", report.TopWeakCategories)}\n错因归集：{string.Join(", ", report.ErrorReasonBreakdown.Select(kv => $"{kv.Key}:{kv.Value}个"))}";

            var modelName = string.IsNullOrWhiteSpace(user.LlmModelName) ? "gpt-4o-mini" : user.LlmModelName;
            var requestBody = new
            {
                model = modelName,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.5
            };

            var raw = string.IsNullOrWhiteSpace(user.LlmBaseUrl) ? "https://generativelanguage.googleapis.com/v1beta/openai/" : user.LlmBaseUrl.TrimEnd('/');
            if (raw.EndsWith("/chat/completions")) raw = raw.Substring(0, raw.Length - "/chat/completions".Length);
            var targetUrl = $"{raw.TrimEnd('/')}/chat/completions";

            var request = new HttpRequestMessage(HttpMethod.Post, targetUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.LlmApiKey.Trim());
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var jsonStr = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonStr);

            int promptTokens = 0, completionTokens = 0, totalTokens = 0;
            if (doc.RootElement.TryGetProperty("usage", out var usageEl))
            {
                promptTokens = usageEl.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                completionTokens = usageEl.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                totalTokens = usageEl.TryGetProperty("total_tokens", out var tt) ? tt.GetInt32() : 0;
            }

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content").GetString() ?? GenerateFallbackAdvice(user.Username, report.OverallMasteryRate, report.TopMasteredCategories, report.TopWeakCategories);

            if (promptTokens == 0) promptTokens = Math.Max(10, (systemPrompt.Length + userPrompt.Length) / 2);
            if (completionTokens == 0) completionTokens = Math.Max(10, content.Length / 2);
            if (totalTokens == 0) totalTokens = promptTokens + completionTokens;

            try
            {
                var log = new LlmGenerationLog
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = null,
                    ModelName = modelName,
                    Subject = "AI学情诊断",
                    Category = "认知报告评语",
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    GeneratedAt = DateTime.Now
                };

                if (_dbContextFactory != null)
                {
                    await using var logCtx = await _dbContextFactory.CreateDbContextAsync();
                    logCtx.LlmGenerationLogs.Add(log);
                    await logCtx.SaveChangesAsync();
                }
                else
                {
                    _dbContext.LlmGenerationLogs.Add(log);
                    await _dbContext.SaveChangesAsync();
                }
            }
            catch
            {
                // 容错：Token 审计失败不阻断报告生成
            }

            return content;
        }

        private string GenerateFallbackAdvice(string username, double accuracy, List<string> mastered, List<string> weak)
        {
            var masteredStr = mastered.Count > 0 ? string.Join("、", mastered) : "基础常识";
            var weakStr = weak.Count > 0 ? string.Join("、", weak) : "综合应用";

            return $"🌟 **{username} 的 AI 导师学情演进诊断**：\n\n" +
                $"你在【{masteredStr}】等板块表现抢眼，综合掌握度达到 **{accuracy:F1}%**！\n" +
                $"🎯 **演进突破方向**：建议近期重点攻坚【{weakStr}】，利用【错题净化副本】与 AI 导师变式训练进行靶向破壁。坚持每日刷题打卡，你的解题能力必将实现跨越式提升！";
        }
    }
}
