using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class ErrorBookService : IErrorBookService
    {
        private readonly AppDbContext _context;
        private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;
        private readonly IGamificationService _gamificationService;
        private readonly IUserSessionService _userSessionService;

        public ErrorBookService(
            AppDbContext context,
            IGamificationService gamificationService,
            IUserSessionService userSessionService,
            IDbContextFactory<AppDbContext>? dbContextFactory = null)
        {
            _context = context;
            _dbContextFactory = dbContextFactory;
            _gamificationService = gamificationService;
            _userSessionService = userSessionService;
        }

        private ValueTask<AsyncDbScope> CreateDbScopeAsync()
        {
            return AsyncDbScope.CreateAsync(_dbContextFactory, _context);
        }

        public async Task<List<ErrorItem>> GetUnmasteredErrorsAsync(Guid? targetUserId = null, Guid? requestorUserId = null)
        {
            var callerId = requestorUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            var effectiveTargetId = targetUserId ?? callerId;

            if (!effectiveTargetId.HasValue || effectiveTargetId.Value == Guid.Empty)
            {
                return new List<ErrorItem>();
            }

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            // 防越权 (IDOR) 鉴权：若查询目标非本人，必须验证调用者权限 (管理员/教师/合法绑定家长)
            if (callerId.HasValue && callerId.Value != effectiveTargetId.Value)
            {
                var isAuthorized = await IsAuthorizedToAccessErrorsAsync(callerId.Value, effectiveTargetId.Value, ctx);
                if (!isAuthorized)
                {
                    return new List<ErrorItem>();
                }
            }

            return await ctx.ErrorItems
                .Include(e => e.Question)
                .Where(e => e.UserId == effectiveTargetId.Value && !e.IsMastered)
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();
        }

        public async Task<List<ErrorItem>> GetMasteredErrorsAsync(Guid? targetUserId = null, Guid? requestorUserId = null)
        {
            var callerId = requestorUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            var effectiveTargetId = targetUserId ?? callerId;

            if (!effectiveTargetId.HasValue || effectiveTargetId.Value == Guid.Empty)
            {
                return new List<ErrorItem>();
            }

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            // 防越权 (IDOR) 鉴权：若查询目标非本人，必须验证调用者权限 (管理员/教师/合法绑定家长)
            if (callerId.HasValue && callerId.Value != effectiveTargetId.Value)
            {
                var isAuthorized = await IsAuthorizedToAccessErrorsAsync(callerId.Value, effectiveTargetId.Value, ctx);
                if (!isAuthorized)
                {
                    return new List<ErrorItem>();
                }
            }

            return await ctx.ErrorItems
                .Include(e => e.Question)
                .Where(e => e.UserId == effectiveTargetId.Value && e.IsMastered)
                .OrderByDescending(e => e.LastRevisedAt)
                .ToListAsync();
        }

        public async Task UpdateErrorReasonAsync(Guid errorItemId, string category, Guid? userId = null)
        {
            var targetUserId = userId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var item = await ctx.ErrorItems.FirstOrDefaultAsync(e => e.Id == errorItemId);
            if (item != null)
            {
                // 横向越权防御：若指定了当前用户身份，仅所有者允许修改该错题的错因
                if (targetUserId.HasValue && targetUserId.Value != Guid.Empty && item.UserId != targetUserId.Value)
                {
                    return;
                }

                item.ErrorReasonCategory = category;
                await ctx.SaveChangesAsync();
            }
        }

        public async Task<RewardResult> MarkErrorAsMasteredAsync(Guid errorItemId, Guid? userId = null)
        {
            var callerId = userId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var item = await ctx.ErrorItems.FirstOrDefaultAsync(e => e.Id == errorItemId);
            if (item == null) return new RewardResult();

            // 防越权鉴权校验
            if (callerId.HasValue && callerId.Value != Guid.Empty && item.UserId != callerId.Value)
            {
                bool isAuthorized = await IsAuthorizedToAccessErrorsAsync(callerId.Value, item.UserId, ctx);
                if (!isAuthorized)
                {
                    return new RewardResult();
                }
            }

            return await _gamificationService.ProcessErrorRevisionRewardAsync(errorItemId, item.UserId);
        }

        public async Task<BatchMarkMasteredResult> BatchMarkErrorsAsMasteredAsync(IEnumerable<Guid> errorItemIds, Guid? userId = null)
        {
            var idList = errorItemIds?.Distinct().ToList() ?? new List<Guid>();
            if (idList.Count == 0) return new BatchMarkMasteredResult();

            var callerId = userId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (!callerId.HasValue || callerId.Value == Guid.Empty)
            {
                return new BatchMarkMasteredResult();
            }

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var items = await ctx.ErrorItems
                .Where(e => idList.Contains(e.Id) && !e.IsMastered)
                .ToListAsync();

            if (items.Count == 0) return new BatchMarkMasteredResult();

            var allowedItems = new List<ErrorItem>();
            foreach (var item in items)
            {
                if (item.UserId == callerId.Value)
                {
                    allowedItems.Add(item);
                }
                else if (await IsAuthorizedToAccessErrorsAsync(callerId.Value, item.UserId, ctx))
                {
                    allowedItems.Add(item);
                }
            }

            if (allowedItems.Count == 0) return new BatchMarkMasteredResult();

            int totalExp = 0;
            int totalCoins = 0;

            foreach (var item in allowedItems)
            {
                item.IsMastered = true;
                item.LastRevisedAt = DateTime.Now;
                item.RevisionCount = Math.Max(item.RevisionCount + 1, 3);
                totalExp += 15;
                totalCoins += 5;
            }

            // 按归属学生用户分组累加统计与成就
            var userGroups = allowedItems.GroupBy(i => i.UserId);
            foreach (var group in userGroups)
            {
                var student = await ctx.Users.FirstOrDefaultAsync(u => u.Id == group.Key);
                if (student != null)
                {
                    int grpCount = group.Count();
                    student.ResolvedErrorsCount += grpCount;
                    student.Exp += grpCount * 15;
                    student.Coins += grpCount * 5;
                    student.TotalAnswered += grpCount;
                    student.TotalCorrect += grpCount;

                    var tempReward = new RewardResult { EarnedExp = grpCount * 15, EarnedCoins = grpCount * 5 };
                    GamificationService.CheckAndProcessLevelUp(student, tempReward);

                    if (student.ResolvedErrorsCount >= 5)
                    {
                        await _gamificationService.UnlockAchievementAsync("ERROR_KILLER_5", student.Id, ctx);
                    }
                }
            }

            await ctx.SaveChangesAsync();

            return new BatchMarkMasteredResult
            {
                MarkedCount = allowedItems.Count,
                EarnedExp = totalExp,
                EarnedCoins = totalCoins
            };
        }

        public async Task<bool> DeleteErrorItemAsync(Guid errorItemId, Guid? userId = null)
        {
            var targetUserId = userId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (!targetUserId.HasValue || targetUserId.Value == Guid.Empty)
            {
                return false;
            }

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var item = await ctx.ErrorItems.FirstOrDefaultAsync(e => e.Id == errorItemId && e.UserId == targetUserId.Value);
            if (item == null) return false;

            ctx.ErrorItems.Remove(item);
            await ctx.SaveChangesAsync();
            return true;
        }

        public async Task<int> BatchDeleteErrorItemsAsync(IEnumerable<Guid> errorItemIds, Guid? userId = null)
        {
            var targetUserId = userId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (!targetUserId.HasValue || targetUserId.Value == Guid.Empty)
            {
                return 0;
            }

            var idList = errorItemIds.ToList();
            if (idList.Count == 0) return 0;

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var itemsToDelete = await ctx.ErrorItems
                .Where(e => idList.Contains(e.Id) && e.UserId == targetUserId.Value)
                .ToListAsync();

            if (itemsToDelete.Count == 0) return 0;

            ctx.ErrorItems.RemoveRange(itemsToDelete);
            await ctx.SaveChangesAsync();
            return itemsToDelete.Count;
        }

        public async Task<int> ClearMasteredErrorsAsync(Guid? userId = null)
        {
            var targetUserId = userId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (!targetUserId.HasValue || targetUserId.Value == Guid.Empty)
            {
                return 0;
            }

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var masteredItems = await ctx.ErrorItems
                .Where(e => e.UserId == targetUserId.Value && e.IsMastered)
                .ToListAsync();

            if (masteredItems.Count == 0) return 0;

            ctx.ErrorItems.RemoveRange(masteredItems);
            await ctx.SaveChangesAsync();
            return masteredItems.Count;
        }

        public async Task<int> GetUnmasteredCountAsync(Guid? userId = null)
        {
            var callerId = _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            var targetUserId = userId ?? callerId;
            if (!targetUserId.HasValue || targetUserId.Value == Guid.Empty) return 0;

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            if (callerId.HasValue && callerId.Value != targetUserId.Value)
            {
                var isAuthorized = await IsAuthorizedToAccessErrorsAsync(callerId.Value, targetUserId.Value, ctx);
                if (!isAuthorized) return 0;
            }

            return await ctx.ErrorItems
                .AsNoTracking()
                .CountAsync(e => e.UserId == targetUserId.Value && !e.IsMastered);
        }

        private async Task<bool> IsAuthorizedToAccessErrorsAsync(Guid callerUserId, Guid studentUserId, AppDbContext? db = null)
        {
            var targetDb = db ?? _context;
            // 1. 检查 caller 用户角色：超级管理员或教师拥有全域或班级审阅权限
            var callerUser = await targetDb.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == callerUserId);
            if (callerUser != null && (callerUser.Role == UserRole.SuperAdmin || callerUser.Role == UserRole.Teacher))
            {
                return true;
            }

            // 2. 检查家长与子女绑定关系：caller 必须在 StudentParentBindings 中合法绑定到该 student
            var hasBinding = await targetDb.StudentParentBindings
                .AsNoTracking()
                .AnyAsync(b => b.ParentUserId == callerUserId && b.StudentUserId == studentUserId);

            return hasBinding;
        }

        public TimeSpan GetRecommendedReviewInterval(int revisionCount)
        {
            if (revisionCount <= 0) return TimeSpan.FromHours(12);
            return revisionCount switch
            {
                1 => TimeSpan.FromHours(36),
                2 => TimeSpan.FromHours(84),
                3 => TimeSpan.FromHours(156),
                _ => TimeSpan.FromHours(336)
            };
        }

        public bool IsReviewDue(ErrorItem item, DateTime? asOf = null)
        {
            if (item == null || item.IsMastered) return false;
            var now = asOf ?? DateTime.Now;
            var baseTime = item.LastRevisedAt ?? item.CreatedAt;
            if (baseTime == default) baseTime = now;
            var interval = GetRecommendedReviewInterval(item.RevisionCount);
            return (now - baseTime) >= interval;
        }

        public double CalculateRetentionHealthScore(ErrorItem item, DateTime? asOf = null)
        {
            if (item == null) return 0.0;
            if (item.IsMastered) return 100.0;

            var now = asOf ?? DateTime.Now;
            var baseTime = item.LastRevisedAt ?? item.CreatedAt;
            if (baseTime == default) baseTime = now;
            var elapsedHours = Math.Max(0, (now - baseTime).TotalHours);
            var intervalHours = Math.Max(1.0, GetRecommendedReviewInterval(item.RevisionCount).TotalHours);

            // 艾宾浩斯指数留存模型: R = e^(-0.693 * t / interval) * 100%
            double ratio = elapsedHours / intervalHours;
            double retention = Math.Exp(-0.693 * ratio) * 100.0;
            return Math.Clamp(Math.Round(retention, 1), 0.0, 100.0);
        }

        public async Task<List<ErrorItem>> GetEbbinghausReviewQueueAsync(Guid userId, string? subject = null, int count = 10)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var query = ctx.ErrorItems
                .Include(e => e.Question)
                .Where(e => e.UserId == userId && !e.IsMastered);

            if (!string.IsNullOrWhiteSpace(subject) && subject != "全部学科" && subject != "全部")
            {
                query = query.Where(e => e.Question != null && e.Question.Subject == subject);
            }

            var allUnmastered = await query.ToListAsync();
            var now = DateTime.Now;

            var queue = allUnmastered
                .Where(e => IsReviewDue(e, now))
                .OrderBy(e => CalculateRetentionHealthScore(e, now)) // 记忆留存率最低、逾期最久的最优先排队
                .Take(count)
                .ToList();

            return queue;
        }

        public async Task<RewardResult> ReviseErrorAsync(Guid errorItemId, bool isCorrect, Guid? userId = null, bool debounce = false)
        {
            var callerId = userId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var item = await ctx.ErrorItems.Include(e => e.Question).FirstOrDefaultAsync(e => e.Id == errorItemId);
            if (item == null)
            {
                return new RewardResult();
            }

            if (callerId.HasValue && callerId.Value != Guid.Empty && item.UserId != callerId.Value)
            {
                return new RewardResult();
            }

            var studentUser = await ctx.Users.FirstOrDefaultAsync(u => u.Id == item.UserId);

            if (isCorrect)
            {
                var now = DateTime.Now;
                // 防刷与防抖保护：开启 debounce 时，2秒内重复提交不重复累加阶段阶梯
                bool isRapidDuplicate = debounce && item.LastRevisedAt.HasValue && (now - item.LastRevisedAt.Value).TotalSeconds < 2;
                item.LastRevisedAt = now;

                if (!isRapidDuplicate)
                {
                    // 若复练累计达到 3 次（当前为第 2 次之后再次答对），交由 ProcessErrorRevisionRewardAsync 标记攻克掌握并派发终极奖励
                    if (item.RevisionCount >= 2)
                    {
                        await ctx.SaveChangesAsync();
                        return await _gamificationService.ProcessErrorRevisionRewardAsync(errorItemId, item.UserId);
                    }

                    // 尚未达到 3 次：作为抗遗忘强化记忆过程，递增复习次数并保留未掌握状态，给予阶段性复习奖励 (15 EXP, 5 Coins)
                    item.RevisionCount++;
                }

                var result = new RewardResult
                {
                    EarnedExp = 15,
                    EarnedCoins = 5
                };

                if (studentUser != null)
                {
                    bool hasExpBoost = studentUser.ExpBoostUntil.HasValue && studentUser.ExpBoostUntil.Value > DateTime.Now;
                    bool hasGoldBoost = studentUser.GoldBoostUntil.HasValue && studentUser.GoldBoostUntil.Value > DateTime.Now;

                    int exp = 15;
                    int coins = 5;
                    if (hasExpBoost) exp = (int)Math.Round(exp * 1.5);
                    if (hasGoldBoost) coins = (int)Math.Round(coins * 1.5);

                    studentUser.Exp += exp;
                    studentUser.Coins += coins;
                    studentUser.TotalAnswered++;
                    studentUser.TotalCorrect++;

                    result.EarnedExp = exp;
                    result.EarnedCoins = coins;
                    result.ExpBoostActive = hasExpBoost;
                    result.GoldBoostActive = hasGoldBoost;

                    GamificationService.CheckAndProcessLevelUp(studentUser, result);
                }

                await ctx.SaveChangesAsync();
                return result;
            }
            else
            {
                // 答错则重置复习基础时间，并在有阶段时适度回退
                item.LastRevisedAt = DateTime.Now;
                if (item.RevisionCount > 0)
                {
                    item.RevisionCount--;
                }

                var result = new RewardResult
                {
                    EarnedExp = 5,
                    EarnedCoins = 2
                };

                if (studentUser != null)
                {
                    studentUser.Exp += 5;
                    studentUser.Coins += 2;
                    studentUser.TotalAnswered++;

                    GamificationService.CheckAndProcessLevelUp(studentUser, result);
                }

                await ctx.SaveChangesAsync();
                return result;
            }
        }

        public async Task<ErrorItem?> GetErrorItemByQuestionAsync(Guid userId, Guid questionId)
        {
            if (userId == Guid.Empty || questionId == Guid.Empty) return null;

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            return await ctx.ErrorItems
                .Include(e => e.Question)
                .FirstOrDefaultAsync(e => e.UserId == userId && e.QuestionId == questionId);
        }
    }
}
