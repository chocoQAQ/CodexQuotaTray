using System.Security.Cryptography;

namespace CodexQuotaTray.Infrastructure;

/// <summary>先在目标盘准备文件，再原子替换；提交前可恢复原文件。</summary>
public sealed class UpdateFileTransaction(string target, string candidate, string sha256) : IDisposable
{
    private readonly string incoming = target + ".incoming-" + Guid.NewGuid().ToString("N");
    private readonly string backup = target + ".backup-" + Guid.NewGuid().ToString("N");
    private bool installed, committed;
    public string BackupPath => backup;

    public void Install()
    {
        File.Copy(candidate, incoming, false);
        using (var file = File.OpenRead(incoming))
        {
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("待安装程序校验失败。");
        }
        // candidate 可能位于其他磁盘，incoming 与目标同盘才能保证 Replace 的原子性。
        File.Replace(incoming, target, backup);
        installed = true;
    }

    public void Commit()
    {
        if (!installed) throw new InvalidOperationException("更新尚未安装。");
        committed = true;
        TryDelete(backup);
    }

    public void Rollback()
    {
        if (!installed || committed) return;
        File.Replace(backup, target, null);
        installed = false;
    }

    public void Dispose()
    {
        Rollback();
        TryDelete(incoming);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
