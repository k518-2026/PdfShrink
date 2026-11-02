using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfShrink.Core;

/// <summary>圧縮の設定。</summary>
public sealed class ShrinkOptions
{
    /// <summary>JPEG の画質（10〜100）。</summary>
    public int Quality { get; init; } = 60;

    /// <summary>画像の長辺の上限（ピクセル）。0 なら縮小しない。</summary>
    public int MaxLongSide { get; init; } = 2000;
}

/// <summary>1 ファイル分の結果。</summary>
public sealed class ShrinkResult
{
    public long OriginalBytes { get; init; }
    public long OutputBytes { get; init; }
    public int ImagesFound { get; init; }
    public int ImagesRecompressed { get; init; }
    public int ImagesSkipped { get; init; }
}

/// <summary>
/// PDF の中の画像を JPEG で圧縮し直して、ファイルを小さくする。
/// PDF の読み書きは PdfSharp（MIT ライセンス）。画像の読み込みと JPEG 化は WPF 標準のコーデックを使う。
///
/// 対象にする画像:
///   ・JPEG（DCTDecode）の RGB / グレー
///   ・無圧縮または Flate 圧縮の RGB / グレー（8 ビット）
/// 触らない画像（見た目が変わるおそれがあるため）:
///   ・CMYK・特色・インデックスカラー、1 ビットの白黒画像、マスク画像、/Decode や色キーマスクを持つもの、JPEG2000 など
/// 透明度（/SMask）は別の画像として残るので、そのまま保たれる。
/// 圧縮し直して小さくならなかった画像は元のままにする。
/// </summary>
public static class PdfCompressor
{
    public static ShrinkResult Compress(
        string inputPath, string outputPath, ShrinkOptions options,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        long originalBytes = new FileInfo(inputPath).Length;

        PdfDocument doc;
        try
        {
            doc = PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex)
        {
            throw new InvalidOperationException("PDF を開けませんでした（パスワード付き・壊れている・未対応の形式の可能性）: " + ex.Message, ex);
        }

        int found = 0, done = 0, skipped = 0;
        var visited = new HashSet<PdfObjectID>();

        using (doc)
        {
            for (int p = 0; p < doc.PageCount; p++)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"{p + 1} / {doc.PageCount} ページ");
                var resources = doc.Pages[p].Elements.GetDictionary("/Resources");
                ProcessResources(resources, options, visited, ref found, ref done, ref skipped, token, depth: 0);
            }

            // 本文（コンテンツ ストリーム）も圧縮して保存する
            doc.Options.CompressContentStreams = true;
            doc.Options.NoCompression = false;
            doc.Save(outputPath);
        }

        return new ShrinkResult
        {
            OriginalBytes = originalBytes,
            OutputBytes = new FileInfo(outputPath).Length,
            ImagesFound = found,
            ImagesRecompressed = done,
            ImagesSkipped = skipped
        };
    }

    private static void ProcessResources(
        PdfDictionary? resources, ShrinkOptions options, HashSet<PdfObjectID> visited,
        ref int found, ref int done, ref int skipped, CancellationToken token, int depth)
    {
        if (resources is null || depth > 10) return;
        var xobjects = resources.Elements.GetDictionary("/XObject");
        if (xobjects is null) return;

        foreach (var key in xobjects.Elements.Keys.ToList())
        {
            token.ThrowIfCancellationRequested();
            if (xobjects.Elements[key] is not PdfReference reference) continue;
            if (!visited.Add(reference.ObjectID)) continue;   // 複数ページで共有している画像は 1 回だけ
            if (reference.Value is not PdfDictionary dict) continue;

            var subtype = dict.Elements.GetName("/Subtype");
            if (subtype == "/Form")
            {
                // フォーム XObject の中にある画像もたどる
                ProcessResources(dict.Elements.GetDictionary("/Resources"), options, visited,
                    ref found, ref done, ref skipped, token, depth + 1);
            }
            else if (subtype == "/Image")
            {
                found++;
                bool replaced;
                try { replaced = TryRecompress(dict, options); }
                catch { replaced = false; }   // 1 枚読めなくても全体は続ける
                if (replaced) done++; else skipped++;
            }
        }
    }

    /// <summary>画像 1 枚を JPEG で圧縮し直す。置き換えたら true。</summary>
    private static bool TryRecompress(PdfDictionary dict, ShrinkOptions options)
    {
        if (dict.Stream is null) return false;
        if (dict.Elements.GetBoolean("/ImageMask")) return false;
        if (dict.Elements.ContainsKey("/Decode")) return false;
        if (dict.Elements["/Mask"] is PdfArray) return false;     // 色キーマスクは JPEG で色が揺れると壊れる

        int width = dict.Elements.GetInteger("/Width");
        int height = dict.Elements.GetInteger("/Height");
        if (width <= 0 || height <= 0) return false;

        int components = ComponentCount(dict.Elements["/ColorSpace"]);
        if (components != 1 && components != 3) return false;

        string filter = SingleFilter(dict);   // "" = 無圧縮
        byte[] original = dict.Stream.Value;
        BitmapSource? source;

        if (filter == "/DCTDecode")
        {
            source = DecodeJpeg(original, components);
        }
        else if (filter is "" or "/FlateDecode")
        {
            if (dict.Elements.GetInteger("/BitsPerComponent") != 8) return false;
            byte[] raw = filter == "" ? original : dict.Stream.UnfilteredValue;
            int stride = width * components;
            // 予測子付きなどで展開結果の長さが合わないものは扱わない
            if (raw is null || raw.Length != (long)stride * height) return false;
            source = BitmapSource.Create(width, height, 96, 96,
                components == 3 ? PixelFormats.Rgb24 : PixelFormats.Gray8, null, raw, stride);
        }
        else
        {
            return false;
        }

        if (source is null) return false;

        if (options.MaxLongSide > 0)
        {
            int longSide = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longSide > options.MaxLongSide)
            {
                double scale = (double)options.MaxLongSide / longSide;
                source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(options.Quality, 10, 100) };
        encoder.Frames.Add(BitmapFrame.Create(source));
        byte[] jpeg;
        using (var ms = new MemoryStream())
        {
            encoder.Save(ms);
            jpeg = ms.ToArray();
        }

        // 小さくならないなら元のまま（縮小した場合も、バイト数が増えるなら置き換えない）
        if (jpeg.Length >= original.Length) return false;

        // できた JPEG の実際の大きさと色数を読み直して、辞書を合わせる
        var check = BitmapDecoder.Create(new MemoryStream(jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        bool gray = check.Format == PixelFormats.Gray8;

        dict.Stream.Value = jpeg;
        dict.Elements.SetName("/Filter", "/DCTDecode");
        dict.Elements.Remove("/DecodeParms");
        dict.Elements.SetInteger("/Width", check.PixelWidth);
        dict.Elements.SetInteger("/Height", check.PixelHeight);
        dict.Elements.SetInteger("/BitsPerComponent", 8);
        dict.Elements.SetName("/ColorSpace", gray ? "/DeviceGray" : "/DeviceRGB");
        return true;
    }

    private static BitmapSource? DecodeJpeg(byte[] data, int components)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(data),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (frame.Format == PixelFormats.Cmyk32) return null;   // CMYK は色が変わりやすいので触らない
        var target = components == 1 ? PixelFormats.Gray8 : PixelFormats.Bgr24;
        return frame.Format == target ? frame : new FormatConvertedBitmap(frame, target, null, 0);
    }

    /// <summary>フィルターが 1 つ（または無し）ならその名前。2 つ以上や不明は "?"。</summary>
    private static string SingleFilter(PdfDictionary dict)
    {
        PdfItem? f = dict.Elements["/Filter"];
        if (f is null) return "";
        if (f is PdfReference r) f = r.Value;
        if (f is PdfName n) return n.Value;
        if (f is PdfArray a)
        {
            if (a.Elements.Count == 0) return "";
            if (a.Elements.Count == 1 && a.Elements[0] is PdfName one) return one.Value;
        }
        return "?";
    }

    /// <summary>色空間の成分数。扱わない色空間は 0。</summary>
    private static int ComponentCount(PdfItem? cs)
    {
        if (cs is PdfReference r) cs = r.Value;
        switch (cs)
        {
            case PdfName n:
                return n.Value switch
                {
                    "/DeviceRGB" or "/CalRGB" => 3,
                    "/DeviceGray" or "/CalGray" => 1,
                    _ => 0
                };
            case PdfArray a when a.Elements.Count >= 1 && a.Elements[0] is PdfName head:
                if (head.Value == "/ICCBased" && a.Elements.Count >= 2)
                {
                    var stream = a.Elements[1] is PdfReference sr ? sr.Value as PdfDictionary : a.Elements[1] as PdfDictionary;
                    int nComp = stream?.Elements.GetInteger("/N") ?? 0;
                    return nComp is 1 or 3 ? nComp : 0;
                }
                if (head.Value is "/CalRGB") return 3;
                if (head.Value is "/CalGray") return 1;
                return 0;
            default:
                return 0;
        }
    }

    /// <summary>「名前_compressed.pdf」。既にあれば「名前_compressed (2).pdf」…</summary>
    public static string OutputPathFor(string inputPath)
    {
        string dir = Path.GetDirectoryName(inputPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(inputPath);
        string path = Path.Combine(dir, stem + "_compressed.pdf");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{stem}_compressed ({i}).pdf");
        return path;
    }
}
