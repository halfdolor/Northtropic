using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Northtropic.Components;
using Northtropic.Data;
using Northtropic.Services;

var builder = WebApplication.CreateBuilder(args);

// 支持以 Windows Service 模式运行 (作为 Windows 服务启动时自动适配工作目录及生命周期)
builder.Host.UseWindowsService();

// 1. 添加 MudBlazor 服务
builder.Services.AddMudServices();

// 配置 DataProtection key 存储路径
var baseDirectory = AppContext.BaseDirectory;
var tempKeysPath = Path.Combine(baseDirectory, "temp-keys");
if (!Directory.Exists(tempKeysPath))
{
    Directory.CreateDirectory(tempKeysPath);
}
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(tempKeysPath));

// 2. 配置 SQLite 数据库 (具备高并发 5 秒 Busy Timeout 防死锁与 WAL 读写并发)
var defaultDbPath = Path.Combine(baseDirectory, "northtropic_study.db");
var rawConnStr = builder.Configuration.GetConnectionString("DefaultConnection") ?? $"Data Source={defaultDbPath}";
var sqliteBuilder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(rawConnStr);
if (sqliteBuilder.DefaultTimeout < 5)
{
    sqliteBuilder.DefaultTimeout = 5;
}
sqliteBuilder.Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate;
var dbConnectionString = sqliteBuilder.ToString();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(dbConnectionString));
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(dbConnectionString));

// 3. 注册应用业务服务 (Scoped/Transient)
builder.Services.AddHttpClient();
builder.Services.AddScoped<IUserSessionService, UserSessionService>();
builder.Services.AddScoped<IGamificationService, GamificationService>();
builder.Services.AddScoped<IPracticeService, PracticeService>();
builder.Services.AddScoped<IErrorBookService, ErrorBookService>();
builder.Services.AddScoped<IAiTutorService, AiTutorService>();
builder.Services.AddScoped<IAiQuestionGeneratorService, AiQuestionGeneratorService>();
builder.Services.AddScoped<IQuestionImportService, QuestionImportService>();
builder.Services.AddScoped<IQuestionManagementService, QuestionManagementService>();
builder.Services.AddScoped<IStudentEvolutionService, StudentEvolutionService>();
builder.Services.AddScoped<ICurriculumConfigService, CurriculumConfigService>();
builder.Services.AddScoped<ISystemHealthService, SystemHealthService>();
builder.Services.AddScoped<ThemeService>();

// 4. 添加 Razor Components 与 Server 交互模式
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// 5. 初始化数据库数据与考点静态缓存预热
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    DbInitializer.Initialize(dbContext);

    // 架构优化：启动期预热全学段科目与专题考点静态缓存，确保无需进入 Settings 页面即可全局开箱即用
    var curriculumService = scope.ServiceProvider.GetRequiredService<ICurriculumConfigService>();
    await curriculumService.EnsureInitializedAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// 仅在明确配置了 HTTPS 端口时启用重定向，避免内部局域网/Windows Server 纯 HTTP 部署时报错或重定向失败
if (app.Configuration["HTTPS_PORT"] != null || app.Configuration["ASPNETCORE_HTTPS_PORTS"] != null)
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseAntiforgery();

// 题库模板下载原生流 API (支持 Excel / CSV / JSON / TXT)
app.MapGet("/api/questions/download-template", (IQuestionImportService importService, string? format) =>
{
    try
    {
        format = format?.ToLowerInvariant();
        if (format == "xlsx" || format == "excel")
        {
            var xlsxBytes = importService.GenerateXlsxTemplateBytes();
            return Results.File(xlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "全题型标准题库模板.xlsx");
        }
        if (format == "json")
        {
            var jsonBytes = importService.GenerateJsonTemplateBytes();
            return Results.File(jsonBytes, "application/json; charset=utf-8", "全题型标准题库模板.json");
        }
        if (format == "txt")
        {
            var txtBytes = importService.GenerateTxtTemplateBytes();
            return Results.File(txtBytes, "text/plain; charset=utf-8", "全题型标准题库模板.txt");
        }

        var bytes = importService.GenerateCsvTemplateBytes();
        return Results.File(bytes, "text/csv; charset=utf-8", "全题型标准题库模板.csv");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: $"生成题库模板异常: {ex.Message}", statusCode: 500);
    }
});

// 题库批量导出原生流 API (支持 Excel / CSV / JSON)
app.MapGet("/api/questions/export", async (
    IQuestionManagementService mgmtService,
    IUserSessionService userSession,
    Northtropic.Data.AppDbContext dbContext,
    string? format,
    string? subject,
    string? category,
    bool? isPublic,
    Northtropic.Models.PublishStatusEnum? status,
    string? ticket) =>
{
    try
    {
        var currentUserId = userSession.CurrentUserId ?? Guid.Empty;
        if (!string.IsNullOrWhiteSpace(ticket))
        {
            var (valid, ticketUserId, purpose, _) = await userSession.ValidateAndConsumeDownloadTicketAsync(ticket);
            if (valid && purpose == "question_export")
            {
                currentUserId = ticketUserId;
            }
        }

        format = format?.ToLowerInvariant();

        if (format == "xlsx" || format == "excel")
        {
            var xlsxBytes = await mgmtService.ExportQuestionsXlsxAsync(currentUserId, subject, category, isPublic, status);
            string filename = $"题库导出_{DateTime.Now:yyyyMMddHHmmss}.xlsx";
            return Results.File(xlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
        }
        if (format == "json")
        {
            var jsonStr = await mgmtService.ExportQuestionsJsonAsync(currentUserId, subject, category, isPublic, status);
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(jsonStr);
            string filename = $"题库导出_{DateTime.Now:yyyyMMddHHmmss}.json";
            return Results.File(jsonBytes, "application/json; charset=utf-8", filename);
        }

        var csvBytes = await mgmtService.ExportQuestionsCsvAsync(currentUserId, subject, category, isPublic, status);
        string csvFilename = $"题库导出_{DateTime.Now:yyyyMMddHHmmss}.csv";
        return Results.File(csvBytes, "text/csv; charset=utf-8", csvFilename);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: $"导出题库数据异常: {ex.Message}", statusCode: 500);
    }
});

// 系统架构热备份快照安全下载 API (面向超级管理员)
app.MapGet("/api/system/backups/download", async (
    ISystemHealthService healthService,
    IUserSessionService userSession,
    Northtropic.Data.AppDbContext dbContext,
    string? fileName,
    string? filename,
    string? ticket) =>
{
    try
    {
        string targetFile = !string.IsNullOrEmpty(fileName) ? fileName : (filename ?? string.Empty);
        if (string.IsNullOrWhiteSpace(targetFile) || targetFile.Contains("..") || targetFile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Results.BadRequest("非法的备份文件名。");
        }

        bool authorized = false;
        string authorizedUsername = "Admin";

        if (!string.IsNullOrWhiteSpace(ticket))
        {
            var (valid, userId, purpose, resource) = await userSession.ValidateAndConsumeDownloadTicketAsync(ticket);
            if (valid && purpose == "backup_download" && (string.IsNullOrEmpty(resource) || string.Equals(resource, targetFile, StringComparison.OrdinalIgnoreCase)))
            {
                var user = await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
                if (user != null && user.CanAccessSystemConfig)
                {
                    authorized = true;
                    authorizedUsername = user.Username;
                }
            }
        }

        if (!authorized)
        {
            var activeUser = await userSession.GetActiveUserAsync();
            if (activeUser != null && activeUser.CanAccessSystemConfig)
            {
                authorized = true;
                authorizedUsername = activeUser.Username;
            }
        }

        if (!authorized)
        {
            return Results.StatusCode(403);
        }

        var baseDir = AppContext.BaseDirectory;
        var backupPath = Path.Combine(baseDir, "backups", targetFile);
        if (!File.Exists(backupPath))
        {
            return Results.NotFound("指定的备份快照文件不存在。");
        }

        var fileBytes = await File.ReadAllBytesAsync(backupPath);
        healthService.RecordArchitectureEvent("HotBackup", "Info", $"管理员 {authorizedUsername} 下载了数据库热备份快照: {targetFile}");
        return Results.File(fileBytes, "application/x-sqlite3", targetFile);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: $"下载备份异常: {ex.Message}", statusCode: 500);
    }
});

// 系统架构诊断审计快照安全导出 API (面向超级管理员 / SRE 架构师)
app.MapGet("/api/system/diagnostics/export", async (
    ISystemHealthService healthService,
    IUserSessionService userSession,
    Northtropic.Data.AppDbContext dbContext,
    string? ticket) =>
{
    try
    {
        bool authorized = false;
        string authorizedUsername = "Admin";

        if (!string.IsNullOrWhiteSpace(ticket))
        {
            var (valid, userId, purpose, _) = await userSession.ValidateAndConsumeDownloadTicketAsync(ticket);
            if (valid && (purpose == "diagnostics_export" || purpose == "backup_download"))
            {
                var user = await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
                if (user != null && user.CanAccessSystemConfig)
                {
                    authorized = true;
                    authorizedUsername = user.Username;
                }
            }
        }

        if (!authorized)
        {
            var activeUser = await userSession.GetActiveUserAsync();
            if (activeUser != null && activeUser.CanAccessSystemConfig)
            {
                authorized = true;
                authorizedUsername = activeUser.Username;
            }
        }

        if (!authorized)
        {
            return Results.StatusCode(403);
        }

        var jsonReport = await healthService.ExportArchitectureDiagnosticReportJsonAsync();
        var reportBytes = System.Text.Encoding.UTF8.GetBytes(jsonReport);
        var exportFileName = $"Northtropic_Architecture_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.json";

        healthService.RecordArchitectureEvent("Telemetry", "Info", $"系统管理员 {authorizedUsername} 导出了全量架构诊断审计报告: {exportFileName}");
        return Results.File(reportBytes, "application/json; charset=utf-8", exportFileName);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: $"导出架构诊断报告异常: {ex.Message}", statusCode: 500);
    }
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

