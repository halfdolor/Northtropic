using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class AnswerCheckResult
    {
        public bool IsCorrect { get; set; }
        public string StandardAnswer { get; set; } = string.Empty;
        public string Analysis { get; set; } = string.Empty;
        public RewardResult Reward { get; set; } = new RewardResult();
        public SubjectiveGradingResult? SubjectiveGrading { get; set; }
        public bool IsEquivalentMatch { get; set; }
        public string? EquivalentMatchReason { get; set; }
        public string? CognitiveClassification { get; set; }
        public string? CognitiveBadgeText { get; set; }
    }

    public class SubjectAnalyticsDto
    {
        public string Subject { get; set; } = string.Empty;
        public int TotalAnswered { get; set; }
        public int CorrectCount { get; set; }
        public double AccuracyRate => TotalAnswered > 0 ? Math.Round((double)CorrectCount / TotalAnswered * 100, 1) : 0;
        public double AverageTimeSeconds { get; set; }
    }

    public class PracticeAnalyticsDto
    {
        public int TotalAnswered { get; set; }
        public int TotalCorrect { get; set; }
        public double OverallAccuracyRate => TotalAnswered > 0 ? Math.Round((double)TotalCorrect / TotalAnswered * 100, 1) : 0;
        public int TotalTimeSpentSeconds { get; set; }
        public double AverageSpeedSeconds => TotalAnswered > 0 ? Math.Round((double)TotalTimeSpentSeconds / TotalAnswered, 1) : 0;
        public int TotalEarnedExp { get; set; }
        public int TotalEarnedCoins { get; set; }
        public int AgileMasteryCount { get; set; }
        public int SteadyMasteryCount { get; set; }
        public int CarelessCount { get; set; }
        public int StrugglingCount { get; set; }
        public string CognitivePaceAdvice { get; set; } = string.Empty;
        public List<SubjectAnalyticsDto> SubjectStats { get; set; } = new List<SubjectAnalyticsDto>();
        public List<PracticeRecord> RecentRecords { get; set; } = new List<PracticeRecord>();
    }

    public class WholePaperSubmissionResult
    {
        public int TotalQuestions { get; set; }
        public int CorrectCount { get; set; }
        public int TotalEarnedExp { get; set; }
        public int TotalEarnedCoins { get; set; }
        public int MaxComboAchieved { get; set; }
        public int AgileMasteryCount { get; set; }
        public int SteadyMasteryCount { get; set; }
        public int CarelessCount { get; set; }
        public int StrugglingCount { get; set; }
        public double AverageTimePerQuestionSeconds { get; set; }
        public string CognitivePaceAdvice { get; set; } = string.Empty;
        public Dictionary<int, AnswerCheckResult> ItemResults { get; set; } = new();
    }

    public interface IPracticeService
    {
        Task<List<Question>> GetQuestionsAsync(string? category = null, int count = 10);
        Task<List<Question>> GetRandomQuestionsAsync(Guid userId, string grade, string subject, string? category = null, int count = 5);
        Task<List<Question>> GetAdaptiveQuestionsAsync(Guid userId, string grade, string subject, string? category = null, int count = 5);
        Task<List<Question>> GetDemoQuestionsAsync(int count = 5);
        Task<List<string>> GetCategoriesAsync(bool forceRefresh = false);
        Task<AnswerCheckResult> SubmitAnswerAsync(Question activeQuestion, string userAnswer, int timeTakenSeconds, int currentCombo, Guid? targetUserId = null, System.Threading.CancellationToken cancellationToken = default);
        Task<WholePaperSubmissionResult> SubmitBatchPaperAsync(List<(Question Question, string UserAnswer, int TimeTakenSeconds)> submissions, int initialCombo, Guid? targetUserId = null, System.Threading.CancellationToken cancellationToken = default);
        Task<List<LlmGenerationLog>> GetLlmGenerationLogsAsync(Guid userId);
        Task<PracticeAnalyticsDto> GetPracticeAnalyticsAsync(Guid userId, string? subject = null, bool? isCorrect = null);
        Task<bool> ToggleFavoriteAsync(Guid userId, Guid questionId, string? note = null);
        Task<bool> IsFavoriteAsync(Guid userId, Guid questionId);
        Task<List<Question>> GetFavoriteQuestionsAsync(Guid userId);
        Task<List<Question>> GetSprintQuestionsFromErrorsAsync(Guid userId, string? subject = null, int count = 10);
        Task<List<Question>> GetQuestionsByIdsAsync(List<Guid> questionIds);
        Task<HomeworkAssignment> CreateHomeworkAssignmentAsync(Guid creatorUserId, Guid studentUserId, string title, string subject, string category, int questionCount, int difficulty, DateTime? deadline, string note);
        Task<HomeworkAssignment?> GetHomeworkAssignmentByIdAsync(Guid assignmentId);
        Task<List<HomeworkAssignment>> GetHomeworkAssignmentsByStudentAsync(Guid studentUserId);
        Task<List<HomeworkAssignment>> GetHomeworkAssignmentsByCreatorAsync(Guid creatorUserId);
        Task<bool> CompleteHomeworkAssignmentAsync(Guid assignmentId, int correctCount, int totalAnswered, int score, Guid? studentUserId = null);
        Task<bool> DeleteHomeworkAssignmentAsync(Guid assignmentId, Guid? requestorUserId = null);
        Task<(bool Success, string Message)> SendHomeworkReminderNudgeAsync(Guid parentId, Guid assignmentId, string? customNudge = null);
        Task<List<PracticeRecord>> GetUserPracticeRecordsAsync(Guid userId, int? take = null);
        Task<List<string>> GetCategoriesBySubjectAsync(string? subject);
    }
}

