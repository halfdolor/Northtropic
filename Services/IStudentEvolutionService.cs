using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Northtropic.Services
{
    public class KnowledgePointMasteryDto
    {
        public string Subject { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int TotalAnswered { get; set; } = 0;
        public int TotalCorrect { get; set; } = 0;
        public double AccuracyRate => TotalAnswered > 0 ? Math.Round((double)TotalCorrect / TotalAnswered * 100, 1) : 0;
        public int MasteryScore { get; set; } = 0; // 0 - 100
        public double AverageSpeedSeconds { get; set; } = 0;
        public string MasteryLevel { get; set; } = "🔒 待探索";
        public DateTime? LastPracticedAt { get; set; }

        // 艾宾浩斯遗忘曲线 Memory Retention & Decay
        public double DaysSinceLastPractice => LastPracticedAt.HasValue ? Math.Round((DateTime.Now - LastPracticedAt.Value).TotalDays, 1) : 999;
        // 艾宾浩斯记忆保留率 (R = e^(-t/S))
        public double MemoryRetentionRate
        {
            get
            {
                if (!LastPracticedAt.HasValue || TotalAnswered == 0) return 0;
                double days = DaysSinceLastPractice;
                if (days <= 0.1) return 100.0;
                // S 强度与 MasteryScore 相关
                double S = Math.Max(1.0, (MasteryScore / 10.0));
                double retention = Math.Exp(-days / (S * 2.0)) * 100.0;
                return Math.Clamp(Math.Round(retention, 1), 5.0, 100.0);
            }
        }
        public string SpacedRepetitionStatus
        {
            get
            {
                if (TotalAnswered == 0) return "🔒 待探索";
                if (MemoryRetentionRate >= 80) return "🔥 强效记忆期";
                if (MemoryRetentionRate >= 50) return "⏳ 宜复习巩固";
                return "⚠️ 遗忘衰减临界";
            }
        }
        public bool NeedsSpacedReview => TotalAnswered > 0 && MemoryRetentionRate < 75;
    }

    public class EvolutionDiagnosisReportDto
    {
        public Guid UserId { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
        public int StarRating { get; set; } = 3; // 1 - 5 星级能力评估
        public double OverallMasteryRate { get; set; } = 0.0;
        public List<string> TopMasteredCategories { get; set; } = new List<string>();
        public List<string> TopWeakCategories { get; set; } = new List<string>();
        public Dictionary<string, int> ErrorReasonBreakdown { get; set; } = new Dictionary<string, int>();
        public string AiGrowthAdvice { get; set; } = string.Empty;
        public string RecommendedSubject { get; set; } = string.Empty;
        public string RecommendedCategory { get; set; } = string.Empty;
        public int RecommendedDifficulty { get; set; } = 3;

        // 周度进步跨越对比 (Weekly Progress Delta)
        public double WeeklyAccuracyDelta { get; set; } = 0.0;
        public int WeakCategoriesReducedCount { get; set; } = 0;
        public double SpeedImprovementSeconds { get; set; } = 0.0;
        public int PendingSpacedReviewCount { get; set; } = 0;
        public string WeeklyProgressSummary { get; set; } = string.Empty;

        // 前置知识图谱溯源预警 (Prerequisite Knowledge Tracing)
        public List<PrerequisiteTraceWarningDto> PrerequisiteWarnings { get; set; } = new List<PrerequisiteTraceWarningDto>();
    }

    public interface IStudentEvolutionService
    {
        Task<List<KnowledgePointMasteryDto>> GetMasteryOverviewAsync(Guid userId, string? subject = null);
        Task<EvolutionDiagnosisReportDto> GenerateDiagnosisReportAsync(Guid userId);
        Task<List<KnowledgePointMasteryDto>> GetPendingSpacedReviewNodesAsync(Guid userId);
        Task<List<PrerequisiteTraceWarningDto>> TraceWeakPrerequisitesAsync(Guid userId, string subject, string category);
    }
}
