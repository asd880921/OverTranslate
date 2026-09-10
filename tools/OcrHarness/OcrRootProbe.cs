using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using RapidOcrNet;
using SkiaSharp;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using OverTranslate.Services;

// Isolated native-library experiment. No grouping, filtering, fusion, or translation.
internal static class OcrRootProbe
{
    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    internal static int Run(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("--ocr-root-probe output-folder images...");
        Directory.CreateDirectory(args[0]);
        if (args[1] == "--replay") return Replay(args);
        var root = Path.Combine(AppContext.BaseDirectory, "ocrmodels", "onnx");
        using var engine = new RapidOcr();
        engine.InitModels(new RapidOcrModelSet {
            DetModelPath = Path.Combine(root,"shared","det.onnx"),
            ClsModelPath = Path.Combine(root,"shared","cls.onnx"),
            RecModelPath = Path.Combine(root,"cjk","rec.onnx"), KeysPath = Path.Combine(root,"cjk","dict.txt"),
            DetMean = [127.5f,127.5f,127.5f], DetStd = [127.5f,127.5f,127.5f]
        }, 2);
        var prep = typeof(RapidOcr).GetMethod("PrepareDetectorInput", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        var recognizer = (TextRecognizer)typeof(RapidOcr).GetField("_textRecognizer", Hidden)!.GetValue(engine)!;
        if (args[1] == "--grid") return Grid(args, engine, recognizer, prep);
        var results = new List<object>();
        bool geometry = args[1] == "--geometry";
        bool preset = args[1] == "--preset";
        bool candidate = args[1] == "--candidate";
        bool sampling = args[1] == "--sampling";
        foreach (var path in args.Skip(geometry || preset || candidate || sampling ? 2 : 1))
        {
            using var original = SKBitmap.Decode(path);
            int size = RealtimeDetectorSize.For(original.Width, original.Height, RealtimeBlockMode.Subtitle).Primary;
            var shipped = OnnxOcrEngine.CreateOptions(size);
            var variants = new (string Name, RapidOcrOptions Options, bool Align)[] {
                ("shipped", shipped, true), ("unaligned", shipped, false),
                ("pad0", shipped with { Padding = 0 }, true),
                ("native", shipped with { Padding = 0, ImgResize = 4096 }, true),
                ("pad24", shipped with { Padding = 24 }, true),
                ("shortside", shipped with { Padding = 0, ImgResize = 0, LimitSideLen = 256 }, false),
                ("shortside512", shipped with { Padding = 0, ImgResize = 0, LimitSideLen = 512 }, false),
            };
            if (geometry) variants = new[] { ("shipped", shipped, true) }.Concat(
                new[] { "iso-0.5", "iso-0.75", "iso-1", "iso-1.25", "iso-white", "iso-black" }
                .Select(n => (n, shipped with { Padding = 0, ImgResize = 8192 }, true))).ToArray();
            if (preset) variants = [("shipped",shipped,true), ("v6-preset",RapidOcrOptions.PPOCRv6 with {DoAngle=false},false)];
            if (candidate) variants = [("shipped",shipped,true), ("iso-0.75",shipped with {Padding=0,ImgResize=8192},true), ("iso-1.25",shipped with {Padding=0,ImgResize=8192},true)];
            if (sampling) variants = [("iso-0.75-linear",shipped with {Padding=0,ImgResize=8192},true), ("iso-1.25-linear",shipped with {Padding=0,ImgResize=8192},true)];
            foreach (var variant in variants)
            {
                using var transformed = Transform(original, variant.Name);
                using var input = variant.Align ? OnnxOcrEngine.AlignForDetector(transformed, variant.Options.Padding) : transformed.Copy();
                using var prepared = (IDisposable)prep.Invoke(null, [input, variant.Options])!;
                var bitmap = (SKBitmap)prepared.GetType().GetField("Bitmap", Hidden)!.GetValue(prepared)!;
                var scale = prepared.GetType().GetField("Scale", Hidden)!.GetValue(prepared)!;
                var scales = scale.GetType().GetFields(Hidden).ToDictionary(f => f.Name, f => f.GetValue(scale));
                using (var img = SKImage.FromBitmap(bitmap))
                using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                using (var stream = File.Create(Path.Combine(args[0], Path.GetFileNameWithoutExtension(path) + "-" + variant.Name + "-input.png"))) data.SaveTo(stream);
                for (int pass = 0; pass < 2; pass++)
                {
                    var watch = Stopwatch.StartNew();
                    var output = engine.Detect(input, variant.Options);
                    var ms = watch.Elapsed.TotalMilliseconds;
                    var boxes = engine.DetectBoxes(input, variant.Options);
                    var (ratio, offset) = TransformSpec(variant.Name);
                    double rx = Math.Round(original.Width * ratio) / original.Width;
                    double ry = Math.Round(original.Height * ratio) / original.Height;
                    var mapped = output.TextBlocks.Select(b => new TextBlock { Text=b.Text, Chars=b.Chars, CharScores=b.CharScores, BoxScore=b.BoxScore,
                        BoxPoints=b.BoxPoints.Select(p => new SKPointI((int)Math.Round((p.X-offset)/rx), (int)Math.Round((p.Y-offset)/ry))).ToArray() }).ToArray();
                    var blocks = OnnxOcrEngine.ApplyBlockFilters(mapped, "JA", true, false);
                    var final = RealtimeTranslationSession.RejectShortReadings(RealtimeTranslationSession.RejectCollapsedBlocks(
                        OcrService.GroupRealtime(blocks, original.Height, RealtimeBlockMode.Subtitle), original.Height, -1), -1);
                    results.Add(new { Image=path, variant.Name, Pass=pass, Size=size, InputWidth=input.Width, InputHeight=input.Height,
                        PreparedWidth=bitmap.Width, PreparedHeight=bitmap.Height, Scale=scales, Milliseconds=ms,
                        Boxes=boxes.Select(b => new { Points=b.BoxPoints.Select(p => new[]{p.X,p.Y}), b.Score }),
                        Text=output.TextBlocks.Select(b => new { b.Text, b.Chars, b.CharScores, Points=b.BoxPoints.Select(p => new[]{p.X,p.Y}) }),
                        Final=final.Select(GroupingPrototype.Block.From).ToArray() });
                    Console.WriteLine($"ROOT {Path.GetFileName(path)} {variant.Name} p{pass} {ms:F1}ms: {string.Join(" | ", output.TextBlocks.Select(b => b.Text))}");
                }
            }
            // Oracle only: known complete text-line rectangles distinguish detector loss
            // from recognizer loss. Never used to select production boxes.
            var crop = path.Contains("game-1") ? new SKRectI(75,130,1070,215) : new SKRectI(15,0,350,original.Height);
            if (path.Contains("ja-game-") && crop.Right <= original.Width && crop.Bottom <= original.Height)
            {
                using var line = new SKBitmap();
                original.ExtractSubset(line, crop);
                var lines = recognizer.GetTextLines([line]);
                results.Add(new { Image=path, Name="oracle-recognition-only", Text=lines.Select(l => string.Concat(l.Chars ?? [])), Scores=lines.Select(l => l.CharScores) });
                Console.WriteLine("ORACLE " + path + ": " + string.Join(" | ", lines.Select(l => string.Concat(l.Chars ?? []))));
            }
        }
        File.WriteAllText(Path.Combine(args[0], "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions {WriteIndented=true}).ReplaceLineEndings("\r\n"));
        return 0;
    }

    private static SKBitmap Transform(SKBitmap source, string name)
    {
        if (!name.StartsWith("iso-")) return source.Copy();
        var (ratio, padding) = TransformSpec(name);
        int w = (int)Math.Round(source.Width * ratio), h = (int)Math.Round(source.Height * ratio);
        var bitmap = new SKBitmap((w + padding * 2 + 31) / 32 * 32, (h + padding * 2 + 31) / 32 * 32);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(name == "iso-black" ? SKColors.Black : SKColors.White);
        using var image = SKImage.FromBitmap(source);
        var sampling = name.EndsWith("-linear") ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None) : new SKSamplingOptions(SKCubicResampler.Mitchell);
        canvas.DrawImage(image, new SKRect(padding,padding,padding+w,padding+h), sampling);
        return bitmap;
    }

    private static (double Ratio, int Padding) TransformSpec(string name) =>
        !name.StartsWith("iso-") ? (1, 0) : name is "iso-white" or "iso-black" ? (1, 32) :
        (double.Parse(name.Split('-')[1], System.Globalization.CultureInfo.InvariantCulture), 0);

    private static int Replay(string[] args)
    {
        var results = new List<object>();
        foreach (var path in args.Skip(2))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Pass", out var pass)) continue;
                string image = row.GetProperty("Image").GetString()!;
                string name = row.GetProperty("Name").GetString()!;
                using var bitmap = SKBitmap.Decode(image);
                var (ratio, offset) = TransformSpec(name);
                double rx = Math.Round(bitmap.Width * ratio) / bitmap.Width, ry = Math.Round(bitmap.Height * ratio) / bitmap.Height;
                var blocks = row.GetProperty("Text").EnumerateArray().Select(t => new TextBlock {
                    Text=t.GetProperty("Text").GetString()!, Chars=t.GetProperty("Chars").Deserialize<string[]>(),
                    CharScores=t.GetProperty("CharScores").Deserialize<float[]>(),
                    BoxPoints=t.GetProperty("Points").EnumerateArray().Select(p => new SKPointI(
                        (int)Math.Round((p[0].GetInt32()-offset)/rx), (int)Math.Round((p[1].GetInt32()-offset)/ry))).ToArray()
                }).ToArray();
                var raw = OnnxOcrEngine.ApplyBlockFilters(blocks,"JA",true,false);
                var final = RealtimeTranslationSession.RejectShortReadings(RealtimeTranslationSession.RejectCollapsedBlocks(
                    OcrService.GroupRealtime(raw,bitmap.Height,RealtimeBlockMode.Subtitle),bitmap.Height,-1),-1);
                results.Add(new { Image=image, Name=name, Pass=pass.GetInt32(), Final=final.Select(GroupingPrototype.Block.From).ToArray() });
                if (pass.GetInt32()==1) Console.WriteLine($"FINAL {Path.GetFileName(image)} {name}: {string.Join(" | ",final.Select(x=>x.Text))}");
            }
        }
        File.WriteAllText(Path.Combine(args[0],"final.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}).ReplaceLineEndings("\r\n"));
        return 0;
    }

    private static int Grid(string[] args, RapidOcr engine, TextRecognizer recognizer, MethodInfo prep)
    {
        var detector = (TextDetector)typeof(RapidOcr).GetField("_textDetector",Hidden)!.GetValue(engine)!;
        var partsMethod = typeof(RapidOcr).Assembly.GetType("RapidOcrNet.OcrUtils")!.GetMethod("GetPartImages", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!;
        var rows = new List<object>();
        foreach (var path in args.Skip(2))
        {
            using var source = SKBitmap.Decode(path);
            var options = OnnxOcrEngine.CreateOptions(RealtimeDetectorSize.For(source.Width,source.Height,RealtimeBlockMode.Subtitle).Primary);
            using var aligned = OnnxOcrEngine.AlignForDetector(source,options.Padding);
            using var prepared = (IDisposable)prep.Invoke(null,[aligned,options])!;
            var bitmap = (SKBitmap)prepared.GetType().GetField("Bitmap",Hidden)!.GetValue(prepared)!;
            var legacy = (ScaleParam)prepared.GetType().GetField("Scale",Hidden)!.GetValue(prepared)!;
            foreach (string name in new[]{"grid-legacy","grid-floor","grid-nearest","grid-ceil"})
            {
                double ratio = (Math.Min(options.ImgResize,Math.Max(aligned.Width,aligned.Height))+2d*options.Padding)/Math.Max(bitmap.Width,bitmap.Height);
                int Round(double n) => Math.Max(32,32*(int)(name=="grid-floor" ? Math.Floor(n/32) : name=="grid-ceil" ? Math.Ceiling(n/32) : Math.Round(n/32)));
                var scale = name=="grid-legacy" ? legacy : new ScaleParam(bitmap.Width,bitmap.Height,Round(bitmap.Width*ratio),Round(bitmap.Height*ratio));
                for (int pass=0;pass<2;pass++)
                {
                    var watch=Stopwatch.StartNew();
                    var boxes=detector.GetTextBoxes(bitmap,scale,options.BoxScoreThresh,options.BoxThresh,options.UnClipRatio);
                    var parts=(SKBitmap[])partsMethod.Invoke(null,[bitmap,boxes])!;
                    try
                    {
                        var lines=recognizer.GetTextLines(parts);
                        var blocks=new List<TextBlock>();
                        for(int i=0;i<boxes!.Count;i++)
                        {
                            if(lines[i].CharScores is not {Length:>0} scores || scores.Average()<options.TextScore) continue;
                            var points=(SKPointI[])boxes[i].BoxPoints.Clone();
                            prepared.GetType().GetMethod("MapToOriginal",Hidden)!.Invoke(prepared,[points]);
                            blocks.Add(new TextBlock{ Text=string.Concat(lines[i].Chars??[]),Chars=lines[i].Chars,CharScores=scores,BoxScore=boxes[i].Score,BoxPoints=points });
                        }
                        var ms=watch.Elapsed.TotalMilliseconds;
                        var kept=OnnxOcrEngine.ApplyBlockFilters(blocks.ToArray(),"JA",true,false);
                        var final=RealtimeTranslationSession.RejectShortReadings(RealtimeTranslationSession.RejectCollapsedBlocks(
                            OcrService.GroupRealtime(kept,source.Height,RealtimeBlockMode.Subtitle),source.Height,-1),-1);
                        if(name=="grid-legacy" && pass==0)
                        {
                            var actual=engine.Detect(aligned,options).TextBlocks.Select(b=>b.Text).ToArray();
                            if(!actual.SequenceEqual(blocks.Select(b=>b.Text))) throw new Exception("Raw path parity failed: "+path);
                        }
                        rows.Add(new{Image=path,Name=name,Pass=pass,Milliseconds=ms,Scale=scale,Text=blocks.Select(b=>new{b.Text,b.CharScores,Points=b.BoxPoints.Select(p=>new[]{p.X,p.Y})}),Final=final.Select(GroupingPrototype.Block.From).ToArray()});
                        Console.WriteLine($"GRID {Path.GetFileName(path)} {name} p{pass} {ms:F1}ms: {string.Join(" | ",final.Select(b=>b.Text))}");
                    }
                    finally{foreach(var part in parts)part.Dispose();}
                }
            }
        }
        File.WriteAllText(Path.Combine(args[0],"grid.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}).ReplaceLineEndings("\r\n"));
        return 0;
    }
}
