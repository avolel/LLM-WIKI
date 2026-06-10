using DotNetEnv;

namespace LlmWiki.Shared.Configuration;

/// <summary>
/// Loads <c>env/.env</c> into the process environment at startup so secrets stay out of
/// source/appsettings/VCS (NFR-01). Walks up from the current directory to find the repo's
/// <c>env/.env</c>; silently no-ops if absent (e.g. CI, where vars come from the environment).
/// </summary>
public static class DotEnvLoader
{
    /// <summary>Locate and load <c>env/.env</c>. Returns the path loaded, or null if none found.</summary>
    public static string? Load()
    {
        var path = FindEnvFile();
        if (path is null)
        {
            return null;
        }

        Env.Load(path, new LoadOptions(setEnvVars: true, clobberExistingVars: false));
        return path;
    }

    private static string? FindEnvFile()
    {
        // Search the working directory and AppContext.BaseDirectory ancestry for env/.env.
        var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var start in starts)
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "env", ".env");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }
}
