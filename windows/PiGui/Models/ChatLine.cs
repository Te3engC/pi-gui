using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PiGui.Models;

public sealed class ChatLine : INotifyPropertyChanged
{
    private string _text;
    private bool _isStreaming;

    public ChatLine(string role, string text)
    {
        Role = role;
        _text = text;
    }

    public string Role { get; }
    public string DisplayRole => Role == "你" ? "你的指令" : Role;
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (_isStreaming == value) return;
            _isStreaming = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStreaming)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
