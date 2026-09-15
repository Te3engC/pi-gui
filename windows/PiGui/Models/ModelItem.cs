namespace PiGui.Models;

public sealed record ModelItem(string Provider, string Id, string Name)
{
    public string Display => string.IsNullOrWhiteSpace(Name) || Name == Id
        ? $"{Provider} / {Id}"
        : $"{Name} · {Provider}";
}
