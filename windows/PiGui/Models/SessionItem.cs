namespace PiGui.Models;

public sealed record SessionItem(string Path, long ModifiedUnixMs, string Title)
{
    public string Display => $"{DateTimeOffset.FromUnixTimeMilliseconds(ModifiedUnixMs).LocalDateTime:MM-dd HH:mm}\n{Title}";
}
