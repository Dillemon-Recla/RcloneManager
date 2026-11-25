using RcloneManager.Core.Engine;

namespace RcloneManager.Core.Storage;

public static class WorkspacePathResolver
{
    public static string ResolveRootDirectory(string? explicitWorkDir = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitWorkDir) && Directory.Exists(explicitWorkDir))
            return Path.GetFullPath(explicitWorkDir);

        var env = Environment.GetEnvironmentVariable("RCLONE_MANAGER_HOME");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return Path.GetFullPath(env);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && dir != null; depth++)
        {
            if (IsManagerRoot(dir.FullName))
                return dir.FullName;
            dir = dir.Parent;
        }

        dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && dir != null; depth++)
        {
            if (HasRcloneBinaryDirectlyOrInSubdir(dir.FullName))
                return dir.FullName;
            dir = dir.Parent;
        }

        var cwd = Directory.GetCurrentDirectory();
        if (IsManagerRoot(cwd) || HasRcloneBinaryDirectlyOrInSubdir(cwd))
            return Path.GetFullPath(cwd);

        return AppContext.BaseDirectory;
    }

    public static string FindRcloneExecutable(string rootDir, string? pinnedVersion = null)
    {
        var normalized = RcloneReleaseManager.TryNormalizeVersion(pinnedVersion, out var v)
            ? v
            : RcloneReleaseManager.DefaultPinnedVersion;

        var expectedPinnedExe = RcloneReleaseManager.GetExpectedExecutablePath(rootDir, normalized);
        if (File.Exists(expectedPinnedExe))
            return expectedPinnedExe;

        var directExe = Path.Combine(rootDir, "rclone.exe");
        if (File.Exists(directExe))
            return directExe;

        if (Directory.Exists(rootDir))
        {
            foreach (var subDir in Directory.EnumerateDirectories(rootDir, "rclone-*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(subDir, "rclone.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return expectedPinnedExe;
    }

    public static string FindExistingFilterFile(string rootDir, string fileName = "exclude_user_folder.txt")
    {
        var rootCandidate = Path.Combine(rootDir, fileName);
        if (File.Exists(rootCandidate))
            return rootCandidate;

        if (Directory.Exists(rootDir))
        {
            foreach (var subDir in Directory.EnumerateDirectories(rootDir, "rclone-*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var subCandidate = Path.Combine(subDir, fileName);
                if (File.Exists(subCandidate))
                    return subCandidate;
            }
        }

        return string.Empty;
    }

    private static bool IsManagerRoot(string path)
    {
        return File.Exists(Path.Combine(path, "rclone_manager_config.json")) ||
               File.Exists(Path.Combine(path, "RcloneManager.sln"));
    }

    private static bool HasRcloneBinaryDirectlyOrInSubdir(string path)
    {
        if (File.Exists(Path.Combine(path, "rclone.exe")))
            return true;

        if (!Directory.Exists(path))
            return false;

        try
        {
            return Directory.EnumerateDirectories(path, "rclone-*")
                .Any(sub => File.Exists(Path.Combine(sub, "rclone.exe")));
        }
        catch
        {
            return false;
        }
    }
}
