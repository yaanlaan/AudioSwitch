using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AudioSwitch.Services;

namespace AudioSwitch.Ui;

/// <summary>轻量的 WPF 取色对话框，替代 WinForms ColorDialog。</summary>
public partial class ColorPickerDialog : Window
{
    private readonly string _initialHex;

    public ColorPickerDialog(string? initialHex)
    {
        InitializeComponent();

        _initialHex = Normalize(initialHex);
        SelectedHex = _initialHex;
        HexBox.Text = _initialHex;

        BuildSwatches();
        UpdatePreview();

        SourceInitialized += (_, _) =>
        {
            if (Application.Current is App app)
            {
                app.Theme?.ApplyWindowEffects(this);
            }
        };
    }

    /// <summary>用户确认的 #RRGGBB 色值。</summary>
    public string SelectedHex { get; private set; }

    private static string Normalize(string? hex)
    {
        return AppSettings.TryParseColor(hex, out uint argb)
            ? $"#{((argb >> 16) & 0xFF):X2}{((argb >> 8) & 0xFF):X2}{(argb & 0xFF):X2}"
            : "#5B9DFF";
    }

    private void BuildSwatches()
    {
        double[] hues = { 0, 25, 45, 90, 145, 175, 195, 215, 240, 265, 290, 320 };
        foreach (double hue in hues)
        {
            AddSwatch(FromHsv(hue, 0.72, 0.96));
        }

        foreach (string neutral in new[] { "#F2F3F7", "#C9CBD3", "#9A9DA8", "#6B6F7B", "#3A3A44", "#1B1B1F" })
        {
            AddSwatch((Color)ColorConverter.ConvertFromString(neutral));
        }
    }

    private void AddSwatch(Color color)
    {
        string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        var border = new Border
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            ToolTip = hex,
            Tag = hex,
        };

        border.MouseLeftButtonUp += (_, e) =>
        {
            HexBox.Text = hex;
            HexBox.CaretIndex = HexBox.Text.Length;
            e.Handled = true;
        };
        border.MouseEnter += (_, _) =>
            border.BorderBrush = (Brush)FindResource("AccentBrush");
        border.MouseLeave += (_, _) =>
            border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        SwatchPanel.Children.Add(border);
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool valid = AppSettings.TryParseColor(HexBox.Text, out _);
        ValidationText.Text = valid ? string.Empty : "格式应为 #RRGGBB";
        OkButton.IsEnabled = valid;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (AppSettings.TryParseColor(HexBox.Text, out uint argb))
        {
            PreviewSwatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF)));
        }
        else
        {
            PreviewSwatch.Background = Brushes.Transparent;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经松开，忽略
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!AppSettings.TryParseColor(HexBox.Text, out _))
        {
            return;
        }

        SelectedHex = Normalize(HexBox.Text);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        double c = value * saturation;
        double x = c * (1 - Math.Abs((hue / 60 % 2) - 1));
        double m = value - c;

        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
