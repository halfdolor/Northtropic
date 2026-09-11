using System;
using System.Threading.Tasks;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class RewardResult
    {
        public int EarnedExp { get; set; }
        public int EarnedCoins { get; set; } // 答对为正加金币，答错为负扣金币
        public bool LeveledUp { get; set; }
        public int NewLevel { get; set; }
        public int CurrentCombo { get; set; }
        public string? UnlockedAchievementTitle { get; set; }

        // 每日 100 题限额与付费刷题机制
        public int TodayAnsweredCount { get; set; }
        public bool IsLimitExceeded { get; set; }
        public bool IsPaidQuestion { get; set; }
        public int PaidEntryFee { get; set; }
        public string? InsufficientCoinsError { get; set; }

        // 增益 Buff 状态
        public bool ExpBoostActive { get; set; }
        public bool GoldBoostActive { get; set; }
        public bool ComboShieldUsed { get; set; }
    }

    public interface IGamificationService
    {
        Task<User> GetCurrentUserAsync();
        Task<RewardResult> ProcessAnswerRewardAsync(bool isCorrect, int baseExp, int combo, int difficulty = 3, int timeTakenSeconds = 20, bool isHistoryWrong = false, Guid? targetUserId = null, Northtropic.Data.AppDbContext? existingContext = null);
        Task<RewardResult> ProcessErrorRevisionRewardAsync(Guid errorItemId, Guid? targetUserId = null);
        Task UpdateStreakAsync(Guid? targetUserId = null);
        Task<bool> UnlockAchievementAsync(string achievementCode, Guid? targetUserId = null, Northtropic.Data.AppDbContext? existingContext = null);
        Task ApplyBuffAsync(string buffType, int durationMinutes);
        Task AddComboShieldAsync(int count);
        Task SetUserTitleAsync(string title);
        string GetLevelTitle(int level);

        // 学霸商店与 Buff 道具购买
        Task<(bool Success, string Message)> BuyExpPotionAsync(Guid userId);
        Task<(bool Success, string Message)> BuyGoldPotionAsync(Guid userId);
        Task<(bool Success, string Message)> BuyComboShieldAsync(Guid userId);
        Task<(bool Success, string Message)> BuyStreakRepairCardAsync(Guid userId);

        // 称号衣橱与穿戴
        Task<List<string>> GetAvailableTitlesAsync(Guid userId);
        Task EquipTitleAsync(Guid userId, string title);

        // 每日目标达标礼包
        Task<(bool Success, string Message, int Exp, int Coins)> ClaimDailyTargetRewardAsync(Guid userId);

        // 荣誉商城与成就聚合概览
        Task<AchievementsOverviewDto> GetAchievementsOverviewAsync(Guid userId);
    }

    public class AchievementsOverviewDto
    {
        public List<Achievement> AllAchievements { get; set; } = new();
        public HashSet<Guid> UnlockedAchievementIds { get; set; } = new();
        public List<string> AvailableTitles { get; set; } = new();
        public int FavoritesCount { get; set; }
        public int AiExploredCount { get; set; }
    }
}
