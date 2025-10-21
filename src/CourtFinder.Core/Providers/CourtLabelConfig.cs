using System.Text.Json;

namespace CourtFinder.Core.Providers;

internal static class CourtLabelConfig
{
    private static readonly object Sync = new();
    private static DateTime _lastLoad = DateTime.MinValue;
    private static Dictionary<string, List<string>> _map = new(StringComparer.OrdinalIgnoreCase);
    private static string? _loadedPath;

    public static bool TryGetLabelsForK(string k, out List<string> labels)
    {
        labels = new List<string>();
        var path = GetPath();
        try
        {
            EnsureLoaded(path);
            if (_map.TryGetValue(k, out var ls))
            {
                labels = ls;
                return labels.Count > 0;
            }
        }
        catch { }
        return false;
    }

    private static void EnsureLoaded(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var fi = new FileInfo(path);
        if (_loadedPath == path && fi.LastWriteTimeUtc <= _lastLoad) return;
        lock (Sync)
        {
            if (_loadedPath == path && fi.LastWriteTimeUtc <= _lastLoad) return;
            try
            {
                var json = File.ReadAllText(path);
                var obj = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
                if (obj != null)
                {
                    _map = new Dictionary<string, List<string>>(obj, StringComparer.OrdinalIgnoreCase);
                    _loadedPath = path;
                    _lastLoad = DateTime.UtcNow;
                }
            }
            catch { }
        }
    }

    private static string? GetPath()
    {
        var env = Environment.GetEnvironmentVariable("COURTFINDER_LABELS_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var cwd = Directory.GetCurrentDirectory();
        var p1 = Path.Combine(cwd, "config", "court_labels.json");
        if (File.Exists(p1)) return p1;
        var p2 = Path.Combine(cwd, "court_labels.json");
        if (File.Exists(p2)) return p2;
        return null;
    }
}

