namespace Melodex.Core.Tests.Support;

/// <summary>A test's own folder of data, removed when it's done.</summary>
public static class TestFolder
{
    /// <summary>
    /// Removes the folder and everything in it. Files a stopping app was still finishing can appear while it's being
    /// emptied, so it's tried again until they've been written.
    /// </summary>
    public static void Delete(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }
}
