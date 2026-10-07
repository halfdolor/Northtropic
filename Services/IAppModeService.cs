using System;
using System.Collections.Generic;

namespace Northtropic.Services
{
    public interface IAppModeService
    {
        bool IsBeginnerMode { get; }
        string ModeName { get; }
        void SetBeginnerMode(bool isBeginner);
        void ToggleMode();
        event Action? OnModeChanged;
        
        /// <summary>
        /// 专业模式 vs 入门模式术语对照
        /// </summary>
        string T(string professionalText, string beginnerText);

        /// <summary>
        /// 自动将文本中的专业术语替换为通俗表达
        /// </summary>
        string Format(string text);
    }

    public class AppModeService : IAppModeService
    {
        public bool IsBeginnerMode { get; private set; } = false;

        public string ModeName => IsBeginnerMode ? "入门模式" : "专业模式";

        public event Action? OnModeChanged;

        private static readonly Dictionary<string, string> TermMappings = new()
        {
            // 动态梯度相关
            { "动态梯度推题", "智能循序渐进推题" },
            { "动态梯度", "循序渐进" },
            { "智能自适应推题 (动态梯度)", "智能难度自适应 (循序渐进)" },
            { "智能自适应推题", "智能难度自适应" },
            { "最近发展区", "能力进阶区" },
            
            // 艾宾浩斯相关
            { "艾宾浩斯抗遗忘记忆留存率", "科学防遗忘掌握度" },
            { "艾宾浩斯抗遗忘记忆曲线", "科学记忆复习曲线" },
            { "艾宾浩斯抗遗忘记忆强化轨迹", "科学复习防遗忘强化轨迹" },
            { "艾宾浩斯抗遗忘记忆全景", "科学防遗忘复习全景" },
            { "艾宾浩斯抗遗忘特训卷", "科学防遗忘特训卷" },
            { "艾宾浩斯抗遗忘特训", "科学防遗忘特训" },
            { "艾宾浩斯抗遗忘强化", "科学防遗忘强化" },
            { "艾宾浩斯抗遗忘", "科学防遗忘复习" },
            { "艾宾浩斯遗忘曲线", "科学记忆曲线" },
            { "艾宾浩斯记忆曲线", "科学记忆曲线" },
            { "艾宾浩斯紧迫度优先", "急需温习优先" },
            { "艾宾浩斯需复习", "急需温习" },
            { "仅艾宾浩斯临界", "仅临界待温习" },
            { "艾宾浩斯节点", "复习提醒节点" },
            { "艾宾浩斯", "科学防遗忘" },
            
            // 苏格拉底相关
            { "苏格拉底式启发引导", "循循善诱启发思考" },
            { "苏格拉底启发引导", "循循善诱引导" },
            { "苏格拉底式启发", "循循善诱启发" },
            { "苏格拉底启发", "启发思考" },
            { "苏格拉底·开", "启发引导·开" },
            { "苏格拉底", "启发式引导" },

            // 净化副本 / 认知图谱相关
            { "前置知识图谱溯源", "基础考点盲区追溯" },
            { "知识图谱溯源", "基础考点追溯" },
            { "认知图谱", "知识树脉络" },
            { "知识图谱", "知识树脉络" },
            { "净化副本", "错题特训" },
            { "错题净化", "错题攻克" },
            { "净化归档", "掌握归档" },
            { "记忆留存率", "记忆掌握度" },
            { "推荐半衰期", "推荐复习周期" },
            { "全真模考", "模拟考试" },
            { "认知专注节奏分层", "专注答题分析" }
        };

        public void SetBeginnerMode(bool isBeginner)
        {
            if (IsBeginnerMode != isBeginner)
            {
                IsBeginnerMode = isBeginner;
                OnModeChanged?.Invoke();
            }
        }

        public void ToggleMode()
        {
            IsBeginnerMode = !IsBeginnerMode;
            OnModeChanged?.Invoke();
        }

        public string T(string professionalText, string beginnerText)
        {
            return IsBeginnerMode ? beginnerText : professionalText;
        }

        public string Format(string text)
        {
            if (string.IsNullOrEmpty(text) || !IsBeginnerMode)
            {
                return text;
            }

            string result = text;
            foreach (var kv in TermMappings)
            {
                if (result.Contains(kv.Key))
                {
                    result = result.Replace(kv.Key, kv.Value);
                }
            }
            return result;
        }
    }
}
