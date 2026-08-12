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
            new LocalModelCatalog(), new BergamotWorkerSessionFactory(options));
        return new LocalNmtTranslationProvider(runtime);
    }

    public static LocalModelManager? TryCreateModelManager()
    {
        var catalog = new LocalModelCatalog();
        return new LocalModelManager(catalog, new LocalModelInstaller(ModelHttp, ResolveModelRoot()));
    }

    private static BergamotWorkerOptions? ResolveOptions()
    {
        var repositoryRoot = FindRepositoryRoot();
        var worker = FirstExistingFile(
            Path.Combine(AppContext.BaseDirectory, "OverTranslate.Nmt.Worker.exe"),
            repositoryRoot is null ? null : Path.Combine(repositoryRoot, "src", "OverTranslate.Nmt.Worker", "bin", "Debug", "net8.0-windows10.0.17763.0", "win-x64", "OverTranslate.Nmt.Worker.exe"),
            repositoryRoot is null ? null : Path.Combine(repositoryRoot, "src", "OverTranslate.Nmt.Worker", "bin", "Release", "net8.0-windows10.0.17763.0", "win-x64", "OverTranslate.Nmt.Worker.exe"));
        var native = FirstExistingFile(
            Path.Combine(AppContext.BaseDirectory, "overtranslate_bergamot.dll"),
            repositoryRoot is null ? null : Path.Combine(repositoryRoot, "artifacts", "nmt-poc", "bergamot-translator", "build-nmake-avx2", "app", "overtranslate_bergamot.dll"));
        return worker is null || native is null
            ? null
            : BergamotWorkerOptions.Create(worker, native, ResolveModelRoot());
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
