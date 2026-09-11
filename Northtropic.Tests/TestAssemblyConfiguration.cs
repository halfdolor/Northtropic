using Xunit;

// 架构保障：全局测试程序集串行化执行配置
// 彻底解决跨测试类并发执行时对单例/静态缓存 (如 PracticeService、CurriculumConfigService、UserSessionService 等) 的偶发并发污染
[assembly: CollectionBehavior(DisableTestParallelization = true)]