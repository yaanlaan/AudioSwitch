using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioSwitch.Services;

namespace AudioSwitch.Ui;

/// <summary>
/// 开发期离屏渲染：把窗口内容渲染成 PNG，便于在没有可见桌面的环境里检查界面。
/// 用法：
///   AudioSwitch.exe --render settings|popup|all [输出目录]
///   AudioSwitch.exe --render-all [输出目录] [--bg 图片路径]
/// </summary>
internal static class DevRenderer
{
    private static readonly Color DeskColor = Color.FromRgb(0x0E, 0x0F, 0x14);

    public static bool TryRun(string[] args, App app, out int exitCode)
    {
        exitCode = 0;

        if (Contains(args, "--render-all"))
        {
            return RunAll(args, app, ref exitCode);
        }

        int index = Array.FindIndex(args, a => string.Equals(a, "--render", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        string target = index + 1 < args.Length ? args[index + 1].ToLowerInvariant() : "all";
        string outputDir = index + 2 < args.Length
            ? args[index + 2]
            : Path.Combine(Environment.CurrentDirectory, "shots");

        try
        {
            Directory.CreateDirectory(outputDir);

            if (target is "all" or "settings")
            {
                string path = Path.Combine(outputDir, "settings.png");
                SavePng(RenderSettings(app, "general", out Size size), size, path);
                Console.Out.WriteLine($"RENDER|settings|{path}|{size.Width}x{size.Height}");
            }

            if (target is "all" or "popup")
            {
                string path = Path.Combine(outputDir, "popup.png");
                SavePng(RenderPopup(app, "cards", out Size size), size, path);
                Console.Out.WriteLine($"RENDER|popup|{path}|{size.Width}x{size.Height}");
            }

            if (target is "all" or "color")
            {
                string path = Path.Combine(outputDir, "color.png");
                SavePng(RenderColorDialog(out Size size), size, path);
                Console.Out.WriteLine($"RENDER|color|{path}|{size.Width}x{size.Height}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RENDER|FAIL|{ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }

        return true;
    }

    private static bool RunAll(string[] args, App app, ref int exitCode)
    {
        ThemeService? theme = app.Theme;
        if (theme is null)
        {
            Console.Error.WriteLine("RENDER|FAIL|主题服务未初始化");
            exitCode = 1;
            return true;
        }

        int index = Array.FindIndex(args, a => string.Equals(a, "--render-all", StringComparison.OrdinalIgnoreCase));
        string outputDir = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : Path.Combine(Environment.CurrentDirectory, "docs");

        string? backgroundPath = FindOption(args, "--bg");
        if (string.IsNullOrWhiteSpace(backgroundPath))
        {
            string candidate = Path.Combine(Environment.CurrentDirectory, "docs", "wallpaper.png");
            backgroundPath = File.Exists(candidate) ? candidate : null;
        }

        if (!string.IsNullOrWhiteSpace(backgroundPath))
        {
            backgroundPath = Path.GetFullPath(backgroundPath);
        }

        if (backgroundPath is not null && !File.Exists(backgroundPath))
        {
            Console.Error.WriteLine($"RENDER|WARN|背景图不存在：{backgroundPath}");
            backgroundPath = null;
        }

        try
        {
            Directory.CreateDirectory(outputDir);
            AppSettings settings = app.Settings;

            foreach (bool dark in new[] { true, false })
            {
                foreach (bool withImage in new[] { false, true })
                {
                    if (withImage && backgroundPath is null)
                    {
                        continue;
                    }

                    settings.ThemeMode = dark ? "dark" : "light";
                    settings.Material = "solid";
                    settings.ColorSource = withImage ? "image" : "fixed";
                    settings.AccentColor = AppSettings.DefaultAccent;
                    settings.BackgroundImage = withImage ? backgroundPath : null;
                    settings.BlurBackground = false;
                    settings.BackgroundOpacity = 0.6;
                    settings.GlassOpacity = 0.72;
                    settings.PopupStyle = "cards";
                    settings.PopupColumns = 0;
                    settings.PopupPosition = "center";
                    settings.PopupShowIcons = true;

                    theme.ApplyBackgroundNow();

                    string themeName = dark ? "dark" : "light";
                    string variant = withImage ? "image" : "solid";

                    string settingsPath = Path.Combine(outputDir, $"settings-{themeName}-{variant}.png");
                    SavePng(RenderSettings(app, "general", out Size settingsSize), settingsSize, settingsPath);
                    Console.Out.WriteLine($"RENDER|settings-{themeName}-{variant}|{settingsPath}|{settingsSize.Width}x{settingsSize.Height}");

                    string popupPath = Path.Combine(outputDir, $"popup-{themeName}-{variant}.png");
                    SavePng(RenderPopup(app, "cards", out Size popupSize), popupSize, popupPath);
                    Console.Out.WriteLine($"RENDER|popup-{themeName}-{variant}|{popupPath}|{popupSize.Width}x{popupSize.Height}");
                }
            }

            // 各功能页与列表样式（深色纯色即可，主要检查布局）
            settings.ThemeMode = "dark";
            settings.Material = "solid";
            settings.ColorSource = "fixed";
            settings.AccentColor = AppSettings.DefaultAccent;
            settings.BackgroundImage = null;
            theme.ApplyBackgroundNow();

            foreach (string page in new[] { "appearance", "popup", "hotkey", "about" })
            {
                string path = Path.Combine(outputDir, $"settings-{page}-dark-solid.png");
                SavePng(RenderSettings(app, page, out Size pageSize), pageSize, path);
                Console.Out.WriteLine($"RENDER|settings-{page}-dark-solid|{path}|{pageSize.Width}x{pageSize.Height}");
            }

            string listPopupPath = Path.Combine(outputDir, "popup-list-dark-solid.png");
            SavePng(RenderPopup(app, "list", out Size listSize), listSize, listPopupPath);
            Console.Out.WriteLine($"RENDER|popup-list-dark-solid|{listPopupPath}|{listSize.Width}x{listSize.Height}");

            string colorPath = Path.Combine(outputDir, "color-dialog-dark-solid.png");
            SavePng(RenderColorDialog(out Size colorSize), colorSize, colorPath);
            Console.Out.WriteLine($"RENDER|color-dialog-dark-solid|{colorPath}|{colorSize.Width}x{colorSize.Height}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RENDER|FAIL|{ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }

        return true;
    }

    private static string? FindOption(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool Contains(string[] args, string name)
        => Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static FrameworkElement RenderSettings(App app, string page, out Size size)
    {
        var window = new SettingsWindow(app);
        window.PrepareForRender(page);

        if (window.Content is not FrameworkElement content)
        {
            throw new InvalidOperationException("设置窗口没有内容");
        }

        window.Content = null;

        size = new Size(window.Width, window.Height);
        var host = new Border
        {
            Background = new SolidColorBrush(DeskColor),
            Child = content,
            Width = size.Width,
            Height = size.Height,
        };

        Layout(host, size);
        return host;
    }

    private static FrameworkElement RenderPopup(App app, string style, out Size size)
    {
        string previousStyle = app.Settings.PopupStyle;
        app.Settings.PopupStyle = style;

        try
        {
            var popup = new SwitcherPopup(app);

            if (popup.Content is not FrameworkElement content)
            {
                throw new InvalidOperationException("切换弹窗没有内容");
            }

            popup.Content = null;

            double width = double.IsNaN(popup.Width) ? 400 : popup.Width;
            var available = new Size(width, 1400);
            content.Measure(available);
            Size desired = content.DesiredSize;
            size = new Size(
                Math.Ceiling(Math.Max(desired.Width, width)),
                Math.Ceiling(Math.Max(desired.Height, 1)));

            // 弹窗自身是透明背景 + 阴影，垫一层桌面色便于观察阴影与透明度
            var host = new Border
            {
                Background = new SolidColorBrush(DeskColor),
                Child = content,
                Width = size.Width,
                Height = size.Height,
            };

            Layout(host, size);
            return host;
        }
        finally
        {
            app.Settings.PopupStyle = previousStyle;
        }
    }

    private static FrameworkElement RenderColorDialog(out Size size)
    {
        var dialog = new ColorPickerDialog(AppSettings.DefaultAccent)
        {
            SizeToContent = SizeToContent.Manual,
            Height = 464,
        };

        if (dialog.Content is not FrameworkElement content)
        {
            throw new InvalidOperationException("取色对话框没有内容");
        }

        dialog.Content = null;

        var available = new Size(dialog.Width, 2000);
        content.Measure(available);
        Size desired = content.DesiredSize;
        size = new Size(dialog.Width, Math.Ceiling(desired.Height));

        var host = new Border
        {
            Background = new SolidColorBrush(DeskColor),
            Child = content,
            Width = size.Width,
            Height = size.Height,
        };

        Layout(host, size);
        return host;
    }

    private static void Layout(FrameworkElement element, Size size)
    {
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();

        // 列表项容器可能要等第一次布局后才生成，再跑一轮
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }

    private static void SavePng(FrameworkElement element, Size size, string path)
    {
        const double dpi = 96;
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(size.Width * dpi / 96));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(size.Height * dpi / 96));

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
