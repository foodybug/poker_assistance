using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using PokerAssistant.Config;
using PokerAssistant.Core;

namespace PokerAssistant.Vision;

public class CardRecognitionEngine
{
    public record RecognitionResult(
        List<Card> HeroCards,
        List<Card> BoardCards,
        double PotAmount,
        double CallAmount,
        bool HeroDetected,
        bool BoardDetected,
        string StatusMessage,
        string DiagnosticDetails
    );

    // Standardized 32x32 Aspect-Ratio Preserved Templates for all 13 ranks and 4 suits (폴백용)
    private static readonly Dictionary<Rank, List<Mat>> RankTemplates = GenerateStandardRankTemplates();
    private static readonly Dictionary<Suit, List<Mat>> SuitTemplates = GenerateStandardSuitTemplates();

    // ONNX 추론 서비스 싱글톤 (비동기 초기화: 앱 시작 시 백그라운드로 빌드)
    private static readonly OnnxInferenceService OnnxService = InitOnnxService();

    private static OnnxInferenceService InitOnnxService()
    {
        var svc = OnnxInferenceService.Instance;
        // 백그라운드에서 초기화 (UI 블로킹 방지)
        System.Threading.Tasks.Task.Run(() =>
        {
            try { svc.Initialize(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[CardRecognitionEngine] ONNX 초기화 실패 (폴백 모드 사용): {ex.Message}");
            }
        });
        return svc;
    }

    public static RecognitionResult RecognizeTableState(System.Drawing.Bitmap frame, GameProfile profile)
    {
        var heroCards = new List<Card>();
        var boardCards = new List<Card>();
        string status = "";
        string diag = "";

        try
        {
            using var heroBmp = ScreenCaptureService.CropRegion(
                frame,
                profile.HeroCardsRegion.X,
                profile.HeroCardsRegion.Y,
                profile.HeroCardsRegion.Width,
                profile.HeroCardsRegion.Height
            );

            using var boardBmp = ScreenCaptureService.CropRegion(
                frame,
                profile.BoardCardsRegion.X,
                profile.BoardCardsRegion.Y,
                profile.BoardCardsRegion.Width,
                profile.BoardCardsRegion.Height
            );

            using var potBmp = ScreenCaptureService.CropRegion(
                frame,
                profile.PotRegion.X,
                profile.PotRegion.Y,
                profile.PotRegion.Width,
                profile.PotRegion.Height
            );

            var (hCards, hDiag) = DetectHeroCardsWithDiag(heroBmp);
            heroCards = hCards;
            diag = hDiag;

            bool heroDetected = heroCards.Count >= 2;
            boardCards = DetectBoardCards(boardBmp);
            bool boardDetected = boardCards.Count >= 3;

            double potAmount = EstimatePotAmount(potBmp);

            if (heroDetected)
            {
                status = $"내 카드: {string.Join(" ", heroCards)} (고정밀 비전 인식 완료)";
                if (boardDetected) status += $" | 보드: {string.Join(" ", boardCards)}";
                if (potAmount > 0)
                {
                    string potDisplay = potAmount % 1 != 0 
                        ? potAmount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) 
                        : (potAmount < 1000 ? potAmount.ToString("0") : potAmount.ToString("N0"));
                    status += $" | 팟: {potDisplay}";
                }
            }
            else if (heroCards.Count == 1)
            {
                status = $"내 카드 1장만 인식됨: {heroCards[0]} (카드 영역을 조금 넓혀주세요)";
            }
            else
            {
                status = "내 카드 미인식. [🎴 내 카드 지정] 버튼으로 카드 2장을 드래그해주세요.";
            }

            return new RecognitionResult(heroCards, boardCards, potAmount, 0, heroDetected, boardDetected, status, diag);
        }
        catch (Exception ex)
        {
            return new RecognitionResult(
                new List<Card>(),
                new List<Card>(),
                2000, 0, false, false,
                $"인식 오류: {ex.Message}",
                ex.ToString()
            );
        }
    }

    public static (List<Card> Cards, string Diagnostics) DetectHeroCardsWithDiag(System.Drawing.Bitmap heroBmp)
    {
        var cards = new List<Card>();
        if (heroBmp.Width < 20 || heroBmp.Height < 20)
        {
            return (cards, $"해상도 부족: {heroBmp.Width}x{heroBmp.Height}");
        }

        using var srcMat = BitmapConverter.ToMat(heroBmp);
        int w = srcMat.Width;
        int h = srcMat.Height;

        // Hero Card 1 search zone: Left 55%
        using var c1Mat = new Mat(srcMat, new Rect(0, 0, (int)(w * 0.55), (int)(h * 0.95)));
        var (c1, d1) = ExtractCardFromMat(c1Mat, "C1");
        if (c1.HasValue) cards.Add(c1.Value);

        // Hero Card 2 search zone: Right 60% (from 40% to 100%)
        int c2Left = (int)(w * 0.40);
        using var c2Mat = new Mat(srcMat, new Rect(c2Left, 0, w - c2Left, (int)(h * 0.95)));
        var (c2, d2) = ExtractCardFromMat(c2Mat, "C2");
        if (c2.HasValue) cards.Add(c2.Value);

        string fullDiag = $"[Vision Engine {w}x{h}] {d1} | {d2}";
        return (cards, fullDiag);
    }

    public static List<Card> DetectHeroCards(System.Drawing.Bitmap heroBmp)
    {
        return DetectHeroCardsWithDiag(heroBmp).Cards;
    }

    public static List<Card> DetectBoardCards(System.Drawing.Bitmap boardBmp)
    {
        var cards = new List<Card>();
        if (boardBmp.Width < 50 || boardBmp.Height < 20) return cards;

        using var srcMat = BitmapConverter.ToMat(boardBmp);
        int w = srcMat.Width;
        int h = srcMat.Height;
        int slotWidth = w / 5;

        for (int i = 0; i < 5; i++)
        {
            int left = i * slotWidth;
            int right = Math.Min(w - 1, (i + 1) * slotWidth);
            int slotW = right - left + 1;
            if (slotW < 14) continue;

            using var slotMat = new Mat(srcMat, new Rect(left, 0, slotW, (int)(h * 0.95)));
            var (card, _) = ExtractCardFromMat(slotMat, $"B{i + 1}");
            if (card.HasValue) cards.Add(card.Value);
        }

        if (cards.Count < 3)
        {
            cards.Clear();
        }

        return cards;
    }

    private static (Card? Card, string Diag) ExtractCardFromMat(Mat inputMat, string tag)
    {
        if (inputMat.Width < 12 || inputMat.Height < 12) return (null, $"{tag}: 크기 부족");

        // 1. Precise Card Face Localization
        var cardFaceRect = FindCardFace(inputMat);
        using var mat = new Mat(inputMat, cardFaceRect);

        if (mat.Width < 12 || mat.Height < 12) return (null, $"{tag}: 카드 영역 부족");

        // 2. Universal Binarization (Foreground glyphs ALWAYS 255, Background 0)
        using var grayMat = new Mat();
        Cv2.CvtColor(mat, grayMat, ColorConversionCodes.BGR2GRAY);

        using var binaryMat = CreateStandardForegroundBinary(grayMat);
        if (binaryMat == null || Cv2.CountNonZero(binaryMat) < 12)
        {
            return (null, $"{tag}: 이진화 유효 픽셀 부족");
        }

        // 3. Find Contours with Two-Level Hierarchy (CComp)
        Cv2.FindContours(
            binaryMat,
            out Point[][] contours,
            out HierarchyIndex[] hierarchy,
            RetrievalModes.CComp,
            ContourApproximationModes.ApproxSimple
        );

        if (contours.Length == 0)
        {
            return (null, $"{tag}: 윤곽선 감지 불가");
        }

        int zoneW = mat.Width;
        int zoneH = mat.Height;

        // 4. Extract Candidate Bounding Boxes
        var baseCandidates = new List<(Rect Box, int ContourIdx)>();
        for (int i = 0; i < contours.Length; i++)
        {
            if (hierarchy[i].Parent != -1) continue; // Only outer contours

            var r = Cv2.BoundingRect(contours[i]);
            int area = r.Width * r.Height;
            double aspect = (double)r.Width / r.Height;

            // Reject card border frame
            if (r.Width >= zoneW * 0.90 && r.Height >= zoneH * 0.90) continue;

            if (r.Width >= 4 && r.Height >= 7 &&
                r.Width <= zoneW * 0.85 && r.Height <= zoneH * 0.85 &&
                aspect >= 0.15 && aspect <= 1.50 && area >= 14)
            {
                baseCandidates.Add((r, i));
            }
        }

        if (baseCandidates.Count == 0)
        {
            return (null, $"{tag}: 글자 후보 없음");
        }

        // 5. Multi-Digit ("10") Pairing
        var candidatesToTest = new List<(Rect Box, bool IsMerged10, int ContourIdx)>();
        foreach (var c in baseCandidates)
        {
            candidatesToTest.Add((c.Box, false, c.ContourIdx));
        }

        for (int i = 0; i < baseCandidates.Count; i++)
        {
            for (int j = 0; j < baseCandidates.Count; j++)
            {
                if (i == j) continue;
                var r1 = baseCandidates[i].Box; // left candidate
                var r2 = baseCandidates[j].Box; // right candidate

                if (r1.Left >= r2.Left) continue;

                int hDist = r2.Left - r1.Right;
                int maxH = Math.Max(r1.Height, r2.Height);
                int minH = Math.Min(r1.Height, r2.Height);
                int vOverlap = Math.Min(r1.Bottom, r2.Bottom) - Math.Max(r1.Top, r2.Top);

                if (hDist >= -4 && hDist <= Math.Max(r1.Width, r2.Width) * 0.85 &&
                    vOverlap >= minH * 0.55 &&
                    (double)minH / maxH >= 0.55)
                {
                    var unionBox = new Rect(
                        Math.Min(r1.Left, r2.Left),
                        Math.Min(r1.Top, r2.Top),
                        Math.Max(r1.Right, r2.Right) - Math.Min(r1.Left, r2.Left),
                        Math.Max(r1.Bottom, r2.Bottom) - Math.Min(r1.Top, r2.Top)
                    );

                    double uAspect = (double)unionBox.Width / unionBox.Height;
                    if (uAspect >= 0.65 && uAspect <= 1.45 && unionBox.Width <= zoneW * 0.88)
                    {
                        candidatesToTest.Add((unionBox, true, -1));
                    }
                }
            }
        }

        // 6. Evaluate all candidates via Multi-Layer Hybrid Classifier
        Rank bestRank = Rank.Ace;
        double maxConfidence = 0.0;
        string bestDiag = "";
        Rect? bestBox = null;

        foreach (var cand in candidatesToTest)
        {
            using var normMat = NormalizeCandidate(binaryMat, cand.Box);
            var eval = EvaluateRankHybrid(normMat, cand.Box, cand.IsMerged10, binaryMat);

            double score = eval.Confidence;
            if (cand.Box.Height >= 14 && cand.Box.Height <= zoneH * 0.70) score += 0.03;
            if (cand.IsMerged10 && eval.Rank == Rank.Ten) score += 0.06;

            if (score > maxConfidence)
            {
                maxConfidence = score;
                bestRank = eval.Rank;
                bestDiag = eval.Diagnostic;
                bestBox = cand.Box;
            }
        }

        if (maxConfidence < 0.48 || bestBox == null)
        {
            return (null, $"{tag}: 신뢰도 미달 ({maxConfidence:P0})");
        }

        // 7. Universal Robust Suit Classification
        var (detectedSuit, suitDiag) = ClassifySuitUniversal(mat, binaryMat, bestBox.Value, contours);

        Suit finalSuit = detectedSuit ?? Suit.Spades;
        var card = new Card(bestRank, finalSuit);
        return (card, $"{tag}: {card.DisplayText} ({maxConfidence:P0}, {bestDiag}, {suitDiag})");
    }

    private static Rect FindCardFace(Mat mat)
    {
        if (mat.Width < 25 || mat.Height < 25) return new Rect(0, 0, mat.Width, mat.Height);

        using var gray = new Mat();
        Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);

        // Run Otsu to separate card rectangle from table felt
        using var b1 = new Mat();
        Cv2.Threshold(gray, b1, 0, 255, ThresholdTypes.Otsu);
        using var b2 = new Mat();
        Cv2.BitwiseNot(b1, b2);

        Rect bestRect = new Rect(0, 0, mat.Width, mat.Height);
        int maxArea = 0;

        foreach (var b in new[] { b1, b2 })
        {
            Cv2.FindContours(b, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            foreach (var c in contours)
            {
                var r = Cv2.BoundingRect(c);

                // Reject full-image background contours
                if (r.Width >= mat.Width * 0.92 && r.Height >= mat.Height * 0.92) continue;

                int area = r.Width * r.Height;
                double aspect = (double)r.Width / r.Height;
                double contourArea = Cv2.ContourArea(c);
                double fillRatio = contourArea / Math.Max(1, area);

                if (r.Width >= mat.Width * 0.28 && r.Height >= mat.Height * 0.40 &&
                    aspect >= 0.35 && aspect <= 1.25 &&
                    fillRatio >= 0.70 &&
                    area > maxArea)
                {
                    maxArea = area;
                    bestRect = r;
                }
            }
        }

        return bestRect;
    }

    private static Mat CreateStandardForegroundBinary(Mat grayMat)
    {
        int w = grayMat.Width;
        int h = grayMat.Height;

        long borderSum = 0;
        int borderCount = 0;

        for (int y = 0; y < h; y += Math.Max(1, h / 8))
        {
            borderSum += grayMat.At<byte>(y, 0);
            borderSum += grayMat.At<byte>(y, w - 1);
            borderCount += 2;
        }
        for (int x = 0; x < w; x += Math.Max(1, w / 8))
        {
            borderSum += grayMat.At<byte>(0, x);
            borderSum += grayMat.At<byte>(h - 1, x);
            borderCount += 2;
        }

        double borderAvg = borderCount > 0 ? (double)borderSum / borderCount : 128.0;
        bool isLightBackground = borderAvg >= 135.0;

        var binary = new Mat();
        if (isLightBackground)
        {
            Cv2.Threshold(grayMat, binary, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.BinaryInv);
        }
        else
        {
            Cv2.Threshold(grayMat, binary, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
        }

        // Non-destructive noise removal: remove isolated 1~2px speckles without eroding character strokes or small holes
        Cv2.FindContours(binary, out Point[][] noiseContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        foreach (var nc in noiseContours)
        {
            var nr = Cv2.BoundingRect(nc);
            if (Cv2.ContourArea(nc) < 2.5 && nr.Width <= 2 && nr.Height <= 2)
            {
                Cv2.DrawContours(binary, new[] { nc }, -1, Scalar.All(0), -1);
            }
        }

        return binary;
    }

    private static Mat NormalizeCandidate(Mat binaryMat, Rect box)
    {
        int bx = Math.Clamp(box.X, 0, binaryMat.Width - 1);
        int by = Math.Clamp(box.Y, 0, binaryMat.Height - 1);
        int bw = Math.Clamp(box.Width, 1, binaryMat.Width - bx);
        int bh = Math.Clamp(box.Height, 1, binaryMat.Height - by);

        using var crop = new Mat(binaryMat, new Rect(bx, by, bw, bh));

        var norm = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(0));

        double scale = 26.0 / Math.Max(bw, bh);
        int targetW = Math.Clamp((int)Math.Round(bw * scale), 2, 28);
        int targetH = Math.Clamp((int)Math.Round(bh * scale), 2, 28);

        using var resizedCrop = new Mat();
        Cv2.Resize(crop, resizedCrop, new OpenCvSharp.Size(targetW, targetH), interpolation: InterpolationFlags.Area);
        Cv2.Threshold(resizedCrop, resizedCrop, 100, 255, ThresholdTypes.Binary);

        int ox = (32 - targetW) / 2;
        int oy = (32 - targetH) / 2;

        using var roi = new Mat(norm, new Rect(ox, oy, targetW, targetH));
        resizedCrop.CopyTo(roi);

        return norm;
    }

    private record EvaluationResult(Rank Rank, double Confidence, string Diagnostic);

    private static EvaluationResult EvaluateRankHybrid(Mat norm32, Rect origBox, bool isMerged10, Mat binaryMat)
    {
        // ============================================================
        // 1단계: ONNX 딥러닝 추론 (우선)
        // ============================================================
        if (OnnxService.IsInitialized && OnnxService.HasRankModel)
        {
            var (onnxRank, onnxConf, onnxDiag) = OnnxService.PredictRank(norm32);

            // isMerged10 플래그를 존중: "10"을 명시적으로 병합 감지된 경우 Ten으로 강제
            if (isMerged10 && onnxRank != Rank.Ten)
            {
                onnxRank = Rank.Ten;
                onnxConf = Math.Max(onnxConf, 0.75);
                onnxDiag += "[10-override]";
            }

            // ONNX 신뢰도가 충분히 높으면 즉시 반환
            if (onnxRank.HasValue && onnxConf >= 0.60)
            {
                return new EvaluationResult(onnxRank.Value, onnxConf, $"ONNX:{onnxDiag}");
            }

            // 중간 신뢰도: 위상학적 불변량으로 교차 검증
            if (onnxRank.HasValue && onnxConf >= 0.40)
            {
                var topo2 = ExtractTopology(norm32, origBox, isMerged10);
                bool topologyOk = ValidateRankWithTopology(onnxRank.Value, topo2, isMerged10);
                if (topologyOk)
                    return new EvaluationResult(onnxRank.Value, onnxConf, $"ONNX+위상:{onnxDiag}");
                // 위상 불일치 → 폴백으로 진행
            }
        }

        // ============================================================
        // 2단계: 기존 템플릿 매칭 + 위상학적 불변량 (폴백)
        // ============================================================
        var topo = ExtractTopology(norm32, origBox, isMerged10);

        var tmplScores = new Dictionary<Rank, double>();
        foreach (var (rank, templates) in RankTemplates)
        {
            double bestRankScore = -1.0;
            foreach (var tmpl in templates)
            {
                using var res = new Mat();
                Cv2.MatchTemplate(norm32, tmpl, res, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(res, out _, out double maxVal);
                if (maxVal > bestRankScore) bestRankScore = maxVal;
            }
            tmplScores[rank] = Math.Clamp(bestRankScore, 0.0, 1.0);
        }

        // 위상학적 불변량 패널티/보너스 적용
        if (topo.HoleCount < 2) tmplScores[Rank.Eight] -= 0.30;
        else tmplScores[Rank.Eight] += 0.35;

        if (isMerged10 || topo.HasDualLobes)
        {
            tmplScores[Rank.Ten] += 0.35;
            tmplScores[Rank.Six] -= 0.35;
            tmplScores[Rank.Queen] -= 0.35;
        }
        else if (topo.AspectRatio <= 0.78)
        {
            tmplScores[Rank.Ten] -= 0.30;
        }

        if (topo.HoleCount >= 1)
        {
            if (topo.HoleY >= 0.55) tmplScores[Rank.Nine] -= 0.35;
            else if (topo.HoleY <= 0.44) tmplScores[Rank.Six] -= 0.35;
        }

        bool isAceFeet = topo.BaselineLeft >= 2 && topo.BaselineRight >= 2;
        bool isFourFoot = topo.BaselineLeft <= 1 && topo.BaselineRight >= 2;

        if (isFourFoot) tmplScores[Rank.Ace] -= 0.35;
        else if (isAceFeet && topo.HorizontalSymmetry >= 0.50) tmplScores[Rank.Four] -= 0.35;

        if (topo.HoleCount == 0)
        {
            if (topo.BottomBarFill >= 0.55) { tmplScores[Rank.Two] += 0.15; tmplScores[Rank.Jack] -= 0.20; }
            if (topo.TopBarFill >= 0.55 && topo.BottomLeftFill < 0.05) { tmplScores[Rank.Seven] += 0.15; tmplScores[Rank.Two] -= 0.20; }
            if (topo.LeftBackboneFill >= 0.60) tmplScores[Rank.King] += 0.15;
            if (topo.BottomLeftFill >= 0.08 && topo.TopBarFill >= 0.40 && topo.BottomRightFill < 0.05) { tmplScores[Rank.Jack] += 0.15; tmplScores[Rank.Two] -= 0.20; }
        }

        Rank bestRank = Rank.Ace;
        double maxConf = -1.0;
        foreach (var (r, score) in tmplScores)
        {
            if (score > maxConf) { maxConf = score; bestRank = r; }
        }

        string diag = $"폴백:{bestRank}({maxConf:P0}, H:{topo.HoleCount})";
        return new EvaluationResult(bestRank, Math.Clamp(maxConf, 0.0, 1.0), diag);
    }

    /// <summary>
    /// ONNX 중간 신뢰도 예측을 위상학적 불변량으로 교차 검증합니다.
    /// </summary>
    private static bool ValidateRankWithTopology(Rank rank, TopologyFeatures topo, bool isMerged10)
    {
        return rank switch
        {
            Rank.Eight => topo.HoleCount >= 2,
            Rank.Ten => isMerged10 || topo.HasDualLobes || topo.AspectRatio >= 0.85,
            Rank.Six => topo.HoleCount >= 1 && topo.HoleY >= 0.50,
            Rank.Nine => topo.HoleCount >= 1 && topo.HoleY <= 0.50,
            Rank.Four => topo.BaselineLeft <= 1,
            Rank.Ace => topo.BaselineLeft >= 2 && topo.BaselineRight >= 2,
            _ => true // 나머지는 ONNX 신뢰
        };
    }

    private record TopologyFeatures(
        int HoleCount,
        double HoleY,
        double BottomLeftFill,
        double BottomRightFill,
        double VeryBottomLeftFill,
        double VeryBottomRightFill,
        double BottomCenterFill,
        int BaselineLeft,
        int BaselineRight,
        int BaselineCenter,
        double TopBarFill,
        double BottomBarFill,
        double TopRightFill,
        double LeftBackboneFill,
        double HorizontalSymmetry,
        bool HasDualLobes,
        double AspectRatio
    );

    private static TopologyFeatures ExtractTopology(Mat norm32, Rect origBox, bool isMerged10)
    {
        int totalPx = Cv2.CountNonZero(norm32);
        if (totalPx == 0) totalPx = 1;

        using var padded = new Mat(36, 36, MatType.CV_8UC1, Scalar.All(0));
        using var roi = new Mat(padded, new Rect(2, 2, 32, 32));
        norm32.CopyTo(roi);

        Cv2.FloodFill(padded, new Point(0, 0), Scalar.All(128));

        using var holesMat = new Mat();
        Cv2.InRange(padded, Scalar.All(0), Scalar.All(0), holesMat);

        Cv2.FindContours(holesMat, out Point[][] holeContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        int holeCount = 0;
        double holeYSum = 0;

        foreach (var hc in holeContours)
        {
            var hr = Cv2.BoundingRect(hc);
            double area = Cv2.ContourArea(hc);
            if (area >= 3.0 || (hr.Width >= 2 && hr.Height >= 2 && hr.Width * hr.Height >= 4))
            {
                holeCount++;
                holeYSum += (hr.Y - 2 + hr.Height / 2.0) / 32.0;
            }
        }
        double avgHoleY = holeCount > 0 ? holeYSum / holeCount : 0.50;

        // Bottom Quadrant Fill (Rows 23..31)
        int blPx = 0, brPx = 0;
        for (int y = 23; y < 32; y++)
        {
            for (int x = 0; x < 12; x++)
            {
                if (norm32.At<byte>(y, x) > 100) blPx++;
            }
            for (int x = 20; x < 32; x++)
            {
                if (norm32.At<byte>(y, x) > 100) brPx++;
            }
        }
        double blFill = (double)blPx / totalPx;
        double brFill = (double)brPx / totalPx;

        // Very bottom 4 rows (Rows 27..31) for feet and center detection
        int vblPx = 0, vbrPx = 0, vbcPx = 0;
        for (int y = 27; y < 32; y++)
        {
            for (int x = 0; x < 11; x++)
            {
                if (norm32.At<byte>(y, x) > 100) vblPx++;
            }
            for (int x = 21; x < 32; x++)
            {
                if (norm32.At<byte>(y, x) > 100) vbrPx++;
            }
            for (int x = 12; x < 20; x++)
            {
                if (norm32.At<byte>(y, x) > 100) vbcPx++;
            }
        }
        double vblFill = (double)vblPx / totalPx;
        double vbrFill = (double)vbrPx / totalPx;
        double vbcFill = (double)vbcPx / totalPx;

        // Dynamic baseline feet analysis (A vs 4 vs Q) relative to glyph's horizontal extent
        int glyphMinX = 32, glyphMaxX = 0;
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                if (norm32.At<byte>(y, x) > 100)
                {
                    if (x < glyphMinX) glyphMinX = x;
                    if (x > glyphMaxX) glyphMaxX = x;
                }
            }
        }
        int glyphWidth = Math.Max(1, glyphMaxX - glyphMinX + 1);
        int third = Math.Max(1, glyphWidth / 3);
        int leftThirdEnd = glyphMinX + third;
        int rightThirdStart = glyphMaxX - third;

        int baselineLeft = 0, baselineRight = 0, baselineCenter = 0;
        for (int y = 28; y < 32; y++)
        {
            for (int x = glyphMinX; x < leftThirdEnd; x++) if (norm32.At<byte>(y, x) > 100) baselineLeft++;
            for (int x = rightThirdStart; x <= glyphMaxX; x++) if (norm32.At<byte>(y, x) > 100) baselineRight++;
            for (int x = leftThirdEnd; x < rightThirdStart; x++) if (norm32.At<byte>(y, x) > 100) baselineCenter++;
        }

        // Top-Right Quadrant Fill (Rows 2..14, Cols 18..30)
        int trPx = 0;
        for (int y = 2; y < 14; y++)
        {
            for (int x = 18; x < 30; x++)
            {
                if (norm32.At<byte>(y, x) > 100) trPx++;
            }
        }
        double topRightFill = (double)trPx / (12 * 12);

        // Top Bar Fill (Rows 2..8)
        int topPx = 0;
        for (int y = 2; y < 9; y++)
        {
            for (int x = 4; x < 28; x++)
            {
                if (norm32.At<byte>(y, x) > 100) topPx++;
            }
        }
        double topBarFill = (double)topPx / (7 * 24);

        // Bottom Bar Fill (Rows 24..30)
        int botBarPx = 0;
        for (int y = 24; y < 31; y++)
        {
            for (int x = 4; x < 28; x++)
            {
                if (norm32.At<byte>(y, x) > 100) botBarPx++;
            }
        }
        double botBarFill = (double)botBarPx / (7 * 24);

        // Left Backbone Fill (Cols 2..8)
        int leftPx = 0;
        for (int y = 4; y < 28; y++)
        {
            for (int x = 2; x < 9; x++)
            {
                if (norm32.At<byte>(y, x) > 100) leftPx++;
            }
        }
        double leftBackboneFill = (double)leftPx / (24 * 7);

        using var flipped = new Mat();
        Cv2.Flip(norm32, flipped, FlipMode.Y);
        using var symMat = new Mat();
        Cv2.BitwiseAnd(norm32, flipped, symMat);
        double symmetry = (double)Cv2.CountNonZero(symMat) / totalPx;

        int[] colSums = new int[32];
        int maxCol = 0;
        for (int x = 0; x < 32; x++)
        {
            int sum = 0;
            for (int y = 0; y < 32; y++)
            {
                if (norm32.At<byte>(y, x) > 100) sum++;
            }
            colSums[x] = sum;
            if (sum > maxCol) maxCol = sum;
        }

        bool hasDualLobes = isMerged10;
        if (!hasDualLobes && maxCol >= 6)
        {
            int leftMax = 0, rightMax = 0, midMin = maxCol;
            for (int x = 4; x < 14; x++) if (colSums[x] > leftMax) leftMax = colSums[x];
            for (int x = 18; x < 28; x++) if (colSums[x] > rightMax) rightMax = colSums[x];
            for (int x = 12; x < 20; x++) if (colSums[x] < midMin) midMin = colSums[x];

            if (leftMax >= 5 && rightMax >= 5 && midMin <= Math.Min(leftMax, rightMax) * 0.35)
            {
                hasDualLobes = true;
            }
        }

        double aspect = (double)origBox.Width / Math.Max(1, origBox.Height);

        return new TopologyFeatures(
            holeCount,
            avgHoleY,
            blFill,
            brFill,
            vblFill,
            vbrFill,
            vbcFill,
            baselineLeft,
            baselineRight,
            baselineCenter,
            topBarFill,
            botBarFill,
            topRightFill,
            leftBackboneFill,
            symmetry,
            hasDualLobes,
            aspect
        );
    }

    private static (Suit? Suit, string Diag) ClassifySuitUniversal(Mat cardMat, Mat binaryMat, Rect rankBox, Point[][] contours)
    {
        using var hsvMat = new Mat();
        Cv2.CvtColor(cardMat, hsvMat, ColorConversionCodes.BGR2HSV);

        // A. Check if card background itself is colored (Winjoy / WPL 4-Color style)
        int insetX = Math.Max(2, cardMat.Width / 6);
        int insetY = Math.Max(2, cardMat.Height / 6);
        using var centerHsv = new Mat(hsvMat, new Rect(insetX, insetY, Math.Max(2, cardMat.Width - 2 * insetX), Math.Max(2, cardMat.Height - 2 * insetY)));

        var (bgSuit, bgDiag) = DetectSuitFromHSVRegion(centerHsv, null, minThreshold: 18, minVal: 60, minSat: 40);
        if (bgSuit.HasValue && bgSuit != Suit.Spades)
        {
            return (bgSuit.Value, $"배경색({bgSuit.Value}): {bgDiag}");
        }

        // B. Search for the Suit Symbol below the Rank box
        Rect? suitSymbolBox = null;
        int minSuitY = rankBox.Bottom;

        var validContours = new List<Rect>();
        for (int i = 0; i < contours.Length; i++)
        {
            var r = Cv2.BoundingRect(contours[i]);
            if (rankBox.Contains(new Point(r.X + r.Width / 2, r.Y + r.Height / 2))) continue;
            if (r.Width < 4 || r.Height < 4) continue;
            if (r.Width >= cardMat.Width * 0.85 && r.Height >= cardMat.Height * 0.85) continue;
            validContours.Add(r);
        }

        foreach (var r in validContours)
        {
            if (r.Top >= minSuitY - 2 && r.Top <= minSuitY + rankBox.Height * 2.5 &&
                r.Width <= cardMat.Width * 0.70 && r.Height <= cardMat.Height * 0.70)
            {
                suitSymbolBox = r;
                break;
            }
        }

        if (!suitSymbolBox.HasValue && validContours.Count > 0)
        {
            var candidate = validContours
                .Where(r => r.Top >= rankBox.Top)
                .OrderByDescending(r => r.Width * r.Height)
                .FirstOrDefault();
            if (candidate.Width * candidate.Height >= 16)
            {
                suitSymbolBox = candidate;
            }
        }

        if (suitSymbolBox.HasValue)
        {
            var sBox = suitSymbolBox.Value;
            int sx = Math.Clamp(sBox.X, 0, cardMat.Width - 1);
            int sy = Math.Clamp(sBox.Y, 0, cardMat.Height - 1);
            int sw = Math.Clamp(sBox.Width, 1, cardMat.Width - sx);
            int sh = Math.Clamp(sBox.Height, 1, cardMat.Height - sy);

            using var symCropHsv = new Mat(hsvMat, new Rect(sx, sy, sw, sh));
            using var symCropBin = new Mat(binaryMat, new Rect(sx, sy, sw, sh));

            // Shape classification (Clubs, Diamonds, Hearts, Spades)
            var shapeRes = ClassifySuitSymbolShape(symCropBin);

            // Test color of the suit symbol - ONLY on foreground pixels!
            var (symColorSuit, symDiag) = DetectSuitFromHSVRegion(symCropHsv, symCropBin, minThreshold: 8, minVal: 40, minSat: 35);

            // Custom deck (e.g. Gold 10♣, Cyan A♦): shape has high confidence
            if (shapeRes.BestSuit.HasValue && shapeRes.Confidence >= 0.70)
            {
                if (shapeRes.BestSuit == Suit.Clubs)
                {
                    return (Suit.Clubs, $"심볼형상: {shapeRes.Details}");
                }
                if (shapeRes.BestSuit == Suit.Diamonds)
                {
                    return (Suit.Diamonds, $"심볼형상: {shapeRes.Details}");
                }
            }

            // 4-color deck (Blue Diamonds, Red Hearts, Green Clubs, Slate/Black Spades)
            if (symColorSuit == Suit.Diamonds) return (Suit.Diamonds, $"파란색 다이아(♦): {symDiag}");
            if (symColorSuit == Suit.Clubs) return (Suit.Clubs, $"초록색 클로버(♣): {symDiag}");
            if (symColorSuit == Suit.Hearts)
            {
                // Distinguish Red Heart vs Red Diamond in 2-color deck
                if (shapeRes.BestSuit == Suit.Diamonds && shapeRes.Confidence > 0.60)
                {
                    return (Suit.Diamonds, $"빨간색 다이아(♦): {shapeRes.Details}");
                }
                return (Suit.Hearts, $"빨간색 하트(♥): {symDiag}");
            }
            if (symColorSuit == Suit.Spades)
            {
                if (shapeRes.BestSuit == Suit.Clubs && shapeRes.Confidence > 0.60)
                {
                    return (Suit.Clubs, $"검은색 클로버(♣): {shapeRes.Details}");
                }
                return (Suit.Spades, $"검은색 스페이드(♠): {symDiag}");
            }

            if (shapeRes.BestSuit.HasValue)
            {
                return (shapeRes.BestSuit.Value, $"심볼형상: {shapeRes.Details}");
            }
        }

        // C. Check overall foreground pixels across the card
        var (fgSuit, fgDiag) = DetectSuitFromHSVRegion(hsvMat, binaryMat, minThreshold: 12, minVal: 40, minSat: 35);
        if (fgSuit.HasValue)
        {
            return (fgSuit.Value, $"전경색({fgSuit.Value}): {fgDiag}");
        }

        return (Suit.Spades, "기본값(♠)");
    }

    private record SuitShapeResult(Suit? BestSuit, double Confidence, string Details);

    private static SuitShapeResult ClassifySuitSymbolShape(Mat symBin)
    {
        if (symBin.Width < 4 || symBin.Height < 4) 
            return new SuitShapeResult(null, 0.0, "심볼 크기 부족");

        int bw = symBin.Width;
        int bh = symBin.Height;

        // 32x32 정규화
        using var norm32 = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(0));
        double scale = 24.0 / Math.Max(bw, bh);
        int targetW = Math.Clamp((int)Math.Round(bw * scale), 2, 28);
        int targetH = Math.Clamp((int)Math.Round(bh * scale), 2, 28);

        using var resized = new Mat();
        Cv2.Resize(symBin, resized, new OpenCvSharp.Size(targetW, targetH), interpolation: InterpolationFlags.Area);

        int ox = (32 - targetW) / 2;
        int oy = (32 - targetH) / 2;
        using (var roi = new Mat(norm32, new Rect(ox, oy, targetW, targetH)))
        {
            resized.CopyTo(roi);
        }

        // ONNX 슈트 추론 우선
        if (OnnxService.IsInitialized && OnnxService.HasSuitModel)
        {
            var (onnxSuit, onnxConf, onnxDiag) = OnnxService.PredictSuit(norm32);
            if (onnxSuit.HasValue && onnxConf >= 0.60)
            {
                return new SuitShapeResult(onnxSuit.Value, onnxConf, $"ONNX:{onnxDiag}");
            }
        }

        var tmplScores = new Dictionary<Suit, double>();
        foreach (var (suit, tmpls) in SuitTemplates)
        {
            double maxVal = -1.0;
            foreach (var tmpl in tmpls)
            {
                using var res = new Mat();
                Cv2.MatchTemplate(norm32, tmpl, res, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(res, out _, out double v);
                if (v > maxVal) maxVal = v;
            }
            tmplScores[suit] = Math.Clamp(maxVal, 0.0, 1.0);
        }

        // 2. Invariant Geometric Shape Analysis
        int totalPx = Cv2.CountNonZero(norm32);
        if (totalPx == 0) totalPx = 1;

        // Top 25% (Y: 4..11)
        int topCenterPx = 0, topSidesPx = 0;
        for (int y = 4; y < 12; y++)
        {
            for (int x = 12; x < 20; x++)
            {
                if (norm32.At<byte>(y, x) > 100) topCenterPx++;
            }
            for (int x = 4; x < 12; x++)
            {
                if (norm32.At<byte>(y, x) > 100) topSidesPx++;
            }
            for (int x = 20; x < 28; x++)
            {
                if (norm32.At<byte>(y, x) > 100) topSidesPx++;
            }
        }

        // Bottom 25% (Y: 20..28) - check for stem
        int botCenterPx = 0, botSidesPx = 0;
        for (int y = 20; y < 28; y++)
        {
            for (int x = 13; x < 19; x++)
            {
                if (norm32.At<byte>(y, x) > 100) botCenterPx++;
            }
            for (int x = 4; x < 13; x++)
            {
                if (norm32.At<byte>(y, x) > 100) botSidesPx++;
            }
            for (int x = 19; x < 28; x++)
            {
                if (norm32.At<byte>(y, x) > 100) botSidesPx++;
            }
        }

        // Mid height width (Y: 12..20)
        int midSidesPx = 0;
        for (int y = 12; y < 20; y++)
        {
            for (int x = 4; x < 10; x++) if (norm32.At<byte>(y, x) > 100) midSidesPx++;
            for (int x = 22; x < 28; x++) if (norm32.At<byte>(y, x) > 100) midSidesPx++;
        }

        // Convex hull solidity
        Cv2.FindContours(norm32, out Point[][] cnts, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        double maxSolidity = 0.0;
        if (cnts.Length > 0)
        {
            var mainCnt = cnts.OrderByDescending(c => Cv2.ContourArea(c)).First();
            var hull = Cv2.ConvexHull(mainCnt);
            double cArea = Cv2.ContourArea(mainCnt);
            double hArea = Cv2.ContourArea(hull);
            if (hArea > 0) maxSolidity = cArea / hArea;
        }

        // Distinctive geometric features:
        // A. Heart: Top cleft! Top sides have mass, top center is empty or lower than sides.
        bool hasTopCleft = topSidesPx >= 4 && topSidesPx > topCenterPx * 1.15;
        if (hasTopCleft && botCenterPx > 0 && botSidesPx < botCenterPx * 0.6)
        {
            tmplScores[Suit.Hearts] += 0.35;
            tmplScores[Suit.Diamonds] -= 0.30;
            tmplScores[Suit.Spades] -= 0.30;
            tmplScores[Suit.Clubs] -= 0.30;
        }

        // B. Diamond: Pointed apex at top, pointed apex at bottom, NO stem, high solidity (straight rhombus).
        bool hasRhombusApexes = topCenterPx >= 2 && topSidesPx <= topCenterPx * 0.65 &&
                               botCenterPx >= 2 && botSidesPx <= botCenterPx * 0.65 &&
                               midSidesPx >= 4;
        if (hasRhombusApexes && maxSolidity >= 0.78)
        {
            tmplScores[Suit.Diamonds] += 0.35;
            tmplScores[Suit.Hearts] -= 0.30;
            tmplScores[Suit.Clubs] -= 0.35;
            tmplScores[Suit.Spades] -= 0.30;
        }

        // C. Club: Has stem at bottom, side lobes at mid height, and top lobe. Deep indentations (solidity <= 0.78).
        bool hasStem = botCenterPx >= 4 && botSidesPx < botCenterPx * 0.8;
        bool hasSideLobes = midSidesPx >= 6;
        if (hasStem && hasSideLobes && maxSolidity < 0.80)
        {
            tmplScores[Suit.Clubs] += 0.30;
            tmplScores[Suit.Diamonds] -= 0.35;
        }

        // D. Spade: Has pointed top apex AND bottom stem.
        bool hasTopApex = topCenterPx >= 3 && topSidesPx <= topCenterPx * 0.70;
        if (hasTopApex && hasStem && maxSolidity < 0.85)
        {
            tmplScores[Suit.Spades] += 0.25;
            tmplScores[Suit.Diamonds] -= 0.30;
            tmplScores[Suit.Hearts] -= 0.30;
        }

        Suit bestSuit = Suit.Spades;
        double maxConf = -1.0;
        foreach (var (s, sc) in tmplScores)
        {
            if (sc > maxConf)
            {
                maxConf = sc;
                bestSuit = s;
            }
        }

        string diag = $"{bestSuit}({maxConf:P0}, Cleft:{hasTopCleft}, Rhombus:{hasRhombusApexes}, Stem:{hasStem}, Sol:{maxSolidity:F2})";
        return new SuitShapeResult(bestSuit, Math.Clamp(maxConf, 0.0, 1.0), diag);
    }

    private static (Suit? Suit, string Diag) DetectSuitFromHSVRegion(Mat hsvRegion, Mat? mask, int minThreshold, int minVal, int minSat)
    {
        // Real chromatic poker suits (Blue, Red, Green) have high saturation (S >= 65)
        int chromSat = Math.Max(minSat, 65);

        // Blue (♦ Diamonds): H in [90..135]
        using var blueMask = new Mat();
        Cv2.InRange(hsvRegion, new Scalar(90, chromSat, minVal), new Scalar(135, 255, 255), blueMask);
        if (mask != null) Cv2.BitwiseAnd(blueMask, mask, blueMask);
        int bluePx = Cv2.CountNonZero(blueMask);

        // Red (♥ Hearts): H in [0..10] or [168..180] (strict red range, excluding gold/yellow H=25)
        using var red1 = new Mat();
        using var red2 = new Mat();
        using var redMask = new Mat();
        Cv2.InRange(hsvRegion, new Scalar(0, chromSat, minVal), new Scalar(10, 255, 255), red1);
        Cv2.InRange(hsvRegion, new Scalar(168, chromSat, minVal), new Scalar(180, 255, 255), red2);
        Cv2.BitwiseOr(red1, red2, redMask);
        if (mask != null) Cv2.BitwiseAnd(redMask, mask, redMask);
        int redPx = Cv2.CountNonZero(redMask);

        // Green (♣ Clubs): H in [35..88]
        using var greenMask = new Mat();
        Cv2.InRange(hsvRegion, new Scalar(35, chromSat, minVal), new Scalar(88, 255, 255), greenMask);
        if (mask != null) Cv2.BitwiseAnd(greenMask, mask, greenMask);
        int greenPx = Cv2.CountNonZero(greenMask);

        // Dark Gray/Slate/Black (♠ Spades): low saturation S <= 60, V in [0..160] (include pure black V=0 and slate gray V=90)
        using var grayMask = new Mat();
        Cv2.InRange(hsvRegion, new Scalar(0, 0, 0), new Scalar(180, 60, 160), grayMask);
        if (mask != null) Cv2.BitwiseAnd(grayMask, mask, grayMask);
        int grayPx = Cv2.CountNonZero(grayMask);

        string diag = $"B:{bluePx}, R:{redPx}, G:{greenPx}, Gr:{grayPx}";

        int chromaticMax = Math.Max(bluePx, Math.Max(redPx, greenPx));

        // If gray/black pixels strongly dominate over chromatic fringes, it is Spades
        if (grayPx >= minThreshold && grayPx > chromaticMax * 1.4)
        {
            return (Suit.Spades, diag);
        }

        if (chromaticMax >= minThreshold)
        {
            if (bluePx > redPx && bluePx > greenPx) return (Suit.Diamonds, diag);
            if (redPx > bluePx && redPx > greenPx) return (Suit.Hearts, diag);
            if (greenPx > bluePx && greenPx > redPx) return (Suit.Clubs, diag);
        }

        if (grayPx >= minThreshold)
        {
            return (Suit.Spades, diag);
        }

        return (null, diag);
    }

    private static Dictionary<Rank, List<Mat>> GenerateStandardRankTemplates()
    {
        var dict = new Dictionary<Rank, List<Mat>>();
        string[] fontNames = { "Arial", "Segoe UI", "Tahoma", "Trebuchet MS", "Impact", "Arial Black", "Consolas", "Verdana", "Georgia" };
        int[] fontSizes = { 18, 22 };
        var ranks = Enum.GetValues<Rank>();

        foreach (var r in ranks)
        {
            dict[r] = new List<Mat>();
            string str = r switch
            {
                Rank.Ace => "A", Rank.King => "K", Rank.Queen => "Q", Rank.Jack => "J",
                Rank.Ten => "10", Rank.Nine => "9", Rank.Eight => "8", Rank.Seven => "7",
                Rank.Six => "6", Rank.Five => "5", Rank.Four => "4", Rank.Three => "3",
                Rank.Two => "2", _ => "A"
            };

            string[] strs = r == Rank.Ten ? new[] { "10", "T" } : new[] { str };

            foreach (var sToRender in strs)
            {
                foreach (var fontName in fontNames)
                {
                    foreach (var fSize in fontSizes)
                    {
                        foreach (var style in new[] { System.Drawing.FontStyle.Bold, System.Drawing.FontStyle.Regular })
                        {
                            try
                            {
                                using var font = new System.Drawing.Font(fontName, fSize, style);
                                using var bmp = new System.Drawing.Bitmap(64, 64, PixelFormat.Format32bppArgb);
                                using (var g = System.Drawing.Graphics.FromImage(bmp))
                                {
                                    g.Clear(System.Drawing.Color.Black);
                                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                                    g.DrawString(sToRender, font, System.Drawing.Brushes.White, 4, 4);
                                }

                                int minX = 64, maxX = 0, minY = 64, maxY = 0;
                                for (int y = 0; y < 64; y++)
                                {
                                    for (int x = 0; x < 64; x++)
                                    {
                                        if (bmp.GetPixel(x, y).R > 100)
                                        {
                                            if (x < minX) minX = x;
                                            if (x > maxX) maxX = x;
                                            if (y < minY) minY = y;
                                            if (y > maxY) maxY = y;
                                        }
                                    }
                                }

                                if (maxX >= minX && maxY >= minY)
                                {
                                    int tw = maxX - minX + 1;
                                    int th = maxY - minY + 1;
                                    using var cropBmp = new System.Drawing.Bitmap(tw, th, PixelFormat.Format32bppArgb);
                                    using (var g2 = System.Drawing.Graphics.FromImage(cropBmp))
                                    {
                                        g2.DrawImage(bmp, new System.Drawing.Rectangle(0, 0, tw, th), new System.Drawing.Rectangle(minX, minY, tw, th), System.Drawing.GraphicsUnit.Pixel);
                                    }

                                    using var tmplMat = BitmapConverter.ToMat(cropBmp);
                                    using var grayMat = new Mat();
                                    Cv2.CvtColor(tmplMat, grayMat, ColorConversionCodes.BGR2GRAY);

                                    var normMat = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(0));
                                    double scale = 26.0 / Math.Max(tw, th);
                                    int targetW = Math.Clamp((int)Math.Round(tw * scale), 2, 28);
                                    int targetH = Math.Clamp((int)Math.Round(th * scale), 2, 28);

                                    using var resized = new Mat();
                                    Cv2.Resize(grayMat, resized, new OpenCvSharp.Size(targetW, targetH), interpolation: InterpolationFlags.Area);

                                    int ox = (32 - targetW) / 2;
                                    int oy = (32 - targetH) / 2;
                                    using var roi = new Mat(normMat, new Rect(ox, oy, targetW, targetH));
                                    resized.CopyTo(roi);

                                    dict[r].Add(normMat);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        return dict;
    }

    private static Dictionary<Suit, List<Mat>> GenerateStandardSuitTemplates()
    {
        var dict = new Dictionary<Suit, List<Mat>>();
        string[] fontNames = { "Segoe UI Symbol", "Segoe UI", "Arial", "Lucida Sans Unicode" };
        var suits = Enum.GetValues<Suit>();

        foreach (var s in suits)
        {
            dict[s] = new List<Mat>();
            string symbolStr = s switch
            {
                Suit.Spades => "♠",
                Suit.Hearts => "♥",
                Suit.Diamonds => "♦",
                Suit.Clubs => "♣",
                _ => "♠"
            };

            foreach (var fontName in fontNames)
            {
                foreach (var style in new[] { System.Drawing.FontStyle.Bold, System.Drawing.FontStyle.Regular })
                {
                    try
                    {
                        using var font = new System.Drawing.Font(fontName, 22, style);
                        using var bmp = new System.Drawing.Bitmap(64, 64, PixelFormat.Format32bppArgb);
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.Clear(System.Drawing.Color.Black);
                            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                            g.DrawString(symbolStr, font, System.Drawing.Brushes.White, 4, 4);
                        }

                        int minX = 64, maxX = 0, minY = 64, maxY = 0;
                        for (int y = 0; y < 64; y++)
                        {
                            for (int x = 0; x < 64; x++)
                            {
                                if (bmp.GetPixel(x, y).R > 100)
                                {
                                    if (x < minX) minX = x;
                                    if (x > maxX) maxX = x;
                                    if (y < minY) minY = y;
                                    if (y > maxY) maxY = y;
                                }
                            }
                        }

                        if (maxX >= minX && maxY >= minY)
                        {
                            int tw = maxX - minX + 1;
                            int th = maxY - minY + 1;
                            using var cropBmp = new System.Drawing.Bitmap(tw, th, PixelFormat.Format32bppArgb);
                            using (var g2 = System.Drawing.Graphics.FromImage(cropBmp))
                            {
                                g2.DrawImage(bmp, new System.Drawing.Rectangle(0, 0, tw, th), new System.Drawing.Rectangle(minX, minY, tw, th), System.Drawing.GraphicsUnit.Pixel);
                            }

                            using var tmplMat = BitmapConverter.ToMat(cropBmp);
                            using var grayMat = new Mat();
                            Cv2.CvtColor(tmplMat, grayMat, ColorConversionCodes.BGR2GRAY);

                            var normMat = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(0));
                            double scale = 24.0 / Math.Max(tw, th);
                            int targetW = Math.Clamp((int)Math.Round(tw * scale), 2, 28);
                            int targetH = Math.Clamp((int)Math.Round(th * scale), 2, 28);

                            using var resized = new Mat();
                            Cv2.Resize(grayMat, resized, new OpenCvSharp.Size(targetW, targetH), interpolation: InterpolationFlags.Area);

                            int ox = (32 - targetW) / 2;
                            int oy = (32 - targetH) / 2;
                            using var roi = new Mat(normMat, new Rect(ox, oy, targetW, targetH));
                            resized.CopyTo(roi);

                            dict[s].Add(normMat);
                        }
                    }
                    catch { }
                }
            }
        }

        return dict;
    }

    private static double EstimatePotAmount(System.Drawing.Bitmap potBmp)
    {
        if (potBmp == null || potBmp.Width < 10 || potBmp.Height < 10) return 0.0;
        var res = PotRecognitionEngine.RecognizePot(potBmp);
        return res.Amount;
    }
}
