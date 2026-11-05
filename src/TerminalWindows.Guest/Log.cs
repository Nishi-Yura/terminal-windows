namespace TerminalWindows.Guest;

internal static class Log
{
    private static readonly object Gate = new();
    public static string? FilePath { get; set; }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (Gate)
        {
            try
            {
                if (FilePath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 10 * 1024 * 1024) File.Move(FilePath, FilePath + ".old", true);
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
