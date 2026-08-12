using OverTranslate.Services.LocalNmt;
using Xunit;

namespace OverTranslate.Tests;

public class LocalModelCatalogTests
{
    private readonly LocalModelCatalog _catalog = new();

    [Theory]
    [InlineData("EN", "ZH-HANT", "en-zh-hant:hy-mt2-1.8b-q4-k-m")]
    [InlineData("EN-US", "ZH-TW", "en-zh-hant:hy-mt2-1.8b-q4-k-m")]
    [InlineData("JA", "ZH-HANT", "ja-zh-hant:hy-mt2-1.8b-q4-k-m")]
    [InlineData("KO", "ZH-HANT", "ko-zh-hant:hy-mt2-1.8b-q4-k-m")]
    [InlineData("ZH-HANT", "EN-US", "zh-hant-en:hy-mt2-1.8b-q4-k-m")]
    public void Resolve_ReturnsExpectedDirectRoute(
        string source,
        string target,
        string routeId)
    {
        var route = _catalog.Resolve(source, target);

        Assert.Equal(routeId, route.RouteId);
        Assert.False(route.IsPivot);
        Assert.Equal("hy-mt2-1.8b-q4-k-m", Assert.Single(route.Models).ModelId);
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
