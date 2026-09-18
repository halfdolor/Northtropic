using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class StudyPlanService : IStudyPlanService
    {
        private readonly AppDbContext _dbContext;
        private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;
        private readonly IStudentEvolutionService _studentEvolutionService;

        public StudyPlanService(
            AppDbContext dbContext,
            IStudentEvolutionService studentEvolutionService,
            IDbContextFactory<AppDbContext>? dbContextFactory = null)
        {
            _dbContext = dbContext;
            _studentEvolutionService = studentEvolutionService;
            _dbContextFactory = dbContextFactory;
        }

        private ValueTask<AsyncDbScope> CreateDbScopeAsync()
        {
            return AsyncDbScope.CreateAsync(_dbContextFactory, _dbContext);
        }

        public async Task<StudyPlan?> GetActivePlanAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            return await ctx.StudyPlans
                .Include(p => p.Tasks)
                .Where(p => p.UserId == userId && p.Status == StudyPlanStatus.Active)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();
        }

        public async Task<StudyPlan> EnsureActivePlanAsync(Guid userId, string? preferredSubject = null)
        {
            var activePlan = await GetActivePlanAsync(userId);
            if (activePlan != null)
            {
                // 检查是否逾期，若逾期超过 3 天自动转为 Expired 并重新生成
                if (DateTime.Now > activePlan.TargetEndDate.AddDays(3))
                {
                    await using var dbScope = await CreateDbScopeAsync();
                    var ctx = dbScope.Context;
                    var tracked = await ctx.StudyPlans.FindAsync(activePlan.Id);
                    if (tracked != null)
                    {
                        tracked.Status = StudyPlanStatus.Expired;
                        tracked.UpdatedAt = DateTime.Now;
                        await ctx.SaveChangesAsync();
                    }
                }
                else
                {
                    return activePlan;
                }
            }

            return await GenerateAdaptivePlanAsync(userId, preferredSubject);
        }

        public async Task<StudyPlan> GenerateAdaptivePlanAsync(Guid userId, string? preferredSubject = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            // 1. 将现有的 Active 计划标记为 Adjusted
            var oldPlans = await ctx.StudyPlans
                .Where(p => p.UserId == userId && p.Status == StudyPlanStatus.Active)
                .ToListAsync();
            foreach (var old in oldPlans)
            {
                old.Status = StudyPlanStatus.Adjusted;
                old.UpdatedAt = DateTime.Now;
            }

            var user = await ctx.Users.FindAsync(userId);
            string userGrade = user?.Grade ?? "初中二年级";

            // 2. 获取学情画像与薄弱考点
            var masteryList = await _studentEvolutionService.GetMasteryOverviewAsync(userId, preferredSubject);
            var spacedNodes = await _studentEvolutionService.GetPendingSpacedReviewNodesAsync(userId);
            var unmasteredErrors = await ctx.ErrorItems
                .Include(e => e.Question)
                .Where(e => e.UserId == userId && !e.IsMastered && e.Question != null)
                .ToListAsync();

            // 3. 构建全新自适应 7 天计划
            var plan = new StudyPlan
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Title = string.IsNullOrWhiteSpace(preferredSubject) || preferredSubject == "全部分科"
                    ? $"🌟 {userGrade} AI 自适应 7 天提分攻坚计划"
                    : $"🎯 {preferredSubject} 专项能力靶向突破 7 天计划",
                Status = StudyPlanStatus.Active,
                StartDate = DateTime.Now,
                TargetEndDate = DateTime.Now.AddDays(7),
                DailyTargetQuestions = 15,
                TargetAccuracyRate = 85.0,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                Tasks = new List<StudyPlanTask>()
            };

            // 挑选弱项考点 (MasteryScore < 70 且已练过的优先，否则未练过的)
            var weakCategories = masteryList
                .Where(m => m.TotalAnswered > 0 && m.MasteryScore < 70)
                .OrderBy(m => m.MasteryScore)
                .Take(2)
                .ToList();

            if (weakCategories.Count == 0)
            {
                weakCategories = masteryList.OrderBy(m => m.MasteryScore).Take(2).ToList();
            }

            // 子任务 1：核心薄弱考点精准攻坚
            if (weakCategories.Count > 0)
            {
                var primaryWeak = weakCategories[0];
                plan.Tasks.Add(new StudyPlanTask
                {
                    Id = Guid.NewGuid(),
                    StudyPlanId = plan.Id,
                    Title = $"攻坚薄弱考点：【{primaryWeak.Category}】",
                    Subject = primaryWeak.Subject,
                    Category = primaryWeak.Category,
                    TaskType = StudyPlanTaskType.WeaknessBreakthrough,
                    TargetCount = 10,
                    CompletedCount = 0,
                    TargetAccuracy = 80.0,
                    CreatedAt = DateTime.Now
                });
            }

            // 子任务 2：次弱考点攻坚或高频考点强化
            if (weakCategories.Count > 1)
            {
                var secondaryWeak = weakCategories[1];
                plan.Tasks.Add(new StudyPlanTask
                {
                    Id = Guid.NewGuid(),
                    StudyPlanId = plan.Id,
                    Title = $"突破攻坚考点：【{secondaryWeak.Category}】",
                    Subject = secondaryWeak.Subject,
                    Category = secondaryWeak.Category,
                    TaskType = StudyPlanTaskType.WeaknessBreakthrough,
                    TargetCount = 10,
                    CompletedCount = 0,
                    TargetAccuracy = 80.0,
                    CreatedAt = DateTime.Now
                });
            }

            // 子任务 3：艾宾浩斯抗遗忘记忆唤醒
            if (spacedNodes.Count > 0)
            {
                var urgentSpaced = spacedNodes.First();
                plan.Tasks.Add(new StudyPlanTask
                {
                    Id = Guid.NewGuid(),
                    StudyPlanId = plan.Id,
                    Title = $"艾宾浩斯抗遗忘唤醒：【{urgentSpaced.Category}】",
                    Subject = urgentSpaced.Subject,
                    Category = urgentSpaced.Category,
                    TaskType = StudyPlanTaskType.SpacedRepetition,
                    TargetCount = 8,
                    CompletedCount = 0,
                    TargetAccuracy = 85.0,
                    CreatedAt = DateTime.Now
                });
            }
            else
            {
                // 若无紧急遗忘节点，增加错题净化任务
                var errorGroup = unmasteredErrors
                    .GroupBy(e => e.Question!.Category)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault();

                string errSubject = errorGroup?.FirstOrDefault()?.Question?.Subject ?? (weakCategories.FirstOrDefault()?.Subject ?? "数学");
                string errCategory = errorGroup?.Key ?? (weakCategories.FirstOrDefault()?.Category ?? "基础综合");

                plan.Tasks.Add(new StudyPlanTask
                {
                    Id = Guid.NewGuid(),
                    StudyPlanId = plan.Id,
                    Title = $"错题变式重练净化：【{errCategory}】",
                    Subject = errSubject,
                    Category = errCategory,
                    TaskType = StudyPlanTaskType.ErrorPurification,
                    TargetCount = 8,
                    CompletedCount = 0,
                    TargetAccuracy = 85.0,
                    CreatedAt = DateTime.Now
                });
            }

            // 子任务 4：全真综合实战演练
            var mainSub = preferredSubject ?? (weakCategories.FirstOrDefault()?.Subject ?? "数学");
            plan.Tasks.Add(new StudyPlanTask
            {
                Id = Guid.NewGuid(),
                StudyPlanId = plan.Id,
                Title = $"中考仿真全卷综合冲刺：【{mainSub}】",
                Subject = mainSub,
                Category = "全部分类",
                TaskType = StudyPlanTaskType.ComprehensiveSprint,
                TargetCount = 12,
                CompletedCount = 0,
                TargetAccuracy = 85.0,
                CreatedAt = DateTime.Now
            });

            plan.PlanGoalSummary = $"本周期聚焦攻坚 {string.Join("与", weakCategories.Select(w => $"【{w.Category}】"))}，强化抗遗忘复习，目标综合正确率达 {plan.TargetAccuracyRate:F0}%！";
            plan.LatestSupervisionMessage = $"🤖 小北助手已为你生成专属提分计划，涵盖 {plan.Tasks.Count} 项重点突破任务。加油冲刺，稳步达成目标！";
            plan.LastSupervisedAt = DateTime.Now;

            ctx.StudyPlans.Add(plan);
            await ctx.SaveChangesAsync();

            return plan;
        }

        public async Task<(bool Updated, string? FeedbackMessage)> RecordPracticeProgressAsync(Guid userId, string subject, string category, bool isCorrect)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var plan = await ctx.StudyPlans
                .Include(p => p.Tasks)
                .Where(p => p.UserId == userId && p.Status == StudyPlanStatus.Active)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (plan == null || plan.Tasks == null || plan.Tasks.Count == 0)
            {
                return (false, null);
            }

            // 查找优先匹配的子任务：
            // 1. 精确匹配 Subject + Category 且未完成
            // 2. 匹配 Subject 且 Category == "全部分类" 且未完成
            // 3. 任意未完成任务
            var targetTask = plan.Tasks
                .Where(t => !t.IsCompleted && t.Subject == subject && t.Category == category)
                .FirstOrDefault();

            if (targetTask == null)
            {
                targetTask = plan.Tasks
                    .Where(t => !t.IsCompleted && t.Subject == subject && (t.Category == "全部分类" || t.Category == "全真综合" || t.TaskType == StudyPlanTaskType.ComprehensiveSprint))
                    .FirstOrDefault();
            }

            if (targetTask == null)
            {
                targetTask = plan.Tasks.Where(t => !t.IsCompleted).FirstOrDefault();
            }

            if (targetTask == null)
            {
                // 任务全完成
                if (plan.Status == StudyPlanStatus.Active)
                {
                    plan.Status = StudyPlanStatus.Completed;
                    plan.CompletedDate = DateTime.Now;
                    plan.UpdatedAt = DateTime.Now;
                    await ctx.SaveChangesAsync();
                }
                return (true, "🎉 恭喜！你已圆满达成当前学习计划的全部任务指标！");
            }

            targetTask.CompletedCount++;
            if (targetTask.CompletedCount >= targetTask.TargetCount)
            {
                targetTask.IsCompleted = true;
                targetTask.CompletedAt = DateTime.Now;
            }

            plan.UpdatedAt = DateTime.Now;

            // 检查整个计划是否因此达成
            bool allFinished = plan.Tasks.All(t => t.IsCompleted);
            if (allFinished)
            {
                plan.Status = StudyPlanStatus.Completed;
                plan.CompletedDate = DateTime.Now;
            }

            await ctx.SaveChangesAsync();

            string feedback = targetTask.IsCompleted
                ? $"🎉 恭喜达成计划任务【{targetTask.Title}】({targetTask.CompletedCount}/{targetTask.TargetCount})！"
                : $"🎯 学习计划推进：【{targetTask.Title}】已完成 {targetTask.CompletedCount}/{targetTask.TargetCount} 题！";

            return (true, feedback);
        }

        public async Task<(string NudgeMessage, string Severity, Guid? UrgentTaskId)> SuperviseAndNudgeAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var plan = await ctx.StudyPlans
                .Include(p => p.Tasks)
                .Where(p => p.UserId == userId && p.Status == StudyPlanStatus.Active)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            var user = await ctx.Users.FindAsync(userId);
            if (user == null)
            {
                return ("欢迎开启今日学习！", "Info", null);
            }

            if (plan == null)
            {
                return ($"🌟 {user.Username}，你当前还没有制定学习计划，点击下方可由超级助手自适应定制一份专属提分计划！", "Info", null);
            }

            var unfinishedTasks = plan.Tasks.Where(t => !t.IsCompleted).ToList();
            if (unfinishedTasks.Count == 0 || plan.IsFullyCompleted)
            {
                return ($"🏆 太出色了，{user.Username}！本周学习计划已 100% 达成！建议进行持续迭代闭环复盘，沉淀经验并开启下一代计划！", "Success", null);
            }

            var daysPassed = (DateTime.Now.Date - plan.StartDate.Date).TotalDays;
            double progress = plan.ProgressPercent;
            var urgentTask = unfinishedTasks.FirstOrDefault();

            string message;
            string severity;

            // 遗忘衰退优先提醒
            var spacedTask = unfinishedTasks.FirstOrDefault(t => t.TaskType == StudyPlanTaskType.SpacedRepetition);
            if (spacedTask != null)
            {
                message = $"⚠️ 【抗遗忘预警】考点【{spacedTask.Category}】记忆处于临界衰减期，建议今天优先完成 {spacedTask.TargetCount - spacedTask.CompletedCount} 道复习唤醒题！";
                severity = "Warning";
                urgentTask = spacedTask;
            }
            else if (daysPassed >= 3 && progress < 30.0)
            {
                message = $"⏳ 【进度稍有滞后】计划已过 {daysPassed:F0} 天，整体进度为 {progress:F0}%。建议抽空突破【{urgentTask?.Title}】，每天坚持 15 题即可按期达标！";
                severity = "Warning";
            }
            else if (user.TodayAnsweredCount == 0)
            {
                message = $"☀️ 【今日打卡督促】今日尚未开始做题，距每日目标还差 {user.DailyTargetQuestions} 题，小步快跑，现在就启动热身吧！";
                severity = "Info";
            }
            else if (progress >= 70.0)
            {
                message = $"🔥 【冲刺在即】计划已完成 {progress:F0}%，冲刺势头非常棒！继续攻坚剩余 {unfinishedTasks.Count} 个任务，冲刺达标勋章！";
                severity = "Success";
            }
            else
            {
                message = $"💪 【保持节奏】当前计划进度 {progress:F0}%，坚持每日按计划练习，稳步攻克薄弱盲区！";
                severity = "Info";
            }

            plan.SupervisionNudgeCount++;
            plan.LastSupervisedAt = DateTime.Now;
            plan.LatestSupervisionMessage = message;
            await ctx.SaveChangesAsync();

            return (message, severity, urgentTask?.Id);
        }

        public async Task<ClosedLoopDiagnosisResultDto> EvaluateClosedLoopProgressAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            var now = DateTime.Now;
            var sevenDaysAgo = now.AddDays(-7);
            var fourteenDaysAgo = now.AddDays(-14);

            // 1. 获取近 7 天与历史基准的答题数据
            var recentRecords = await ctx.PracticeRecords
                .AsNoTracking()
                .Include(r => r.Question)
                .Where(r => r.UserId == userId && r.AnsweredAt >= sevenDaysAgo)
                .ToListAsync();

            var prevRecords = await ctx.PracticeRecords
                .AsNoTracking()
                .Include(r => r.Question)
                .Where(r => r.UserId == userId && r.AnsweredAt >= fourteenDaysAgo && r.AnsweredAt < sevenDaysAgo)
                .ToListAsync();

            // 若 prevRecords 记录少于 3 条，尝试拉取更早的历史总记录作为对照均线
            double prevAccuracy;
            double prevSpeed;
            if (prevRecords.Count >= 3)
            {
                prevAccuracy = (double)prevRecords.Count(r => r.IsCorrect) / prevRecords.Count * 100.0;
                prevSpeed = prevRecords.Average(r => r.TimeTakenSeconds);
            }
            else
            {
                var olderRecords = await ctx.PracticeRecords
                    .AsNoTracking()
                    .Where(r => r.UserId == userId && r.AnsweredAt < sevenDaysAgo)
                    .Select(r => new { r.IsCorrect, r.TimeTakenSeconds })
                    .ToListAsync();

                if (olderRecords.Count > 0)
                {
                    prevAccuracy = (double)olderRecords.Count(r => r.IsCorrect) / olderRecords.Count * 100.0;
                    prevSpeed = olderRecords.Average(r => r.TimeTakenSeconds);
                }
                else
                {
                    prevAccuracy = recentRecords.Count > 0 ? (double)recentRecords.Count(r => r.IsCorrect) / recentRecords.Count * 100.0 : 70.0;
                    prevSpeed = recentRecords.Count > 0 ? recentRecords.Average(r => r.TimeTakenSeconds) : 45.0;
                }
            }

            double recentAccuracy = recentRecords.Count > 0
                ? (double)recentRecords.Count(r => r.IsCorrect) / recentRecords.Count * 100.0
                : prevAccuracy;

            double recentSpeed = recentRecords.Count > 0
                ? recentRecords.Average(r => r.TimeTakenSeconds)
                : prevSpeed;

            double accuracyDelta = Math.Round(recentAccuracy - prevAccuracy, 1);
            double speedImprovement = Math.Round(Math.Max(-20.0, Math.Min(60.0, prevSpeed - recentSpeed)), 1);

            // 2. 统计错题净化成效与薄弱点突破
            var purifiedRecentErrors = await ctx.ErrorItems
                .AsNoTracking()
                .Where(e => e.UserId == userId && e.IsMastered && e.LastRevisedAt >= sevenDaysAgo)
                .CountAsync();

            var totalUnmasteredErrors = await ctx.ErrorItems
                .AsNoTracking()
                .Where(e => e.UserId == userId && !e.IsMastered)
                .CountAsync();

            // 真实考点突破：近期有作答且正确率超过 75% 的考点数
            var recentCategories = recentRecords
                .Where(r => r.Question != null)
                .GroupBy(r => r.Question!.Category)
                .Select(g => new
                {
                    Category = g.Key,
                    Total = g.Count(),
                    Correct = g.Count(r => r.IsCorrect),
                    Acc = (double)g.Count(r => r.IsCorrect) / g.Count() * 100.0
                })
                .Where(c => c.Total >= 3 && c.Acc >= 75.0)
                .ToList();

            int weaknessOvercomeCount = recentCategories.Count;

            // 3. 判定学生是否进步 (ProgressEvaluationStatus)
            ProgressEvaluationStatus status;
            bool hasImproved;

            if (recentRecords.Count >= 5 && (accuracyDelta >= 5.0 || weaknessOvercomeCount >= 2 || (accuracyDelta >= 0 && purifiedRecentErrors >= 3)))
            {
                status = ProgressEvaluationStatus.Progressing;
                hasImproved = true;
            }
            else if (recentRecords.Count >= 3 && (accuracyDelta >= 0.0 || speedImprovement >= 3.0 || purifiedRecentErrors >= 1))
            {
                status = ProgressEvaluationStatus.SteadilyImproving;
                hasImproved = true;
            }
            else if (accuracyDelta < -4.0 && recentRecords.Count >= 5)
            {
                status = ProgressEvaluationStatus.Regressing;
                hasImproved = false;
            }
            else
            {
                status = ProgressEvaluationStatus.Stagnant;
                hasImproved = false;
            }

            var result = new ClosedLoopDiagnosisResultDto
            {
                UserId = userId,
                AnalyzedAt = DateTime.Now,
                EvaluationStatus = status,
                HasImproved = hasImproved,
                AccuracyDelta = accuracyDelta,
                SpeedDeltaSeconds = speedImprovement,
                WeaknessOvercomeCount = weaknessOvercomeCount,
                PurifiedErrorsCount = purifiedRecentErrors,
                PotentialScore = Math.Clamp((int)Math.Round(recentAccuracy * 0.7 + (weaknessOvercomeCount * 5) + (purifiedRecentErrors * 3)), 40, 99)
            };

            switch (status)
            {
                case ProgressEvaluationStatus.Progressing:
                    result.StatusBadgeTitle = "🎉 显著飞跃";
                    result.StatusBadgeColor = "Success";
                    break;
                case ProgressEvaluationStatus.SteadilyImproving:
                    result.StatusBadgeTitle = "⚡ 稳步提升";
                    result.StatusBadgeColor = "Info";
                    break;
                case ProgressEvaluationStatus.Stagnant:
                    result.StatusBadgeTitle = "⏳ 遭遇瓶颈";
                    result.StatusBadgeColor = "Warning";
                    break;
                case ProgressEvaluationStatus.Regressing:
                default:
                    result.StatusBadgeTitle = "⚠️ 出现下滑";
                    result.StatusBadgeColor = "Error";
                    break;
            }

            // 4. 深度归因剖析：如果没进步，是什么原因？(Root Cause Analysis)
            var failureReasons = new List<ProgressFailureReason>();
            var failureDetails = new List<string>();
            var correctiveActions = new List<string>();

            if (recentRecords.Count < 10)
            {
                failureReasons.Add(ProgressFailureReason.InsufficientVolume);
                failureDetails.Add("• **刷题刺激量不足**：近 7 天累计作答仅 " + recentRecords.Count + " 题，未达到巩固记忆与熟练度所需的最低刷题阈值。");
                correctiveActions.Add("• 建议每天保持 15 道题的微步节奏，优先通过每日签到打卡重新建立解题手感。");
            }

            // 检查艾宾浩斯临界
            var spacedNodes = await _studentEvolutionService.GetPendingSpacedReviewNodesAsync(userId);
            if (spacedNodes.Count >= 2)
            {
                failureReasons.Add(ProgressFailureReason.NeglectedSpacedRepetition);
                failureDetails.Add($"• **艾宾浩斯记忆衰减临界未及时复习**：当前有 {spacedNodes.Count} 个考点记忆保留率已跌破 70%，旧错题反复丢分导致净正确率被拉低。");
                correctiveActions.Add("• 优先进入【记忆唤醒副本】复习衰减考点，阻止知识滑坡。");
            }

            // 检查解题速度与粗心失误 (若错因多为审题不清或平均速度过快但错误高)
            var carelessErrors = await ctx.ErrorItems
                .AsNoTracking()
                .Where(e => e.UserId == userId && (e.ErrorReasonCategory.Contains("审题") || e.ErrorReasonCategory.Contains("粗心") || e.ErrorReasonCategory.Contains("计算")))
                .CountAsync();

            if (carelessErrors >= 3 || (recentSpeed < 20.0 && recentAccuracy < 70.0))
            {
                failureReasons.Add(ProgressFailureReason.CarelessnessAndSpeed);
                failureDetails.Add("• **审题过急与粗心失误率过高**：做题时审题停留时长偏短，非受迫性概念误读或计算失误占比较大。");
                correctiveActions.Add("• 启用【草稿纸步骤核算习惯】，强制审题至少读题 8 秒，勾画关键词后再作答。");
            }

            // 检查前置概念断层
            var weakCategories = await ctx.ErrorItems
                .AsNoTracking()
                .Include(e => e.Question)
                .Where(e => e.UserId == userId && !e.IsMastered && e.Question != null)
                .Select(e => new { e.Question!.Subject, e.Question!.Category })
                .Distinct()
                .Take(2)
                .ToListAsync();

            foreach (var wc in weakCategories)
            {
                var prereqWarnings = await _studentEvolutionService.TraceWeakPrerequisitesAsync(userId, wc.Subject, wc.Category);
                if (prereqWarnings.Count > 0)
                {
                    failureReasons.Add(ProgressFailureReason.PrerequisiteKnowledgeDeficit);
                    failureDetails.Add($"• **前置知识网断层卡点**：攻坚【{wc.Category}】时受阻，根源在前置概念【{prereqWarnings[0].PrerequisiteCategory}】未彻底掌握。");
                    correctiveActions.Add($"• 暂停直接死磕高难综合题，先回溯巩固前置基础【{prereqWarnings[0].PrerequisiteCategory}】。");
                    break;
                }
            }

            if (!hasImproved && failureReasons.Count == 0)
            {
                failureReasons.Add(ProgressFailureReason.CognitiveOverload);
                failureDetails.Add("• **题目综合难度偏高**：当前练习试题难度梯度过陡，直接冲击高星试题导致思维受阻。");
                correctiveActions.Add("• 将做题难度自适应下调至 Lv.2-3 中档巩固题，以稳固信心为主。");
            }

            result.FailureReasons = failureReasons;
            result.RootCauseDiagnosis = failureDetails.Count > 0
                ? string.Join("\n", failureDetails)
                : "当前处于平稳上升期，未检测到重大阻碍短板。";
            result.CorrectivePrescription = correctiveActions.Count > 0
                ? string.Join("\n", correctiveActions)
                : "继续保持当前节奏，按部就班推进自适应计划。";

            // 5. 深度经验沉淀：如果有进步，积累了什么宝贵经验？(Success Experience Harvest)
            var successExperiences = new List<ProgressSuccessExperience>();
            var successSummaries = new List<string>();

            if (weaknessOvercomeCount > 0)
            {
                successExperiences.Add(ProgressSuccessExperience.MasteredKeyBreakthrough);
                var overcameNames = string.Join("、", recentCategories.Take(2).Select(c => $"【{c.Category}】"));
                successSummaries.Add($"🏆 **靶向攻坚突破策略**：在 {overcameNames} 考点上集中火力专项突破，做题正确率已跃升至 75% 以上，证明集中精力攻坚薄弱考点极其高效！");
            }

            if (purifiedRecentErrors > 0)
            {
                successExperiences.Add(ProgressSuccessExperience.HighAccuracyPurification);
                successSummaries.Add($"🪄 **错题闭环净化红利**：本周期成功消灭净化了 {purifiedRecentErrors} 道历史错题，消灭死角的同时彻底筑牢了知识网底座。");
            }

            if (user?.CurrentStreak >= 3)
            {
                successExperiences.Add(ProgressSuccessExperience.StreakDisciplineBonus);
                successSummaries.Add($"🔥 **连续打卡心流增益**：已保持连续 {user.CurrentStreak} 天坚持打卡，稳定的做题节律极大减少了应试心理波动与生疏感。");
            }

            if (speedImprovement >= 3.0 && recentAccuracy >= 75.0)
            {
                successExperiences.Add(ProgressSuccessExperience.OptimalSpeedAccuracyBalance);
                successSummaries.Add($"⚡ **速度与准度黄金平衡**：解题平均用时缩短 {speedImprovement} 秒且保持高准确率，说明核心题型已实现肌肉记忆般的高效识别。");
            }

            if (successExperiences.Count == 0 && hasImproved)
            {
                successExperiences.Add(ProgressSuccessExperience.SpacedRepetitionDividend);
                successSummaries.Add("🌱 **科学复习增益**：知识点掌握度平稳上升，循序渐进的系统练习正在沉淀为长期核心实力。");
            }

            result.SuccessExperiences = successExperiences;
            result.SuccessExperienceSummary = successSummaries.Count > 0
                ? string.Join("\n", successSummaries)
                : "尚未沉淀明显突破经验，坚持执行计划即可见证成长！";

            // 6. 反哺下一代计划的演进建议
            result.NextEvolutionStrategy = hasImproved
                ? $"🚀 **下一代计划演进导向**：基于本周期在 {string.Join("与", recentCategories.Take(2).Select(c => c.Category))} 的优异战果，下一期学习计划将上述考点降频转为抗遗忘巡检，并将重心顺延推进至更深层综合变式与跨学科大题！"
                : $"🔄 **下一代计划演进导向**：下一期计划将自动启动纠偏重构：适度降低任务难度梯度，插入针对性【前置基础回溯补漏】与【错题变式轻量过关】，帮助你卸下负荷、稳稳重回上升轨道！";

            // 7. 持久化记录
            var insightEntity = new EvolutionClosedLoopInsight
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AnalyzedAt = DateTime.Now,
                EvaluationStatus = status,
                AccuracyDelta = accuracyDelta,
                SpeedDeltaSeconds = speedImprovement,
                WeaknessOvercomeCount = weaknessOvercomeCount,
                PurifiedErrorsCount = purifiedRecentErrors,
                PotentialScore = result.PotentialScore,
                FailureReasonsCsv = string.Join(",", failureReasons.Select(r => (int)r)),
                RootCauseDiagnosis = result.RootCauseDiagnosis,
                CorrectivePrescription = result.CorrectivePrescription,
                SuccessExperiencesCsv = string.Join(",", successExperiences.Select(s => (int)s)),
                SuccessExperienceSummary = result.SuccessExperienceSummary,
                NextEvolutionStrategy = result.NextEvolutionStrategy
            };

            ctx.EvolutionClosedLoopInsights.Add(insightEntity);
            await ctx.SaveChangesAsync();

            return result;
        }

        public async Task<ClosedLoopDiagnosisResultDto?> GetLatestClosedLoopInsightAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var entity = await ctx.EvolutionClosedLoopInsights
                .AsNoTracking()
                .Where(e => e.UserId == userId)
                .OrderByDescending(e => e.AnalyzedAt)
                .FirstOrDefaultAsync();

            if (entity == null) return null;

            var result = new ClosedLoopDiagnosisResultDto
            {
                UserId = entity.UserId,
                AnalyzedAt = entity.AnalyzedAt,
                EvaluationStatus = entity.EvaluationStatus,
                HasImproved = entity.HasImproved,
                AccuracyDelta = entity.AccuracyDelta,
                SpeedDeltaSeconds = entity.SpeedDeltaSeconds,
                WeaknessOvercomeCount = entity.WeaknessOvercomeCount,
                PurifiedErrorsCount = entity.PurifiedErrorsCount,
                PotentialScore = entity.PotentialScore,
                RootCauseDiagnosis = entity.RootCauseDiagnosis,
                CorrectivePrescription = entity.CorrectivePrescription,
                SuccessExperienceSummary = entity.SuccessExperienceSummary,
                NextEvolutionStrategy = entity.NextEvolutionStrategy
            };

            switch (entity.EvaluationStatus)
            {
                case ProgressEvaluationStatus.Progressing:
                    result.StatusBadgeTitle = "🎉 显著飞跃";
                    result.StatusBadgeColor = "Success";
                    break;
                case ProgressEvaluationStatus.SteadilyImproving:
                    result.StatusBadgeTitle = "⚡ 稳步提升";
                    result.StatusBadgeColor = "Info";
                    break;
                case ProgressEvaluationStatus.Stagnant:
                    result.StatusBadgeTitle = "⏳ 遭遇瓶颈";
                    result.StatusBadgeColor = "Warning";
                    break;
                case ProgressEvaluationStatus.Regressing:
                default:
                    result.StatusBadgeTitle = "⚠️ 出现下滑";
                    result.StatusBadgeColor = "Error";
                    break;
            }

            if (!string.IsNullOrWhiteSpace(entity.FailureReasonsCsv))
            {
                var parts = entity.FailureReasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (int.TryParse(p, out int val) && Enum.IsDefined(typeof(ProgressFailureReason), val))
                    {
                        result.FailureReasons.Add((ProgressFailureReason)val);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(entity.SuccessExperiencesCsv))
            {
                var parts = entity.SuccessExperiencesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (int.TryParse(p, out int val) && Enum.IsDefined(typeof(ProgressSuccessExperience), val))
                    {
                        result.SuccessExperiences.Add((ProgressSuccessExperience)val);
                    }
                }
            }

            return result;
        }
    }
}
