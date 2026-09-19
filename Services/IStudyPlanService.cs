using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Northtropic.Models;

namespace Northtropic.Services
{
    public interface IStudyPlanService
    {
        /// <summary>
        /// 获取学员当前活跃中的自适应学习计划（包含子任务列表及实时进度）
        /// </summary>
        Task<StudyPlan?> GetActivePlanAsync(Guid userId, Guid? requestorUserId = null);

        /// <summary>
        /// 确保学员存在当前执行中的学习计划，若无则自适应生成一份
        /// </summary>
        Task<StudyPlan> EnsureActivePlanAsync(Guid userId, string? preferredSubject = null, Guid? requestorUserId = null);

        /// <summary>
        /// 基于学情与薄弱考点，自适应生成全新的定制提分学习计划（7 天周期）
        /// </summary>
        Task<StudyPlan> GenerateAdaptivePlanAsync(Guid userId, string? preferredSubject = null, Guid? requestorUserId = null);

        /// <summary>
        /// 在完成题目作答后，同步推进当前计划中匹配学科与考点的子任务进度
        /// </summary>
        Task<(bool Updated, string? FeedbackMessage)> RecordPracticeProgressAsync(Guid userId, string subject, string category, bool isCorrect);

        /// <summary>
        /// 超级智能学习辅助助手主动督促：评估当前进度、拖延情况与遗忘风险，生成针对性督促提醒
        /// </summary>
        Task<(string NudgeMessage, string Severity, Guid? UrgentTaskId)> SuperviseAndNudgeAsync(Guid userId, Guid? requestorUserId = null);

        /// <summary>
        /// 持续迭代闭环核心评估：深度分析学生是否进步、未进步多维根因剖析与纠偏处方、有进步成功经验提取与策略沉淀
        /// </summary>
        Task<ClosedLoopDiagnosisResultDto> EvaluateClosedLoopProgressAsync(Guid userId, Guid? requestorUserId = null);

        /// <summary>
        /// 获取最新的持续迭代闭环复盘洞察记录
        /// </summary>
        Task<ClosedLoopDiagnosisResultDto?> GetLatestClosedLoopInsightAsync(Guid userId, Guid? requestorUserId = null);
    }
}
