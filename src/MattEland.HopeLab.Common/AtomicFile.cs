namespace MattEland.HopeLab.Common;

/// <summary>Writes files through a temporary file and a rename, so readers never see a half-written file.</summary>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, contents, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>Restricts a file to the current user (no-op on Windows, where profile ACLs already do this).</summary>
    public static void RestrictToUser(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
