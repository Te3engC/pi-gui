namespace PiGui.Models;

public sealed record CommandItem(string Name, string Description, string Source)
{
    public string SourceLabel => Source switch { "extension" => "扩展", "skill" => "技能", "prompt" => "模板", _ => "Pi" };
}
