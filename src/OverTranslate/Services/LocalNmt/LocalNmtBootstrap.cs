using System.IO;
using System.Net.Http;
using OverTranslate.Services.Providers;

namespace OverTranslate.Services.LocalNmt;

internal static class LocalNmtBootstrap
{
    private static readonly HttpClient ModelHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static bool IsConfigured => ResolveOptions() is not null;

    public static ITranslationProvider? TryCreateProvider()
    {
        var options = ResolveOptions();
        if (options is null) return null;
        var runtime = new RoutedLocalTranslationRuntime(
            new LocalModelCatalog(), new LlamaCppSessionFactory(options));
        return new LocalNmtTranslationProvider(runtime);
    }

    public static LocalModelManager? TryCreateModelManager()
    {
        var catalog = new LocalModelCatalog();
        return new LocalModelManager(catalog, new LocalModelInstaller(ModelHttp, ResolveModelRoot()));
    }

    private static LlamaCppOptions? ResolveOptions()
    {
        var repositoryRoot = FindRepositoryRoot();
        var server = FirstExistingFile(
            Path.Combine(AppContext.BaseDirectory, "llama-server.exe"),
            repositoryRoot is null ? null : Path.Combine(repositoryRoot, "artifacts", "nmt-poc", "llama.cpp", "b10362", "llama-server.exe"));
        return server is null
            ? null
            : LlamaCppOptions.Create(server, ResolveModelRoot());
    }

    private static string ResolveModelRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OverTranslate", "nmtModels");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string? FirstExistingFile(params string?[] candidates) =>
        candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
                return directory.FullName;
        }

        return null;
    }
}
