using System.IO;
using System.Text.Json;

namespace VoxOralExam.DesktopApp.Services;

public static class LocalFileLogger
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly string LogDirectory =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

    private static readonly string LogFilePath =
        Path.Combine(LogDirectory, "desktopapp.jsonl");

    public static void Info(string category, string eventName, object? data = null)
    {
        Write("info", category, eventName, data);
    }

    public static void Error(string category, string eventName, Exception ex, object? data = null)
    {
        Write("error", category, eventName, new
        {
            data,
            exception = ex.ToString()
        });
    }

    public static void Clear()
    {
        try
        {
            lock (Sync)
            {
                if (File.Exists(LogFilePath))
                {
                    File.Delete(LogFilePath);
                }
            }
        }
        catch
        {
        }
    }

    private static void Write(string level, string category, string eventName, object? data)
    {
        try
        {
            var entry = new
            {
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                level,
                category,
                @event = eventName,
                data
            };

            var line = JsonSerializer.Serialize(entry, JsonOptions);
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
