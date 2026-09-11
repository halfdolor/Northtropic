using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Northtropic.Models
{
    public class UserFavorite
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid UserId { get; set; }

        public Guid QuestionId { get; set; }

        [ForeignKey("QuestionId")]
        public Question? Question { get; set; }

        [MaxLength(200)]
        public string Note { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
