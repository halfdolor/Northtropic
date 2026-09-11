using System;
using System.ComponentModel.DataAnnotations;

namespace Northtropic.Models
{
    public enum QuestionType
    {
        SingleChoice,   // 单选题
        MultipleChoice, // 不定项选择题
        FillInBlank,    // 填空题
        ShortAnswer,    // 简答题
        EssayAnalysis   // 问答解析大题
    }

    public enum PublishStatusEnum
    {
        Private,  // 私有 (仅自己可见)
        Pending,  // 待审核 (申请转为公共库)
        Approved, // 已批准公开 (公共库全网可见)
        Rejected  // 已退回 (驳回转公开申请)
    }

    public class Question
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        [MaxLength(50)]
        public string Subject { get; set; } = "通用知识"; // 科目，如 数学、C#编程、英语、算法

        [Required]
        [MaxLength(100)]
        public string Category { get; set; } = "基础概念"; // 知识点/分类

        public string GradeTarget { get; set; } = "通用"; // 适配年级 (如：小学、初中、高中、大学C#)

        public QuestionType Type { get; set; } = QuestionType.SingleChoice;

        [Required]
        public string Stem { get; set; } = string.Empty; // 题干内容 (支持 Markdown)

        // 选项列表的 JSON 数组字符串 (如 ["A. 选项1", "B. 选项2", ...])
        public string OptionsJson { get; set; } = "[]";

        [Required]
        public string CorrectAnswer { get; set; } = string.Empty; // 正确答案 (如 "A", ["A","B"], "true", 文本)

        public string StandardAnalysis { get; set; } = string.Empty; // 官方标准解析

        // 难度等级 1 - 5
        public int Difficulty { get; set; } = 1;

        // 答对奖励经验基础分
        public int BaseExpReward { get; set; } = 10;

        // 创建者 UserId
        public Guid? CreatedByUserId { get; set; }

        // 是否为全网公共题库 (true: 所有人可见; false: 仅创建者可见)
        public bool IsPublic { get; set; } = false;

        // 审核发布状态
        public PublishStatusEnum PublishStatus { get; set; } = PublishStatusEnum.Private;

        // 创建时间
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        private string? _cachedOptionsJson;
        private System.Collections.Generic.List<string>? _cachedOptions;

        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public System.Collections.Generic.List<string> Options
        {
            get
            {
                if (string.IsNullOrWhiteSpace(OptionsJson)) return new System.Collections.Generic.List<string>();
                if (_cachedOptions != null && string.Equals(_cachedOptionsJson, OptionsJson, StringComparison.Ordinal))
                {
                    return _cachedOptions;
                }
                try
                {
                    _cachedOptions = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<string>>(OptionsJson) ?? new System.Collections.Generic.List<string>();
                    _cachedOptionsJson = OptionsJson;
                    return _cachedOptions;
                }
                catch
                {
                    return new System.Collections.Generic.List<string>();
                }
            }
        }

        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public string Explanation => StandardAnalysis;

        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public string TypeDisplayName => GetTypeName(Type);

        public static string GetTypeName(QuestionType type) => type switch
        {
            QuestionType.SingleChoice => "单选题",
            QuestionType.MultipleChoice => "多选题",
            QuestionType.FillInBlank => "填空题",
            QuestionType.ShortAnswer => "简答题",
            QuestionType.EssayAnalysis => "综合大题",
            _ => "未知题型"
        };
    }
}
