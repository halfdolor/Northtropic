using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class AiExplanationResult
    {
        public string Summary { get; set; } = string.Empty; // 3秒简明总结
        public List<string> KeyConcepts { get; set; } = new List<string>(); // 核心考点
        public string WhyWrongAnalysis { get; set; } = string.Empty; // 错误归因剖析
        public string StepByStepReasoning { get; set; } = string.Empty; // 分步推理过程
    }

    public class SubjectiveGradingResult
    {
        public int Score { get; set; } = 0; // 0 - 100 评分
        public bool IsPassed { get; set; } = false; // 是否判定通过
        public string Feedback { get; set; } = string.Empty; // 批改意见与亮点
        public List<string> MissingPoints { get; set; } = new List<string>(); // 缺少的核心要点
        public string SuggestedAnswer { get; set; } = string.Empty; // 建议完善标准答案
        public List<string> RubricBreakdown { get; set; } = new List<string>(); // 多维度采分点明细 (如 逻辑完整性、术语规范性等)
        public string EncouragementAdvice { get; set; } = string.Empty; // UX 专家级激励与进阶行动建议
    }

    public class SocraticGuidanceResult
    {
        public string ThinkingHint { get; set; } = string.Empty; // 启发性思维提示
        public List<string> ProgressiveQuestions { get; set; } = new List<string>(); // 2-3个递进思考提问
        public string ReflectionPrompt { get; set; } = string.Empty; // 反思核验导引
        public string TargetCategory { get; set; } = string.Empty; // 考察的核心考点
    }

    public class TestConnectionResult
    {
        public bool IsSuccess { get; set; }
        public bool Success { get => IsSuccess; set => IsSuccess = value; }
        public int LatencyMs { get; set; }
        public string ModelName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string SampleResponse { get; set; } = string.Empty;
    }

    public interface IAiTutorService
    {
        Task<AiExplanationResult> GetExplanationAsync(Question question, string? userAnswer = null);
        Task<SocraticGuidanceResult> GetSocraticGuidanceAsync(Question question, string? userAnswer = null);
        Task<Question> GenerateVariationQuestionAsync(Question originalQuestion);
        Task<string> AskAiTutorAsync(string questionContext, string userPrompt);
        Task<SubjectiveGradingResult> GradeSubjectiveAnswerAsync(Question question, string userAnswer);
        Task<TestConnectionResult> TestConnectionAsync(string apiKey, string baseUrl, string modelName);
    }
}

