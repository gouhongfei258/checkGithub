namespace CheckGithub.Models;

public enum CloneState
{
    NotDownloaded,
    Queued,
    Downloading,
    Completed,
    AlreadyExists,
    Failed,
    Cancelled,
}
