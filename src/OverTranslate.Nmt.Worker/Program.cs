using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length == 0 || args[0] != "--bergamot-worker") return 2;
try
{
    var options = WorkerOptions.Parse(args.Skip(1).ToArray());
    using var runtime = new NativeBergamotRuntime(options.NativeLibrary, options.ModelConfig, options.PivotModelConfig);
    await WriteAsync(new WorkerMessage("ready"));
    while (await Console.In.ReadLineAsync() is { } line)
    {
        try
        {
            var request = JsonSerializer.Deserialize<WorkerMessage>(line.TrimStart('\uFEFF'));
            if (request?.Type != "translate" || request.Texts is null)
                throw new InvalidDataException("Invalid Bergamot worker request.");
            await WriteAsync(new WorkerMessage("result", request.Id, Translations: runtime.Translate(request.Texts)));
        }
        catch (Exception exception) { await WriteAsync(new WorkerMessage("error", Error: exception.Message)); }
    }
    return 0;
}
catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 3; }

static async Task WriteAsync(WorkerMessage message)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message));
    await Console.Out.FlushAsync();
}

sealed record WorkerMessage(string Type, long Id = 0, string[]? Texts = null, string[]? Translations = null, string? Error = null);

sealed record WorkerOptions(string NativeLibrary, string ModelConfig, string? PivotModelConfig)
{
    public static WorkerOptions Parse(string[] args)
    {
        string? native = null, model = null, pivot = null;
        for (var index = 0; index < args.Length; index++)
        {
            string Value() => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"Missing value after {args[index]}.");
            switch (args[index])
            {
                case "--native-library": native = Value(); break;
                case "--model-config": model = Value(); break;
                case "--pivot-model-config": pivot = Value(); break;
                default: throw new ArgumentException($"Unknown worker argument: {args[index]}.");
            }
        }
        return new(native ?? throw new ArgumentException("--native-library is required."), model ?? throw new ArgumentException("--model-config is required."), pivot);
    }
}

sealed class NativeBergamotRuntime : IDisposable
{
    private readonly nint _library;
    private readonly TranslateDelegate _translate;
    private readonly FreeDelegate _free;
    private readonly DestroyDelegate _destroy;
    private nint _handle;

    public NativeBergamotRuntime(string libraryPath, string configPath, string? pivotConfigPath)
    {
        _library = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        try
        {
            var create = Load<CreateDelegate>("ot_bergamot_create");
            var createPivot = Load<CreatePivotDelegate>("ot_bergamot_create_pivot");
            _translate = Load<TranslateDelegate>("ot_bergamot_translate");
            _free = Load<FreeDelegate>("ot_bergamot_free");
            _destroy = Load<DestroyDelegate>("ot_bergamot_destroy");
            var first = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(configPath));
            var second = pivotConfigPath is null ? nint.Zero : Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(pivotConfigPath));
            try
            {
                nint error;
                _handle = second == nint.Zero ? create(first, out error) : createPivot(first, second, out error);
                if (_handle == nint.Zero) throw new InvalidOperationException(ReadAndFree(error));
                Free(error);
            }
            finally { Marshal.FreeCoTaskMem(first); if (second != nint.Zero) Marshal.FreeCoTaskMem(second); }
        }
        catch { NativeLibrary.Free(_library); throw; }
    }

    public string[] Translate(string[] texts)
    {
        var inputs = texts.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var outputs = new nint[texts.Length];
        try
        {
            if (_translate(_handle, inputs, (nuint)inputs.Length, outputs, out var error) != 0)
                throw new InvalidOperationException(ReadAndFree(error));
            Free(error);
            return outputs.Select(pointer => Marshal.PtrToStringUTF8(pointer) ?? "").ToArray();
        }
        finally { foreach (var input in inputs) Marshal.FreeCoTaskMem(input); foreach (var output in outputs) Free(output); }
    }

    public void Dispose() { if (_handle != nint.Zero) _destroy(_handle); NativeLibrary.Free(_library); }
    private T Load<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    private string ReadAndFree(nint pointer) { if (pointer == nint.Zero) return "Bergamot operation failed."; try { return Marshal.PtrToStringUTF8(pointer) ?? "Bergamot operation failed."; } finally { _free(pointer); } }
    private void Free(nint pointer) { if (pointer != nint.Zero) _free(pointer); }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CreateDelegate(nint config, out nint error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CreatePivotDelegate(nint first, nint second, out nint error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TranslateDelegate(nint handle, [In] nint[] inputs, nuint count, [Out] nint[] outputs, out nint error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeDelegate(nint memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyDelegate(nint handle);
}
