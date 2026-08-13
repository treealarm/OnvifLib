namespace OnvifLib.Gui.Infrastructure;

/// <summary>One line in the bottom activity feed: a library call and how it ended.</summary>
public sealed record ActivityEntry(DateTime Time, string Text, bool IsError)
{
  public string Display => $"{Time:HH:mm:ss}  {Text}";
}
