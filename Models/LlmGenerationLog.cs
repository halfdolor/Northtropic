using System;
using System.ComponentModel.DataAnnotations;

namespace Northtropic.Models
{
    public class LlmGenerationLog
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid UserId { get; set; }

        public Guid? QuestionId { get; set; }

        public string ModelName { get; set; } = string.Empty;

        public string Subject { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public int PromptTokens { get; set; }

        public int CompletionTokens { get; set; }

        public int TotalTokens { get; set; }

        public DateTime GeneratedAt { get; set; } = DateTime.Now;

        // 导航属性
        public Question? Question { get; set; }
    }
}
