namespace Opencode2Api.IO;

/// <summary>
/// 所有持久化快照共用的崩溃安全状态文件写入器：先在目标目录
/// 写临时文件，再重命名覆盖目标。对齐 Go 状态存储使用的
/// os.CreateTemp + Sync + os.Rename 序列。
/// </summary>
public static class AtomicText
{
    public static void Write(string path, string content, string tempPrefix)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temp = path + "." + tempPrefix + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
