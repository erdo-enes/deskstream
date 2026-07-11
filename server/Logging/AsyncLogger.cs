using System.Collections.Concurrent;
using System.IO;

namespace DeskStreamer.Server.Logging;

public static class AsyncLogger
{
    private static readonly ConcurrentQueue<LogEntry> Queue = new();
    private static readonly BlockingCollection<LogEntry> Collection = new(Queue);
    private static readonly Thread WriterThread;
    private static string? _logPath;
    private static bool _enabled;

    static AsyncLogger()
    {
        WriterThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "async-logger"
        };
        WriterThread.Start();
    }

    /// <summary>
    /// Initializes the logger to write to the specified file path.
    /// </summary>
    public static void Initialize(string logPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _logPath = logPath;
            _enabled = true;

            Info("=== DeskStream Server Log Started ===");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[logger] Failed to initialize: {ex.Message}");
        }
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);
    public static void Debug(string message) => Log("DEBUG", message);

    private static void Log(string level, string message)
    {
        if (!_enabled || _logPath == null) return;

        var entry = new LogEntry(
            DateTimeOffset.Now,
            Thread.CurrentThread.ManagedThreadId,
            Thread.CurrentThread.Name ?? "worker",
            level,
            message
        );

        try
        {
            Collection.Add(entry);
        }
        catch
        {
            // Ignore if collection is completed/closed
        }
    }

    private static void WriteLoop()
    {
        while (true)
        {
            try
            {
                if (Collection.TryTake(out LogEntry entry, Timeout.Infinite))
                {
                    if (_logPath == null) continue;

                    // Open file stream and batch-write any pending items
                    using (var writer = new StreamWriter(_logPath, append: true) { AutoFlush = false })
                    {
                        WriteEntry(writer, entry);
                        while (Collection.TryTake(out LogEntry nextEntry, 0))
                        {
                            WriteEntry(writer, nextEntry);
                        }
                        writer.Flush();
                    }
                }
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
            catch (Exception ex)
            {
                try
                {
                    Console.Error.WriteLine($"[logger] Disk write failure: {ex.Message}");
                }
                catch { }
                Thread.Sleep(1000); // Wait on I/O error to prevent high CPU loop
            }
        }
    }

    private static void WriteEntry(StreamWriter writer, LogEntry entry)
    {
        writer.WriteLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{entry.ThreadId:D2}:{entry.ThreadName}] {entry.Level,-5} {entry.Message}");
    }

    private readonly struct LogEntry
    {
        public readonly DateTimeOffset Timestamp;
        public readonly int ThreadId;
        public readonly string ThreadName;
        public readonly string Level;
        public readonly string Message;

        public LogEntry(DateTimeOffset timestamp, int threadId, string threadName, string level, string message)
        {
            Timestamp = timestamp;
            ThreadId = threadId;
            ThreadName = threadName;
            Level = level;
            Message = message;
        }
    }
}
