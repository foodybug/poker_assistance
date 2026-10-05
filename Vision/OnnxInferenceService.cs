using System;
using System.IO;
using System.Linq;
using Microsoft.ML;
using OpenCvSharp;
using PokerAssistant.Core;

namespace PokerAssistant.Vision;

/// <summary>
/// ML.NET PredictionEngine 기반 카드 랭크/슈트 추론 서비스.
/// OnnxConverter LabelEncoder 호환성 문제를 완전히 우회하여
/// ML.NET native .zip 모델을 PredictionEngine으로 직접 추론합니다.
/// </summary>
public sealed class OnnxInferenceService : IDisposable
{
    private static OnnxInferenceService? _instance;
    private static readonly object _lock = new();

    private PredictionEngine<OnnxModelBuilder.CardSample, OnnxModelBuilder.CardPrediction>? _rankEngine;
    private PredictionEngine<OnnxModelBuilder.CardSample, OnnxModelBuilder.CardPrediction>? _suitEngine;

    private string[] _rankLabels = Array.Empty<string>();
    private string[] _suitLabels = Array.Empty<string>();

    private bool _initialized = false;
    private bool _disposed = false;

    private OnnxInferenceService() { }

    public static OnnxInferenceService Instance
    {
        get
        {
            if (_instance == null)
                lock (_lock) { _instance ??= new OnnxInferenceService(); }
            return _instance;
        }
    }

    /// <summary>
    /// ML.NET 모델을 초기화합니다. 없으면 빌드합니다.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;

        lock (_lock)
        {
            if (_initialized) return;

            Log("ML.NET 카드 추론 서비스 초기화 시작...");

            OnnxModelBuilder.BuildIfNeeded(force: false);

            var mlCtx = OnnxModelBuilder.GetMLContext();

            // 랭크 모델 로드
            if (File.Exists(OnnxModelBuilder.RankModelPath))
            {
                var rankModel = mlCtx.Model.Load(OnnxModelBuilder.RankModelPath, out _);
                _rankEngine = mlCtx.Model.CreatePredictionEngine<
                    OnnxModelBuilder.CardSample,
                    OnnxModelBuilder.CardPrediction>(rankModel);
                Log("랭크 ML.NET 모델 로드 완료.");
            }

            // 슈트 모델 로드
            if (File.Exists(OnnxModelBuilder.SuitModelPath))
            {
                var suitModel = mlCtx.Model.Load(OnnxModelBuilder.SuitModelPath, out _);
                _suitEngine = mlCtx.Model.CreatePredictionEngine<
                    OnnxModelBuilder.CardSample,
                    OnnxModelBuilder.CardPrediction>(suitModel);
                Log("슈트 ML.NET 모델 로드 완료.");
            }

            // 레이블 맵 로드
            if (File.Exists(OnnxModelBuilder.RankMapPath))
                _rankLabels = File.ReadAllLines(OnnxModelBuilder.RankMapPath);
            if (File.Exists(OnnxModelBuilder.SuitMapPath))
                _suitLabels = File.ReadAllLines(OnnxModelBuilder.SuitMapPath);

            _initialized = true;
            Log("ML.NET 카드 추론 서비스 초기화 완료.");
        }
    }

    /// <summary>
    /// 32x32 이진화 글리프 Mat에서 랭크를 추론합니다.
    /// </summary>
    public (Rank? Rank, double Confidence, string Diag) PredictRank(Mat norm32)
    {
        if (!_initialized) Initialize();
        if (_rankEngine == null) return (null, 0.0, "랭크 엔진 없음");

        try
        {
            var sample = new OnnxModelBuilder.CardSample
            {
                Features = MatToFeatures(norm32),
                Label = ""
            };

            var prediction = _rankEngine.Predict(sample);
            var label = prediction.PredictedLabel?.Trim();
            var rank = OnnxModelBuilder.LabelToRank(label ?? "");

            double conf = 0.0;
            if (prediction.Score?.Length > 0)
            {
                var softmax = Softmax(prediction.Score);
                conf = softmax.Max();
            }

            return (rank, conf, $"ML({label},{conf:P0})");
        }
        catch (Exception ex)
        {
            return (null, 0.0, $"ML 랭크 오류: {ex.Message}");
        }
    }

    /// <summary>
    /// 32x32 이진화 심볼 Mat에서 슈트를 추론합니다.
    /// </summary>
    public (Suit? Suit, double Confidence, string Diag) PredictSuit(Mat norm32)
    {
        if (!_initialized) Initialize();
        if (_suitEngine == null) return (null, 0.0, "슈트 엔진 없음");

        try
        {
            var sample = new OnnxModelBuilder.CardSample
            {
                Features = MatToFeatures(norm32),
                Label = ""
            };

            var prediction = _suitEngine.Predict(sample);
            var label = prediction.PredictedLabel?.Trim();
            var suit = OnnxModelBuilder.LabelToSuit(label ?? "");

            double conf = 0.0;
            if (prediction.Score?.Length > 0)
            {
                var softmax = Softmax(prediction.Score);
                conf = softmax.Max();
            }

            return (suit, conf, $"ML({label},{conf:P0})");
        }
        catch (Exception ex)
        {
            return (null, 0.0, $"ML 슈트 오류: {ex.Message}");
        }
    }

    /// <summary>
    /// Mat(32x32 CV_8UC1)을 1024차원 float 특징 벡터로 변환합니다.
    /// </summary>
    public static float[] MatToFeatures(Mat norm32)
    {
        var features = new float[1024];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                features[y * 32 + x] = norm32.At<byte>(y, x) > 100 ? 1.0f : 0.0f;
        return features;
    }

    private static float[] Softmax(float[] logits)
    {
        float max = logits.Max();
        float[] exp = logits.Select(x => (float)Math.Exp(x - max)).ToArray();
        float sum = exp.Sum();
        return exp.Select(e => e / sum).ToArray();
    }

    public bool IsInitialized => _initialized;
    public bool HasRankModel => _rankEngine != null;
    public bool HasSuitModel => _suitEngine != null;

    private static void Log(string msg)
    {
        Console.WriteLine($"[OnnxInferenceService] {msg}");
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
        try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [OnnxInferenceService] {msg}\r\n"); } catch { }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _rankEngine?.Dispose();
            _suitEngine?.Dispose();
            _disposed = true;
        }
    }
}
