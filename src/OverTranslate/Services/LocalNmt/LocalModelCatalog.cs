namespace OverTranslate.Services.LocalNmt;

public sealed record LocalModelDescriptor(
    string ModelId,
    string Version,
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<LocalModelArtifact> Artifacts);

public enum LocalModelArtifactRole { Model, Vocabulary, LexicalShortlist }

public sealed record LocalModelArtifact(
    LocalModelArtifactRole Role,
    Uri DownloadUri,
    string FileName,
    long UncompressedSize,
    string UncompressedSha256);

public sealed record LocalTranslationRoute(
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<LocalModelDescriptor> Models)
{
    public bool IsPivot => Models.Count > 1;
    public string RouteId => string.Join("+", Models.Select(model => model.ModelId));
}

/// <summary>The deliberately small, Phase 2 catalog of model directions validated by issue #47.</summary>
public sealed class LocalModelCatalog
{
    public const string CatalogVersion = "issue-47-v1";

    private const string BaseUrl =
        "https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/";

    private static readonly LocalModelDescriptor EnglishToTraditionalChinese = new(
        "bergamot-en-zh-hant", "559ab90d723a", "EN", "ZH-HANT",
        Artifacts(
            "models/en-zh_hant/zh_hant_llmaat_finetune10M_qe8_f2_aQ8azdOMQOSBVjBDOVDIZQ/exported/",
            (LocalModelArtifactRole.Model, "model.enzh_hant.intgemm.alphas.bin", 43849787, "559ab90d723a58c1f1e2ab7cc12137bc667af5ba3e325e3eb30b5cdc930db520"),
            (LocalModelArtifactRole.Vocabulary, "srcvocab.enzh_hant.spm", 803694, "2266df70492162a249ab1c0154f929bd6098b246544c666c1a0d5a24dde7d2ea"),
            (LocalModelArtifactRole.Vocabulary, "trgvocab.enzh_hant.spm", 751671, "22b91a4436d70b91ab8777c677252ab5fae2bc284d71f977df5206c110e3444c"),
            (LocalModelArtifactRole.LexicalShortlist, "lex.50.50.enzh_hant.s2t.bin", 4057188, "d891404d1436a7334df12539fe30a26f9e9f2b80bd42fdb8b5f8849e8a1e942b")));

    private static readonly LocalModelDescriptor JapaneseToEnglish = new(
        "bergamot-ja-en", "a9bf800679bb", "JA", "EN",
        Artifacts(
            "models/ja-en/cjk_icu_base_U4VUAW3STh-bF0Sr-dX69g/exported/",
            (LocalModelArtifactRole.Model, "model.jaen.intgemm.alphas.bin", 59504955, "a9bf800679bba570520e1161d7b4fbfcb957add32ca35812134add85689752ad"),
            (LocalModelArtifactRole.Vocabulary, "vocab.jaen.spm", 1443222, "5cb217758bae05877bb3f0c2f612e4e7c1e4cb03c10db11f4a47098d7ae62919"),
            (LocalModelArtifactRole.LexicalShortlist, "lex.50.50.jaen.s2t.bin", 9346816, "8f858a72fcbaa476c582577b04d6f5f89d645d2335b0b4a794c2706d4b1f75ff")));

    private static readonly LocalModelDescriptor KoreanToEnglish = new(
        "bergamot-ko-en", "1c902d6f7a8d", "KO", "EN",
        Artifacts(
            "models/ko-en/cjk_icu_base_BnKgBdd0Rzq87oUYN3L9-A/exported/",
            (LocalModelArtifactRole.Model, "model.koen.intgemm.alphas.bin", 59504955, "1c902d6f7a8d7e3efe6ff4f7d4960a369957bca4ce2ce4a6e8572c231d525090"),
            (LocalModelArtifactRole.Vocabulary, "vocab.koen.spm", 1410063, "1c72b740ab793cdc3a8f16913dd6b4e806c77421077dd2d85edeb7be38418598"),
            (LocalModelArtifactRole.LexicalShortlist, "lex.50.50.koen.s2t.bin", 8617080, "471cd980c4ba08c240246f9361f64eb5d627848a135b5731d665f9efaa1e26ae")));

    private static readonly LocalModelDescriptor TraditionalChineseToEnglish = new(
        "bergamot-zh-hant-en", "0aee91790894", "ZH-HANT", "EN",
        Artifacts(
            "models/zh_hant-en/zh_hant_openlid_zh_tw_lr0002_WJi5Ozi7SZWC6hgfD5GhTA/exported/",
            (LocalModelArtifactRole.Model, "model.zh_hanten.intgemm.alphas.bin", 43849787, "0aee91790894458f5d367551f6edcd4c9cb97852c34f221bcbf9f4701ebcf0cd"),
            (LocalModelArtifactRole.Vocabulary, "srcvocab.zh_hanten.spm", 769669, "5cc6a76611dbf86219f109141533606b15ecb34eee83673bb86b2c16b14734db"),
            (LocalModelArtifactRole.Vocabulary, "trgvocab.zh_hanten.spm", 812572, "7bf002db37c10d3b114cc5588d7fdcb16c57d0fd1e2c34354c22cc9f0b6c3c29"),
            (LocalModelArtifactRole.LexicalShortlist, "lex.50.50.zh_hanten.s2t.bin", 6385944, "aa7daf6cfc85c0cd2c10e2944d66f6da55497c9c6408789f3adfded4074c2fb1")));

    public IReadOnlyList<LocalModelDescriptor> Models { get; } =
    [
        EnglishToTraditionalChinese,
        JapaneseToEnglish,
        KoreanToEnglish,
        TraditionalChineseToEnglish,
    ];

    private static IReadOnlyList<LocalModelArtifact> Artifacts(
        string prefix,
        params (LocalModelArtifactRole Role, string Name, long Size, string Sha256)[] artifacts) =>
        artifacts.Select(artifact => new LocalModelArtifact(
            artifact.Role,
            new Uri(BaseUrl + prefix + artifact.Name + ".gz"),
            artifact.Name,
            artifact.Size,
            artifact.Sha256)).ToArray();

    public bool TryResolve(
        string sourceLanguage,
        string targetLanguage,
        out LocalTranslationRoute? route,
        out string? diagnostic)
    {
        var source = NormalizeSource(sourceLanguage);
        var target = NormalizeTarget(targetLanguage);

        if (source == "AUTO")
        {
            route = null;
            diagnostic = "Local translation requires a resolved source language; AUTO cannot select a model safely.";
            return false;
        }

        route = (source, target) switch
        {
            ("EN", "ZH-HANT") => new(source, target, [EnglishToTraditionalChinese]),
            ("JA", "ZH-HANT") => new(source, target, [JapaneseToEnglish, EnglishToTraditionalChinese]),
            ("KO", "ZH-HANT") => new(source, target, [KoreanToEnglish, EnglishToTraditionalChinese]),
            ("ZH-HANT", "EN") => new(source, target, [TraditionalChineseToEnglish]),
            _ => null,
        };

        diagnostic = route is null
            ? $"Local translation does not support {sourceLanguage} to {targetLanguage}."
            : null;
        return route is not null;
    }

    public LocalTranslationRoute Resolve(string sourceLanguage, string targetLanguage) =>
        TryResolve(sourceLanguage, targetLanguage, out var route, out var diagnostic)
            ? route!
            : throw new NotSupportedException(diagnostic);

    private static string NormalizeSource(string language) => language.Trim().ToUpperInvariant() switch
    {
        "EN-US" or "EN-GB" => "EN",
        "ZH-TW" => "ZH-HANT",
        var normalized => normalized,
    };

    private static string NormalizeTarget(string language) => language.Trim().ToUpperInvariant() switch
    {
        "EN-US" or "EN-GB" => "EN",
        "ZH-TW" => "ZH-HANT",
        var normalized => normalized,
    };
}
