using MattEland.HopeLab.Common;

namespace MattEland.HopeLab.Updates;

/// <summary>
/// Replaces an install folder's files with a staged release, in place, while the old version may still be running.
/// Each file being replaced is first moved aside into <c>.update-old/&lt;stamp&gt;/</c> inside the install folder —
/// Windows lets a running executable or loaded DLL be renamed on its own volume, though not overwritten or deleted —
/// and the new file moved in. Any failure moves everything back. The folder's path never changes, so anything
/// that holds an absolute path into it (Claude Code hooks, a login entry, a shortcut) keeps working.
/// </summary>
public static class InstallSwap
{
    public const string OldFolder = ".update-old";

    /// <returns>The number of files installed.</returns>
    public static Result<int> Apply(string staging, string installDirectory, DateTimeOffset now)
    {
        var install = Path.GetFullPath(installDirectory);
        var aside = Path.Combine(install, OldFolder, now.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff"));
        var done = new List<(string Target, string? Aside)>();
        try
        {
            foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(staging, source);
                if (relative.StartsWith(OldFolder, StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(install, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? old = null;
                if (File.Exists(target))
                {
                    old = Path.Combine(aside, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(old)!);
                    File.Move(target, old);
                }
                // Record before moving in, so a failed move still gets its moved-aside file restored.
                done.Add((target, old));
                File.Move(source, target);
            }
            return Result<int>.Ok(done.Count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var restored = RollBack(done);
            return Result<int>.Fail($"Couldn't replace the files in {install}: {e.Message}" +
                                    (restored ? " Nothing was changed." : $" Some files could not be put back; the old ones are in {aside}."));
        }
    }

    private static bool RollBack(List<(string Target, string? Aside)> done)
    {
        var ok = true;
        for (var i = done.Count - 1; i >= 0; i--)
        {
            var (target, old) = done[i];
            try
            {
                if (old is null)
                {
                    if (File.Exists(target)) File.Delete(target);
                }
                else if (File.Exists(old))
                {
                    // On Windows a just-installed file that already started can't be deleted, but it can be renamed away.
                    if (File.Exists(target)) File.Move(target, target + ".failed-" + Guid.NewGuid().ToString("N")[..6]);
                    File.Move(old, target);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>
    /// Deletes what earlier updates moved aside. Files the previous version still has open (it may not have exited
    /// yet) are left for next time.
    /// </summary>
    public static void CleanUp(string installDirectory)
    {
        var old = Path.Combine(installDirectory, OldFolder);
        if (!Directory.Exists(old)) return;
        foreach (var dir in Directory.EnumerateDirectories(old))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        try { if (!Directory.EnumerateFileSystemEntries(old).Any()) Directory.Delete(old); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
