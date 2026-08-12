using System.Text.Json;
using OverTranslate.Services;

namespace OverTranslate.TranslationHarness;

public static class BergamotWorkerHost
{
    public const string Command = "--bergamot-worker";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = WorkerOptions.Parse(args);
            using var provider = new BergamotTranslationProvider(
                options.NativeLibraryPath, options.ModelConfigPath, options.PivotModelConfigPath);
            await WriteAsync(new WorkerMessage("ready"));

            while (await Console.In.ReadLineAsync() is { } line)
            {
                WorkerMessage? request;
                try
                {
                    request = JsonSerializer.Deserialize<WorkerMessage>(line);
                    if (request?.Type != "translate" || request.Texts is null)
                        throw new InvalidDataException("Invalid Bergamot worker request.");

                    var blocks = request.Texts.Select(text =>
                        new OcrTextBlock(text, System.Windows.Rect.Empty)).ToList();
                    var result = await provider.TranslateAsync(
                        blocks, request.SourceLanguage ?? string.Empty,
                        request.TargetLanguage ?? string.Empty, string.Empty);
                    await WriteAsync(new WorkerMessage(
                        "result", request.Id,
                        Translations: result.Blocks.Select(block => block.TranslatedText).ToArray()));
                }
                catch (Exception exception)
                {
                    await WriteAsync(new WorkerMessage("error", Error: exception.Message));
                }
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 3;
        }
    }

    private static async Task WriteAsync(WorkerMessage message)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message));
        await Console.Out.FlushAsync();
    }

    internal sealed record WorkerMessage(
        string Type,
        long Id = 0,
        string[]? Texts = null,
        string? SourceLanguage = null,
        string? TargetLanguage = null,
        string[]? Translations = null,
        string? Error = null);

    private sealed record WorkerOptions(
        string NativeLibraryPath,
        string ModelConfigPath,
        string? PivotModelConfigPath)
    {
        public static WorkerOptions Parse(string[] args)
        {
            string? nativeLibrary = null;
            string? modelConfig = null;
            string? pivotModelConfig = null;

            for (var index = 0; index < args.Length; index++)
            {
                string Value() => index + 1 < args.Length
                    ? args[++index]
                    : throw new ArgumentException($"Missing value after {args[index]}.");

                switch (args[index])
                {
                    case "--native-library": nativeLibrary = Value(); break;
                    case "--model-config": modelConfig = Value(); break;
                    case "--pivot-model-config": pivotModelConfig = Value(); break;
                    default: throw new ArgumentException($"Unknown worker argument: {args[index]}.");
                }
            }

            return new WorkerOptions(
                nativeLibrary ?? throw new ArgumentException("--native-library is required."),
                modelConfig ?? throw new ArgumentException("--model-config is required."),
                pivotModelConfig);
        }
    }
}
