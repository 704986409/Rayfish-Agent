namespace RayLink.App.Models;

public sealed record ChatEntry(string Sender, string Text, DateTimeOffset Timestamp, bool IsLocal)
{
    public string Time => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string Display => $"[{Time}] {Sender}: {Text}";
}
