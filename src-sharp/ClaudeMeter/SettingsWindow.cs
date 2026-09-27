using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeMeter;

/// <summary>Modal settings editor. Mutates the shared <see cref="Settings"/> instance on Save.</summary>
public sealed class SettingsWindow : Window
{
    static readonly (string Key, string Label)[] PlanChoices =
    [
        ("unknown", "Don't know / unspecified"),
        ("pro", "Claude Pro"),
        ("max_5x", "Claude Max (5×)"),
        ("max_20x", "Claude Max (20×)"),
        ("api_only", "Pay-as-you-go API only"),
    ];

    public SettingsWindow(Settings s)
    {
        Title = "Claude Meter — Settings";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string label, UIElement control)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new Label { Content = label, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, grid.RowDefinitions.Count - 1);
            Grid.SetRow(control, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(control, 1);
            if (control is FrameworkElement fe) fe.Margin = new Thickness(4, 3, 0, 3);
            grid.Children.Add(l);
            grid.Children.Add(control);
        }
        TextBox Num(int value) => new() { Text = value.ToString(), Width = 80, HorizontalAlignment = HorizontalAlignment.Left };

        var refresh = Num(s.RefreshSeconds);
        Row("Refresh interval (s, 0 = auto):", refresh);

        var plan = new ComboBox { ItemsSource = PlanChoices.Select(p => p.Label).ToList() };
        plan.SelectedIndex = Math.Max(0, Array.FindIndex(PlanChoices, p => p.Key == s.Plan));
        Row("Plan (for cost estimate):", plan);

        var apiKey = new PasswordBox { Password = s.ManualApiKey ?? "", ToolTip = "Leave empty — uses Claude Code credentials" };
        Row("Manual API key:", apiKey);

        var credPath = new TextBox { Text = s.CredentialsPath ?? "", ToolTip = @"e.g. \\wsl.localhost\Ubuntu\home\you\.claude\.credentials.json" };
        var browse = new Button { Content = "Browse…", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Claude credentials|.credentials.json;*.json|All files|*.*" };
            if (dlg.ShowDialog(this) == true) credPath.Text = dlg.FileName;
        };
        var credRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        credRow.Children.Add(browse);
        credRow.Children.Add(credPath);
        Row("Credentials file (optional):", credRow);

        var notify = new CheckBox { Content = "Toast at 75% / 90% / 95% and when usage resets", IsChecked = s.NotificationsEnabled, VerticalAlignment = VerticalAlignment.Center };
        Row("Notifications:", notify);

        var fullscreen = new CheckBox { Content = "Hide when an app is fullscreen", IsChecked = s.HideWhenFullscreen, VerticalAlignment = VerticalAlignment.Center };
        Row("Auto-hide:", fullscreen);

        var offRight = Num(s.PosOffsetRight);
        Row("Offset from system tray (px):", offRight);
        var offBottom = Num(s.PosOffsetBottom);
        Row("Offset from taskbar (px):", offBottom);
        var opacity = Num((int)Math.Round(s.Opacity * 100));
        Row("Widget opacity (30–100 %):", opacity);
        var snooze = Num(s.SnoozeMinutes);
        Row("Snooze duration (min):", snooze);

        var note = new TextBlock
        {
            Text = "Cost estimate is approximate. The Anthropic API doesn't expose exact token spend — "
                 + "we infer from utilization × plan caps. Credentials file: set this if Claude Code runs only inside WSL.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7E, 0x88)),
            FontSize = 11,
            Margin = new Thickness(14, 0, 14, 8),
        };

        var save = new Button { Content = "Save", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        save.Click += (_, _) =>
        {
            // Clamp like the old spin boxes did; unparsable input keeps the old value.
            static int Parse(TextBox t, int min, int max, int fallback) =>
                int.TryParse(t.Text.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;
            s.RefreshSeconds = Parse(refresh, 0, 3600, s.RefreshSeconds);
            s.Plan = PlanChoices[Math.Max(0, plan.SelectedIndex)].Key;
            s.ManualApiKey = string.IsNullOrWhiteSpace(apiKey.Password) ? null : apiKey.Password.Trim();
            s.CredentialsPath = string.IsNullOrWhiteSpace(credPath.Text) ? null : credPath.Text.Trim();
            s.NotificationsEnabled = notify.IsChecked == true;
            s.HideWhenFullscreen = fullscreen.IsChecked == true;
            s.PosOffsetRight = Parse(offRight, 0, 1000, s.PosOffsetRight);
            s.PosOffsetBottom = Parse(offBottom, 0, 200, s.PosOffsetBottom);
            s.Opacity = Parse(opacity, 30, 100, (int)Math.Round(s.Opacity * 100)) / 100.0;
            s.SnoozeMinutes = Parse(snooze, 1, 240, s.SnoozeMinutes);
            DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 0, 14, 14) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var root = new StackPanel();
        root.Children.Add(grid);
        root.Children.Add(note);
        root.Children.Add(buttons);
        Content = root;
    }
}
