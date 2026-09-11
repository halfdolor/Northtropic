using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Northtropic.Models
{
    public class ErrorItem
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid UserId { get; set; }

        public Guid QuestionId { get; set; }

        [ForeignKey("QuestionId")]
        public Question? Question { get; set; }

        public string UserWrongAnswer { get; set; } = string.Empty; // 用户做错时的答案

        // 错因归因分类：粗心大意 / 概念模糊 / 公式记错 / 审题不清 / 逻辑计算错误 / 完全不会
        public string ErrorReasonCategory { get; set; } = "未分类";

        // 改错重练次数
        public int RevisionCount { get; set; } = 0;

        // 累计错误/待消灭次数
        [NotMapped]
        public int ErrorCount => RevisionCount + 1;

        // 是否已经净化/彻底掌握
        public bool IsMastered { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? LastRevisedAt { get; set; }

        // AI 给出的定制化改错建议
        public string AiCustomAdvice { get; set; } = string.Empty;
    }
}
