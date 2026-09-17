# Northtropic 智能学习系统

<p align="center">
  <strong>基于 .NET 10 Blazor 与 MudBlazor 构建的高性能 AI 智适应精准助学与错题净化提分系统</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Blazor-Interactive%20Server-512BD4?style=flat-square&logo=blazor" alt="Blazor" />
  <img src="https://img.shields.io/badge/UI-MudBlazor%207-7E6FFF?style=flat-square" alt="MudBlazor" />
  <img src="https://img.shields.io/badge/Database-PostgreSQL%20%7C%20SQLite-336791?style=flat-square&logo=postgresql" alt="PostgreSQL / SQLite" />
  <img src="https://img.shields.io/badge/CI%2FCD-Gitea%20Webhook%20AutoDeploy-28a745?style=flat-square&logo=gitea" alt="Gitea AutoDeploy" />
  <img src="https://img.shields.io/badge/Deployment-Windows%20Service-0078D6?style=flat-square&logo=windows" alt="Windows Service" />
</p>

---

## 📖 项目简介

**Northtropic** 是一套专为中小学及职业教育打造的现代化、沉浸式智能学习与知识净化平台。系统深度融合了游戏化激励设计、AI 大模型互动辅导、本地离线 PaddleOCR 拍照搜题，以及科学的艾宾浩斯错题净化闭环。无论是学员闯关练习、家长学情追踪，还是教师题库管理与作业布置，系统均提供了开箱即用、顺畅极致的全流程交互体验。

---

## ✨ 核心特色与功能模块

### 1. 🎮 游戏化智能刷题中心 (Practice)
- **沉浸式闯关体验**：关卡推进模式，支持单选题、多选题、判断题、填空题与简答题全题型支持。
- **智能防作弊洗牌**：选项与题目支持动态随机打乱，提升练习真实度。
- **正向激励循环**：集成连击（Streak）、金币（Coins）、经验升级（Lv/Exp）体系，答题音效动态反馈。

### 2. 🪄 错题净化副本 (Error Book)
- **错题自动收录**：答题失误自动归入专属错题库，支持按考点与错因类型（审题不清、概念模糊、运算错误等）分类归档。
- **三层净化闭环**：未净化 $\rightarrow$ 净化中 $\rightarrow$ 已净化，科学检验错题掌握程度。
- **AI 智能复习解析**：错题支持唤起 AI 导师进行针对性思路启发与巩固练习。

### 3. 📊 多维学情大盘与家校互联 (Analytics & Portal)
- **学情雷达与知识图谱**：全景可视化考点掌握度热力分布，自动关联前驱依赖知识点。
- **家长端学情看板**：实时同步孩子今日刷题量、正确率趋势、薄弱考点，支持向孩子布置定制针对性练习与每日鼓励寄语。
- **教师端作业管控**：支持快捷布置班级作业，统一跟踪学员完成情况。

### 4. 📚 题库全生命周期管理与智能录入 (Question Bank)
- **多格式模版导入**：支持一键下载标准模版，支持 Excel (`.xlsx`)、CSV、JSON、TXT 格式批量解析导入。
- **本地离线 PaddleOCR 识别**：内置 PaddleOCR 深度学习模型，支持试卷、教辅习题拍照上传，毫秒级本地提取文字并自动结构化解析为题目。
- **AI 智能批量出题**：支持指定学段、学科、知识点与难度，由 AI 自动生成标准试题并一键入库。

### 5. 🤖 AI 智能助教与 LLM 账单透明化 (AI Tutor & Billing)
- **启发式解题导师**：不直接给出答案，通过步骤提问与引导式点拨培养解题思维。
- **LLM Token 监控抽屉**：管理员可实时查看大模型调用记录，包含提示词 Tokens、生成 Tokens、总用量及估算成本明细。

### 6. 🛡️ 完善的安全鉴权与 RBAC 多角色体系
- **强鉴权登录门禁**：支持手机密码登录与短信验证码登录双通道；具备防暴力破解锁定机制及 60 秒短信防刷频限制。
- **四类角色隔离**：学员 (Student)、家长 (Parent)、名师 (Teacher)、超级管理员 (SuperAdmin)。
- **🎭 一键体验身份热切换**：在开发与演示环境中无需反复注销重新输入密码，一键切换各角色视角。

### 7. 🎨 精致 UI 与多套玻璃拟态主题
- 基于 MudBlazor 7 打造，全面适配桌面端、平板与移动端浏览器。
- 支持暗色模式/亮色模式及多款定制主题无缝平滑切换。

---

## 🛠️ 技术栈与架构设计

| 层次 | 技术选型 | 说明 |
| :--- | :--- | :--- |
| **应用架构** | ASP.NET Core 8.0 Blazor Server | 交互式服务端渲染 (Interactive Server)，极佳的实时响应与双向数据流 |
| **前端组件** | MudBlazor 7.x | 丰富的 Material Design 组件生态与主题系统 |
| **数据持久化** | SQLite + Entity Framework Core 8 | 开启 WAL (Write-Ahead Logging) 预写日志与 5s 忙等待，兼顾单机轻量与高并发 |
| **AI / OCR 引擎** | PaddleOCR + OpenCvSharp4 + LLM API | 支持全本地离线 OCR 推理，同时灵活兼容主流大语言模型 API |
| **宿主与运维** | Windows Services (`Microsoft.Extensions.Hosting.WindowsServices`) | 原生支持作为 Windows 服务后台运行，开机自启、崩溃自愈 |

---

## 🚀 快速开始 (本地开发)

### 前置要求
- 安装 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 (v17.8+) 或 JetBrains Rider / VS Code

### 运行步骤
1. **克隆项目到本地**：
   ```bash
   git clone https://github.com/halfdolor/Northtropic.git
   cd Northtropic
   ```

2. **还原依赖并启动开发服务**：
   ```bash
   dotnet restore
   dotnet run
   ```

3. **访问系统**：
   打开浏览器访问 `http://localhost:5000` 即可开始体验。系统首次启动会自动初始化 SQLite 数据库及内置基础考点。

4. **运行单元测试套件**：
   ```bash
   dotnet test
   ```

---

## 🔑 内置预设体验账号

系统内置了涵盖四大角色的预设账号，所有账号初始默认密码均为：`123456`

| 角色 | 姓名 | 手机号码 (账号) | 初始密码 | 核心权限与体验入口 |
| :--- | :--- | :--- | :--- | :--- |
| 🛡️ **超级管理员** | 管理员 | `13800000000` | `123456` | 系统总控、用户账号审批、LLM 账单大盘、全局配置 |
| 🎒 **学员用户** | 李小明 | `13800000001` | `123456` | 游戏化刷题、净化错题副本、荣誉商城、成就徽章 |
| 👨‍👩‍👧 **家长用户** | 李大强 | `13800000002` | `123456` | 学员李小明学情雷达、定制薄弱题推送、每日打气寄语 |
| 👩‍🏫 **教师用户** | 张老师 | `13800000003` | `123456` | 题库批量管理、OCR拍照入库、AI 出题、作业布置与跟踪 |

> 💡 **提示**：进入系统后，右上角顶部菜单支持通过 **【🎭 切身份】** 按钮一键热切换体验身份，无需反复登出重输密码。

---

## 📦 Windows Server 生产环境一键部署

本系统提供了极其便捷的独立自包含一键打包与服务化部署套件。目标 Windows Server **无需安装任何 .NET SDK 或运行库**。

### 1. 本地一键打包
在项目根目录直接运行打包脚本：
```cmd
package_deploy.bat
```
或在 PowerShell 中执行：
```powershell
.\package_deploy.ps1
```
脚本将全自动完成 `win-x64` 自包含编译发布，并在根目录生成免安装部署包 `Northtropic_DeployPackage/` 与 `Northtropic_Windows_Server_Deploy.zip`。

### 2. 服务器一键安装
1. 将 `Northtropic_DeployPackage` 文件夹（或解压后的 ZIP 文件）复制到目标 Windows Server（例如：`D:\NorthtropicServer`）。
2. 鼠标右键点击 **`1-安装服务(以管理员身份运行).bat`** 并选择“以管理员身份运行”。
3. 按照向导输入所需端口（默认 5000），安装脚本将自动完成：
   - 注册并启动开机自启 Windows 服务
   - 自动在 Windows 高级防火墙中放行指定端口
   - 打开浏览器并输出内网与公网访问地址

### 3. 便捷运维脚本清单
| 脚本名称 | 功能说明 |
| :--- | :--- |
| `1-安装服务(以管理员身份运行).bat` | 首次部署或重装配置 Windows 后台服务 |
| `2-启动服务.bat` | 启动已注册的 Windows 服务 |
| `3-停止服务.bat` | 暂停后台运行的 Windows 服务 |
| `4-重启服务.bat` | 重启服务（如更新配置或替换数据库后） |
| `5-卸载服务(以管理员身份运行).bat` | 注销服务与防火墙规则（业务数据库文件依然完整保留） |
| `6-控制台调试运行.bat` | 以命令行前台模式启动，便于排查异常及查看即时日志 |

### 4. 数据备份与迁移
- **数据库路径**：`app\northtropic_study.db`
- **备份/迁移方法**：直接复制该文件到备份目录；若需迁移至新服务器，直接将该 `.db` 文件覆盖至新安装包的 `app\` 目录下即可无缝迁移所有学员、题目与学习记录。

---

## 📁 项目目录结构

```text
Northtropic/
├── Components/                 # Blazor UI 页面与组件
│   ├── Dialogs/                # 弹窗与抽屉交互 (OCR导入、LLM账单、密码修改等)
│   ├── Layout/                 # 核心布局与响应式侧边导航 (MainLayout)
│   └── Pages/                  # 核心功能路由页面 (刷题、错题、学情看板、题库管理等)
├── Data/                       # 数据上下文与初始化
│   ├── AppDbContext.cs         # EF Core 数据模型映射
│   ├── AsyncDbScope.cs         # 高并发异步 DB 作用域辅助
│   └── DbInitializer.cs        # 数据库自动化迁移、默认账号及静态数据预热
├── Helpers/                    # 辅助工具 (密码加密、题目乱序洗牌、JSON提取)
├── Models/                     # 实体领域模型 (用户、题目、错题、成就、日志等)
├── Services/                   # 核心业务服务与抽象契约
│   ├── AiQuestionGeneratorService.cs  # AI 出题引擎
│   ├── AiTutorService.cs              # AI 导师交互
│   ├── ErrorBookService.cs            # 错题净化逻辑
│   ├── GamificationService.cs         # 游戏化经验/金币/连击体系
│   ├── LocalPaddleOcrEngine.cs        # 本地 PaddleOCR 离线图像识别
│   ├── PracticeService.cs             # 刷题交互与判定
│   ├── QuestionImportService.cs       # 多格式题库模版解析与导出
│   └── UserSessionService.cs          # 会话管理、RBAC 权限网关与安全策略
├── deploy_template/            # Windows Server 部署模板与运维批处理脚本
├── Northtropic.Tests/          # 覆盖核心业务、架构与安全的 xUnit 自动化测试工程
├── package_deploy.ps1          # 自动化构建与发布部署包脚本
├── package_deploy.bat          # 打包一键批处理入口
├── Program.cs                  # Web 应用启动引导配置与依赖注入
└── Northtropic.csproj          # 项目工程配置
```

---

## 📄 开源许可证

本项目基于 [MIT License](LICENSE) 开源。
