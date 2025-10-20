using System.Reflection;

namespace CourtFinder.Core.Tests;

internal static class TestUtilities
{
    public static string FindRepoRoot()
    {
        // Walk up until we find a folder containing 'sample_data' and 'src'
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var sample = Path.Combine(dir.FullName, "sample_data");
            var src = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(sample) && Directory.Exists(src))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found from test base directory.");
    }

    public static string SampleDataPath() => Path.Combine(FindRepoRoot(), "sample_data");
}

