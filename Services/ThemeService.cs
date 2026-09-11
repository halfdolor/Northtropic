using System;
using System.Collections.Generic;
using MudBlazor;

namespace Northtropic.Services
{
    public class AppThemeInfo
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsDarkMode { get; set; } = false;
        
        // 严格 5 色系统
        public string PrimaryColor { get; set; } = string.Empty;       // 色彩 1: 主调提神色
        public string BackgroundColor { get; set; } = string.Empty;    // 色彩 2: 页面基底色
        public string SurfaceColor { get; set; } = string.Empty;       // 色彩 3: 卡片/容器背景色
        public string TextPrimaryColor { get; set; } = string.Empty;   // 色彩 4: 主要文字色
        public string TextSecondaryColor { get; set; } = string.Empty; // 色彩 5: 次要辅助文字色

        public string AppbarStyle { get; set; } = string.Empty;
        public string DrawerStyle { get; set; } = string.Empty;
        public string CssVariables { get; set; } = string.Empty;
    }

    public class ThemeService
    {
        public event Action? OnThemeChanged;

        public List<AppThemeInfo> AvailableThemes { get; } = new List<AppThemeInfo>
        {
            // 主题 1：现代科技 (4个字)
            new AppThemeInfo
            {
                Code = "tech-gray",
                Name = "现代科技",
                IsDarkMode = false,
                PrimaryColor = "#0066cc",       // 苹果蓝
                BackgroundColor = "#f5f5f7",    // 苹果浅灰
                SurfaceColor = "#ffffff",       // 纯白
                TextPrimaryColor = "#1d1d1f",   // 深碳灰
                TextSecondaryColor = "#6e6e73", // 石墨灰
                AppbarStyle = "background: rgba(255, 255, 255, 0.95); border-bottom: 1px solid rgba(0, 0, 0, 0.08); color: #1d1d1f; transition: all 0.3s ease;",
                DrawerStyle = "background: #f5f5f7; border-right: 1px solid rgba(0, 0, 0, 0.08); color: #1d1d1f; transition: all 0.3s ease;",
                CssVariables = "--glass-card-bg: #ffffff; --glass-card-border: rgba(0, 0, 0, 0.08); --glass-card-shadow: 0 4px 20px rgba(0, 0, 0, 0.04); --glass-text-primary: #1d1d1f; --glass-text-secondary: #6e6e73; --sub-card-bg: #f5f5f7; --accent-color: #0066cc;"
            },

            // 主题 2：悠长夏日 (4个字)
            new AppThemeInfo
            {
                Code = "vacation-blue",
                Name = "悠长夏日",
                IsDarkMode = false,
                PrimaryColor = "#0284c7",       // 天空海蓝
                BackgroundColor = "#f0f9ff",    // 柔性海蓝色调
                SurfaceColor = "#ffffff",       // 纯白
                TextPrimaryColor = "#0f172a",   // 海军深蓝
                TextSecondaryColor = "#475569", // 海蓝灰
                AppbarStyle = "background: rgba(255, 255, 255, 0.95); border-bottom: 1px solid rgba(2, 132, 199, 0.15); color: #0f172a; transition: all 0.3s ease;",
                DrawerStyle = "background: #f0f9ff; border-right: 1px solid rgba(2, 132, 199, 0.15); color: #0f172a; transition: all 0.3s ease;",
                CssVariables = "--glass-card-bg: #ffffff; --glass-card-border: rgba(2, 132, 199, 0.18); --glass-card-shadow: 0 4px 20px rgba(2, 132, 199, 0.06); --glass-text-primary: #0f172a; --glass-text-secondary: #475569; --sub-card-bg: #e0f2fe; --accent-color: #0284c7;"
            },

            // 主题 3：太空探秘 (4个字)
            new AppThemeInfo
            {
                Code = "space-dark",
                Name = "太空探秘",
                IsDarkMode = true,
                PrimaryColor = "#8b5cf6",       // 星空紫
                BackgroundColor = "#090d16",    // 宇宙深黑
                SurfaceColor = "#151c2c",       // 深太空灰
                TextPrimaryColor = "#f8fafc",   // 星光纯白
                TextSecondaryColor = "#94a3b8", // 星云灰
                AppbarStyle = "background: rgba(21, 28, 44, 0.95); border-bottom: 1px solid rgba(139, 92, 246, 0.3); color: #f8fafc; transition: all 0.3s ease;",
                DrawerStyle = "background: #090d16; border-right: 1px solid rgba(139, 92, 246, 0.3); color: #f8fafc; transition: all 0.3s ease;",
                CssVariables = "--glass-card-bg: #151c2c; --glass-card-border: rgba(139, 92, 246, 0.25); --glass-card-shadow: 0 6px 24px rgba(0, 0, 0, 0.4); --glass-text-primary: #f8fafc; --glass-text-secondary: #94a3b8; --sub-card-bg: #090d16; --accent-color: #8b5cf6;"
            }
        };

        public AppThemeInfo CurrentTheme { get; private set; }

        public ThemeService()
        {
            CurrentTheme = AvailableThemes[0];
        }

        public void SetTheme(string code)
        {
            var target = AvailableThemes.Find(t => t.Code == code);
            if (target != null && target.Code != CurrentTheme.Code)
            {
                CurrentTheme = target;
                OnThemeChanged?.Invoke();
            }
        }

        public MudTheme GetMudTheme()
        {
            if (CurrentTheme.IsDarkMode)
            {
                return new MudTheme
                {
                    PaletteDark = new PaletteDark
                    {
                        Primary = CurrentTheme.PrimaryColor,
                        Secondary = CurrentTheme.PrimaryColor,
                        Background = CurrentTheme.BackgroundColor,
                        Surface = CurrentTheme.SurfaceColor,
                        AppbarBackground = CurrentTheme.SurfaceColor,
                        DrawerBackground = CurrentTheme.BackgroundColor,
                        TextPrimary = CurrentTheme.TextPrimaryColor,
                        TextSecondary = CurrentTheme.TextSecondaryColor,
                        ActionDefault = CurrentTheme.TextPrimaryColor,
                        LinesInputs = CurrentTheme.TextSecondaryColor,
                        TableLines = CurrentTheme.TextSecondaryColor,
                        TableStriped = CurrentTheme.SurfaceColor,
                        TableHover = CurrentTheme.SurfaceColor
                    }
                };
            }
            else
            {
                return new MudTheme
                {
                    PaletteLight = new PaletteLight
                    {
                        Primary = CurrentTheme.PrimaryColor,
                        Secondary = CurrentTheme.PrimaryColor,
                        Background = CurrentTheme.BackgroundColor,
                        Surface = CurrentTheme.SurfaceColor,
                        AppbarBackground = CurrentTheme.SurfaceColor,
                        DrawerBackground = CurrentTheme.BackgroundColor,
                        TextPrimary = CurrentTheme.TextPrimaryColor,
                        TextSecondary = CurrentTheme.TextSecondaryColor,
                        ActionDefault = CurrentTheme.TextPrimaryColor,
                        LinesInputs = CurrentTheme.TextSecondaryColor,
                        TableLines = CurrentTheme.TextSecondaryColor,
                        TableStriped = CurrentTheme.SurfaceColor,
                        TableHover = CurrentTheme.SurfaceColor
                    }
                };
            }
        }
    }
}
