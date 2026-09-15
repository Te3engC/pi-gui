using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PiGui.Models;

public sealed class TodoItem : INotifyPropertyChanged
{
    private string _status;

    public TodoItem(string id, string subject, string status)
    {
        Id = id;
        Subject = subject;
        _status = status;
    }

    public string Id { get; }
    public string Subject { get; }
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Marker)));
        }
    }

    public string Marker => Status switch { "completed" => "✓", "in_progress" => "●", _ => "○" };
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ProcessItem(string Time, string Title, string Detail, bool IsActive);
