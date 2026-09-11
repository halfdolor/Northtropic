using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Northtropic.Models
{
    public class StudentParentBinding
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid ParentUserId { get; set; }

        [Required]
        public Guid StudentUserId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [MaxLength(20)]
        public string RelationType { get; set; } = "监护人"; // 如：爸爸、妈妈、监护人

        [ForeignKey("ParentUserId")]
        public virtual User? ParentUser { get; set; }

        [ForeignKey("StudentUserId")]
        public virtual User? StudentUser { get; set; }
    }
}
