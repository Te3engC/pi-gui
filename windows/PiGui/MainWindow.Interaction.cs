using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PiGui;

public partial class MainWindow
{
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static void ApplyUiFontSize(double size)
    {
        foreach (var baseline in new[] { 9, 10, 11, 12, 16, 18 })
            Application.Current.Resources["Ui" + baseline] = baseline * size / 12;
    }

    private void UiSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingUiSize || UiSizeBox.SelectedItem is not double size) return;
        ApplyUiFontSize(size);
        _uiPreferences = _uiPreferences with { UiFontSize = size };
        SaveUiPreferences();
    }

    private void DeliveryBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDeliveryHint();
    private void UpdateDeliveryHint()
    {
        // SelectionChanged can run before the rest of the XAML is constructed.
        if (DeliveryHint is null || SendButton is null) return;
        var steer = !UseCliSync && DeliveryBox.SelectedIndex == 1;
        DeliveryHint.Text = !_busy ? "空闲时立即发送" : steer
            ? "下个安全执行点介入，不强行中断工具"
            : "等待当前任务完成后执行";
        if (UseCliSync) DeliveryHint.Text += " · 同步模式仅支持等待";
        SendButton.ToolTip = _sending ? "正在提交…" : !_busy ? "发送消息" : steer ? "发送引导" : "加入等待队列";
    }

    private static IEnumerable<string> ReadQueue(JsonElement queue, string label)
    {
        if (queue.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in queue.EnumerateArray().Take(20))
        {
            var text = entry.ValueKind == JsonValueKind.String ? entry.GetString() ?? "" : MessageText(entry);
            if (string.IsNullOrWhiteSpace(text) && entry.ValueKind == JsonValueKind.Object)
                text = JsonString(entry, "message");
            if (string.IsNullOrWhiteSpace(text)) text = "消息已排队";
            text = text.Replace('\n', ' ');
            yield return $"{label} · {(text.Length > 160 ? text[..160] + "…" : text)}";
        }
    }

    private void ResetQueue()
    {
        QueuePanel.Visibility = Visibility.Collapsed;
        QueueText.Text = "队列为空";
        QueueDetails.Text = "";
    }

    private void Composer_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => AnimateComposer(true);
    private void Composer_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => AnimateComposer(false);
    private void AnimateComposer(bool focused)
    {
        if (ComposerBorder is null) return;
        var from = (ComposerBorder.BorderBrush as SolidColorBrush)?.Color ?? Colors.Gray;
        var to = focused ? Color.FromRgb(189, 104, 76) : _darkTheme ? Color.FromRgb(81, 81, 81) : Color.FromRgb(207, 201, 190);
        var brush = new SolidColorBrush(from);
        ComposerBorder.BorderBrush = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, to, TimeSpan.FromMilliseconds(160)));
    }
}
