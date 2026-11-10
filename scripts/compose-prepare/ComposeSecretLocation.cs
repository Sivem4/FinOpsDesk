namespace FinOpsDesk.ComposePrepare;

public static class ComposeSecretLocation
{
    public const int Outside = 0;

    public const int Refused = 1;

    public static int Check(string destination, string repositoryRoot, TextWriter error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryResolve(repositoryRoot, out string resolvedRepository))
        {
            error.WriteLine("Refusing to write compose secrets through an unresolved redirect.");
            return Refused;
        }

        if (!TryResolve(destination, out string resolvedDestination))
        {
            error.WriteLine("Refusing to write compose secrets through an unresolved redirect.");
            return Refused;
        }

        if (IsInside(resolvedDestination, resolvedRepository))
        {
            error.WriteLine("Refusing to write compose secrets inside the repository.");
            return Refused;
        }

        string lexicalDestination = Path.GetFullPath(destination);
        foreach (string name in ComposeSecretSet.SecretFileNames)
        {
            string file = Path.Combine(lexicalDestination, name);
            if (Describe(file) is null)
            {
                continue;
            }

            if (!TryResolve(file, out string resolvedFile))
            {
                error.WriteLine("Refusing to write compose secrets through an unresolved redirect.");
                return Refused;
            }

            if (IsInside(resolvedFile, resolvedRepository))
            {
                error.WriteLine("Refusing to write compose secrets inside the repository.");
                return Refused;
            }
        }

        return Outside;
    }

    private static bool TryResolve(string path, out string resolved)
    {
        resolved = string.Empty;
        try
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            string relative = full[root.Length..].TrimStart(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string current = root;
            if (relative.Length == 0)
            {
                resolved = Path.GetFullPath(current);
                return true;
            }

            bool missing = false;
            foreach (string segment in relative.Split(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (missing)
                {
                    current = Path.Combine(current, segment);
                    continue;
                }

                string candidate = Path.Combine(current, segment);
                FileSystemInfo? existing = Describe(candidate);
                if (existing is null)
                {
                    missing = true;
                    current = candidate;
                    continue;
                }

                if (existing.LinkTarget is not null)
                {
                    FileSystemInfo? final = existing.ResolveLinkTarget(returnFinalTarget: true);
                    if (final is null)
                    {
                        return false;
                    }

                    current = Path.GetFullPath(final.FullName);
                    continue;
                }

                current = Path.GetFullPath(existing.FullName);
            }

            resolved = Path.GetFullPath(current);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static FileSystemInfo? Describe(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null || directory.Exists)
        {
            return directory;
        }

        var file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Exists)
        {
            return file;
        }

        return null;
    }

    private static bool IsInside(string path, string repositoryRoot)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(candidate, root, comparison))
        {
            return true;
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}