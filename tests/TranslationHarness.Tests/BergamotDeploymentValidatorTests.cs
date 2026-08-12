using OverTranslate.TranslationHarness;
using Xunit;

namespace TranslationHarness.Tests;

public sealed class BergamotDeploymentValidatorTests
{
    [Fact]
    public void Validate_MissingNativeLibraryHasActionablePath()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll");

        var exception = Assert.Throws<FileNotFoundException>(() =>
            BergamotDeploymentValidator.Validate(missing, "unused.yml", avx2Supported: true));

        Assert.Equal(Path.GetFullPath(missing), exception.FileName);
        Assert.Contains("native library", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MissingOpenBlasDependencyHasActionablePath()
    {
        using var files = DeploymentFiles.Create(includeOpenBlas: false);

        var exception = Assert.Throws<FileNotFoundException>(() =>
            BergamotDeploymentValidator.Validate(files.Library, files.Config, avx2Supported: true));

        Assert.Equal(Path.Combine(files.Root, "libopenblas.dll"), exception.FileName);
        Assert.Contains("libopenblas.dll", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MissingModelArtifactHasActionablePath()
    {
        using var files = DeploymentFiles.Create(includeOpenBlas: true, includeModel: false);

        var exception = Assert.Throws<FileNotFoundException>(() =>
            BergamotDeploymentValidator.Validate(files.Library, files.Config, avx2Supported: true));

        Assert.Equal(Path.Combine(files.Root, "missing-model.bin"), exception.FileName);
        Assert.Contains("models artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MissingConfigHasActionablePath()
    {
        using var files = DeploymentFiles.Create(includeOpenBlas: true);
        File.Delete(files.Config);

        var exception = Assert.Throws<FileNotFoundException>(() =>
            BergamotDeploymentValidator.Validate(files.Library, files.Config, avx2Supported: true));

        Assert.Equal(files.Config, exception.FileName);
        Assert.Contains("model config", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MissingVocabularyHasActionablePath()
    {
        using var files = DeploymentFiles.Create(includeOpenBlas: true);
        var missingVocab = Path.Combine(files.Root, "missing-vocab.spm");
        File.AppendAllText(files.Config, $"{Environment.NewLine}vocabs:{Environment.NewLine}  - {missingVocab}");

        var exception = Assert.Throws<FileNotFoundException>(() =>
            BergamotDeploymentValidator.Validate(files.Library, files.Config, avx2Supported: true));

        Assert.Equal(missingVocab, exception.FileName);
        Assert.Contains("vocabs artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_UnsupportedCpuFailsBeforeLoadingNativeCode()
    {
        using var files = DeploymentFiles.Create(includeOpenBlas: true);

        var exception = Assert.Throws<PlatformNotSupportedException>(() =>
            BergamotDeploymentValidator.Validate(files.Library, files.Config, avx2Supported: false));

        Assert.Contains("AVX2", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DeploymentFiles : IDisposable
    {
        private DeploymentFiles(string root, string library, string config)
        {
            Root = root;
            Library = library;
            Config = config;
        }

        public string Root { get; }
        public string Library { get; }
        public string Config { get; }

        public static DeploymentFiles Create(bool includeOpenBlas, bool includeModel = true)
        {
            var root = Path.Combine(Path.GetTempPath(), $"bergamot-deployment-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var library = Path.Combine(root, "overtranslate_bergamot.dll");
            var config = Path.Combine(root, "config.yml");
            File.WriteAllText(library, "test");
            if (includeOpenBlas) File.WriteAllText(Path.Combine(root, "libopenblas.dll"), "test");
            if (includeModel) File.WriteAllText(Path.Combine(root, "missing-model.bin"), "test");
            File.WriteAllText(config, $"models:{Environment.NewLine}  - {Path.Combine(root, "missing-model.bin")}");
            return new DeploymentFiles(root, library, config);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
