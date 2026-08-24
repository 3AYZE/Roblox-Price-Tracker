namespace RobloxPriceTracker.Infrastructure;

public sealed class AppLogger
{
    private readonly string _path;
    private readonly object _gate = new();

    public AppLogger(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}";
        lock (_gate)
        {
            RotateIfNeeded();
            File.AppendAllText(_path, line + Environment.NewLine);
        }

        Console.WriteLine(line);
    }

    private void RotateIfNeeded()
    {
        const long maxBytes = 2 * 1024 * 1024;
        if (!File.Exists(_path) || new FileInfo(_path).Length < maxBytes)
        {
            return;
        }

        var archived = _path + ".1";
        if (File.Exists(archived))
        {
            File.Delete(archived);
        }
        File.Move(_path, archived);
    }
}
