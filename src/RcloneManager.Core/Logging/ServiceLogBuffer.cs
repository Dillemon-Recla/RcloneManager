using System.Collections.Concurrent;

namespace RcloneManager.Core.Logging;

public sealed class ServiceLogBuffer
{
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly int _maxEntries;
    private readonly string _logFilePath;
    private readonly object _fileLock = new();

    public ServiceLogBuffer(string workingDirectory, int maxEntries = 500)
    {
        _maxEntries = maxEntries;
        _logFilePath = Path.Combine(workingDirectory, "rclone_manager_service.log");
    }

    public string LogFilePath => _logFilePath;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy/MM/dd HH:mm:ss} [{level}] {message}";
        _entries.Enqueue(line);
        while (_entries.Count > _maxEntries && _entries.TryDequeue(out _))
        {
        }

        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Evita derrubar o serviço caso o arquivo esteja bloqueado por leitor externo.
        }
    }

    public List<string> GetRecent(int maxCount = 200)
    {
        var snapshot = _entries.ToArray();
        if (snapshot.Length <= maxCount)
            return snapshot.ToList();

        return snapshot[^maxCount..].ToList();
    }
}
