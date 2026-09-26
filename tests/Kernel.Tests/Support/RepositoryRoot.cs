namespace Platform.Kernel.Tests.Support;

internal static class RepositoryRoot
{
    public static string Path(params string[] relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Platform.slnx")))
            {
                return System.IO.Path.Combine([dir.FullName, .. relative]);
            }
        }

        throw new InvalidOperationException("Platform.slnx not found above " + AppContext.BaseDirectory);
    }
}
