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
        var options = ResolveOptions();
        if (options is null) return null;
        var catalog = new LocalModelCatalog();
        return new LocalModelManager(catalog, new LocalModelInstaller(ModelHttp, options.ModelRoot));
    }

    private static BergamotWorkerOptions? ResolveOptions()
    {
        var worker = Environment.GetEnvironmentVariable("OVERTRANSLATE_NMT_WORKER");
        var native = Environment.GetEnvironmentVariable("OVERTRANSLATE_NMT_NATIVE");
        var modelRoot = Environment.GetEnvironmentVariable("OVERTRANSLATE_NMT_MODEL_ROOT");
        return string.IsNullOrWhiteSpace(worker) || !File.Exists(worker) ||
               string.IsNullOrWhiteSpace(native) || !File.Exists(native) ||
               string.IsNullOrWhiteSpace(modelRoot) || !Directory.Exists(modelRoot)
            ? null
            : BergamotWorkerOptions.Create(worker, native, modelRoot);
    }
}
