using System.IO;
using OverTranslate.Services.LocalNmt;
using Xunit;

namespace OverTranslate.Tests;

public class BergamotWorkerSessionFactoryTests
{
    [Fact]
    public async Task Create_MissingWorkerReportsExactFileBeforeStartingProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), $"overtranslate-worker-test-{Guid.NewGuid():N}");
        var options = BergamotWorkerOptions.Create(
            Path.Combine(root, "missing-worker.exe"),
            Path.Combine(root, "missing-native.dll"),
            root);
        var factory = new BergamotWorkerSessionFactory(options);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            factory.CreateAsync(new LocalModelCatalog().Resolve("EN", "ZH-HANT")));

        Assert.Equal(Path.Combine(root, "missing-worker.exe"), error.FileName);
        Assert.Contains("worker executable", error.Message);
    }

    [Fact]
    public async Task Create_MissingNativeLibraryReportsExactFileBeforeReadingModel()
    {
        var root = Directory.CreateTempSubdirectory("overtranslate-worker-test-").FullName;
        try
        {
            var worker = Path.Combine(root, "worker.exe");
            await File.WriteAllBytesAsync(worker, [0]);
            var native = Path.Combine(root, "missing-native.dll");
            var factory = new BergamotWorkerSessionFactory(
                BergamotWorkerOptions.Create(worker, native, root));

            var error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
                factory.CreateAsync(new LocalModelCatalog().Resolve("EN", "ZH-HANT")));

            Assert.Equal(native, error.FileName);
            Assert.Contains("native library", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
