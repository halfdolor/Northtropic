using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Northtropic.Models
{
    public class HomeworkAssignment
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid CreatorUserId { get; set; }

        [Required]
        public Guid StudentUserId { get; set; }

        [Required]
        [MaxLength(100)]
        public string Title { get; set; } = "专属专项强化作业";

        [Required]
        [MaxLength(50)]
        public string Subject { get; set; } = "初中物理";

        [MaxLength(50)]
        public string Category { get; set; } = "全部分类";

        public int QuestionCount { get; set; } = 5;

        public int TargetDifficulty { get; set; } = 3;

        public DateTime? Deadline { get; set; }

        [MaxLength(300)]
        public string ParentNote { get; set; } = string.Empty;

        public bool IsCompleted { get; set; } = false;

        public int Score { get; set; } = 0;

        public int AccuracyRate { get; set; } = 0;

        public int CorrectCount { get; set; } = 0;

        public int TotalAnswered { get; set; } = 0;

        public DateTime? CompletedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [ForeignKey("CreatorUserId")]
        public virtual User? CreatorUser { get; set; }

        [ForeignKey("StudentUserId")]
        public virtual User? StudentUser { get; set; }
    }
}
