using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PiGui.Models;
using PiGui.Rpc;

namespace PiGui;

public partial class MainWindow : Window
{
    private readonly SshRpcClient _client = new();
    private readonly CliSyncClient _syncClient = new();
    private bool UseCliSync => !string.IsNullOrWhiteSpace(_options.SyncToken);
    private ConnectionOptions _options = new("user@<server-ip>", "/home/<user>", "/home/<user>/bin/pi", "/home/<user>/local/bin", "ssh");
    private ChatLine? _streamingLine;
    private ChatLine? _thinkingLine;
    private readonly ScaleTransform _statusPulseTransform = new(1, 1);
    private bool _busy;
    private bool _sending;
    private bool _settingUiSize;
    private readonly Dictionary<string, ChatLine> _toolCards = [];
    private LocalAttachment? _attachment;
    private bool _switchingSession;
    private bool _darkTheme;
    private UiPreferences _uiPreferences = UiPreferences.Default;
    private readonly System.Windows.Threading.DispatcherTimer _thinkingTimer = new() { Interval = TimeSpan.FromMilliseconds(360) };
    private int _thinkingFrame;
    private readonly List<CommandItem> _commands = [];
    private readonly ObservableCollection<ModelItem> _models = [];
    private readonly ObservableCollection<string> _thinkingLevels = [];
    private bool _settingModel;
    private bool _settingThinking;
    private Brush _assistantBrush = Brushes.WhiteSmoke;
    private Brush _mutedBrush = Brushes.DarkGray;
    public ObservableCollection<SessionItem> Sessions { get; } = [];
    public ObservableCollection<ChatLine> ChatMessages { get; } = [];

    public MainWindow()
    {
        _uiPreferences = UiPreferencesStore.Load();
        ApplyUiFontSize(_uiPreferences.UiFontSize);
        InitializeComponent();
        _settingUiSize = true;
        UiSizeBox.ItemsSource = new double[] { 11, 12, 13, 14, 15, 16 };
        UiSizeBox.SelectedItem = _uiPreferences.UiFontSize;
        _settingUiSize = false;
        SessionList.ItemsSource = Sessions;
        CommandPopup.PlacementTarget = PromptTextBox;
        ModelBox.ItemsSource = _models;
        ThinkingBox.ItemsSource = _thinkingLevels;
        Width = _uiPreferences.WindowWidth; Height = _uiPreferences.WindowHeight;
        ApplyTheme(_uiPreferences.DarkTheme);
        TranscriptItems.FontFamily = PromptTextBox.FontFamily = new FontFamily(_uiPreferences.FontFamily);
        TranscriptItems.FontSize = PromptTextBox.FontSize = _uiPreferences.FontSize;
        TranscriptItems.FontWeight = PromptTextBox.FontWeight = _uiPreferences.Bold ? FontWeights.Bold : FontWeights.Normal;
        _thinkingTimer.Tick += (_, _) =>
        {
            _thinkingFrame = (_thinkingFrame + 1) % 4;
            StatusText.Text = "Pi 思考中" + new string('.', _thinkingFrame);
            SetStreamingCursorVisible(_thinkingFrame % 2 == 0);
        };
        _client.EventReceived += item => Dispatcher.InvokeAsync(() => HandleEvent(item));
        _syncClient.EventReceived += item => Dispatcher.InvokeAsync(() => HandleEvent(item));
        _syncClient.ErrorReceived += error => Dispatcher.InvokeAsync(() => Append("错误", error, Brushes.IndianRed));
        _client.ErrorReceived += error => Dispatcher.InvokeAsync(() => Append("错误", error, Brushes.IndianRed));
        _client.Disconnected += () => Dispatcher.InvokeAsync(SetDisconnectedUi);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AnimateEntrance(SidebarBorder, -18, TimeSpan.Zero);
        AnimateEntrance(HeaderBorder, -10, TimeSpan.FromMilliseconds(70));
        AnimateEntrance(TranscriptBorder, 14, TimeSpan.FromMilliseconds(120));
        AnimateEntrance(ComposerBorder, 16, TimeSpan.FromMilliseconds(180));
        StatusDot.RenderTransform = _statusPulseTransform;

    }

    private static void AnimateEntrance(UIElement element, double offsetY, TimeSpan delay)
    {
        var transform = new TranslateTransform(0, offsetY);
        element.RenderTransform = transform;
        element.Opacity = 0;
        var duration = new Duration(TimeSpan.FromMilliseconds(260));
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = delay });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, duration) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void StartActivityPulse()
    {
        _statusPulseTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.65, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        _statusPulseTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.65, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        StatusDot.Fill = Brushes.Coral; _busy = true; UpdateDeliveryHint();
    }

    private void StopActivityPulse()
    {
        _statusPulseTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _statusPulseTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _statusPulseTransform.ScaleX = _statusPulseTransform.ScaleY = 1;
        StatusDot.Fill = Brushes.MediumSeaGreen; _busy = false; UpdateDeliveryHint();
    }

    private void AppearanceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AppearanceSettingsWindow(_darkTheme, TranscriptItems.FontFamily.Source, TranscriptItems.FontSize, TranscriptItems.FontWeight == FontWeights.Bold) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.Dark != _darkTheme) ApplyTheme(dialog.Dark);
        TranscriptItems.FontFamily = PromptTextBox.FontFamily = new FontFamily(dialog.Family);
        TranscriptItems.FontSize = PromptTextBox.FontSize = dialog.Size;
        TranscriptItems.FontWeight = PromptTextBox.FontWeight = dialog.Bold ? FontWeights.Bold : FontWeights.Normal;
        _uiPreferences = new UiPreferences(dialog.Dark, dialog.Family, dialog.Size, dialog.Bold, Width, Height) { UiFontSize = _uiPreferences.UiFontSize };
        SaveUiPreferences();
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e) => ApplyTheme(!_darkTheme);

    private void ApplyTheme(bool dark)
    {
        _darkTheme = dark;
        var window = dark ? "#171717" : "#FAF9F6";
        var panel = dark ? "#1E1E1E" : "#FFFEFB";
        var sidebar = dark ? "#202020" : "#F0EEE8";
        var input = dark ? "#242424" : "#FFFFFF";
        var foreground = dark ? "#F4F1EC" : "#2B2926";
        var button = dark ? "#2B2B2B" : "#E9E6DE";
        var border = dark ? "#3B3B3B" : "#D9D4C9";
        Brush brush(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        Background = brush(window); Foreground = brush(foreground); ContentPanel.Background = brush(window);
        SidebarBorder.Background = brush(sidebar); SidebarBorder.BorderBrush = brush(border);
        HeaderBorder.Background = brush(dark ? "#1B1B1B" : "#FCFBF8"); HeaderBorder.BorderBrush = brush(border);
        TranscriptBorder.Background = brush(window); TranscriptBorder.BorderBrush = brush(border);
        AttachmentPanel.Background = brush(dark ? "#202020" : "#F7F4EE"); AttachmentPanel.BorderBrush = brush(border);
        QueuePanel.Background = brush(dark ? "#302720" : "#FBF2E9"); QueuePanel.BorderBrush = brush(dark ? "#70533F" : "#E4C5A9"); QueueText.Foreground = brush(dark ? "#E9C8AF" : "#795844");
        ComposerBorder.Background = brush(input); ComposerBorder.BorderBrush = brush(dark ? "#515151" : "#CFC9BE");
        // Resource lookup also reaches popup contents created after startup.
        var resources = Application.Current.Resources;
        resources["Canvas"] = brush(window); resources["Surface"] = brush(input);
        resources["Sidebar"] = brush(sidebar); resources["Ink"] = brush(foreground);
        resources["Muted"] = brush(dark ? "#B3AEA5" : "#79746B");
        resources["Line"] = brush(border); resources["Hover"] = brush(dark ? "#38352F" : "#EAE6DE");
        resources["Selected"] = brush(dark ? "#494035" : "#E8DFD2");
        SendButton.Background = brush("#C76C4E"); SendButton.BorderBrush = brush("#C76C4E"); SendButton.Foreground = Brushes.White;
        TranscriptItems.Foreground = brush(foreground); PromptTextBox.Background = Brushes.Transparent; PromptTextBox.Foreground = brush(foreground);
        Resources["MessageCardBackground"] = brush(dark ? "#1F1F1F" : "#FFFFFF");
        Resources["MessageCardBorder"] = brush(dark ? "#7A7770" : "#B5AEA4");
        Resources["UserCardBackground"] = brush(dark ? "#39312A" : "#F3EAE0");
        Resources["ActivityCardBackground"] = brush(dark ? "#292824" : "#F2F0EB");
        Resources["MessageText"] = brush(foreground);
        SessionList.Foreground = brush(foreground); _assistantBrush = brush(foreground); _mutedBrush = brush(dark ? "#B3AEA5" : "#6C675F");
        ThemeButton.Content = dark ? "浅色" : "深色";
    }

    private void SaveUiPreferences()
    {
        try { UiPreferencesStore.Save(_uiPreferences); }
        catch (Exception exception) { Append("错误", "无法保存界面设置：" + exception.Message, Brushes.IndianRed); }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private void FontBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || FontBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        var family = item.Content?.ToString() ?? "Consolas";
        TranscriptItems.FontFamily = new FontFamily(family); PromptTextBox.FontFamily = new FontFamily(family);
    }
    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        TranscriptItems.FontSize = e.NewValue; PromptTextBox.FontSize = e.NewValue;
    }
    private void FontWeightBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var weight = FontWeightBox.SelectedIndex == 1 ? FontWeights.Bold : FontWeights.Normal;
        TranscriptItems.FontWeight = weight; PromptTextBox.FontWeight = weight;
    }
    private void PromptTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var text = PromptTextBox.Text;
        if (!text.StartsWith('/')) { CommandPopup.IsOpen = false; return; }
        UpdateCommandSuggestions(text, PromptTextBox);
        if (CommandSuggestions.Items.Count > 0) CommandSuggestions.SelectedIndex = 0;
    }
    private void CommandSuggestions_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CommandSuggestions.SelectedItem is not CommandItem command || Keyboard.FocusedElement != CommandSuggestions) return;
        InsertCommand(command);
    }
    private void PromptTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CommandPopup.IsOpen && e.Key == Key.Tab && CommandSuggestions.SelectedItem is CommandItem command)
        { InsertCommand(command); e.Handled = true; return; }
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        { e.Handled = true; _ = SendAsync(); }
    }

    private async Task LoadCommandsAsync()
    {
        CommandItem[] commands;
        if (UseCliSync) commands = await _syncClient.GetCommandsAsync(_options.SyncPort, _options.SyncToken);
        else
        {
            var response = await _client.SendCommandAsync(new { type = "get_commands" });
            if (!Success(response) || !response.TryGetProperty("data", out var data) || !data.TryGetProperty("commands", out var values)) return;
            commands = values.EnumerateArray().Select(item => new CommandItem(
                "/" + JsonString(item, "name"), JsonString(item, "description"), JsonString(item, "source"))).Where(command => command.Name.Length > 1).ToArray();
        }
        _commands.Clear(); _commands.AddRange(commands.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase));
    }

    private void CommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (_commands.Count == 0) { StatusText.Text = "暂无可用命令"; return; }
        CommandFilterBox.Text = "";
        UpdateCommandSuggestions("", CommandButton);
        CommandPopup.IsOpen = true;
        CommandFilterBox.Focus();
    }

    private void CommandFilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateCommandSuggestions(CommandFilterBox.Text, CommandButton);

    private void CommandFilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { CommandSuggestions.Focus(); CommandSuggestions.SelectedIndex = Math.Max(0, CommandSuggestions.SelectedIndex); e.Handled = true; return; }
        if (e.Key == Key.Escape) { CommandPopup.IsOpen = false; PromptTextBox.Focus(); e.Handled = true; return; }
        if (e.Key == Key.Enter && CommandSuggestions.SelectedItem is CommandItem command) { InsertCommand(command); e.Handled = true; }
    }

    private void InsertCommand(CommandItem command)
    {
        PromptTextBox.Text = command.Name + " "; PromptTextBox.CaretIndex = PromptTextBox.Text.Length; CommandPopup.IsOpen = false; PromptTextBox.Focus();
    }

    private void UpdateCommandSuggestions(string filter, UIElement placementTarget)
    {
        var query = filter.Trim();
        var matches = _commands.Where(command => string.IsNullOrEmpty(query) || command.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || command.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(12).ToList();
        CommandSuggestions.ItemsSource = matches;
        CommandPopup.PlacementTarget = placementTarget;
        if (placementTarget == PromptTextBox) CommandPopup.IsOpen = matches.Count > 0;
    }

    private async Task LoadModelsAsync()
    {
        if (UseCliSync) { ModelBox.ToolTip = "CLI 同步模式不支持从 GUI 切换模型"; return; }
        var modelsResponse = await _client.SendCommandAsync(new { type = "get_available_models" });
        var stateResponse = await _client.SendCommandAsync(new { type = "get_state" });
        if (!Success(modelsResponse) || !modelsResponse.TryGetProperty("data", out var modelData) || !modelData.TryGetProperty("models", out var values)) return;
        var activeProvider = ""; var activeId = "";
        if (Success(stateResponse) && stateResponse.TryGetProperty("data", out var state) && state.TryGetProperty("model", out var active))
        { activeProvider = JsonString(active, "provider"); activeId = JsonString(active, "id"); }
        _settingModel = true;
        try
        {
            _models.Clear();
            foreach (var model in values.EnumerateArray())
            {
                var provider = JsonString(model, "provider"); var id = JsonString(model, "id");
                if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(id)) _models.Add(new ModelItem(provider, id, JsonString(model, "name")));
            }
            ModelBox.SelectedItem = _models.FirstOrDefault(model => model.Provider == activeProvider && model.Id == activeId);
            if (ModelBox.SelectedItem is null && _models.Count > 0) ModelBox.SelectedIndex = 0;
            ModelBox.IsEnabled = _models.Count > 0;
        }
        finally { _settingModel = false; }
    }

    private async void ModelBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settingModel || ModelBox.SelectedItem is not ModelItem model || !_client.IsConnected) return;
        try
        {
            ModelBox.IsEnabled = false; StatusText.Text = "正在切换模型…";
            var response = await _client.SendCommandAsync(new { type = "set_model", provider = model.Provider, modelId = model.Id });
            if (!Success(response)) throw new InvalidOperationException(Error(response));
            StatusText.Text = "已切换到 " + model.Display;
        }
        catch (Exception exception) { Append("错误", "切换模型失败：" + exception.Message, Brushes.IndianRed); await LoadModelsAsync(); }
        finally { ModelBox.IsEnabled = _models.Count > 0; }
    }

    private async Task LoadThinkingLevelsAsync()
    {
        if (UseCliSync) { ThinkingBox.ToolTip = "CLI 同步模式不支持从 GUI 切换思考强度"; return; }
        var levelsResponse = await _client.SendCommandAsync(new { type = "get_available_thinking_levels" });
        var stateResponse = await _client.SendCommandAsync(new { type = "get_state" });
        if (!Success(levelsResponse) || !levelsResponse.TryGetProperty("data", out var levelData) || !levelData.TryGetProperty("levels", out var values)) return;
        var active = Success(stateResponse) && stateResponse.TryGetProperty("data", out var state) ? JsonString(state, "thinkingLevel") : "";
        _settingThinking = true;
        try
        {
            _thinkingLevels.Clear();
            foreach (var level in values.EnumerateArray())
            {
                var value = level.GetString();
                if (!string.IsNullOrWhiteSpace(value)) _thinkingLevels.Add(value);
            }
            ThinkingBox.SelectedItem = _thinkingLevels.FirstOrDefault(level => level == active);
            if (ThinkingBox.SelectedItem is null && _thinkingLevels.Count > 0) ThinkingBox.SelectedIndex = 0;
            ThinkingBox.IsEnabled = _thinkingLevels.Count > 0;
        }
        finally { _settingThinking = false; }
    }

    private async void ThinkingBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settingThinking || ThinkingBox.SelectedItem is not string level || !_client.IsConnected) return;
        try
        {
            ThinkingBox.IsEnabled = false; StatusText.Text = "正在设置思考强度…";
            var response = await _client.SendCommandAsync(new { type = "set_thinking_level", level });
            if (!Success(response)) throw new InvalidOperationException(Error(response));
            StatusText.Text = "思考强度：" + level;
        }
        catch (Exception exception) { Append("错误", "设置思考强度失败：" + exception.Message, Brushes.IndianRed); await LoadThinkingLevelsAsync(); }
        finally { ThinkingBox.IsEnabled = _thinkingLevels.Count > 0; }
    }

    private static string JsonString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionSettingsWindow(_options) { Owner = this };
        if (dialog.ShowDialog() == true) _options = dialog.Options;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        StatusText.Text = "正在连接…";
        try
        {
            if (UseCliSync) await _syncClient.ConnectAsync(_options, _options.SyncToken, _options.SyncPort, CancellationToken.None);
            else { await _client.ConnectAsync(_options, CancellationToken.None); await RestoreMessagesAsync(); await RefreshSessionsAsync(); await LoadModelsAsync(); await LoadThinkingLevelsAsync(); }
            await LoadCommandsAsync();
            StatusText.Text = "已连接"; StatusDot.Fill = Brushes.MediumSeaGreen;
            SendButton.IsEnabled = true;
            SteerOption.IsEnabled = !UseCliSync;
            if (UseCliSync) DeliveryBox.SelectedIndex = 0;
            UpdateDeliveryHint();
            NewSessionButton.IsEnabled = SidebarNewSessionButton.IsEnabled = !UseCliSync;
        }
        catch (Exception exception)
        {
            SetDisconnectedUi();
            Append("错误", $"连接失败：{exception.Message}\n请检查 ⚙ SSH 设置，并先在 PowerShell 执行：ssh {_options.Target} true", Brushes.IndianRed);
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (_sending) return;
        var attachment = _attachment;
        var text = PromptTextBox.Text.Trim();
        if (text.Length == 0 && attachment is null) return;
        if (!(UseCliSync ? _syncClient.IsConnected : _client.IsConnected)) { Append("错误", "尚未连接 Pi。", Brushes.IndianRed); return; }
        _sending = true; SendButton.IsEnabled = false;
        var delivery = !UseCliSync && DeliveryBox.SelectedIndex == 1 ? "steer" : "followUp";
        try
        {
            var prompt = text;
            object? images = null;
            if (attachment is { IsImage: true } image)
                images = new[] { new { type = "image", data = Convert.ToBase64String(image.Bytes), mimeType = image.MimeType } };
            else if (attachment is { } file)
            {
                StatusText.Text = "正在通过 SSH 上传文件…";
                var path = await UploadFileAsync(file);
                prompt += $"\n\n本地附件已上传到远端：{path}\n请读取并分析此文件。";
            }

            StatusText.Text = "Pi 正在处理…";
            if (UseCliSync) {
                object? syncImage = attachment is { IsImage: true } direct ? new { data = Convert.ToBase64String(direct.Bytes), mimeType = direct.MimeType } : null;
                await _syncClient.SendPromptAsync(_options.SyncPort, _options.SyncToken, prompt, syncImage);
            } else {
                var command = new Dictionary<string, object?> { ["type"] = "prompt", ["message"] = prompt, ["streamingBehavior"] = delivery };
                if (images is not null) command["images"] = images;
                var response = await _client.SendCommandAsync(command);
                if (!Success(response)) throw new InvalidOperationException(Error(response));
            }
            Append("你", text + (attachment is null ? "" : $"\n[附件：{attachment.Name}]"), Brushes.DodgerBlue);
            if (PromptTextBox.Text.Trim() == text) PromptTextBox.Clear();
            if (ReferenceEquals(_attachment, attachment)) ClearAttachment();
        }
        catch (Exception exception) { Append("错误", $"发送失败：{exception.Message}", Brushes.IndianRed); }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = UseCliSync ? _syncClient.IsConnected : _client.IsConnected;
            UpdateDeliveryHint();
        }
    }

    private async Task<string> UploadFileAsync(LocalAttachment file)
    {
        if (file.Bytes.Length > 20 * 1024 * 1024) throw new InvalidOperationException("文件超过 20 MiB 限制。");
        var safeName = string.Concat(file.Name.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_'));
        var directory = $"{_options.WorkingDirectory}/.pi-gui-uploads";
        var remotePath = $"{directory}/{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}-{safeName}";
        var command = $"mkdir -p {QuoteShell(directory)} && base64 -d > {QuoteShell(remotePath)}";
        var info = new ProcessStartInfo { FileName = _options.SshExecutable, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-T"); info.ArgumentList.Add("-o"); info.ArgumentList.Add("BatchMode=yes"); info.ArgumentList.Add(_options.Target); info.ArgumentList.Add(command);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 ssh 上传进程。");
        await process.StandardInput.WriteAsync(Convert.ToBase64String(file.Bytes));
        process.StandardInput.Close();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"文件上传失败：{stderr.Trim()}");
        return remotePath;
    }

    private async void RefreshSessionsButton_Click(object sender, RoutedEventArgs e) => await RefreshSessionsAsync();

    private async Task RefreshSessionsAsync()
    {
        if (!_client.IsConnected) return;
        StatusText.Text = "正在读取服务器历史对话…";
        const string script = """
import base64,glob,json,os
root=os.path.expanduser('~/.pi/agent/sessions')
items=[]
for path in sorted(glob.glob(root+'/**/*.jsonl',recursive=True),key=os.path.getmtime,reverse=True)[:100]:
    fallback=os.path.basename(path).split('_',1)[0]
    prompt_title=''
    session_name=''
    try:
        with open(path,encoding='utf-8',errors='replace') as f:
            for _,line in zip(range(160),f):
                row=json.loads(line)
                if row.get('type')=='session_info' and row.get('name'):
                    session_name=str(row['name']).replace('\\n',' ')[:56]
                msg=row.get('message',{})
                if not prompt_title and msg.get('role')=='user':
                    value=msg.get('content','')
                    if isinstance(value,list): value=' '.join(x.get('text','') for x in value if isinstance(x,dict))
                    if value: prompt_title=str(value).replace('\\n',' ')[:56]
    except Exception: pass
    title=session_name or prompt_title or fallback
    encoded_path=base64.b64encode(path.encode()).decode()
    encoded_title=base64.b64encode(title.encode()).decode()
    print(encoded_path+chr(9)+str(int(os.path.getmtime(path)*1000))+chr(9)+encoded_title)
""";
        try
        {
            var output = await RunRemoteCommandAsync($"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(script))} | base64 -d | python3");
            var items = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t'))
                .Where(parts => parts.Length == 3 && long.TryParse(parts[1], out _))
                .Select(parts => new SessionItem(
                    Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])),
                    long.Parse(parts[1]),
                    Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]))))
                .ToList();
            var current = (SessionList.SelectedItem as SessionItem)?.Path;
            Sessions.Clear(); foreach (var item in items) Sessions.Add(item);
            if (current is not null) SessionList.SelectedItem = Sessions.FirstOrDefault(x => x.Path == current);
            StatusText.Text = "已同步历史对话。";
        }
        catch (Exception exception) { Append("错误", "读取历史对话失败：" + exception.Message, Brushes.IndianRed); }
    }

    private async void SessionList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_switchingSession || SessionList.SelectedItem is not SessionItem selected || !_client.IsConnected) return;
        _switchingSession = true;
        try
        {
            StatusText.Text = "正在切换对话…";
            var response = await _client.SendCommandAsync(new { type = "switch_session", sessionPath = selected.Path });
            if (!Success(response)) { Append("错误", Error(response), Brushes.IndianRed); return; }
            await RestoreMessagesAsync();
            await LoadModelsAsync();
            await LoadThinkingLevelsAsync();
            ConversationTitle.Text = selected.Title;
            StatusText.Text = "已切换：" + selected.Title;
        }
        catch (Exception exception) { Append("错误", "切换失败：" + exception.Message, Brushes.IndianRed); }
        finally { _switchingSession = false; }
    }

    private async Task<string> RunRemoteCommandAsync(string command)
    {
        var info = new ProcessStartInfo { FileName = _options.SshExecutable, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-T"); info.ArgumentList.Add("-o"); info.ArgumentList.Add("BatchMode=yes"); info.ArgumentList.Add(_options.Target); info.ArgumentList.Add(command);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 ssh.exe。");
        var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var output = await outputTask; var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
        return output;
    }

    private async void NewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "创建新的 Pi 对话？", "Pi GUI", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var response = await _client.SendCommandAsync(new { type = "new_session" });
        if (Success(response)) { ChatMessages.Clear(); ResetQueue(); await RefreshSessionsAsync(); await LoadModelsAsync(); await LoadThinkingLevelsAsync(); ConversationTitle.Text = "新对话"; StatusText.Text = "已创建新对话"; }
        else Append("错误", Error(response), Brushes.IndianRed);
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (UseCliSync) await _syncClient.AbortAsync(_options.SyncPort, _options.SyncToken);
            else await _client.SendCommandAsync(new { type = "abort" });
        }
        catch (Exception exception) { Append("错误", exception.Message, Brushes.IndianRed); }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control || e.Key != Key.V) return;
        try
        {
            if (!Clipboard.ContainsImage()) return;
            var bitmap = Clipboard.GetImage(); if (bitmap is null) return; bitmap.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream);
            SetAttachment(new LocalAttachment("clipboard.png", stream.ToArray(), "image/png", true, bitmap)); e.Handled = true;
        }
        catch (Exception exception) { Append("错误", exception.Message, Brushes.IndianRed); }
    }

    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var bytes = File.ReadAllBytes(dialog.FileName);
        var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        var image = extension is ".png" or ".jpg" or ".jpeg" or ".webp";
        BitmapSource? preview = null;
        if (image) { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri(dialog.FileName); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze(); preview = bitmap; }
        var mime = extension switch { ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".png" => "image/png", _ => "application/octet-stream" };
        SetAttachment(new LocalAttachment(Path.GetFileName(dialog.FileName), bytes, mime, image, preview));
    }

    private void ClearAttachmentButton_Click(object sender, RoutedEventArgs e) => ClearAttachment();
    private void SetAttachment(LocalAttachment attachment)
    {
        _attachment = attachment; AttachmentPanel.Visibility = Visibility.Visible; AttachmentText.Text = $"附件：{attachment.Name}（{attachment.Bytes.Length / 1024.0:F1} KiB）"; ClearAttachmentButton.Visibility = Visibility.Visible;
        PreviewImage.Source = attachment.Preview; PreviewImage.Visibility = attachment.Preview is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ClearAttachment()
    {
        _attachment = null; AttachmentPanel.Visibility = Visibility.Collapsed; PreviewImage.Source = null; PreviewImage.Visibility = Visibility.Collapsed; ClearAttachmentButton.Visibility = Visibility.Collapsed;
        AttachmentText.Text = "点击 + 选择图片或文件；Ctrl+V 粘贴图片。";
    }

    private async Task RestoreMessagesAsync()
    {
        var response = await _client.SendCommandAsync(new { type = "get_messages" });
        if (!Success(response) || !response.TryGetProperty("data", out var data) || !data.TryGetProperty("messages", out var messages)) return;
        ChatMessages.Clear(); ResetQueue();
        foreach (var message in messages.EnumerateArray())
        {
            var role = message.GetProperty("role").GetString();
            var text = MessageText(message);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (role == "user") Append("你", text, Brushes.DodgerBlue);
            else if (role == "assistant") Append("Pi", text, _assistantBrush);
        }
    }

    private void HandleEvent(JsonElement item)
    {
        var type = item.TryGetProperty("type", out var t) ? t.GetString() : "";
        if (type == "agent_start") { _thinkingFrame = 0; _thinkingTimer.Start(); StartActivityPulse(); StopButton.IsEnabled = true; _thinkingLine = Append("思考", "Pi 正在分析…", Brushes.MediumPurple); return; }
        if (type is "agent_settled" or "agent_end") { _thinkingTimer.Stop(); StopActivityPulse(); StatusText.Text = "已就绪"; StopButton.IsEnabled = false; ClearStreamingCursor(); if (_streamingLine is not null) _streamingLine.IsStreaming = false; _streamingLine = _thinkingLine = null; return; }
        if (type == "message_start" && item.TryGetProperty("message", out var start) && Role(start, "assistant")) { _streamingLine = Append("Pi", "", _assistantBrush); _streamingLine.IsStreaming = true; AddStreamingCursor(); return; }
        if (type == "message_update" && item.TryGetProperty("assistantMessageEvent", out var delta) && delta.TryGetProperty("type", out var dt))
        {
            var deltaType = dt.GetString(); var value = delta.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "";
            if (deltaType == "thinking_delta") { _thinkingLine ??= Append("思考", "", Brushes.MediumPurple); _thinkingLine.Text += value; ScrollEnd(); }
            else if (deltaType == "text_delta") { _streamingLine ??= Append("Pi", "", _assistantBrush); _streamingLine.IsStreaming = true; ClearStreamingCursor(); _streamingLine.Text += value; AddStreamingCursor(); ScrollEnd(); }
            return;
        }
        if (type == "tool_execution_start") { var id=item.GetProperty("toolCallId").GetString() ?? Guid.NewGuid().ToString(); var tool = item.GetProperty("toolName").GetString() ?? "工具"; _toolCards[id]=Append("工具", "▶ " + tool + " 运行中…", Brushes.DarkGoldenrod); return; }
        if (type == "tool_execution_update" && item.TryGetProperty("toolCallId", out var updateId) && _toolCards.TryGetValue(updateId.GetString() ?? "", out var card)) { card.Text += "\n… 实时输出已更新"; ScrollEnd(); return; }
        if (type == "tool_execution_end" && item.TryGetProperty("toolCallId", out var endId) && _toolCards.TryGetValue(endId.GetString() ?? "", out var done)) { var failed = item.TryGetProperty("isError",out var err)&&err.GetBoolean(); done.Text += "\n" + (failed ? "✗ 执行失败" : "✓ 执行完成"); return; }
        if (type == "queue_update")
        {
            var followUp = item.TryGetProperty("followUp", out var followUpQueue) && followUpQueue.ValueKind == JsonValueKind.Array ? followUpQueue.GetArrayLength() : 0;
            var steering = item.TryGetProperty("steering", out var steeringQueue) && steeringQueue.ValueKind == JsonValueKind.Array ? steeringQueue.GetArrayLength() : 0;
            QueueDetails.Text = string.Join("\n", ReadQueue(steeringQueue, "引导").Concat(ReadQueue(followUpQueue, "等待")));
            var count = followUp + steering;
            var wasHidden = QueuePanel.Visibility != Visibility.Visible;
            QueuePanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (count > 0)
            {
                QueueText.Text = $"等待 {followUp} 条 · 引导 {steering} 条（下个安全执行点介入）";
                if (wasHidden) AnimateEntrance(QueuePanel, 6, TimeSpan.Zero);
            }
            return;
        }
        if (type == "compaction_start") { Append("上下文", "正在压缩上下文…", Brushes.SteelBlue); return; }
        if (type == "auto_retry_start") { Append("重试", "请求失败，Pi 正在自动重试…", Brushes.IndianRed); }
    }

    private const string VisibleCursor = " ▍";
    private const string HiddenCursor = "  ";

    private void AddStreamingCursor()
    {
        if (_streamingLine is null || _streamingLine.Text.EndsWith(VisibleCursor, StringComparison.Ordinal) || _streamingLine.Text.EndsWith(HiddenCursor, StringComparison.Ordinal)) return;
        _streamingLine.Text += VisibleCursor;
    }

    private void SetStreamingCursorVisible(bool visible)
    {
        if (_streamingLine is null) return;
        if (_streamingLine.Text.EndsWith(VisibleCursor, StringComparison.Ordinal) || _streamingLine.Text.EndsWith(HiddenCursor, StringComparison.Ordinal))
            _streamingLine.Text = _streamingLine.Text[..^2] + (visible ? VisibleCursor : HiddenCursor);
    }

    private void ClearStreamingCursor()
    {
        if (_streamingLine is null) return;
        if (_streamingLine.Text.EndsWith(VisibleCursor, StringComparison.Ordinal) || _streamingLine.Text.EndsWith(HiddenCursor, StringComparison.Ordinal))
            _streamingLine.Text = _streamingLine.Text[..^2];
    }

    private ChatLine Append(string role, string text, Brush color)
    {
        var line = new ChatLine(role == "我" ? "你" : role, text);
        ChatMessages.Add(line);
        ScrollEnd();
        return line;
    }

    private void ScrollEnd() => Dispatcher.BeginInvoke(TranscriptScrollViewer.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
    private static bool Role(JsonElement message, string role) => message.TryGetProperty("role", out var r) && r.GetString() == role;
    private static string MessageText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var c)) return ""; if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        return c.ValueKind == JsonValueKind.Array ? string.Concat(c.EnumerateArray().Where(x => x.TryGetProperty("type", out var t) && t.GetString() == "text").Select(x => x.GetProperty("text").GetString())) : "";
    }
    private static bool Success(JsonElement result) => result.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
    private static string Error(JsonElement result) => result.TryGetProperty("error", out var e) ? e.GetString() ?? "RPC 失败" : "RPC 失败";
    private static string QuoteShell(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private void SetDisconnectedUi()
    {
        _thinkingTimer.Stop(); StopActivityPulse(); ClearStreamingCursor(); ResetQueue();
        ConnectButton.IsEnabled = true; SendButton.IsEnabled = NewSessionButton.IsEnabled = SidebarNewSessionButton.IsEnabled = StopButton.IsEnabled = false;
        ModelBox.IsEnabled = ThinkingBox.IsEnabled = false; StatusText.Text = "未连接"; StatusDot.Fill = Brushes.DimGray;
    }
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiPreferences = _uiPreferences with { WindowWidth = Width, WindowHeight = Height };
        SaveUiPreferences();
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _syncClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

public sealed record LocalAttachment(string Name, byte[] Bytes, string MimeType, bool IsImage, BitmapSource? Preview);
