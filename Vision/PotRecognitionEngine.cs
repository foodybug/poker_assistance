using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text.RegularExpressions;

namespace PokerAssistant.Vision;

public class PotRecognitionEngine
{
    public record PotResult(double Amount, string RawText, double Confidence);

    private record CharCluster(
        int MinX, int MinY, int MaxX, int MaxY,
        int PixelCount
    )
    {
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
    }

    private static readonly Dictionary<char, List<bool[,]>> CharTemplates = GenerateCharTemplates();

    public static unsafe PotResult RecognizePot(Bitmap? potBmp)
    {
        if (potBmp == null || potBmp.Width < 12 || potBmp.Height < 8)
        {
            return new PotResult(0, "", 0.0);
        }

        int w = potBmp.Width;
        int h = potBmp.Height;

        BitmapData data = potBmp.LockBits(
            new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb
        );

        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;

            // 1. Detect background color via color quantization histogram
            var (bgR, bgG, bgB) = EstimateBackground(scan0, stride, w, h);

            // 2. Find foreground character ink clusters via 4-way BFS
            var clusters = FindTextClusters(scan0, stride, w, h, bgR, bgG, bgB);
            if (clusters.Count == 0)
            {
                return new PotResult(0, "", 0.0);
            }

            // 3. Filter clusters by size (preserve punctuation dots like commas, periods, colon dots)
            int maxCharH = (int)(h * 0.95);
            var majorClusters = clusters.Where(c => c.Height >= Math.Max(5, (int)(h * 0.15)) && c.Height <= maxCharH).ToList();
            if (majorClusters.Count == 0) majorClusters = clusters;
            int maxDigitH = majorClusters.Count > 0 ? majorClusters.Max(c => c.Height) : h;
            int baselineY = majorClusters.Count > 0 ? majorClusters.Max(c => c.MaxY) : h;

            // Keep all valid glyphs and punctuation
            var charClusters = clusters.Where(c =>
                c.Height >= 2 && c.Height <= maxCharH &&
                c.PixelCount >= 2 &&
                c.MinY >= baselineY - (int)(maxDigitH * 1.35)
            ).OrderBy(c => c.MinX).ThenBy(c => c.MinY).ToList();

            if (charClusters.Count == 0)
            {
                return new PotResult(0, "", 0.0);
            }

            // 4. Identify each glyph
            var recognizedChars = new List<(char Ch, double Conf, int X, int W)>();

            for (int i = 0; i < charClusters.Count; i++)
            {
                var cluster = charClusters[i];
                Console.WriteLine($"[DEBUG Pot Cluster {i}] X=[{cluster.MinX}..{cluster.MaxX}] W={cluster.Width} H={cluster.Height} Px={cluster.PixelCount} MaxY={cluster.MaxY} (baselineY={baselineY}, maxH={maxDigitH})");

                // Check for colon ':' formed by two vertically stacked dots
                if (cluster.Height <= (int)(maxDigitH * 0.55) && i + 1 < charClusters.Count)
                {
                    var nextC = charClusters[i + 1];
                    if (nextC.Height <= (int)(maxDigitH * 0.55) &&
                        Math.Abs(cluster.MinX - nextC.MinX) <= Math.Max(6, (int)(maxDigitH * 0.35)) &&
                        nextC.MinY > cluster.MaxY &&
                        nextC.MinY <= cluster.MaxY + (int)(maxDigitH * 0.70))
                    {
                        recognizedChars.Add((':', 0.98, cluster.MinX, Math.Max(cluster.Width, nextC.Width)));
                        i++; // Skip the second dot
                        continue;
                    }
                }

                // Baseline punctuation: Comma ',' or Period '.'
                // Height <= 55% of max digit height, bottom located near baseline
                if (cluster.Height <= (int)(maxDigitH * 0.55) && cluster.MaxY >= baselineY - (int)(maxDigitH * 0.40))
                {
                    if (cluster.PixelCount >= 1 && cluster.Width >= 1)
                    {
                        char punct = (cluster.Height > (int)(cluster.Width * 1.35) && cluster.Height >= 3) ? ',' : '.';
                        recognizedChars.Add((punct, 0.98, cluster.MinX, cluster.Width));
                    }
                    continue;
                }

                // Normal glyph template matching (20x28 grid)
                bool[,] grid = ExtractCharGrid(scan0, stride, cluster.MinX, cluster.MinY, cluster.Width, cluster.Height, bgR, bgG, bgB);
                var (bestCh, conf) = MatchCharacter(grid, (double)cluster.Width / cluster.Height);

                if (conf >= 0.45)
                {
                    recognizedChars.Add((bestCh, conf, cluster.MinX, cluster.Width));
                }
            }

            if (recognizedChars.Count == 0)
            {
                return new PotResult(0, "", 0.0);
            }

            // Build rawText with space insertion where gaps between glyphs are significant
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < recognizedChars.Count; i++)
            {
                if (i > 0)
                {
                    int prevEnd = recognizedChars[i - 1].X + recognizedChars[i - 1].W;
                    int gap = recognizedChars[i].X - prevEnd;
                    char currCh = recognizedChars[i].Ch;
                    char prevCh = recognizedChars[i - 1].Ch;

                    // Do not insert spaces around decimal points or commas (e.g. "1.5" instead of "1 . 5")
                    if (currCh != '.' && currCh != ',' && prevCh != '.' && prevCh != ',' && gap >= (int)(maxDigitH * 0.32))
                    {
                        sb.Append(' ');
                    }
                }
                sb.Append(recognizedChars[i].Ch);
            }

            string rawText = sb.ToString();
            double avgConf = recognizedChars.Average(rc => rc.Conf);
            double amount = ParsePotAmount(rawText);

            return new PotResult(amount, rawText, avgConf);
        }
        catch
        {
            return new PotResult(0, "", 0.0);
        }
        finally
        {
            potBmp.UnlockBits(data);
        }
    }

    public static double ParsePotAmount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;

        // Clean spaces around decimal separators/commas (e.g. "1 . 5" -> "1.5", "50 , 000" -> "50,000")
        raw = Regex.Replace(raw, @"\s*([\.,])\s*", "$1");

        // 1. Specifically search for 'pot : 숫자' pattern (e.g. 'POT : 50,000', 'pot: 150만', 'POT 2.5M', 'POT: 1.5BB')
        var potPattern = Regex.Match(
            raw,
            @"(?:P[0O][T7t]|pot|Pot|POT|팟|총\s*팟|Total\s*Pot)\s*[:\-\.]?\s*([0-9,\.\s만억천조kmbB]+)",
            RegexOptions.IgnoreCase
        );

        if (potPattern.Success && !string.IsNullOrWhiteSpace(potPattern.Groups[1].Value))
        {
            double val = ParseNumericValue(potPattern.Groups[1].Value);
            if (val > 0) return val;
        }

        // 2. Fallback: clean up known labels and parse whatever number remains
        string clean = raw.Replace("POT", "", StringComparison.OrdinalIgnoreCase)
                          .Replace("팟", "")
                          .Replace("총", "")
                          .Replace("Total", "", StringComparison.OrdinalIgnoreCase)
                          .Replace(":", "")
                          .Trim();

        return ParseNumericValue(clean);
    }

    public static double ParseNormalizedNumber(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return 0;
        str = str.Trim();

        // Remove spaces around dots/commas
        str = Regex.Replace(str, @"\s*([\.,])\s*", "$1");

        // 1. If both '.' and ',' are present
        int lastDot = str.LastIndexOf('.');
        int lastComma = str.LastIndexOf(',');

        if (lastDot >= 0 && lastComma >= 0)
        {
            if (lastDot > lastComma)
            {
                // Format: 1,234.56 or 1,500.5 -> comma is thousands separator
                str = str.Replace(",", "");
            }
            else
            {
                // Format: 1.234,56 -> dot is thousands separator
                str = str.Replace(".", "").Replace(",", ".");
            }
        }
        else if (lastComma >= 0)
        {
            // Only comma is present.
            int digitsAfter = str.Length - 1 - lastComma;
            int commaCount = str.Count(c => c == ',');
            if (commaCount > 1 || digitsAfter == 3)
            {
                // Thousands separator (e.g. 50,000 or 1,500,000)
                str = str.Replace(",", "");
            }
            else
            {
                // Decimal separator (e.g. 32,5 or 2,75 or 0,5)
                str = str.Replace(",", ".");
            }
        }
        else if (lastDot >= 0)
        {
            // Only dot is present.
            int digitsAfter = str.Length - 1 - lastDot;
            int dotCount = str.Count(c => c == '.');
            if (dotCount > 1)
            {
                str = str.Replace(".", "");
            }
            else if (dotCount == 1 && digitsAfter == 3 && !str.StartsWith("0.") && !str.StartsWith("0,"))
            {
                // Thousands separator for European/OCR numbers (e.g. 50.000 or 1.000)
                str = str.Replace(".", "");
            }
            // Otherwise, keep the dot as decimal separator (e.g. 1.5, 2.75, 0.5, 12.5, 0.005)
        }

        if (double.TryParse(str, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
        {
            return val;
        }

        return 0;
    }

    public static double ParseNumericValue(string clean)
    {
        if (string.IsNullOrWhiteSpace(clean)) return 0;
        clean = clean.Replace(" ", "").Trim();

        try
        {
            // 1. Big Blind (BB) unit check:
            // In poker, '32bb', '32.5bb', '32BB', '0.5bb' represents Big Blinds, NOT a numeric multiplier like Billion.
            var bbMatch = Regex.Match(clean, @"([\d\.,]+)\s*(?:bb|BB|Bb|bB)", RegexOptions.IgnoreCase);
            if (bbMatch.Success)
            {
                double bbVal = ParseNormalizedNumber(bbMatch.Groups[1].Value);
                if (bbVal > 0)
                {
                    return bbVal;
                }
            }

            // Korean units: 조 (10^12), 억 (10^8), 만 (10^4), 천 (10^3)
            double total = 0;
            bool hasUnit = false;

            var eokMatch = Regex.Match(clean, @"([\d\.,]+)억");
            if (eokMatch.Success)
            {
                double eokVal = ParseNormalizedNumber(eokMatch.Groups[1].Value);
                if (eokVal > 0)
                {
                    total += eokVal * 100_000_000;
                    hasUnit = true;
                    clean = clean.Substring(eokMatch.Index + eokMatch.Length);
                }
            }

            var manMatch = Regex.Match(clean, @"([\d\.,]+)만");
            if (manMatch.Success)
            {
                double manVal = ParseNormalizedNumber(manMatch.Groups[1].Value);
                if (manVal > 0)
                {
                    total += manVal * 10_000;
                    hasUnit = true;
                    clean = clean.Substring(manMatch.Index + manMatch.Length);
                }
            }

            var cheonMatch = Regex.Match(clean, @"([\d\.,]+)천");
            if (cheonMatch.Success)
            {
                double cheonVal = ParseNormalizedNumber(cheonMatch.Groups[1].Value);
                if (cheonVal > 0)
                {
                    total += cheonVal * 1_000;
                    hasUnit = true;
                    clean = clean.Substring(cheonMatch.Index + cheonMatch.Length);
                }
            }

            // English units: B (10^9), M (10^6), K (10^3)
            // Ensure single B is NOT part of 'bb' or 'BB'
            var bMatch = Regex.Match(clean, @"([\d\.,]+)(?<![bB])[Bb](?![bB])");
            if (bMatch.Success)
            {
                double bVal = ParseNormalizedNumber(bMatch.Groups[1].Value);
                if (bVal > 0) return bVal * 1_000_000_000;
            }

            var mMatch = Regex.Match(clean, @"([\d\.,]+)[Mm]");
            if (mMatch.Success)
            {
                double mVal = ParseNormalizedNumber(mMatch.Groups[1].Value);
                if (mVal > 0) return mVal * 1_000_000;
            }

            var kMatch = Regex.Match(clean, @"([\d\.,]+)[Kk]");
            if (kMatch.Success)
            {
                double kVal = ParseNormalizedNumber(kMatch.Groups[1].Value);
                if (kVal > 0) return kVal * 1_000;
            }

            if (hasUnit)
            {
                // Any remaining digits
                var remMatch = Regex.Match(clean, @"^([\d\.,]+)");
                if (remMatch.Success)
                {
                    double remVal = ParseNormalizedNumber(remMatch.Groups[1].Value);
                    total += remVal;
                }
                return total;
            }

            // Pure numeric string with commas/periods (e.g. "32.5", "32,5", "50,000", "1,500.5")
            double normNum = ParseNormalizedNumber(clean);
            if (normNum > 0)
            {
                return normNum;
            }

            // Extract any sequence of digits as fallback
            var fallbackDigits = Regex.Match(clean, @"\d+");
            if (fallbackDigits.Success && double.TryParse(fallbackDigits.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fb))
            {
                return fb;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static unsafe (byte R, byte G, byte B) EstimateBackground(byte* scan0, int stride, int w, int h)
    {
        int[] hist = new int[4096];
        int stepX = Math.Max(1, w / 40);
        int stepY = Math.Max(1, h / 40);

        for (int y = 0; y < h; y += stepY)
        {
            byte* row = scan0 + (y * stride);
            for (int x = 0; x < w; x += stepX)
            {
                byte b = row[x * 4];
                byte g = row[x * 4 + 1];
                byte r = row[x * 4 + 2];
                int bin = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                hist[bin]++;
            }
        }

        int bestBin = 0;
        int maxCount = -1;
        for (int i = 0; i < 4096; i++)
        {
            if (hist[i] > maxCount)
            {
                maxCount = hist[i];
                bestBin = i;
            }
        }

        byte bgR = (byte)(((bestBin >> 8) & 0x0F) * 17);
        byte bgG = (byte)(((bestBin >> 4) & 0x0F) * 17);
        byte bgB = (byte)((bestBin & 0x0F) * 17);
        return (bgR, bgG, bgB);
    }

    public static bool IsForegroundPixel(byte r, byte g, byte b, byte bgR, byte bgG, byte bgB)
    {
        int dr = r - bgR;
        int dg = g - bgG;
        int db = b - bgB;
        int distSq = (dr * dr) + (dg * dg) + (db * db);
        if (distSq < 1500) return false;

        int lum = (r * 30 + g * 59 + b * 11) / 100;
        int bgLum = (bgR * 30 + bgG * 59 + bgB * 11) / 100;
        int lumDiff = Math.Abs(lum - bgLum);

        // Case 1: Gold / Yellow / Amber text on purple / blue / dark felt (User's real client)
        if (r > b + 20 && g > b + 10 && (bgB >= bgG || bgB >= bgR || bgLum < 100))
        {
            return distSq >= 2000;
        }

        // Case 2: Standard high-contrast text (White on dark, or dark on light)
        // Strict threshold to prevent halo bridging of touching zeros / digits
        return distSq >= 6400 && lumDiff >= 50;
    }

    private static unsafe List<CharCluster> FindTextClusters(
        byte* scan0, int stride, int w, int h,
        byte bgR, byte bgG, byte bgB)
    {
        var clusters = new List<CharCluster>();
        bool[] visited = new bool[w * h];

        // 8-way connectivity to preserve diagonal glyph strokes in low-res text (e.g. 'o', '5', 'B')
        int[] dxs = { 0, 0, -1, 1, -1, 1, -1, 1 };
        int[] dys = { -1, 1, 0, 0, -1, -1, 1, 1 };

        for (int y = 0; y < h; y++)
        {
            byte* row = scan0 + (y * stride);
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                if (visited[idx]) continue;

                byte b = row[x * 4];
                byte g = row[x * 4 + 1];
                byte r = row[x * 4 + 2];

                if (!IsForegroundPixel(r, g, b, bgR, bgG, bgB))
                {
                    visited[idx] = true;
                    continue;
                }

                // 8-way BFS flood fill
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue((x, y));
                visited[idx] = true;

                int minX = x, maxX = x, minY = y, maxY = y;
                int pixelCount = 0;

                while (queue.Count > 0)
                {
                    var (cx, cy) = queue.Dequeue();
                    pixelCount++;

                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;

                    for (int d = 0; d < 8; d++)
                    {
                        int nx = cx + dxs[d];
                        int ny = cy + dys[d];
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int nIdx = ny * w + nx;

                        if (!visited[nIdx])
                        {
                            visited[nIdx] = true;
                            byte* nrow = scan0 + (ny * stride);
                            byte nb = nrow[nx * 4];
                            byte ng = nrow[nx * 4 + 1];
                            byte nr = nrow[nx * 4 + 2];
                            if (IsForegroundPixel(nr, ng, nb, bgR, bgG, bgB))
                            {
                                queue.Enqueue((nx, ny));
                            }
                        }
                    }
                }

                int cw = maxX - minX + 1;
                int ch = maxY - minY + 1;

                // If cluster is unusually wide (multiple touching digits, e.g. "00" or "000"), split proportionally
                // Requires cw >= ch * 1.55 AND cw >= 18 to never split single glyphs (e.g. 'M', 'W', '5', 'o')
                if (cw >= (int)(ch * 1.55) && cw >= 18 && ch >= 8)
                {
                    int parts = Math.Max(2, (int)Math.Round((double)cw / (ch * 0.75)));
                    int partW = cw / parts;
                    for (int p = 0; p < parts; p++)
                    {
                        int pMinX = minX + (p * partW);
                        int pMaxX = (p == parts - 1) ? maxX : (minX + ((p + 1) * partW) - 1);
                        clusters.Add(new CharCluster(pMinX, minY, pMaxX, maxY, pixelCount / parts));
                    }
                }
                else if (pixelCount >= 2 && cw >= 1 && ch >= 2 && cw <= w * 0.50 && ch <= h * 0.95)
                {
                    clusters.Add(new CharCluster(minX, minY, maxX, maxY, pixelCount));
                }
            }
        }

        return clusters;
    }

    private static unsafe bool[,] ExtractCharGrid(
        byte* scan0, int stride,
        int minX, int minY, int width, int height,
        byte bgR, byte bgG, byte bgB)
    {
        bool[,] grid = new bool[20, 28];
        if (width <= 0 || height <= 0) return grid;

        for (int ty = 0; ty < 28; ty++)
        {
            int sy = minY + (ty * height / 28);
            byte* row = scan0 + (sy * stride);

            for (int tx = 0; tx < 20; tx++)
            {
                int sx = minX + (tx * width / 20);
                byte b = row[sx * 4];
                byte g = row[sx * 4 + 1];
                byte r = row[sx * 4 + 2];
                grid[tx, ty] = IsForegroundPixel(r, g, b, bgR, bgG, bgB);
            }
        }
        return grid;
    }

    private static (char BestChar, double Confidence) MatchCharacter(bool[,] candidate, double aspectRatio)
    {
        double maxScore = -1;
        char bestCh = ' ';

        int candInk = 0;
        for (int y = 0; y < 28; y++)
            for (int x = 0; x < 20; x++)
                if (candidate[x, y]) candInk++;

        if (candInk <= 4) return (' ', 0.0);

        foreach (var (ch, templates) in CharTemplates)
        {
            foreach (var tmpl in templates)
            {
                int match = 0;
                int tmplInk = 0;

                for (int y = 0; y < 28; y++)
                {
                    for (int x = 0; x < 20; x++)
                    {
                        if (tmpl[x, y]) tmplInk++;
                        if (candidate[x, y] && tmpl[x, y]) match++;
                    }
                }

                double score = 2.0 * match / (candInk + tmplInk);

                // Aspect ratio penalties:
                // Digits (0..9) typically have aspect ratio 0.40 ~ 0.72.
                // Wide characters (M, W, 만, 억, 천, 조, K) have aspect ratio > 0.75.
                if (aspectRatio < 0.72 && (ch == 'M' || ch == 'm' || ch == '만' || ch == '억' || ch == '천' || ch == '조' || ch == 'W' || ch == 'w' || ch == 'K' || ch == 'k'))
                {
                    score *= 0.50;
                }
                else if (aspectRatio > 0.85 && (ch == '1' || ch == 'I' || ch == 'l' || ch == ':'))
                {
                    score *= 0.50;
                }
                else if (aspectRatio > 0.55 && ch == ':')
                {
                    score *= 0.40;
                }

                // Digits have priority over letters when matching is close, unless matching POT label characters
                if (char.IsDigit(ch))
                {
                    score += 0.05;
                }

                if (score > maxScore)
                {
                    maxScore = score;
                    bestCh = ch;
                }
            }
        }

        // P vs B / 8 disambiguation:
        // 'P' has an open/empty lower-right quadrant (x in [10..18], y in [15..25]).
        // 'B' and '8' have a closed bottom loop.
        int lowerRightInk = 0;
        for (int y = 15; y <= 25; y++)
        {
            for (int x = 10; x <= 18; x++)
            {
                if (candidate[x, y]) lowerRightInk++;
            }
        }
        if ((bestCh == 'B' || bestCh == '8') && lowerRightInk <= 4)
        {
            bestCh = 'P';
        }

        // T vs 7 / 1 disambiguation:
        // 'T' has a wide top crossbar across the full width and a central stem, with empty lower corners.
        int lowerCornersInk = 0;
        for (int y = 16; y <= 26; y++)
        {
            for (int x = 1; x <= 6; x++) if (candidate[x, y]) lowerCornersInk++;
            for (int x = 13; x <= 18; x++) if (candidate[x, y]) lowerCornersInk++;
        }
        if ((bestCh == '7' || bestCh == '1') && candidate[2, 2] && candidate[17, 2] && lowerCornersInk <= 3)
        {
            bestCh = 'T';
        }

        return (bestCh, Math.Clamp(maxScore, 0.0, 1.0));
    }

    private static Dictionary<char, List<bool[,]>> GenerateCharTemplates()
    {
        var templates = new Dictionary<char, List<bool[,]>>();
        char[] chars = {
            '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
            '만', '억', '천', '조',
            'K', 'M', 'B', 'k', 'm', 'b',
            'P', 'O', 'T', 'p', 'o', 't',
            ':'
        };

        string[] fontNames = { "Segoe UI", "Malgun Gothic", "Arial", "Tahoma" };

        foreach (char c in chars)
        {
            templates[c] = new List<bool[,]>();
            string str = c.ToString();

            foreach (string fontName in fontNames)
            {
                try
                {
                    using var font = new Font(fontName, 22, FontStyle.Bold);
                    using var bmp = new Bitmap(50, 50, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Black);
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                        g.DrawString(str, font, Brushes.White, 2, 2);
                    }

                    int tMinX = 50, tMaxX = 0, tMinY = 50, tMaxY = 0;
                    for (int y = 0; y < 50; y++)
                    {
                        for (int x = 0; x < 50; x++)
                        {
                            if (bmp.GetPixel(x, y).R > 80)
                            {
                                if (x < tMinX) tMinX = x;
                                if (x > tMaxX) tMaxX = x;
                                if (y < tMinY) tMinY = y;
                                if (y > tMaxY) tMaxY = y;
                            }
                        }
                    }

                    if (tMaxX >= tMinX && tMaxY >= tMinY)
                    {
                        int tW = tMaxX - tMinX + 1;
                        int tH = tMaxY - tMinY + 1;
                        bool[,] grid = new bool[20, 28];

                        for (int ty = 0; ty < 28; ty++)
                        {
                            int sy = tMinY + (ty * tH / 28);
                            for (int tx = 0; tx < 20; tx++)
                            {
                                int sx = tMinX + (tx * tW / 20);
                                grid[tx, ty] = bmp.GetPixel(sx, sy).R > 80;
                            }
                        }
                        templates[c].Add(grid);
                    }
                }
                catch { }
            }
        }

        return templates;
    }
}
