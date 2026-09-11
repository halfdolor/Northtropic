using System;
using System.ComponentModel.DataAnnotations;

namespace Northtropic.Models
{
    /// <summary>
    /// 学科与专题考点配置实体（支持按年级灵活配置学科及其对应的专题知识点分类）
    /// </summary>
    public class CurriculumSubjectConfig
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        // 适用年级 (如: 小学三年级, 初中二年级, 高中一年级, 大学/职业软件工程, 通用等)
        [Required]
        [MaxLength(50)]
        public string Grade { get; set; } = string.Empty;

        // 学科名称 (如: 语文, 数学, 英语, 物理, 化学, 生物, 道德与法治, 思想政治, 历史, 地理, 科学, 信息技术, 通用技术 等)
        [Required]
        [MaxLength(50)]
        public string Subject { get; set; } = string.Empty;

        // 专题/知识点列表 (JSON 字符串数组存储，如: ["全部", "力学与牛顿定律", "压强与浮力", ...])
        public string TopicsJson { get; set; } = "[]";

        // 排序顺序
        public int SortOrder { get; set; } = 0;

        // 是否为系统内置核心科目
        public bool IsBuiltIn { get; set; } = true;

        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
