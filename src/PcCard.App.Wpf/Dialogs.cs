using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcCard.Core.Devices;
using PcCard.Core.Models;

namespace PcCard.AppWpf;

/// <summary>简单信息框（暗色，模态）。</summary>
public sealed class InfoDialog : Window
{
    private InfoDialog(string title, string message)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.Resources["PcSurface"];
        WindowStyle = WindowStyle.SingleBorderWindow;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["PcTextSecondary"],
        });
        var ok = new Button
        {
            Content = "知道了",
            Width = 96,
            Height = 30,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["PcSecondaryButton"],
            IsDefault = true,
        };
        ok.Click += (_, _) => Close();
        panel.Children.Add(ok);
        Content = panel;
    }

    public static void Show(Window owner, string title, string message)
    {
        var dlg = new InfoDialog(title, message) { Owner = owner };
        dlg.ShowDialog();
    }
}

/// <summary>确认框（暗色，模态；返回用户是否点了主按钮）。</summary>
public sealed class ConfirmDialog : Window
{
    private bool _ok;

    private ConfirmDialog(string title, string message, string okText)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.Resources["PcSurface"];

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["PcTextSecondary"],
        });

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 88,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)Application.Current.Resources["PcSecondaryButton"],
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();
        var ok = new Button
        {
            Content = okText,
            Width = 96,
            Height = 30,
            Style = (Style)Application.Current.Resources["PcPrimaryButton"],
            IsDefault = false,
        };
        ok.Click += (_, _) => { _ok = true; Close(); };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        panel.Children.Add(row);
        Content = panel;
    }

    public static bool Ask(Window owner, string title, string message, string okText)
    {
        var dlg = new ConfirmDialog(title, message, okText) { Owner = owner };
        dlg.ShowDialog();
        return dlg._ok;
    }
}

/// <summary>设备设置对话框：童锁 / 指示灯 / 通电自动开机（写操作即时生效、失败回滚）。</summary>
public sealed class SettingsDialog : Window
{
    private readonly Func<ComputerPowerCard.CardSwitch, bool, Task> _writer;
    private readonly TextBlock _status;
    private bool _loading = true;

    public SettingsDialog(string deviceName, ComputerPowerCard.CardSwitchState state,
        Func<ComputerPowerCard.CardSwitch, bool, Task> writer)
    {
        _writer = writer;
        Title = $"设备设置 · {deviceName}";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.Resources["PcSurface"];

        var panel = new StackPanel { Margin = new Thickness(18), Width = 420 };
        _status = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["PcTextTertiary"],
        };

        AddRow(panel, "童锁", "打开后机箱电源按钮不可用（云端控制不受影响）",
            ComputerPowerCard.CardSwitch.ChildLock, state.ChildLock);
        AddRow(panel, "指示灯", "关闭后设备指示灯熄灭",
            ComputerPowerCard.CardSwitch.Indicator, state.Indicator);
        AddRow(panel, "通电自动开机", "设备重新通电后自动给电脑开机（适合停电恢复场景）",
            ComputerPowerCard.CardSwitch.AutoBoot, state.AutoBoot);

        panel.Children.Add(_status);

        var done = new Button
        {
            Content = "完成",
            Width = 96,
            Height = 30,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["PcSecondaryButton"],
            IsDefault = true,
        };
        done.Click += (_, _) => Close();
        panel.Children.Add(done);
        Content = panel;

        _loading = false;
    }

    private void AddRow(Panel panel, string title, string subtitle,
        ComputerPowerCard.CardSwitch kind, bool? value)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            Foreground = (Brush)Application.Current.Resources["PcTextPrimary"],
        });
        texts.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["PcTextTertiary"],
        });
        grid.Children.Add(texts);

        var check = new CheckBox
        {
            IsChecked = value == true,
            IsEnabled = value is not null,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["PcTextPrimary"],
        };
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);

        var known = value; // 最近一次已确认值
        async void OnChanged(object? s, RoutedEventArgs e)
        {
            if (_loading || known is null || check.IsChecked == known) return;
            var target = check.IsChecked == true;
            check.IsEnabled = false;
            try
            {
                await _writer(kind, target);
                known = target;
                _status.Text = $"「{title}」已{(target ? "打开" : "关闭")}";
                _status.Foreground = (Brush)Application.Current.Resources["PcTextTertiary"];
            }
            catch (Exception ex)
            {
                _loading = true;
                check.IsChecked = known.Value; // 回滚
                _loading = false;
                _status.Text = $"设置「{title}」失败：{ex.Message}";
                _status.Foreground = (Brush)Application.Current.Resources["PcWarn"];
            }
            finally
            {
                check.IsEnabled = true;
            }
        }

        check.Checked += OnChanged;
        check.Unchecked += OnChanged;
        panel.Children.Add(grid);
    }
}

/// <summary>新增定时任务对话框。</summary>
public sealed class AddTaskDialog : Window
{
    private readonly TextBox _nameBox;
    private readonly ComboBox _actionBox;
    private readonly CheckBox _forceBox;
    private readonly ComboBox _triggerBox;
    private readonly TextBox _timeBox;
    private readonly TextBox _onceBox;
    private readonly List<(CheckBox Box, string DayName)> _dayChecks = new();
    private readonly StackPanel _daysPanel;

    public ScheduleTask? Task { get; private set; }

    public AddTaskDialog()
    {
        Title = "新增定时任务";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.Resources["PcSurface"];

        var form = new StackPanel { Margin = new Thickness(18) };
        TextBox AddBox(string header, string text)
        {
            var tb = new TextBox { Text = text, Margin = new Thickness(0, 4, 0, 8) };
            form.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["PcTextSecondary"],
            });
            form.Children.Add(tb);
            return tb;
        }

        _nameBox = AddBox("任务名", "定时开机");

        form.Children.Add(new TextBlock
        {
            Text = "动作",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["PcTextSecondary"],
        });
        _actionBox = new ComboBox { SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 8) };
        _actionBox.Items.Add("开机");
        _actionBox.Items.Add("关机");
        _actionBox.Items.Add("重启");
        form.Children.Add(_actionBox);

        _forceBox = new CheckBox
        {
            Content = "强制（仅关机/重启生效）",
            IsEnabled = false,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["PcTextSecondary"],
            Margin = new Thickness(0, 0, 0, 8),
        };
        _actionBox.SelectionChanged += (_, _) => _forceBox.IsEnabled = _actionBox.SelectedIndex != 0;
        form.Children.Add(_forceBox);

        form.Children.Add(new TextBlock
        {
            Text = "触发方式",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["PcTextSecondary"],
        });
        _triggerBox = new ComboBox { SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 8) };
        _triggerBox.Items.Add("每天");
        _triggerBox.Items.Add("每周");
        _triggerBox.Items.Add("单次");
        form.Children.Add(_triggerBox);

        _timeBox = AddBox("时间（HH:mm）", "08:00");
        _onceBox = AddBox("单次时间（yyyy-MM-ddTHH:mm）", DateTime.Now.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm"));
        _onceBox.Visibility = Visibility.Collapsed;

        _daysPanel = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        var dayNames = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
        var dayLabels = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var i = 0; i < dayNames.Length; i++)
        {
            var cb = new CheckBox { Content = dayLabels[i], Margin = new Thickness(0, 0, 6, 0), Foreground = UiBrushes.WhiteBrush() };
            _dayChecks.Add((cb, dayNames[i]));
            _daysPanel.Children.Add(cb);
        }
        form.Children.Add(_daysPanel);

        _triggerBox.SelectionChanged += (_, _) =>
        {
            var idx = _triggerBox.SelectedIndex;
            _timeBox.Visibility = idx == 2 ? Visibility.Collapsed : Visibility.Visible;
            _daysPanel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            _onceBox.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 88,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)Application.Current.Resources["PcSecondaryButton"],
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();
        var create = new Button
        {
            Content = "创建",
            Width = 96,
            Height = 30,
            Style = (Style)Application.Current.Resources["PcPrimaryButton"],
        };
        create.Click += (_, _) => TryCreate();
        row.Children.Add(cancel);
        row.Children.Add(create);
        form.Children.Add(row);
        Content = form;
    }

    private void TryCreate()
    {
        var name = string.IsNullOrWhiteSpace(_nameBox.Text) ? "未命名任务" : _nameBox.Text.Trim();
        var force = _forceBox.IsChecked == true;
        var action = _actionBox.SelectedIndex switch
        {
            0 => ActionKind.PowerOn,
            1 => force ? ActionKind.ForcePowerOff : ActionKind.PowerOff,
            _ => force ? ActionKind.ForceReboot : ActionKind.Reboot,
        };

        var task = new ScheduleTask
        {
            Name = name,
            Action = action,
            Time = _timeBox.Text.Trim(),
        };

        switch (_triggerBox.SelectedIndex)
        {
            case 1:
                task.Trigger = TriggerKind.Weekly;
                task.DaysOfWeek = _dayChecks.Where(d => d.Box.IsChecked == true).Select(d => d.DayName).ToList();
                if (task.DaysOfWeek.Count == 0)
                {
                    InfoDialog.Show(this, "参数有误", "「每周」至少选一个星期数。");
                    return;
                }
                break;
            case 2:
                task.Trigger = TriggerKind.Once;
                task.OnceAt = _onceBox.Text.Trim();
                break;
            default:
                task.Trigger = TriggerKind.Daily;
                break;
        }

        Task = task;
        DialogResult = true;
        Close();
    }
}

internal static class UiBrushes
{
    public static Brush WhiteBrush()
        => (Brush)Application.Current.Resources["PcTextPrimary"];
}
