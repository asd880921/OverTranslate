namespace OverTranslate.TranslationHarness;

public interface IBenchmarkProcessResources
{
    ProcessResourceSnapshot CaptureProcessResources();
}

public readonly record struct ProcessResourceSnapshot(
    long WorkingSetBytes,
    TimeSpan TotalProcessorTime);
