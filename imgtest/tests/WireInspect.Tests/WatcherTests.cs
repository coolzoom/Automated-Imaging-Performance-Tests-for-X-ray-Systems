using WireInspect.Core;

namespace WireInspect.Tests;

public class WatcherTests
{
    static byte[] Jpg(bool complete)
    {
        var b = new byte[8192];
        b[0] = 0xFF; b[1] = 0xD8;
        if (complete) { b[^2] = 0xFF; b[^1] = 0xD9; }
        return b;
    }

    [Fact]
    public void Poll_StableCompleteFileOnly()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "ok.jpg"), Jpg(true));
        File.WriteAllBytes(Path.Combine(tmp.Path, "half.jpg"), Jpg(false));   // no FFD9 end marker
        var s = new Session(tmp.Path);
        var w = new FolderWatcher(s, settle: 0);

        Assert.Empty(w.Poll());                 // first poll only records signatures
        Assert.Equal(["ok.jpg"], w.Poll());     // stable + complete on the second poll
        Assert.Empty(w.Poll());                 // queued files are not handed out twice
        Assert.Empty(w.Poll());                 // half.jpg never becomes ready
    }

    [Fact]
    public void Poll_SkipsDetectedAndRetriesCap()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "done.png"), Jpg(true));
        var s = new Session(tmp.Path);
        s.Rec("done.png").DetectedAt = Session.Now();
        var w = new FolderWatcher(s, settle: 0);
        w.Poll();
        Assert.Empty(w.Poll());                 // has_detection → skipped

        Assert.True(w.Failed("x.jpg"));         // 1..3 may be retried on a later poll
        Assert.True(w.Failed("x.jpg"));
        Assert.True(w.Failed("x.jpg"));
        Assert.False(w.Failed("x.jpg"));        // 4th → caller records the error permanently
        w.Done("x.jpg");
        Assert.True(w.Failed("x.jpg"));         // Done resets the counter
    }

    [Fact]
    public void LogResult_WritesHeaderAndRow()
    {
        using var tmp = new TempDir();
        var s = new Session(tmp.Path);
        var defects = new List<Defect> { new() { Type = DefectTypes.Broken }, new() { Type = DefectTypes.Bond1 } };
        FolderWatcher.LogResult(s, "sub/a.jpg", "NG", defects, "P1");
        var path = Path.Combine(tmp.Path, Session.StateDir, "watch_log.csv");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3).ToArray());   // utf-8-sig for Excel
        var lines = File.ReadAllLines(path);
        Assert.Equal("时间,图片,结果,疑似缺陷数,缺陷类型,产品", lines[0]);
        Assert.Contains("sub/a.jpg,NG,2,", lines[1]);
        Assert.EndsWith(",P1", lines[1]);
        FolderWatcher.LogResult(s, "b.jpg", "OK", [], "P1");
        Assert.Equal(3, File.ReadAllLines(path).Length);             // appends without a second header
    }
}
