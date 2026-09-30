namespace CheckGithub.Models;

public sealed class AppSettings
{
    public string Token { get; set; } = "";
    public string RootDirectory { get; set; } = "";
    public int Parallelism { get; set; } = 3;
    public bool ShallowClone { get; set; }
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
}
