using System.Runtime.Intrinsics.X86;

namespace OverTranslate.TranslationHarness;

public static class BergamotDeploymentValidator
{
    private static readonly string[] ListPathKeys = ["models", "vocabs", "shortlist"];

    public static void Validate(
        string nativeLibraryPath,
        string modelConfigPath,
        string? pivotModelConfigPath = null,
        bool? avx2Supported = null)
    {
        var library = RequireFile(nativeLibraryPath, "Bergamot native library");
        if (!(avx2Supported ?? Avx2.IsSupported))
            throw new PlatformNotSupportedException(
                "This Bergamot native build requires an AVX2-capable x64 CPU.");

        var openBlas = Path.Combine(Path.GetDirectoryName(library)!, "libopenblas.dll");
        RequireFile(openBlas, "Bergamot runtime dependency libopenblas.dll");

        ValidateConfig(modelConfigPath, "Bergamot model config");
        if (pivotModelConfigPath is not null)
            ValidateConfig(pivotModelConfigPath, "Bergamot pivot model config");
    }

    private static void ValidateConfig(string configPath, string description)
    {
        var config = RequireFile(configPath, description);
        var currentList = string.Empty;

        foreach (var rawLine in File.ReadLines(config))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var listKey = ListPathKeys.FirstOrDefault(key =>
                line.Equals($"{key}:", StringComparison.Ordinal));
            if (listKey is not null)
            {
                currentList = listKey;
                continue;
            }

            if (line.StartsWith("ssplit-prefix-file:", StringComparison.Ordinal))
            {
                var value = CleanValue(line[(line.IndexOf(':') + 1)..]);
                RequireReferencedFile(value, config, "sentence-splitting prefix file");
                currentList = string.Empty;
                continue;
            }

            if (line.StartsWith('-') && currentList.Length > 0)
            {
                var value = CleanValue(line[1..]);
                if (!value.Equals("false", StringComparison.OrdinalIgnoreCase))
                    RequireReferencedFile(value, config, $"{currentList} artifact");
                continue;
            }

            if (!char.IsWhiteSpace(rawLine, 0)) currentList = string.Empty;
        }
    }

    private static string CleanValue(string value)
    {
        var withoutComment = value.Split('#', 2)[0].Trim();
        return withoutComment.Trim('"', '\'');
    }

    private static void RequireReferencedFile(string path, string configPath, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException($"The {description} path is empty in '{configPath}'.");

        var resolved = Path.IsPathFullyQualified(path)
            ? path
            : Path.Combine(Environment.CurrentDirectory, path);
        RequireFile(resolved, description);
    }

    private static string RequireFile(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The {description} was not found: '{fullPath}'.", fullPath);
        return fullPath;
    }
}
