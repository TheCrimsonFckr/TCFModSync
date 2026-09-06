namespace TCFModSync.Shared.Paths;

public static class SptRootLocator
{
    public static string? FindRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "BepInEx")) ||
                File.Exists(Path.Combine(dir.FullName, "EscapeFromTarkov.exe")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
