using OverTranslate.Services.Providers;

namespace OverTranslate.Services.LocalNmt;

internal static class LocalNmtBootstrap
{
    public static bool IsConfigured => ResolveOptions() is not null;

    public static ITranslationProvider? TryCreateProvider()
    {
        var options = ResolveOptions();
        if (options is null) return null;
        var runtime = new RoutedLocalTranslationRuntime(
            new LocalModelCatalog(), new BergamotWorkerSessionFactory(options));
        return new LocalNmtTranslationProvider(runtime);
    }

    private static BergamotWorkerOptions? ResolveOptions()
    {
        var worker = Environment.GetEnvironmentVariable("OVERTRANSLATE_NMT_WORKER");
        var native = Environment.GetEnvironmentVariable("OVERTRANSLATE_NMT_NATIVE");
        var modelRoot = Environment.GetEnvironmentVariable("OVERTRANSLATE_NMT_MODEL_ROOT");
        return string.IsNullOrWhiteSpace(worker) || string.IsNullOrWhiteSpace(native) ||
               string.IsNullOrWhiteSpace(modelRoot)
            ? null
            : BergamotWorkerOptions.Create(worker, native, modelRoot);
    }
}
