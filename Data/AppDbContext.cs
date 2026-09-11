using Microsoft.EntityFrameworkCore;
using Northtropic.Models;

namespace Northtropic.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; } = null!;
        public DbSet<Question> Questions { get; set; } = null!;
        public DbSet<ErrorItem> ErrorItems { get; set; } = null!;
        public DbSet<PracticeRecord> PracticeRecords { get; set; } = null!;
        public DbSet<Achievement> Achievements { get; set; } = null!;
        public DbSet<UserAchievement> UserAchievements { get; set; } = null!;
        public DbSet<LlmGenerationLog> LlmGenerationLogs { get; set; } = null!;
        public DbSet<StudentParentBinding> StudentParentBindings { get; set; } = null!;
        public DbSet<UserFavorite> UserFavorites { get; set; } = null!;
        public DbSet<CurriculumSubjectConfig> CurriculumSubjectConfigs { get; set; } = null!;
        public DbSet<HomeworkAssignment> HomeworkAssignments { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 核心高频查询与并发性能索引配置
            modelBuilder.Entity<ErrorItem>()
                .HasIndex(e => new { e.UserId, e.IsMastered });
            modelBuilder.Entity<ErrorItem>()
                .HasIndex(e => new { e.UserId, e.QuestionId });

            modelBuilder.Entity<PracticeRecord>()
                .HasIndex(p => new { p.UserId, p.QuestionId });
            modelBuilder.Entity<PracticeRecord>()
                .HasIndex(p => new { p.UserId, p.AnsweredAt });

            modelBuilder.Entity<Question>()
                .HasIndex(q => new { q.Subject, q.Category, q.IsPublic });
            modelBuilder.Entity<Question>()
                .HasIndex(q => q.CreatedByUserId);

            modelBuilder.Entity<User>()
                .HasIndex(u => u.PhoneNumber);
            modelBuilder.Entity<User>()
                .HasIndex(u => u.BindingCode);

            modelBuilder.Entity<StudentParentBinding>()
                .HasIndex(b => new { b.ParentUserId, b.StudentUserId });

            modelBuilder.Entity<HomeworkAssignment>()
                .HasIndex(h => new { h.StudentUserId, h.IsCompleted });
            modelBuilder.Entity<HomeworkAssignment>()
                .HasIndex(h => h.CreatorUserId);

            // 架构性能扩展索引：收藏夹、LLM 日志流水与成就达成判断
            modelBuilder.Entity<UserFavorite>()
                .HasIndex(f => new { f.UserId, f.QuestionId });
            modelBuilder.Entity<UserFavorite>()
                .HasIndex(f => new { f.UserId, f.CreatedAt });

            modelBuilder.Entity<LlmGenerationLog>()
                .HasIndex(l => new { l.UserId, l.GeneratedAt });

            modelBuilder.Entity<UserAchievement>()
                .HasIndex(a => new { a.UserId, a.AchievementId });

            // 架构性能扩展索引：全学段学科与考点配置表 (高频按 Grade 与 Grade+Subject 查询)
            modelBuilder.Entity<CurriculumSubjectConfig>()
                .HasIndex(c => new { c.Grade, c.Subject });
            modelBuilder.Entity<CurriculumSubjectConfig>()
                .HasIndex(c => c.Grade);

            var parentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var studentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

            modelBuilder.Entity<User>().HasData(
                new User
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Username = "超级管理员-张老师",
                    PhoneNumber = "13800000000",
                    Role = UserRole.SuperAdmin,
                    Level = 5,
                    Exp = 450,
                    Coins = 500,
                    CurrentStreak = 7,
                    LastStudyDate = DateTime.Now,
                    MaxCombo = 10,
                    TotalAnswered = 50,
                    TotalCorrect = 45,
                    ResolvedErrorsCount = 12,
                    Grade = "高中二年级",
                    BindingCode = "ADMIN1",
                    LlmApiKey = string.Empty,
                    LlmBaseUrl = "https://api.openai.com/v1",
                    LlmModelName = "gpt-4o-mini"
                },
                new User
                {
                    Id = parentId,
                    Username = "王家长",
                    PhoneNumber = "13900000000",
                    Role = UserRole.Parent,
                    Level = 3,
                    Exp = 200,
                    Coins = 300,
                    CurrentStreak = 4,
                    LastStudyDate = DateTime.Now,
                    MaxCombo = 6,
                    TotalAnswered = 30,
                    TotalCorrect = 25,
                    ResolvedErrorsCount = 5,
                    Grade = "初中二年级",
                    BindingCode = "PAR001",
                    LlmApiKey = string.Empty,
                    LlmBaseUrl = "https://api.openai.com/v1",
                    LlmModelName = "gpt-4o-mini"
                },
                new User
                {
                    Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    Username = "特级教师-李老师",
                    PhoneNumber = "13600000000",
                    Role = UserRole.Teacher,
                    Level = 4,
                    Exp = 320,
                    Coins = 400,
                    CurrentStreak = 5,
                    LastStudyDate = DateTime.Now,
                    MaxCombo = 8,
                    TotalAnswered = 40,
                    TotalCorrect = 36,
                    ResolvedErrorsCount = 8,
                    Grade = "初中二年级",
                    BindingCode = "TCH001",
                    LlmApiKey = string.Empty,
                    LlmBaseUrl = "https://api.openai.com/v1",
                    LlmModelName = "gpt-4o-mini"
                },
                new User
                {
                    Id = studentId,
                    Username = "极客学霸-小明",
                    PhoneNumber = "13700000000",
                    Role = UserRole.Student,
                    Level = 2,
                    Exp = 120,
                    Coins = 150,
                    CurrentStreak = 3,
                    LastStudyDate = DateTime.Now,
                    MaxCombo = 5,
                    TotalAnswered = 20,
                    TotalCorrect = 16,
                    ResolvedErrorsCount = 3,
                    Grade = "初中二年级",
                    BindingCode = "STUD01",
                    LlmApiKey = string.Empty,
                    LlmBaseUrl = "https://api.openai.com/v1",
                    LlmModelName = "gpt-4o-mini"
                }
            );

            modelBuilder.Entity<StudentParentBinding>().HasData(
                new StudentParentBinding
                {
                    Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    ParentUserId = parentId,
                    StudentUserId = studentId,
                    RelationType = "监护人",
                    CreatedAt = DateTime.Now
                }
            );
        }
    }
}
