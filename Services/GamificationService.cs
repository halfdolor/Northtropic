using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class GamificationService : IGamificationService
    {
        // 架构并发隔离与用户状态原子性保护：防止多请求/多并发作答结算产生脏写与更新丢失 (Lost Update)
        private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _userLocks = new();
        private static SemaphoreSlim GetUserLock(Guid userId) => _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

        private readonly AppDbContext _context;
        private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;
        private readonly IUserSessionService _userSessionService;

        private ValueTask<AsyncDbScope> CreateDbScopeAsync()
        {
            return AsyncDbScope.CreateAsync(_dbContextFactory, _context);
        }

        public GamificationService(AppDbContext context, IUserSessionService userSessionService, IDbContextFactory<AppDbContext>? dbContextFactory = null)
        {
            _context = context;
            _userSessionService = userSessionService;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<User> GetCurrentUserAsync()
        {
            var user = await _userSessionService.GetActiveUserAsync();
            if (user != null) return user;

            // 架构防污染加固：未登录状态下返回独立访客实体，严禁窃取超级管理员账号导致数据污染
            return new User { Id = Guid.Empty, Username = "未登录用户", Role = UserRole.Student };
        }

        public async Task<RewardResult> ProcessAnswerRewardAsync(bool isCorrect, int baseExp, int combo, int difficulty = 3, int timeTakenSeconds = 20, bool isHistoryWrong = false, Guid? targetUserId = null, AppDbContext? existingContext = null)
        {
            var activeUser = await GetCurrentUserAsync();
            var effectiveUserId = targetUserId.HasValue && targetUserId.Value != Guid.Empty
                ? targetUserId.Value
                : activeUser.Id;

            SemaphoreSlim? userLock = null;
            if (effectiveUserId != Guid.Empty)
            {
                userLock = GetUserLock(effectiveUserId);
                await userLock.WaitAsync();
            }

            AsyncDbScope? dbScope = null;
            try
            {
                AppDbContext ctx;
                if (existingContext != null)
                {
                    ctx = existingContext;
                }
                else
                {
                    dbScope = await CreateDbScopeAsync();
                    ctx = dbScope.Value.Context;
                }

                User? user = null;
                if (effectiveUserId != Guid.Empty)
                {
                    user = ctx.Users.Local.FirstOrDefault(u => u.Id == effectiveUserId)
                        ?? await ctx.Users.FirstOrDefaultAsync(u => u.Id == effectiveUserId);
                }
                if (user == null)
                {
                    user = effectiveUserId == Guid.Empty ? activeUser : new User { Id = effectiveUserId, Username = "临时用户", Role = UserRole.Student };
                }

                var result = new RewardResult();
                bool isGuest = user.Id == Guid.Empty;

                // 1. 每日 100 题限额日期重置判定
                var today = DateTime.Today;
                if (user.TodayCountDate.Date != today)
                {
                    user.TodayAnsweredCount = 0;
                    user.TodayCountDate = today;
                }

                // 2. 根据题目难度与随机性设置题目金币分值
                // 难度等级 (1..5) 基础分：1:6金币, 2:10金币, 3:14金币, 4:18金币, 5:22金币
                int baseQuestionCoins = difficulty switch
                {
                    1 => 6,
                    2 => 10,
                    3 => 14,
                    4 => 18,
                    5 => 22,
                    _ => 12
                };

                int questionCoinValue = baseQuestionCoins;

                // 3. 统计题量与限额判断
                user.TotalAnswered++;
                user.TodayAnsweredCount++;

                // 如果今天刷题超过 100 题限额，给予警示标记
                result.TodayAnsweredCount = user.TodayAnsweredCount;
                result.IsLimitExceeded = user.TodayAnsweredCount > 100;

                // 检查 Buff
                bool hasExpBoost = user.ExpBoostUntil.HasValue && user.ExpBoostUntil.Value > DateTime.Now;
                bool hasGoldBoost = user.GoldBoostUntil.HasValue && user.GoldBoostUntil.Value > DateTime.Now;
                result.ExpBoostActive = hasExpBoost;
                result.GoldBoostActive = hasGoldBoost;

                if (isCorrect)
                {
                    user.TotalCorrect++;
                    int newCombo = combo + 1;
                    result.CurrentCombo = newCombo;
                    if (newCombo > user.MaxCombo)
                    {
                        user.MaxCombo = newCombo;
                    }

                    // 计算经验值加成 (连击系数 + 错题强化加成 + Buff)
                    double comboMultiplier = CalculateComboMultiplier(newCombo);
                    int earnedExp = (int)Math.Round(baseExp * comboMultiplier);

                    if (isHistoryWrong)
                    {
                        earnedExp += 10; // 历史错题重做正确额外奖励
                    }

                    if (hasExpBoost)
                    {
                        earnedExp *= 2; // 双倍经验药水
                    }

                    // 计算金币加成 (基础题目分值 * 连击系数 + 速度奖励 + Buff)
                    int earnedCoins = (int)Math.Round(questionCoinValue * comboMultiplier);

                    // 极速作答加成：如果 15 秒内且难度 >= 3，金币 +5
                    if (timeTakenSeconds <= 15 && difficulty >= 3)
                    {
                        earnedCoins += 5;
                    }

                    if (hasGoldBoost)
                    {
                        earnedCoins = (int)Math.Round(earnedCoins * 1.5); // 金币暴击符加成 +50%
                    }

                    user.Exp += earnedExp;
                    user.Coins += earnedCoins;

                    result.EarnedExp = earnedExp;
                    result.EarnedCoins = earnedCoins;

                    // 架构算法修复：连环升级支持（调用统一升级结算算法，处理超额暴击经验）
                    CheckAndProcessLevelUp(user, result);

                    if (!isGuest)
                    {
                        // 自动检测解锁成就
                        if (user.TotalAnswered == 1)
                        {
                            await CheckAndUnlockAchievement(ctx, user.Id, "FIRST_BLOOD", result);
                        }
                        if (newCombo >= 5)
                        {
                            await CheckAndUnlockAchievement(ctx, user.Id, "COMBO_5", result);
                        }
                        if (newCombo >= 10)
                        {
                            await CheckAndUnlockAchievement(ctx, user.Id, "COMBO_10", result);
                        }
                        if (user.TotalAnswered >= 50)
                        {
                            await CheckAndUnlockAchievement(ctx, user.Id, "SCHOLAR_50", result);
                        }
                    }
                }
                else
                {
                    // 检查是否有连击保护符
                    if (user.ComboShieldCount > 0 && combo > 1)
                    {
                        user.ComboShieldCount--;
                        result.ComboShieldUsed = true;
                        result.CurrentCombo = combo; // 保护连击不断！
                    }
                    else
                    {
                        result.CurrentCombo = 0;
                    }

                    int penaltyCoins = questionCoinValue;
                    // 扣除金币 (保底不为负数)
                    user.Coins = Math.Max(0, user.Coins - penaltyCoins);
                    result.EarnedCoins = -penaltyCoins; // 返回负数表达扣金币
                    result.EarnedExp = 0;
                }

                if (!isGuest)
                {
                    await ctx.SaveChangesAsync();
                }
                return result;
            }
            finally
            {
                if (dbScope.HasValue)
                {
                    await dbScope.Value.DisposeAsync();
                }
                userLock?.Release();
            }
        }

        public async Task<RewardResult> ProcessErrorRevisionRewardAsync(Guid errorItemId, Guid? targetUserId = null)
        {
            var result = new RewardResult();
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var errorItem = await ctx.ErrorItems.FirstOrDefaultAsync(e => e.Id == errorItemId);
            if (errorItem == null || errorItem.IsMastered) return result;

            var callerId = targetUserId.HasValue && targetUserId.Value != Guid.Empty
                ? targetUserId.Value
                : (await GetCurrentUserAsync()).Id;

            if (callerId == Guid.Empty) return result;

            var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == callerId);
            if (user == null || user.Id == Guid.Empty) return result;

            // 横向越权防御：验证错题所有权，严禁冒领或篡改他人错题
            if (errorItem.UserId != user.Id)
            {
                return result;
            }

            errorItem.IsMastered = true;
            errorItem.LastRevisedAt = DateTime.Now;
            errorItem.RevisionCount++;

            user.ResolvedErrorsCount++;

            // 检查 Buff
            bool hasExpBoost = user.ExpBoostUntil.HasValue && user.ExpBoostUntil.Value > DateTime.Now;
            bool hasGoldBoost = user.GoldBoostUntil.HasValue && user.GoldBoostUntil.Value > DateTime.Now;

            // 错题净化给予丰厚经验与金币奖励
            int earnedExp = 35;
            int earnedCoins = 25;

            if (hasExpBoost) earnedExp = (int)Math.Round(earnedExp * 1.5);
            if (hasGoldBoost) earnedCoins = (int)Math.Round(earnedCoins * 1.5);

            user.Exp += earnedExp;
            user.Coins += earnedCoins;

            result.EarnedExp = earnedExp;
            result.EarnedCoins = earnedCoins;

            CheckAndProcessLevelUp(user, result);

            // 检测错题克星成就
            if (user.ResolvedErrorsCount >= 5)
            {
                await CheckAndUnlockAchievement(ctx, user.Id, "ERROR_KILLER_5", result);
            }

            await ctx.SaveChangesAsync();
            return result;
        }

        public async Task UpdateStreakAsync(Guid? targetUserId = null)
        {
            var callerId = targetUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (!callerId.HasValue || callerId.Value == Guid.Empty) return;

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == callerId.Value);
            if (user == null || user.Id == Guid.Empty) return;

            var today = DateTime.Today;
            var lastDate = user.LastStudyDate.Date;

            if (user.CurrentStreak == 0)
            {
                user.CurrentStreak = 1;
                user.LastStudyDate = DateTime.Now;
            }
            else if (lastDate == today.AddDays(-1))
            {
                user.CurrentStreak++;
                user.LastStudyDate = DateTime.Now;
            }
            else if (lastDate < today.AddDays(-1))
            {
                user.CurrentStreak = 1;
                user.LastStudyDate = DateTime.Now;
            }
            else
            {
                // 当天重复打卡学习，刷新最后学习时间戳至最新时刻
                user.LastStudyDate = DateTime.Now;
            }

            if (user.CurrentStreak >= 7)
            {
                var dummyResult = new RewardResult();
                await CheckAndUnlockAchievement(ctx, user.Id, "STREAK_7", dummyResult);
            }

            await ctx.SaveChangesAsync();
        }

        public async Task<bool> UnlockAchievementAsync(string achievementCode, Guid? targetUserId = null, AppDbContext? existingContext = null)
        {
            var targetId = targetUserId ?? (await GetCurrentUserAsync())?.Id ?? Guid.Empty;
            if (targetId == Guid.Empty) return false;

            if (existingContext != null)
            {
                var res = new RewardResult();
                return await CheckAndUnlockAchievement(existingContext, targetId, achievementCode, res);
            }

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var result = new RewardResult();
            return await CheckAndUnlockAchievement(ctx, targetId, achievementCode, result);
        }

        public async Task ApplyBuffAsync(string buffType, int durationMinutes)
        {
            var activeUser = await GetCurrentUserAsync();
            if (activeUser.Id == Guid.Empty) return;

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.FindAsync(activeUser.Id);
            if (user == null) return;

            var expiry = DateTime.Now.AddMinutes(durationMinutes);

            if (buffType == "EXP")
            {
                user.ExpBoostUntil = expiry;
            }
            else if (buffType == "GOLD")
            {
                user.GoldBoostUntil = expiry;
            }

            await ctx.SaveChangesAsync();
        }

        public async Task AddComboShieldAsync(int count)
        {
            var activeUser = await GetCurrentUserAsync();
            if (activeUser.Id == Guid.Empty) return;

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.FindAsync(activeUser.Id);
            if (user == null) return;

            user.ComboShieldCount += count;
            await ctx.SaveChangesAsync();
        }

        public async Task SetUserTitleAsync(string title)
        {
            var activeUser = await GetCurrentUserAsync();
            if (activeUser.Id == Guid.Empty) return;

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.FindAsync(activeUser.Id);
            if (user == null) return;

            user.ActiveTitle = title;
            await ctx.SaveChangesAsync();
        }

        public string GetLevelTitle(int level)
        {
            return level switch
            {
                1 => "🌱 启蒙萌新",
                2 => "⚡ 进阶求索者",
                3 => "🔥 破壁刷题狂人",
                4 => "💎 智械算力大师",
                5 => "👑 乾坤全知学神",
                6 => "🌌 维度解构宗师",
                _ => $"🌟 超凡圣哲 Lv.{level}"
            };
        }

        public async Task<(bool Success, string Message)> BuyExpPotionAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.FindAsync(userId);
            if (user == null) return (false, "用户未找到");
            const int cost = 40;
            if (user.Coins < cost) return (false, $"金币不足，需要 {cost} 金币！");

            user.Coins -= cost;
            var baseTime = user.ExpBoostUntil.HasValue && user.ExpBoostUntil.Value > DateTime.Now
                ? user.ExpBoostUntil.Value
                : DateTime.Now;
            user.ExpBoostUntil = baseTime.AddHours(1);
            await ctx.SaveChangesAsync();
            return (true, $"✨ 成功服用【2倍经验药水】！增益已生效至 {user.ExpBoostUntil.Value:HH:mm}，刷题与改错经验翻倍！");
        }

        public async Task<(bool Success, string Message)> BuyGoldPotionAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.FindAsync(userId);
            if (user == null) return (false, "用户未找到");
            const int cost = 40;
            if (user.Coins < cost) return (false, $"金币不足，需要 {cost} 金币！");

            user.Coins -= cost;
            var baseTime = user.GoldBoostUntil.HasValue && user.GoldBoostUntil.Value > DateTime.Now
                ? user.GoldBoostUntil.Value
                : DateTime.Now;
            user.GoldBoostUntil = baseTime.AddHours(1);
            await ctx.SaveChangesAsync();
            return (true, $"💰 成功佩戴【金币暴击符】！增益已生效至 {user.GoldBoostUntil.Value:HH:mm}，答题金币收益提升 +50%！");
        }

        public async Task<(bool Success, string Message)> BuyComboShieldAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.FindAsync(userId);
            if (user == null) return (false, "用户未找到");
            const int cost = 30;
            if (user.Coins < cost) return (false, $"金币不足，需要 {cost} 金币！");

            user.Coins -= cost;
            user.ComboShieldCount += 1;
            await ctx.SaveChangesAsync();
            return (true, "🛡️ 成功购买【连击保护符】！答错时将自动护盾免除清空连击！");
        }

        public async Task<(bool Success, string Message)> BuyStreakRepairCardAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.FindAsync(userId);
            if (user == null) return (false, "用户未找到");
            const int cost = 50;
            if (user.Coins < cost) return (false, $"金币不足，需要 {cost} 金币！");

            user.Coins -= cost;
            user.CurrentStreak = Math.Max(1, user.CurrentStreak + 1);
            user.LastStudyDate = DateTime.Today;
            await ctx.SaveChangesAsync();
            return (true, "🎉 成功使用【打卡断签补签卡】！连续打卡天数已成功恢复并延长！");
        }

        public async Task<List<string>> GetAvailableTitlesAsync(Guid userId)
        {
            var defaultTitles = new List<string> { "初出茅庐", "刷题达人" };
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.FindAsync(userId);
            if (user == null) return defaultTitles;

            var unlocked = await ctx.UserAchievements
                .Include(ua => ua.Achievement)
                .Where(ua => ua.UserId == userId && ua.Achievement != null)
                .Select(ua => ua.Achievement!.Title)
                .ToListAsync();

            var all = defaultTitles.Concat(unlocked).Distinct().ToList();
            if (!string.IsNullOrWhiteSpace(user.ActiveTitle) && !all.Contains(user.ActiveTitle)) all.Add(user.ActiveTitle);
            if (user.MaxCombo >= 5 && !all.Contains("连击大师")) all.Add("连击大师");
            if (user.TotalAnswered >= 50 && !all.Contains("博学鸿儒")) all.Add("博学鸿儒");
            if (user.Level >= 5 && !all.Contains("极客学霸")) all.Add("极客学霸");
            return all;
        }

        public async Task EquipTitleAsync(Guid userId, string title)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.FindAsync(userId);
            if (user != null)
            {
                user.ActiveTitle = title;
                await ctx.SaveChangesAsync();
            }
        }

        public async Task<(bool Success, string Message, int Exp, int Coins)> ClaimDailyTargetRewardAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var user = await ctx.Users.FindAsync(userId);
            if (user == null) return (false, "用户未找到", 0, 0);

            var today = DateTime.Today;
            if (user.TodayCountDate.Date != today)
            {
                user.TodayAnsweredCount = 0;
                user.TodayCountDate = today;
            }

            if (user.LastDailyRewardClaimDate.HasValue && user.LastDailyRewardClaimDate.Value.Date == today)
            {
                return (false, "今天已成功领取每日达标打卡礼包，明天继续保持哦！", 0, 0);
            }

            if (user.TodayAnsweredCount < user.DailyTargetQuestions)
            {
                return (false, $"今日打卡目标为 {user.DailyTargetQuestions} 题，目前已完成 {user.TodayAnsweredCount} 题，还差 {user.DailyTargetQuestions - user.TodayAnsweredCount} 题即可领取！", 0, 0);
            }

            int rewardExp = 50;
            int rewardCoins = 30;

            // 检查 Buff
            if (user.ExpBoostUntil.HasValue && user.ExpBoostUntil.Value > DateTime.Now)
            {
                rewardExp *= 2;
            }
            if (user.GoldBoostUntil.HasValue && user.GoldBoostUntil.Value > DateTime.Now)
            {
                rewardCoins = (int)Math.Round(rewardCoins * 1.5);
            }

            user.Exp += rewardExp;
            user.Coins += rewardCoins;
            user.LastDailyRewardClaimDate = today;

            // 升级判断（统一结算算法，支持多级突破）
            CheckAndProcessLevelUp(user);

            await ctx.SaveChangesAsync();
            return (true, $"🎉 恭喜达成每日打卡目标！成功领取 +{rewardExp} 经验值与 +{rewardCoins} 金币！", rewardExp, rewardCoins);
        }

        public async Task<AchievementsOverviewDto> GetAchievementsOverviewAsync(Guid userId)
        {
            var dto = new AchievementsOverviewDto();
            if (userId == Guid.Empty) return dto;

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            dto.AllAchievements = await ctx.Achievements.AsNoTracking().ToListAsync();

            var userAchIds = await ctx.UserAchievements
                .AsNoTracking()
                .Where(ua => ua.UserId == userId)
                .Select(ua => ua.AchievementId)
                .ToListAsync();
            dto.UnlockedAchievementIds = new HashSet<Guid>(userAchIds);

            dto.AvailableTitles = await GetAvailableTitlesAsync(userId);

            dto.FavoritesCount = await ctx.UserFavorites
                .AsNoTracking()
                .CountAsync(f => f.UserId == userId);

            dto.AiExploredCount = await ctx.LlmGenerationLogs
                .AsNoTracking()
                .CountAsync(l => l.UserId == userId);

            return dto;
        }

        private async Task<bool> CheckAndUnlockAchievement(AppDbContext ctx, Guid userId, string code, RewardResult result)
        {
            var achievement = await ctx.Achievements.FirstOrDefaultAsync(a => a.Code == code);
            if (achievement == null) return false;

            bool alreadyUnlocked = await ctx.UserAchievements.AnyAsync(ua => ua.UserId == userId && ua.AchievementId == achievement.Id);
            if (!alreadyUnlocked)
            {
                ctx.UserAchievements.Add(new UserAchievement
                {
                    UserId = userId,
                    AchievementId = achievement.Id,
                    UnlockedAt = DateTime.Now
                });

                var user = userId != Guid.Empty
                    ? (ctx.Users.Local.FirstOrDefault(u => u.Id == userId) ?? await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId))
                    : null;
                if (user != null)
                {
                    user.Exp += achievement.RewardExp;
                    user.Coins += achievement.RewardCoins;

                    result.UnlockedAchievementTitle = achievement.Title;
                    // 成就经验奖励即时触发升级结算与等级反馈
                    CheckAndProcessLevelUp(user, result);
                }

                await ctx.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public static bool CheckAndProcessLevelUp(User user, RewardResult? result = null)
        {
            if (user == null) return false;
            bool leveledUp = false;
            int expNeeded = CalculateExpNeeded(user.Level);
            while (user.Exp >= expNeeded)
            {
                user.Exp -= expNeeded;
                user.Level++;
                leveledUp = true;
                if (result != null)
                {
                    result.LeveledUp = true;
                    result.NewLevel = user.Level;
                }
                expNeeded = CalculateExpNeeded(user.Level);
            }
            return leveledUp;
        }

        public static double CalculateComboMultiplier(int combo)
        {
            return combo switch
            {
                <= 1 => 1.0,
                2 => 1.2,
                3 => 1.3,
                4 => 1.5,
                5 => 1.8,
                6 => 2.0,
                7 => 2.2,
                8 => 2.5,
                9 => 2.8,
                _ => 3.0
            };
        }

        public static int CalculateExpNeeded(int level) => Math.Max(100, level * 100);
    }
}
