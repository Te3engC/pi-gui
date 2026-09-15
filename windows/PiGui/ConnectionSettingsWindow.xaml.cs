using System.Windows;
using PiGui.Rpc;

namespace PiGui;

public partial class ConnectionSettingsWindow : Window
{
    public ConnectionOptions Options { get; private set; }

    public ConnectionSettingsWindow(ConnectionOptions options)
    {
        InitializeComponent();
        Options = options;
        TargetBox.Text = options.Target;
        WorkdirBox.Text = options.WorkingDirectory;
        PiPathBox.Text = options.PiExecutable;
        NodePathBox.Text = options.NodeBinDirectory;
        SshPathBox.Text = options.SshExecutable;
        SyncPortBox.Text = options.SyncPort.ToString();
        SyncTokenBox.Text = options.SyncToken;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TargetBox.Text) || string.IsNullOrWhiteSpace(WorkdirBox.Text) ||
            string.IsNullOrWhiteSpace(PiPathBox.Text) || string.IsNullOrWhiteSpace(NodePathBox.Text) ||
            string.IsNullOrWhiteSpace(SshPathBox.Text))
        {
            MessageBox.Show(this, "所有 SSH 设置均不能为空。", "Pi GUI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(SyncPortBox.Text, out var port) || port < 1024 || port > 65535) { MessageBox.Show(this, "同步端口无效。", "Pi GUI"); return; }
        Options = new ConnectionOptions(TargetBox.Text.Trim(), WorkdirBox.Text.Trim(), PiPathBox.Text.Trim(),
            NodePathBox.Text.Trim(), SshPathBox.Text.Trim(), SyncTokenBox.Text.Trim(), port);
        DialogResult = true;
    }
}
