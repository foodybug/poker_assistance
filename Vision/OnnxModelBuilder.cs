using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Microsoft.ML;
using Microsoft.ML.Data;
using PokerAssistant.Core;

namespace PokerAssistant.Vision;

/// <summary>
/// ML.NET 기반 합성 훈련 데이터 생성 + 모델 빌드/저장 클래스.
/// - 13개 랭크 분류기 (card_rank.zip)
/// - 4개 슈트 분류기 (card_suit.zip)
/// 최초 실행 시 모델이 없으면 자동으로 생성하여 Assets/Models/에 저장합니다.
/// </summary>
public static class OnnxModelBuilder
{
    private static readonly string ModelsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Models");
    public static string RankModelPath => Path.Combine(ModelsDir, "card_rank.zip");
    public static string SuitModelPath => Path.Combine(ModelsDir, "card_suit.zip");
    public static string RankMapPath => Path.Combine(ModelsDir, "rank_labels.txt");
    public static string SuitMapPath => Path.Combine(ModelsDir, "suit_labels.txt");

    // 32x32 = 1024 픽셀 특징 벡터
    private const int FeatureCount = 1024;

    // 훈련 폰트 목록 (실제 포커 클라이언트에서 사용되는 폰트 최대한 포함)
    private static readonly string[] RankFonts =
    {
        "Arial", "Segoe UI", "Tahoma", "Impact", "Arial Black",
        "Verdana", "Georgia", "Trebuchet MS", "Consolas",
        "Times New Roman", "Calibri", "Franklin Gothic Medium"
    };

    private static readonly int[] FontSizes = { 16, 20, 24, 28 };

    // ML.NET 데이터 클래스
    public class CardSample
    {
        [VectorType(FeatureCount)]
        public float[] Features { get; set; } = new float[FeatureCount];
        public string Label { get; set; } = "";
    }

    public class CardPrediction
    {
        [ColumnName("PredictedLabel")]
        public string PredictedLabel { get; set; } = "";
        [ColumnName("Score")]
        public float[] Score { get; set; } = Array.Empty<float>();
    }

    private static MLContext? _mlCtx;
    public static MLContext GetMLContext() => _mlCtx ??= new MLContext(seed: 42);

    /// <summary>
    /// 랭크 및 슈트 ML.NET 모델이 없거나 force=true 이면 새로 빌드합니다.
    /// </summary>
    public static void BuildIfNeeded(bool force = false)
    {
        Directory.CreateDirectory(ModelsDir);

        bool rankMissing = !File.Exists(RankModelPath) || !File.Exists(RankMapPath);
        bool suitMissing = !File.Exists(SuitModelPath) || !File.Exists(SuitMapPath);

        if (rankMissing || force)
        {
            Log("랭크 ML 모델 생성 시작...");
            BuildRankModel();
            Log("랭크 ML 모델 생성 완료.");
        }
        else
        {
            Log("랭크 ML 모델이 이미 존재합니다.");
        }

        if (suitMissing || force)
        {
            Log("슈트 ML 모델 생성 시작...");
            BuildSuitModel();
            Log("슈트 ML 모델 생성 완료.");
        }
        else
        {
            Log("슈트 ML 모델이 이미 존재합니다.");
        }
    }

    private static void BuildRankModel()
    {
        var mlCtx = GetMLContext();

        // 레이블 맵 저장
        var rankLabels = Enum.GetValues<Rank>().Select(RankToLabel).ToArray();
        File.WriteAllLines(RankMapPath, rankLabels);

        Log("랭크 훈련 데이터 생성 중...");
        var samples = GenerateRankSamples();
        Log($"랭크 훈련 샘플 총 {samples.Count}개 생성 완료.");

        var dataView = mlCtx.Data.LoadFromEnumerable(samples);

        // MapKeyToValue 없이 훈련 (PredictedLabel 디코딩은 추론 서비스에서 처리)
        var pipeline = mlCtx.Transforms.Conversion.MapValueToKey("Label", "Label")
            .Append(mlCtx.Transforms.NormalizeMinMax("Features"))
            .Append(mlCtx.MulticlassClassification.Trainers.SdcaMaximumEntropy(
                labelColumnName: "Label",
                featureColumnName: "Features",
                maximumNumberOfIterations: 150))
            .Append(mlCtx.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

        Log("랭크 모델 훈련 중 (SdcaMaximumEntropy)...");
        var model = pipeline.Fit(dataView);

        // ML.NET native .zip 형식으로 저장
        mlCtx.Model.Save(model, dataView.Schema, RankModelPath);
        Log($"랭크 모델 저장: {RankModelPath}");
    }

    private static void BuildSuitModel()
    {
        var mlCtx = GetMLContext();

        var suitLabels = Enum.GetValues<Suit>().Select(SuitToLabel).ToArray();
        File.WriteAllLines(SuitMapPath, suitLabels);

        Log("슈트 훈련 데이터 생성 중...");
        var samples = GenerateSuitSamples();
        Log($"슈트 훈련 샘플 총 {samples.Count}개 생성 완료.");

        var dataView = mlCtx.Data.LoadFromEnumerable(samples);

        var pipeline = mlCtx.Transforms.Conversion.MapValueToKey("Label", "Label")
            .Append(mlCtx.Transforms.NormalizeMinMax("Features"))
            .Append(mlCtx.MulticlassClassification.Trainers.SdcaMaximumEntropy(
                labelColumnName: "Label",
                featureColumnName: "Features",
                maximumNumberOfIterations: 150))
            .Append(mlCtx.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

        Log("슈트 모델 훈련 중 (SdcaMaximumEntropy)...");
        var model = pipeline.Fit(dataView);

        mlCtx.Model.Save(model, dataView.Schema, SuitModelPath);
        Log($"슈트 모델 저장: {SuitModelPath}");
    }

    // =============================================================
    // 합성 훈련 데이터 생성
    // =============================================================

    private static List<CardSample> GenerateRankSamples()
    {
        var samples = new List<CardSample>();
        var ranks = Enum.GetValues<Rank>();
        var rng = new Random(42);

        foreach (var rank in ranks)
        {
            string label = RankToLabel(rank);
            string baseStr = RankToString(rank);
            string[] strs = rank == Rank.Ten ? new[] { "10", "T" } : new[] { baseStr };

            foreach (var str in strs)
            {
                foreach (var fontName in RankFonts)
                {
                    foreach (var fontSize in FontSizes)
                    {
                        foreach (var style in new[] { FontStyle.Bold, FontStyle.Regular })
                        {
                            // 기본 샘플
                            var feat = RenderRankToFeatures(str, fontName, fontSize, style, 0, 0.0, rng);
                            if (feat != null) samples.Add(new CardSample { Features = feat, Label = label });

                            // 회전 증강
                            foreach (float rot in new[] { -12f, -6f, 6f, 12f })
                            {
                                var rf = RenderRankToFeatures(str, fontName, fontSize, style, rot, 0.0, rng);
                                if (rf != null) samples.Add(new CardSample { Features = rf, Label = label });
                            }

                            // 노이즈 증강
                            for (int n = 0; n < 4; n++)
                            {
                                var nf = RenderRankToFeatures(str, fontName, fontSize, style, 0, 0.04 + n * 0.04, rng);
                                if (nf != null) samples.Add(new CardSample { Features = nf, Label = label });
                            }
                        }
                    }
                }
            }
        }

        return samples;
    }

    private static List<CardSample> GenerateSuitSamples()
    {
        var samples = new List<CardSample>();
        var suits = Enum.GetValues<Suit>();
        var rng = new Random(42);
        string[] suitFonts = { "Segoe UI Symbol", "Arial Unicode MS", "Arial", "Segoe UI", "Tahoma", "Lucida Sans Unicode" };

        foreach (var suit in suits)
        {
            string label = SuitToLabel(suit);
            string suitChar = suit switch
            {
                Suit.Spades => "♠", Suit.Hearts => "♥",
                Suit.Diamonds => "♦", Suit.Clubs => "♣", _ => "♠"
            };

            foreach (var fontName in suitFonts)
            {
                foreach (var fontSize in new[] { 18, 22, 26 })
                {
                    var feat = RenderSuitToFeatures(suitChar, fontName, fontSize, 0, 0.0, rng);
                    if (feat != null) samples.Add(new CardSample { Features = feat, Label = label });

                    foreach (float rot in new[] { -10f, 10f })
                    {
                        var rf = RenderSuitToFeatures(suitChar, fontName, fontSize, rot, 0.0, rng);
                        if (rf != null) samples.Add(new CardSample { Features = rf, Label = label });
                    }

                    for (int n = 0; n < 5; n++)
                    {
                        var nf = RenderSuitToFeatures(suitChar, fontName, fontSize, 0, 0.04 + n * 0.04, rng);
                        if (nf != null) samples.Add(new CardSample { Features = nf, Label = label });
                    }
                }
            }
        }

        return samples;
    }

    // =============================================================
    // 렌더링 유틸리티
    // =============================================================

    private static float[]? RenderRankToFeatures(
        string text, string fontName, int fontSize, FontStyle style,
        float rotation, double noise, Random rng)
    {
        try
        {
            using var font = new Font(fontName, fontSize, style);
            using var bmp = new Bitmap(72, 72, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                if (rotation != 0)
                {
                    g.TranslateTransform(36, 36);
                    g.RotateTransform(rotation);
                    g.TranslateTransform(-36, -36);
                }
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                g.DrawString(text, font, Brushes.White, 4, 4);
            }
            return BitmapToNormalized32x32Features(bmp, noise, rng);
        }
        catch { return null; }
    }

    private static float[]? RenderSuitToFeatures(
        string symbol, string fontName, int fontSize,
        float rotation, double noise, Random rng)
    {
        try
        {
            using var font = new Font(fontName, fontSize, FontStyle.Regular);
            using var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                if (rotation != 0)
                {
                    g.TranslateTransform(32, 32);
                    g.RotateTransform(rotation);
                    g.TranslateTransform(-32, -32);
                }
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                g.DrawString(symbol, font, Brushes.White, 4, 4);
            }
            return BitmapToNormalized32x32Features(bmp, noise, rng);
        }
        catch { return null; }
    }

    private static float[]? BitmapToNormalized32x32Features(Bitmap bmp, double noise, Random rng)
    {
        int w = bmp.Width, h = bmp.Height;
        int minX = w, maxX = 0, minY = h, maxY = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (bmp.GetPixel(x, y).R > 100)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }

        if (maxX < minX || maxY < minY) return null;

        int gw = maxX - minX + 1, gh = maxY - minY + 1;
        double scale = 26.0 / Math.Max(gw, gh);
        int tw = Math.Clamp((int)Math.Round(gw * scale), 2, 28);
        int th = Math.Clamp((int)Math.Round(gh * scale), 2, 28);
        int ox = (32 - tw) / 2, oy = (32 - th) / 2;

        using var crop = new Bitmap(gw, gh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
            g.DrawImage(bmp, new Rectangle(0, 0, gw, gh), new Rectangle(minX, minY, gw, gh), GraphicsUnit.Pixel);

        using var resized = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
            g.DrawImage(crop, 0, 0, tw, th);

        var features = new float[1024];
        for (int ry = 0; ry < th; ry++)
        {
            for (int rx = 0; rx < tw; rx++)
            {
                int fx = ox + rx, fy = oy + ry;
                if (fx < 0 || fx >= 32 || fy < 0 || fy >= 32) continue;
                float val = resized.GetPixel(rx, ry).R > 100 ? 1.0f : 0.0f;
                if (noise > 0)
                {
                    double n = rng.NextGaussian() * noise;
                    val = (val + (float)n) > 0.5f ? 1.0f : 0.0f;
                }
                features[fy * 32 + fx] = val;
            }
        }
        return features;
    }

    // =============================================================
    // 레이블 변환 유틸리티
    // =============================================================

    public static string RankToLabel(Rank r) => r switch
    {
        Rank.Ace => "A", Rank.King => "K", Rank.Queen => "Q", Rank.Jack => "J",
        Rank.Ten => "T", Rank.Nine => "9", Rank.Eight => "8", Rank.Seven => "7",
        Rank.Six => "6", Rank.Five => "5", Rank.Four => "4", Rank.Three => "3",
        Rank.Two => "2", _ => "A"
    };

    public static Rank? LabelToRank(string label) => label switch
    {
        "A" => Rank.Ace, "K" => Rank.King, "Q" => Rank.Queen, "J" => Rank.Jack,
        "T" => Rank.Ten, "9" => Rank.Nine, "8" => Rank.Eight, "7" => Rank.Seven,
        "6" => Rank.Six, "5" => Rank.Five, "4" => Rank.Four, "3" => Rank.Three,
        "2" => Rank.Two, _ => null
    };

    public static string SuitToLabel(Suit s) => s switch
    {
        Suit.Spades => "S", Suit.Hearts => "H", Suit.Diamonds => "D", Suit.Clubs => "C", _ => "S"
    };

    public static Suit? LabelToSuit(string label) => label switch
    {
        "S" => Suit.Spades, "H" => Suit.Hearts, "D" => Suit.Diamonds, "C" => Suit.Clubs, _ => null
    };

    private static string RankToString(Rank r) => r switch
    {
        Rank.Ace => "A", Rank.King => "K", Rank.Queen => "Q", Rank.Jack => "J",
        Rank.Ten => "10", Rank.Nine => "9", Rank.Eight => "8", Rank.Seven => "7",
        Rank.Six => "6", Rank.Five => "5", Rank.Four => "4", Rank.Three => "3",
        Rank.Two => "2", _ => "A"
    };

    private static void Log(string msg)
    {
        Console.WriteLine($"[OnnxModelBuilder] {msg}");
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
        try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [OnnxModelBuilder] {msg}\r\n"); } catch { }
    }
}

internal static class RandomExtensions
{
    public static double NextGaussian(this Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
