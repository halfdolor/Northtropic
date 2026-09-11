using System;
using System.ComponentModel.DataAnnotations;

namespace Northtropic.Models
{
    public class Achievement
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public string Code { get; set; } = string.Empty; // 如 "FIRST_BLOOD", "COMBO_5", "ERROR_KILLER_10"

        [Required]
        public string Title { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Icon { get; set; } = "EmojiEvents"; // MudBlazor Icon 名称

        public int RewardExp { get; set; } = 50;

        public int RewardCoins { get; set; } = 20;
    }

    public class UserAchievement
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid UserId { get; set; }

        public Guid AchievementId { get; set; }

        public Achievement? Achievement { get; set; }

        public DateTime UnlockedAt { get; set; } = DateTime.Now;
    }
}
