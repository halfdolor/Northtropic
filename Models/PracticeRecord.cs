using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Northtropic.Models
{
    public class PracticeRecord
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid UserId { get; set; }

        public Guid QuestionId { get; set; }

        [ForeignKey("QuestionId")]
        public Question? Question { get; set; }

        public string UserAnswer { get; set; } = string.Empty;

        public bool IsCorrect { get; set; }

        public int TimeTakenSeconds { get; set; }

        public int ComboAtAnswer { get; set; } // 答该题时的连击数

        public int EarnedExp { get; set; } // 获得的经验值

        public int EarnedCoins { get; set; } // 获得的金币

        public DateTime AnsweredAt { get; set; } = DateTime.Now;
    }
}
