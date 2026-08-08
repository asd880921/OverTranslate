using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The point of this budget is how it behaves on machines nobody here has, so the table is a test
/// rather than a comment.
/// </summary>
public class OcrThreadBudgetTests
{
    [Theory]
    [InlineData(2, 2, 1)]
    [InlineData(4, 2, 1)]
    [InlineData(6, 2, 1)]
    [InlineData(8, 2, 2)]
    [InlineData(12, 3, 2)]
    [InlineData(16, 4, 2)]
    [InlineData(24, 4, 3)]
    [InlineData(64, 4, 4)]
    public void TheBudgetIsSpentAsTheTableSays(int cores, int threads, int slots)
    {
        var budget = OcrThreadBudget.For(cores);

        Assert.Equal(threads, budget.Threads);
        Assert.Equal(slots, budget.Slots);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(64)]
    public void NoMachineGivesAwayMoreThanHalfOfItself(int cores)
    {
        // The one invariant. Everything else here is a choice about how to spend the budget; this is
        // the budget, and this application runs behind a game that needs the rest.
        var (threads, slots) = OcrThreadBudget.For(cores);

        Assert.True(
            threads * slots <= Math.Max(2, cores / 2),
            $"{cores} cores would take {threads}x{slots}={threads * slots}");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void SmallMachinesAreLeftExactlyWhereTheyWere(int cores)
    {
        // The measurements behind the larger thread counts were taken on a 16-core machine, where
        // four threads is a quarter of it. They say nothing about a four-core machine, where four
        // threads is all of it — so a small machine gets the shipped behaviour unchanged: two
        // threads, and the same slot count the old Clamp(cores / 4, 1, 4) produced.
        var (threads, slots) = OcrThreadBudget.For(cores);

        Assert.Equal(2, threads);
        Assert.Equal(Math.Clamp(cores / 4, 1, 4), slots);
    }

    [Fact]
    public void ThreadsNeverExceedWhereTheModelStopsScaling()
    {
        // Six threads measured slower than four on a 16-core machine, so nothing is gained by
        // handing a bigger machine more of them.
        foreach (var cores in new[] { 16, 32, 64, 128, 256 })
            Assert.True(OcrThreadBudget.For(cores).Threads <= OcrThreadBudget.MaxThreads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnImpossibleCoreCountStillProducesAUsableBudget(int cores)
    {
        // Environment.ProcessorCount should never be this, but a budget of zero threads would wedge
        // recognition for good, and that is a bad way to find out it can be.
        var (threads, slots) = OcrThreadBudget.For(cores);

        Assert.True(threads >= 1);
        Assert.True(slots >= 1);
    }
}
