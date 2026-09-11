using System;
using System.ComponentModel.DataAnnotations;

namespace Northtropic.Models
{
    public enum UserRole
    {
        [Display(Name = "超级管理员")]
        SuperAdmin = 0,     // 超级管理员：所有权限

        [Display(Name = "家长用户")]
        Parent = 1,         // 家长用户：管理所属学生账号、查看子女学情分析

        [Display(Name = "老师用户")]
        Teacher = 2,        // 老师用户：题库管理、试卷/题目录入与 AI 组题

        [Display(Name = "学生用户")]
        Student = 3         // 学生用户：最终学习终端，刷题/错题净化/个人成就
    }

    public enum UserAccountStatus
    {
        [Display(Name = "正常可用/已审批")]
        Approved = 0,

        [Display(Name = "待审批")]
        PendingApproval = 1,

        [Display(Name = "已拒绝")]
        Rejected = 2
    }

    public class User
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        [MaxLength(50)]
        public string Username { get; set; } = "学霸学员";

        // 个性化头像 (Emoji 图标或图片 URL，默认 🎓)
        [MaxLength(50)]
        public string Avatar { get; set; } = "🎓";

        [MaxLength(20)]
        public string PhoneNumber { get; set; } = string.Empty;

        [MaxLength(100)]
        public string Email { get; set; } = string.Empty;

        public UserAccountStatus AccountStatus { get; set; } = UserAccountStatus.Approved;

        [MaxLength(200)]
        public string RejectReason { get; set; } = string.Empty;

        public DateTime RegisteredAt { get; set; } = DateTime.Now;

        public DateTime? ApprovedAt { get; set; }

        public UserRole Role { get; set; } = UserRole.Student;

        public int Level { get; set; } = 1;

        public int Exp { get; set; } = 0;

        public int Coins { get; set; } = 100;

        public int CurrentStreak { get; set; } = 1;

        public DateTime LastStudyDate { get; set; } = DateTime.Now;

        // 连击最高纪录
        public int MaxCombo { get; set; } = 0;

        // 总答题数与答对数
        public int TotalAnswered { get; set; } = 0;
        public int TotalCorrect { get; set; } = 0;

        // 每日刷题数统计与日期 (每天上限 100 题免费额度)
        public int TodayAnsweredCount { get; set; } = 0;
        public DateTime TodayCountDate { get; set; } = DateTime.Today;

        // 已净化的错题数
        public int ResolvedErrorsCount { get; set; } = 0;

        // 学生年级/学段 (如：小学五年级、初中二年级、高中一年级、大学/职业软件工程)
        public string Grade { get; set; } = "初中二年级";

        // 学生专属关联绑定码（6位大写字母数字，便于家长绑定）
        public string BindingCode { get; set; } = string.Empty;

        // LLM 参数配置
        public string LlmApiKey { get; set; } = string.Empty;
        public string LlmBaseUrl { get; set; } = "https://api.openai.com/v1";
        public string LlmModelName { get; set; } = "gpt-4o-mini";

        // 账号鉴权与安全设置 (默认密码 123456，首次登录强制改密)
        [MaxLength(200)]
        public string Password { get; set; } = "123456";
        public bool MustChangePassword { get; set; } = true;

        // 短信验证码 API 配置 (支持阿里云短信 Dysmsapi 与通用网关)
        public string SmsToken { get; set; } = string.Empty;
        public string SmsApiEndpoint { get; set; } = string.Empty;
        public string SmsTemplate { get; set; } = "您的登录验证码为{0}";
        public int SessionTimeoutMinutes { get; set; } = 30;

        // 阿里云短信服务 (Aliyun Dysmsapi) 配置
        public string AliyunAccessKeyId { get; set; } = string.Empty;
        public string AliyunAccessKeySecret { get; set; } = string.Empty;
        public string AliyunSmsSignName { get; set; } = string.Empty; // 例如: 智学系统 或 阿里云短信测试
        public string AliyunSmsTemplateCode { get; set; } = string.Empty; // 例如: SMS_154950909
        public string AliyunSmsTemplateParam { get; set; } = "code"; // 模板变量名，对应 ${code}
        public string AliyunSmsEndpoint { get; set; } = "dysmsapi.aliyuncs.com";
        public string AliyunSmsRegionId { get; set; } = "cn-hangzhou";

        // OCR 模块配置 (默认本地原生内嵌 PaddleOCR，0 Token 纯本地运行)
        public string OcrProvider { get; set; } = "PaddleOcr"; // PaddleOcr, BaiduOcr, VisionLlm
        public string BaiduApiKey { get; set; } = string.Empty;
        public string BaiduSecretKey { get; set; } = string.Empty;
        public string BaiduOcrEndpoint { get; set; } = "https://aip.baidubce.com/rest/2.0/ocr/v1/accurate_basic";

        // 佩戴称号与个性化装扮
        public string ActiveTitle { get; set; } = "青铜学童";

        // 声音与动效偏好开关
        public bool SoundEffectsEnabled { get; set; } = true;
        public bool ConfettiEnabled { get; set; } = true;
        public bool AutoVoiceGuidance { get; set; } = true;

        // 道具 Buff 增益状态
        public DateTime? ExpBoostUntil { get; set; }
        public DateTime? GoldBoostUntil { get; set; }
        public int ComboShieldCount { get; set; } = 0;

        // 每日计划刷题目标量 (默认 20 题)
        public int DailyTargetQuestions { get; set; } = 20;

        // 每日打卡达标奖励最后领取日期
        public DateTime? LastDailyRewardClaimDate { get; set; }

        // 家长关怀寄语/挑战鼓励留言
        [MaxLength(300)]
        public string ParentEncouragementNote { get; set; } = string.Empty;
        public DateTime? ParentNoteUpdatedAt { get; set; }

        // 权限判断辅助计算属性
        public bool CanManageUsers => Role == UserRole.SuperAdmin;
        public bool CanAccessSystemConfig => Role == UserRole.SuperAdmin;
        public bool CanManageQuestionBank => Role == UserRole.SuperAdmin || Role == UserRole.Teacher;
        public bool CanModifyQuestionBank => CanManageQuestionBank;
        public bool CanManageBoundStudents => Role == UserRole.SuperAdmin || Role == UserRole.Parent;
        public bool CanViewStudentAnalytics => Role == UserRole.SuperAdmin || Role == UserRole.Parent || Role == UserRole.Teacher;
        
        public string RoleDisplayName => Role switch
        {
            UserRole.SuperAdmin => "超级管理员",
            UserRole.Parent => "家长用户",
            UserRole.Teacher => "老师用户",
            UserRole.Student => "学生用户",
            _ => "未知角色"
        };
    }
}
