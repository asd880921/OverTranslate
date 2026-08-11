using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using GTranslate.Translators;
using OverTranslate.Services.Providers;

namespace OverTranslate.TranslationHarness;

public static class TranslationHarnessCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var arguments = Arguments.Parse(args);
            var corpus = await CorpusLoader.LoadAsync(arguments.CorpusPath);
            Console.WriteLine(
                $"corpus={corpus.CorpusId}@{corpus.CorpusVersion} cases={corpus.Cases.Count} " +
                $"directions={corpus.Cases.Select(item => $"{item.SourceLanguage}->{item.TargetLanguage}").Distinct().Count()}");

            if (arguments.ValidateOnly)
            {
                Console.WriteLine("Corpus is valid and marked deidentified.");
                return 0;
            }

            using var http = new HttpClient { Timeout = arguments.Timeout };
            var initialization = Stopwatch.StartNew();
            ITranslationProvider provider = arguments.Provider.ToLowerInvariant() switch
            {
                "microsoft" => new GTranslateProvider(new MicrosoftTranslator(http)),
                "bergamot" => new BergamotTranslationProvider(
                    arguments.NativeLibraryPath ?? throw new ArgumentException(
                        "--native-library is required for the Bergamot provider."),
                    arguments.ModelConfigPath ?? throw new ArgumentException(
                        "--model-config is required for the Bergamot provider.")),
                "bergamot-pivot" => new BergamotTranslationProvider(
                    arguments.NativeLibraryPath ?? throw new ArgumentException(
                        "--native-library is required for the Bergamot pivot provider."),
                    arguments.ModelConfigPath ?? throw new ArgumentException(
                        "--model-config is required for the first Bergamot pivot model."),
                    arguments.PivotModelConfigPath ?? throw new ArgumentException(
                        "--pivot-model-config is required for the second Bergamot pivot model.")),
                _ => throw new ArgumentException(
                    $"Unknown provider '{arguments.Provider}'. Supported providers: " +
                    "microsoft, bergamot, bergamot-pivot."),
            };
            initialization.Stop();
            using var providerLifetime = provider as IDisposable;
            Console.WriteLine($"providerInitialization={initialization.Elapsed.TotalMilliseconds:F0}ms");

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            var options = new BenchmarkOptions(
                arguments.Runs,
                arguments.WarmupRuns,
                arguments.BatchSizes,
                arguments.Timeout,
                arguments.HardwareProfile,
                arguments.DurationSeconds);
            var report = await BenchmarkRunner.RunAsync(
                corpus, arguments.CorpusPath, arguments.Provider, provider, options,
                initialization.Elapsed.TotalMilliseconds, cancellation.Token);

            foreach (var result in report.Results)
            {
                Console.WriteLine(
                    $"{result.SourceLanguage}->{result.TargetLanguage} batch={result.BatchSize} " +
                    $"first={result.FirstTranslationMs:F0}ms p50={result.P50Ms:F0}ms " +
                    $"p90={result.P90Ms:F0}ms p95={result.P95Ms:F0}ms max={result.MaxMs:F0}ms " +
                    $"cpu={result.MeanCpuPercent:F1}% elapsed={result.ElapsedSeconds:F1}s " +
                    $"ws={result.WorkingSetBeforeBytes / 1048576.0:F1}->" +
                    $"{result.WorkingSetAfterBytes / 1048576.0:F1}MiB " +
                    $"wsPeak={result.PeakWorkingSetBytes / 1048576.0:F1}MiB " +
                    $"requests={result.SuccessfulRequests}/{result.RequestCount} " +
                    $"firstOk={result.FirstTranslationSucceeded}");
            }

            var outputPath = arguments.OutputPath ?? Path.Combine(
                "artifacts", "translation-harness",
                $"{report.CorpusId}-{report.Provider}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }), cancellation.Token);
            Console.WriteLine($"report={Path.GetFullPath(outputPath)}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Benchmark cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("TranslationHarness --corpus <file.json> --hardware-profile <name> [options]");
        Console.WriteLine("  --provider <name>          microsoft, bergamot, or bergamot-pivot");
        Console.WriteLine("  --native-library <dll>     Bergamot C ABI library path");
        Console.WriteLine("  --model-config <yaml>      Direct or first pivot model configuration");
        Console.WriteLine("  --pivot-model-config <yml> Second pivot model configuration");
        Console.WriteLine("  --runs 10                  Measured corpus passes per batch size");
        Console.WriteLine("  --duration-seconds <n>     Run each direction/batch for at least this duration");
        Console.WriteLine("  --warmup-runs 1            Unmeasured warmup passes");
        Console.WriteLine("  --batch-sizes 1,2,4,8      Block counts per provider call");
        Console.WriteLine("  --timeout-seconds 20       Per-request timeout");
        Console.WriteLine("  --output <report.json>     Output path (default: artifacts/...)");
        Console.WriteLine("  --validate-only            Validate corpus without network calls");
    }

    private sealed record Arguments(
        string CorpusPath,
        string Provider,
        int Runs,
        int WarmupRuns,
        IReadOnlyList<int> BatchSizes,
        TimeSpan Timeout,
        string HardwareProfile,
        string? OutputPath,
        string? NativeLibraryPath,
        string? ModelConfigPath,
        string? PivotModelConfigPath,
        double? DurationSeconds,
        bool ValidateOnly)
    {
        public static Arguments Parse(string[] args)
        {
            string? corpus = null;
            string? output = null;
            string? hardwareProfile = null;
            string? nativeLibrary = null;
            string? modelConfig = null;
            string? pivotModelConfig = null;
            var provider = "microsoft";
            var runs = 10;
            var warmupRuns = 1;
            IReadOnlyList<int> batchSizes = [1, 2, 4, 8];
            var timeout = TimeSpan.FromSeconds(20);
            double? durationSeconds = null;
            var validateOnly = false;

            for (var index = 0; index < args.Length; index++)
            {
                string Value() => index + 1 < args.Length
                    ? args[++index]
                    : throw new ArgumentException($"Missing value after {args[index]}.");

                switch (args[index])
                {
                    case "--corpus": corpus = Value(); break;
                    case "--provider": provider = Value(); break;
                    case "--runs": runs = int.Parse(Value()); break;
                    case "--warmup-runs": warmupRuns = int.Parse(Value()); break;
                    case "--batch-sizes":
                        batchSizes = Value().Split(',').Select(int.Parse).Distinct().ToArray();
                        break;
                    case "--timeout-seconds": timeout = TimeSpan.FromSeconds(double.Parse(Value())); break;
                    case "--duration-seconds": durationSeconds = double.Parse(Value()); break;
                    case "--hardware-profile": hardwareProfile = Value(); break;
                    case "--output": output = Value(); break;
                    case "--native-library": nativeLibrary = Value(); break;
                    case "--model-config": modelConfig = Value(); break;
                    case "--pivot-model-config": pivotModelConfig = Value(); break;
                    case "--validate-only": validateOnly = true; break;
                    default: throw new ArgumentException($"Unknown argument: {args[index]}.");
                }
            }

            if (string.IsNullOrWhiteSpace(corpus))
                throw new ArgumentException("--corpus is required.");
            if (!File.Exists(corpus))
                throw new FileNotFoundException("Corpus file was not found.", corpus);
            if (!validateOnly && string.IsNullOrWhiteSpace(hardwareProfile))
                throw new ArgumentException("--hardware-profile is required for measured runs.");

            return new Arguments(
                corpus, provider, runs, warmupRuns, batchSizes, timeout,
                hardwareProfile ?? "validation-only", output, nativeLibrary, modelConfig,
                pivotModelConfig, durationSeconds, validateOnly);
        }
    }
}
