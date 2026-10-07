using System.Diagnostics;

namespace DualLink;

/// <summary>Bounded local lifecycle log. Callers must not include credentials or destination addresses.</summary>
public sealed class DiagnosticLog(string path)
{
    private readonly object _gate = new();
    public void Write(string message)
    {
        Trace.WriteLine(message);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= 512 * 1024)
                    File.Move(path, path + ".old", true);
                var singleLine = message.Replace('\r', ' ').Replace('\n', ' ');
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {singleLine[..Math.Min(singleLine.Length, 1024)]}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
