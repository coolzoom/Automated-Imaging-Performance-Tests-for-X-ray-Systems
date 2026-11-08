using System.Text;

namespace WireInspect.Core;

/// <summary>Hot-folder watching for live inspection next to the X-ray vendor software.
///
/// Polling (no OS file events: they are unreliable on network shares). A file is handed over only when
/// its size and mtime are unchanged between two polls and it has not been written for <see cref="Settle"/>
/// seconds, so half-written images are never read. Originals are only read, never moved, renamed or locked.
/// Already detected files (session.json) are skipped, so a restart does not re-process anything.</summary>
public sealed class FolderWatcher
{
    public const int MaxRetries = 3;

    readonly Session _session;
    readonly Dictionary<string, (long Size, double Mtime)> _seen = [];
    readonly HashSet<string> _queued = [];
    readonly Dictionary<string, int> _retries = [];

    public double Settle { get; }

    public FolderWatcher(Session session, double settle = 1.0)
    {
        _session = session;
        Settle = settle;
    }

    /// <summary>Newly complete image files (relative paths), oldest first.</summary>
    public List<string> Poll()
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ready = new List<(double Mtime, string Rel)>();
        foreach (var rel in Scanner.Scan(_session.Root))
        {
            if (_queued.Contains(rel) || _session.HasDetection(rel)) continue;
            var path = Path.Combine(_session.Root, rel);
            FileInfo info;
            try { info = new FileInfo(path); }
            catch (Exception) { continue; }
            if (!info.Exists) continue;
            double mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            var sig = (info.Length, mtime);
            if (info.Length > 0 && _seen.TryGetValue(rel, out var prev) && prev == sig
                && now - mtime >= Settle && IsComplete(path))
                ready.Add((mtime, rel));
            _seen[rel] = sig;
        }
        ready.Sort((a, b) => a.Mtime != b.Mtime ? a.Mtime.CompareTo(b.Mtime) : string.CompareOrdinal(a.Rel, b.Rel));
        var outList = ready.Select(r => r.Rel).ToList();
        foreach (var r in outList) _queued.Add(r);
        return outList;
    }

    /// <summary>A queued file could not be read; true if it should be retried on a later poll.</summary>
    public bool Failed(string rel)
    {
        _queued.Remove(rel);
        _seen.Remove(rel);
        var n = _retries.TryGetValue(rel, out var c) ? c + 1 : 1;
        _retries[rel] = n;
        return n <= MaxRetries;
    }

    public void Done(string rel)
    {
        _queued.Remove(rel);
        _retries.Remove(rel);
    }

    /// <summary>JPEG / PNG end markers present (a truncated JPEG still decodes, with a grey bottom part).</summary>
    public static bool IsComplete(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png")) return true;
        byte[] tail;
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var n = f.Length;
            f.Seek(Math.Max(0, n - 4096), SeekOrigin.Begin);
            tail = new byte[n - f.Position];
            var off = 0;
            while (off < tail.Length)
            {
                var r = f.Read(tail, off, tail.Length - off);
                if (r <= 0) break;
                off += r;
            }
            if (off < tail.Length) Array.Resize(ref tail, off);
        }
        catch (Exception) { return false; }
        if (ext is ".jpg" or ".jpeg")
            for (var i = 0; i + 1 < tail.Length; i++)
                if (tail[i] == 0xFF && tail[i + 1] == 0xD9) return true;
        if (ext == ".png")
            for (var i = 0; i + 3 < tail.Length; i++)
                if (tail[i] == 'I' && tail[i + 1] == 'E' && tail[i + 2] == 'N' && tail[i + 3] == 'D') return true;
        return false;
    }

    /// <summary>Append one line to &lt;folder&gt;/.xray_wire/watch_log.csv (traceability for production).</summary>
    public static void LogResult(Session session, string rel, string verdict, List<Defect> defects, string product)
    {
        var path = Path.Combine(session.Root, Session.StateDir, "watch_log.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        using var w = new StreamWriter(path, true, new UTF8Encoding(true));
        if (isNew) w.WriteLine("时间,图片,结果,疑似缺陷数,缺陷类型,产品");
        string types = string.Join('/', defects.Select(d => d.Type).Distinct().OrderBy(t => t, StringComparer.Ordinal));
        w.WriteLine(string.Join(',', new[]
        {
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), rel, verdict, defects.Count.ToString(), types, product,
        }.Select(Csv)));
    }

    static string Csv(string s) => s.Any(c => c is ',' or '"' or '\n' or '\r') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
