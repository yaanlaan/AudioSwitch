using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AudioSwitch.Services;

public static class Log
{
    private static readonly object Gate = new();

    public static string DirectoryPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioSwitch");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "audioSwitch.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null
            ? message
            : $"{message} :: {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded();
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败绝不影响主流程
        }

        Debug.Write(line);
    }

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length <= 1_048_576)
        {
            return;
        }

        string backup = FilePath + ".1";
        if (File.Exists(backup))
        {
            File.Delete(backup);
        }

        File.Move(FilePath, backup);
    }
}
