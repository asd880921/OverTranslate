using System.Runtime.InteropServices;
using OverTranslate.Services;
using OverTranslate.Services.Providers;

namespace OverTranslate.TranslationHarness;

public sealed class BergamotTranslationProvider : ITranslationProvider, IDisposable
{
    private readonly nint _library;
    private readonly CreateDelegate _create;
    private readonly CreatePivotDelegate _createPivot;
    private readonly TranslateDelegate _translate;
    private readonly FreeDelegate _free;
    private readonly DestroyDelegate _destroy;
    private nint _handle;
    private bool _disposed;

    public BergamotTranslationProvider(
        string nativeLibraryPath,
        string modelConfigPath,
        string? pivotModelConfigPath = null)
    {
        var fullLibraryPath = Path.GetFullPath(nativeLibraryPath);
        var fullConfigPath = Path.GetFullPath(modelConfigPath);
        var fullPivotConfigPath = pivotModelConfigPath is null
            ? null
            : Path.GetFullPath(pivotModelConfigPath);
        if (!File.Exists(fullLibraryPath))
            throw new FileNotFoundException("The Bergamot native library was not found.", fullLibraryPath);
        if (!File.Exists(fullConfigPath))
            throw new FileNotFoundException("The Bergamot model config was not found.", fullConfigPath);
        if (fullPivotConfigPath is not null && !File.Exists(fullPivotConfigPath))
            throw new FileNotFoundException("The Bergamot pivot model config was not found.", fullPivotConfigPath);

        _library = NativeLibrary.Load(fullLibraryPath);
        try
        {
            _create = LoadDelegate<CreateDelegate>("ot_bergamot_create");
            _createPivot = LoadDelegate<CreatePivotDelegate>("ot_bergamot_create_pivot");
            _translate = LoadDelegate<TranslateDelegate>("ot_bergamot_translate");
            _free = LoadDelegate<FreeDelegate>("ot_bergamot_free");
            _destroy = LoadDelegate<DestroyDelegate>("ot_bergamot_destroy");

            var config = StringToUtf8(fullConfigPath);
            var pivotConfig = fullPivotConfigPath is null ? nint.Zero : StringToUtf8(fullPivotConfigPath);
            try
            {
                nint error;
                _handle = pivotConfig == nint.Zero
                    ? _create(config, out error)
                    : _createPivot(config, pivotConfig, out error);
                if (_handle == nint.Zero)
                    throw new InvalidOperationException(ReadAndFree(error, "Bergamot model loading failed."));
                FreeIfPresent(error);
            }
            finally
            {
                Marshal.FreeCoTaskMem(config);
                if (pivotConfig != nint.Zero) Marshal.FreeCoTaskMem(pivotConfig);
            }
        }
        catch
        {
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public bool RequiresApiKey => false;

    public Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks,
        string sourceLang,
        string targetLang,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var translations = Translate(blocks.Select(block => block.Text).ToArray());
            var translatedBlocks = blocks.Zip(translations, (block, translation) => new TranslatedBlock(
                block.Text,
                translation,
                block.Bounds,
                block.SourceLineBounds,
                block.SourceGlyphHeight)).ToList();
            return (translatedBlocks, sourceLang);
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != nint.Zero)
        {
            _destroy(_handle);
            _handle = nint.Zero;
        }
        NativeLibrary.Free(_library);
        GC.SuppressFinalize(this);
    }

    private string[] Translate(IReadOnlyList<string> inputs)
    {
        var inputPointers = inputs.Select(StringToUtf8).ToArray();
        var outputPointers = new nint[inputs.Count];
        try
        {
            var result = _translate(_handle, inputPointers, (nuint)inputs.Count, outputPointers, out var error);
            if (result != 0)
                throw new InvalidOperationException(ReadAndFree(error, "Bergamot translation failed."));
            FreeIfPresent(error);

            return outputPointers.Select(pointer =>
                Marshal.PtrToStringUTF8(pointer) ?? throw new InvalidOperationException(
                    "Bergamot returned a null translation.")).ToArray();
        }
        finally
        {
            foreach (var pointer in inputPointers) Marshal.FreeCoTaskMem(pointer);
            foreach (var pointer in outputPointers) FreeIfPresent(pointer);
        }
    }

    private T LoadDelegate<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private string ReadAndFree(nint pointer, string fallback)
    {
        if (pointer == nint.Zero) return fallback;
        try
        {
            return Marshal.PtrToStringUTF8(pointer) ?? fallback;
        }
        finally
        {
            _free(pointer);
        }
    }

    private void FreeIfPresent(nint pointer)
    {
        if (pointer != nint.Zero) _free(pointer);
    }

    private static nint StringToUtf8(string value) => Marshal.StringToCoTaskMemUTF8(value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateDelegate(nint configPath, out nint error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreatePivotDelegate(nint firstConfigPath, nint secondConfigPath, out nint error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TranslateDelegate(
        nint handle,
        [In] nint[] inputs,
        nuint count,
        [Out] nint[] outputs,
        out nint error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeDelegate(nint memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyDelegate(nint handle);
}
