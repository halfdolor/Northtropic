using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Northtropic.Models
{
    public enum ProgressEvaluationStatus
    {
        Progressing = 0,        // 显著飞跃
        SteadilyImproving = 1,  // 稳步提升
        Stagnant = 2,           // 遭遇瓶颈
        Regressing = 3          // 出现下滑
    }

    public enum ProgressFailureReason
    {
        InsufficientVolume = 0,           // 刷题量未达标/练习断续
        NeglectedSpacedRepetition = 1,    // 艾宾浩斯临界衰退未及时复习
        PrerequisiteKnowledgeDeficit = 2, // 前置知识网断层卡点
        CarelessnessAndSpeed = 3,         // 审题粗心/盲目求快导致失误
        CognitiveOverload = 4             // 题目难度过陡/挫败感过强
    }

    public enum ProgressSuccessExperience
    {
        MasteredKeyBreakthrough = 0,      // 重点薄弱考点攻坚突破
        SpacedRepetitionDividend = 1,     // 规律抗遗忘复习获得记忆红利
        HighAccuracyPurification = 2,     // 错题变式重练高效净化
        StreakDisciplineBonus = 3,        // 连续打卡培养做题惯性与专注度
        OptimalSpeedAccuracyBalance = 4   // 审题敏锐度与解题速度黄金平衡
    }

    public class EvolutionClosedLoopInsight
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid UserId { get; set; }

        public DateTime AnalyzedAt { get; set; } = DateTime.Now;

        // 进步状态评估
        public ProgressEvaluationStatus EvaluationStatus { get; set; } = ProgressEvaluationStatus.SteadilyImproving;

        // 本周期正确率变动 (+/- %)
        public double AccuracyDelta { get; set; } = 0.0;

        // 解题平均用时缩短 (+/- 秒)
        public double SpeedDeltaSeconds { get; set; } = 0.0;

        // 真实突破攻克的薄弱考点数
        public int WeaknessOvercomeCount { get; set; } = 0;

        // 本周期成功净化的错题数
        public int PurifiedErrorsCount { get; set; } = 0;

        // 综合提分潜力分 (0 - 100)
        public int PotentialScore { get; set; } = 80;

        // 是否判定为有进步
        public bool HasImproved => EvaluationStatus == ProgressEvaluationStatus.Progressing || EvaluationStatus == ProgressEvaluationStatus.SteadilyImproving;

        // === 没进步原因深度剖析 (Root Cause Analysis) ===
        // 存储失败原因枚举列表（逗号分隔）
        [MaxLength(200)]
        public string FailureReasonsCsv { get; set; } = string.Empty;

        // 没进步的归因诊断剖析正文
        [MaxLength(1000)]
        public string RootCauseDiagnosis { get; set; } = string.Empty;

        // 靶向调优纠偏处方
        [MaxLength(1000)]
        public string CorrectivePrescription { get; set; } = string.Empty;

        // === 有进步经验宝藏提取 (Success Strategy Repository) ===
        // 存储成功经验枚举列表（逗号分隔）
        [MaxLength(200)]
        public string SuccessExperiencesCsv { get; set; } = string.Empty;

        // 沉淀提炼的成功策略与方法锦囊
        [MaxLength(1000)]
        public string SuccessExperienceSummary { get; set; } = string.Empty;

        // 反哺下一代自适应计划的演进建议
        [MaxLength(1000)]
        public string NextEvolutionStrategy { get; set; } = string.Empty;

        [ForeignKey("UserId")]
        public virtual User? User { get; set; }
    }

    public class ClosedLoopDiagnosisResultDto
    {
        public Guid UserId { get; set; }
        public DateTime AnalyzedAt { get; set; } = DateTime.Now;
        public ProgressEvaluationStatus EvaluationStatus { get; set; }
        public string StatusBadgeTitle { get; set; } = string.Empty;
        public string StatusBadgeColor { get; set; } = "Success";
        public bool HasImproved { get; set; }
        public double AccuracyDelta { get; set; }
        public double SpeedDeltaSeconds { get; set; }
        public int WeaknessOvercomeCount { get; set; }
        public int PurifiedErrorsCount { get; set; }
        public int PotentialScore { get; set; }

        public List<ProgressFailureReason> FailureReasons { get; set; } = new();
        public string RootCauseDiagnosis { get; set; } = string.Empty;
        public string CorrectivePrescription { get; set; } = string.Empty;

        public List<ProgressSuccessExperience> SuccessExperiences { get; set; } = new();
        public string SuccessExperienceSummary { get; set; } = string.Empty;
        public string NextEvolutionStrategy { get; set; } = string.Empty;
    }
}
