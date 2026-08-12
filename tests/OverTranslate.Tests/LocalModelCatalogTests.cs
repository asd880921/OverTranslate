using OverTranslate.Services.LocalNmt;
using Xunit;

namespace OverTranslate.Tests;

public class LocalModelCatalogTests
{
    private readonly LocalModelCatalog _catalog = new();

    [Theory]
    [InlineData("EN", "ZH-HANT", "bergamot-en-zh-hant", false)]
    [InlineData("EN-US", "ZH-TW", "bergamot-en-zh-hant", false)]
    [InlineData("JA", "ZH-HANT", "bergamot-ja-en+bergamot-en-zh-hant", true)]
    [InlineData("KO", "ZH-HANT", "bergamot-ko-en+bergamot-en-zh-hant", true)]
    [InlineData("ZH-HANT", "EN-US", "bergamot-zh-hant-en", false)]
    public void Resolve_ReturnsExpectedDirectOrPivotRoute(
        string source,
        string target,
        string routeId,
        bool isPivot)
    {
        var route = _catalog.Resolve(source, target);

        Assert.Equal(routeId, route.RouteId);
        Assert.Equal(isPivot, route.IsPivot);
    }

    [Fact]
    public void Resolve_AutomaticSourceFailsInsteadOfGuessingAModel()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            _catalog.Resolve("AUTO", "ZH-HANT"));

        Assert.Contains("resolved source language", error.Message);
    }

    [Theory]
    [InlineData("JA", "EN-US")]
    [InlineData("ZH", "ZH-HANT")]
    [InlineData("FR", "ZH-HANT")]
    public void TryResolve_UnsupportedDirectionReturnsDiagnostic(string source, string target)
    {
        var resolved = _catalog.TryResolve(source, target, out var route, out var diagnostic);

        Assert.False(resolved);
        Assert.Null(route);
        Assert.Contains(source, diagnostic);
        Assert.Contains(target, diagnostic);
    }
}
