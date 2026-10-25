namespace DS5BtHaptics;

/// <summary>Where bridge messages go: the console for the CLI, a log file for the tray app.</summary>
internal static class AppLog
{
    public static Action<string> Sink = Console.WriteLine;

    public static void Write(string message) => Sink(message);
}
