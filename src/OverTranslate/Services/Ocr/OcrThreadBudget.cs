namespace OverTranslate.Services.Ocr;

/// <summary>
/// How much of the machine recognition may use, split into threads inside one inference and how many
/// inferences may run at once.
/// </summary>
/// <remarks>
/// The two are decided together because their product is the whole of it. That product stays at half
/// the machine, where it has always been; what this chooses is how a large machine spends it.
///
/// <code>
///   cores   threads x slots = peak     before
///     64        4 x 4 = 16             2 x 4 =  8
///     16        4 x 2 =  8             2 x 4 =  8
///     12        3 x 2 =  6             2 x 3 =  6
///      8        2 x 2 =  4             2 x 2 =  4
///      4        2 x 1 =  2             2 x 1 =  2
///      2        2 x 1 =  2             2 x 1 =  2
/// </code>
///
/// Nothing at eight cores or below moves. That matters more than the gain does: the measurements
/// behind this were taken on a sixteen-core machine, where four threads is a quarter of it, and they
/// do not transfer to a four-core machine where four threads is all of it — and this application's
/// premise is a game running behind it.
///
/// Four threads is the ceiling because the model stops scaling there rather than because of the
/// machine: measured on sixteen cores, six threads (370ms) was slower than four (356ms). Above
/// sixteen cores the peak is allowed to grow to 16, which is still a quarter of a 64-core machine.
///
/// A pure function of the core count so the table above can be a test rather than a comment. The
/// rule it replaces was <c>Clamp(ProcessorCount, 1, 2)</c>, which reads as though it scales with the
/// machine and does not — everything from two cores to sixty-four came out at two.
/// </remarks>
internal static class OcrThreadBudget
{
    /// <summary>Threads the model stops making use of, measured rather than assumed.</summary>
    public const int MaxThreads = 4;

    /// <summary>Below this, one inference at a time and two threads is the whole budget.</summary>
    public const int MinThreads = 2;

    public static (int Threads, int Slots) For(int logicalProcessors)
    {
        var cores = Math.Max(1, logicalProcessors);

        var threads = Math.Clamp(cores / 4, MinThreads, MaxThreads);

        // Half the machine, spent at whatever the thread count above costs per inference. The floor
        // of one is what a machine too small to divide gets; the cap of four is the throughput
        // measurement the capture side was tuned against.
        var slots = Math.Clamp(cores / (2 * threads), 1, 4);

        return (threads, slots);
    }
}
