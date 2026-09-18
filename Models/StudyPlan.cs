using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Northtropic.Models
{
    public enum StudyPlanStatus
    {
        Active = 0,      // 进行中
        Completed = 1,   // 已达标完成
        Expired = 2,     // 逾期未完成
        Adjusted = 3     // 经闭环分析已演进升级为下一代计划
    }

    public enum StudyPlanTaskType
    {
        WeaknessBreakthrough = 0,  // 弱项考点攻坚
        SpacedRepetition = 1,      // 艾宾浩斯记忆防遗忘复习
        ErrorPurification = 2,     // 错题重练与变式净化
        ComprehensiveSprint = 3    // 全真综合能力冲刺
    }

    public class StudyPlan
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid UserId { get; set; }

        [Required]
        [MaxLength(150)]
        public string Title { get; set; } = "自适应 7 天提分演进计划";

        public StudyPlanStatus Status { get; set; } = StudyPlanStatus.Active;

        public DateTime StartDate { get; set; } = DateTime.Now;

        public DateTime TargetEndDate { get; set; } = DateTime.Now.AddDays(7);

        public DateTime? CompletedDate { get; set; }

        // 每日建议刷题打卡量
        public int DailyTargetQuestions { get; set; } = 15;

        // 期望达标正确率 (例如 85.0%)
        public double TargetAccuracyRate { get; set; } = 85.0;

        [MaxLength(500)]
        public string PlanGoalSummary { get; set; } = string.Empty;

        // 超级智能助手督促次数
        public int SupervisionNudgeCount { get; set; } = 0;

        // 最近一次督促时间
        public DateTime? LastSupervisedAt { get; set; }

        // 最近一次助手的督促提醒语
        [MaxLength(500)]
        public string LatestSupervisionMessage { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        // 关联计划分解子任务
        public virtual ICollection<StudyPlanTask> Tasks { get; set; } = new List<StudyPlanTask>();

        [ForeignKey("UserId")]
        public virtual User? User { get; set; }

        [NotMapped]
        public double ProgressPercent
        {
            get
            {
                if (Tasks == null || Tasks.Count == 0) return 0.0;
                int totalTarget = 0;
                int totalCompleted = 0;
                foreach (var t in Tasks)
                {
                    totalTarget += Math.Max(1, t.TargetCount);
                    totalCompleted += Math.Min(t.TargetCount, t.CompletedCount);
                }
                if (totalTarget == 0) return 0.0;
                return Math.Round((double)totalCompleted / totalTarget * 100.0, 1);
            }
        }

        [NotMapped]
        public bool IsFullyCompleted => Tasks != null && Tasks.Count > 0 && Tasks.All(t => t.IsCompleted);

        [NotMapped]
        public int RemainingDays
        {
            get
            {
                var diff = (TargetEndDate.Date - DateTime.Now.Date).TotalDays;
                return Math.Max(0, (int)diff);
            }
        }
    }

    public class StudyPlanTask
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid StudyPlanId { get; set; }

        [Required]
        [MaxLength(100)]
        public string Title { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string Subject { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string Category { get; set; } = string.Empty;

        public StudyPlanTaskType TaskType { get; set; } = StudyPlanTaskType.WeaknessBreakthrough;

        // 目标刷题或净化题量
        public int TargetCount { get; set; } = 10;

        // 已完成题量
        public int CompletedCount { get; set; } = 0;

        // 期望达标正确率
        public double TargetAccuracy { get; set; } = 80.0;

        public bool IsCompleted { get; set; } = false;

        public DateTime? CompletedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [ForeignKey("StudyPlanId")]
        public virtual StudyPlan? StudyPlan { get; set; }

        [NotMapped]
        public double TaskProgressPercent => TargetCount > 0
            ? Math.Clamp(Math.Round((double)CompletedCount / TargetCount * 100.0, 1), 0.0, 100.0)
            : (IsCompleted ? 100.0 : 0.0);
    }
}
