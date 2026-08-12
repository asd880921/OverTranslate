using OverTranslate.Services.LocalNmt;
using Xunit;

namespace OverTranslate.Tests;

public class LocalNmtBootstrapTests
{
    [Fact]
    public void TryCreateModelManager_DoesNotRequireNativeRuntimeConfiguration()
    {
        var manager = LocalNmtBootstrap.TryCreateModelManager();

        Assert.NotNull(manager);
    }
}
