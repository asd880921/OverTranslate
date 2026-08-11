using OverTranslate.TranslationHarness;
using Xunit;

namespace TranslationHarness.Tests;

public sealed class BergamotTranslationProviderTests
{
    [Fact]
    public void Constructor_MissingNativeLibraryFailsBeforeEnteringNativeCode()
    {
        var missingLibrary = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll");

        var exception = Assert.Throws<FileNotFoundException>(() =>
            new BergamotTranslationProvider(missingLibrary, "missing-model.yml"));

        Assert.Equal(Path.GetFullPath(missingLibrary), exception.FileName);
    }
}
