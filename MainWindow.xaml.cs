using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PokerAssistant.Config;
using PokerAssistant.Core;
using PokerAssistant.Engine;
using PokerAssistant.UI;
using PokerAssistant.Vision;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace PokerAssistant;

public partial class MainWindow : Window
{
    private readonly ProfileManager _profileManager = new();
    private TurnDetector? _turnDetector;
    private readonly System.Windows.Threading.DispatcherTimer _periodicTimer = new();
    private ulong _lastRegionsHash = 0;
    private string _lastCardStateKey = "";
    private List<Card> _currentHeroCards = new() { new(Rank.Ace, Suit.Spades), new(Rank.King, Suit.Hearts) };
    private List<Card> _currentBoardCards = new();
    private readonly List<(IntPtr Hwnd, string Title)> _detectedWindows = new();

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private static void LogStartup(string msg)
    {
        try
        {
            string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n");
        }
        catch { }
    }

    public MainWindow()
    {
        LogStartup("MainWindow ctor: Starting InitializeComponent");
        InitializeComponent();
        LogStartup("MainWindow ctor: InitializeComponent complete");
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (s, e) => LogStartup("MainWindow Closed");
        Unloaded += (s, e) => LogStartup("MainWindow Unloaded");
        KeyDown += MainWindow_KeyDown;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LogStartup("MainWindow_Loaded started");
        try
        {
            LogStartup("Loading profile...");
            _profileManager.LoadProfile();
            LogStartup("Initializing turn detector...");
            InitTurnDetector();
            LogStartup("Populating windows list...");
            PopulateWindowsList();
            LogStartup("Rendering cards...");
            RenderCards();
            LogStartup("Running analysis...");
            RunAnalysis();
            LogStartup("Refreshing inspectors...");
            RefreshInspectors();

            // ⏱️ Start 1-second periodic polling timer
            _periodicTimer.Interval = TimeSpan.FromSeconds(1.0);
            _periodicTimer.Tick += PeriodicTimer_Tick;
            _periodicTimer.Start();

            // Wire up events safely after loaded
            PlatformComboBox.SelectionChanged += Platform_SelectionChanged;
            AutoStartCheck.IsChecked = IsAutoStartEnabled();
            AutoStartCheck.Checked += AutoStart_Changed;
            AutoStartCheck.Unchecked += AutoStart_Changed;
            AutoDetectCheck.Checked += AutoDetect_Changed;
            AutoDetectCheck.Unchecked += AutoDetect_Changed;
            PositionComboBox.SelectionChanged += PotSettings_Changed;
            SensitivityComboBox.SelectionChanged += Sensitivity_SelectionChanged;
            TargetWindowComboBox.SelectionChanged += TargetWindow_SelectionChanged;
            LogStartup("MainWindow_Loaded successfully completed");
        }
        catch (Exception ex)
        {
            LogStartup($"[CRITICAL ERROR in MainWindow_Loaded] {ex}");
            MessageBox.Show($"창 초기화 중 오류가 발생했습니다:\r\n{ex.Message}\r\n{ex.StackTrace}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            ManualCapture_Click(this, new RoutedEventArgs());
        }
    }

    private void InitTurnDetector()
    {
        IntPtr currentHwnd = _turnDetector?.TargetHwnd ?? IntPtr.Zero;
        double currentThresh = _turnDetector?.Threshold ?? 0.020;

        _turnDetector?.Dispose();
        _turnDetector = new TurnDetector(_profileManager.CurrentProfile)
        {
            Threshold = currentThresh
        };

        if (currentHwnd != IntPtr.Zero)
        {
            _turnDetector.SetTargetWindow(currentHwnd);
        }

        _turnDetector.TurnStarted += OnTurnStarted;
        _turnDetector.TurnEnded += OnTurnEnded;
        _turnDetector.TurnSampleUpdated += OnTurnSampleUpdated;

        if (AutoDetectCheck?.IsChecked == true)
        {
            _turnDetector.StartMonitoring();
        }
    }

    public void PopulateWindowsList()
    {
        TargetWindowComboBox.Items.Clear();
        _detectedWindows.Clear();

        // 1. Desktop option
        TargetWindowComboBox.Items.Add("🖥️ 전체 바탕화면 (모니터 전체)");
        _detectedWindows.Add((IntPtr.Zero, "전체 바탕화면"));

        // 2. Discover running game / browser windows
        var windows = ScreenCaptureService.FindWindows("");
        int selectIndex = 0;
        int idx = 1;

        foreach (var (hwnd, title) in windows)
        {
            _detectedWindows.Add((hwnd, title));
            TargetWindowComboBox.Items.Add(title);

            // Auto-select poker game if matched
            if (title.Contains("WPL", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("poker", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("포커", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("netmarble", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("winjoy", StringComparison.OrdinalIgnoreCase))
            {
                selectIndex = idx;
            }
            idx++;
        }

        TargetWindowComboBox.SelectedIndex = selectIndex;
    }

    private void TargetWindow_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TargetWindowComboBox.SelectedIndex < 0 || TargetWindowComboBox.SelectedIndex >= _detectedWindows.Count) return;

        var selected = _detectedWindows[TargetWindowComboBox.SelectedIndex];
        _turnDetector?.SetTargetWindow(selected.Hwnd);
        RefreshInspectors();
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e)
    {
        PopulateWindowsList();
    }

    private void PickWindow_Click(object sender, RoutedEventArgs e)
    {
        var overlay = new WindowPickerOverlay(pickedHwnd =>
        {
            if (pickedHwnd != IntPtr.Zero)
            {
                string title = ScreenCaptureService.GetWindowDisplay(pickedHwnd);
                _turnDetector?.SetTargetWindow(pickedHwnd);

                int found = -1;
                for (int i = 0; i < _detectedWindows.Count; i++)
                {
                    if (_detectedWindows[i].Hwnd == pickedHwnd) { found = i; break; }
                }

                if (found >= 0)
                {
                    TargetWindowComboBox.SelectedIndex = found;
                }
                else
                {
                    _detectedWindows.Add((pickedHwnd, title));
                    TargetWindowComboBox.Items.Add(title);
                    TargetWindowComboBox.SelectedIndex = _detectedWindows.Count - 1;
                }

                RefreshInspectors();
                MessageBox.Show($"포커 게임 창이 지정되었습니다:\n{title}", "창 지정 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });
        overlay.Show();
    }

    // ==========================================
    // ⭐ [신설] 4대 기준 영역 마우스 직접 지정 기능
    // ==========================================
    private void SetHeroRegion_Click(object sender, RoutedEventArgs e)
    {
        var targetHwnd = _turnDetector?.TargetHwnd ?? IntPtr.Zero;
        var overlay = new RegionSelectorOverlay("내 카드 영역 (두 장의 카드)", targetHwnd, Color.FromRgb(59, 130, 246), rect =>
        {
            _profileManager.CurrentProfile.HeroCardsRegion = rect;
            _profileManager.SaveProfile();
            RereadCards_Click(this, new RoutedEventArgs());
            RefreshInspectors();
            MessageBox.Show($"내 카드 영역이 저장되었습니다.\n(X: {rect.X:P0}, Y: {rect.Y:P0}, W: {rect.Width:P0}, H: {rect.Height:P0})", "영역 설정 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        overlay.Show();
    }

    private void SetBoardRegion_Click(object sender, RoutedEventArgs e)
    {
        var targetHwnd = _turnDetector?.TargetHwnd ?? IntPtr.Zero;
        var overlay = new RegionSelectorOverlay("보드 카드 영역 (커뮤니티 카드 5장)", targetHwnd, Color.FromRgb(16, 185, 129), rect =>
        {
            _profileManager.CurrentProfile.BoardCardsRegion = rect;
            _profileManager.SaveProfile();
            RereadCards_Click(this, new RoutedEventArgs());
            RefreshInspectors();
            MessageBox.Show($"보드 카드 영역이 저장되었습니다.\n(X: {rect.X:P0}, Y: {rect.Y:P0}, W: {rect.Width:P0}, H: {rect.Height:P0})", "영역 설정 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        overlay.Show();
    }

    private void SetTimerRegion_Click(object sender, RoutedEventArgs e)
    {
        var targetHwnd = _turnDetector?.TargetHwnd ?? IntPtr.Zero;
        var overlay = new RegionSelectorOverlay("타이머/게이지 영역 (내 턴 표시)", targetHwnd, Color.FromRgb(239, 68, 68), rect =>
        {
            _profileManager.CurrentProfile.TimerGaugeRegion = rect;
            _profileManager.SaveProfile();
            RefreshInspectors();
            MessageBox.Show($"타이머 영역이 저장되었습니다.\n(X: {rect.X:P0}, Y: {rect.Y:P0}, W: {rect.Width:P0}, H: {rect.Height:P0})", "영역 설정 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        overlay.Show();
    }

    private void SetPotRegion_Click(object sender, RoutedEventArgs e)
    {
        var targetHwnd = _turnDetector?.TargetHwnd ?? IntPtr.Zero;
        var overlay = new RegionSelectorOverlay("팟 금액 영역 (현재 팟 크기)", targetHwnd, Color.FromRgb(245, 158, 11), rect =>
        {
            _profileManager.CurrentProfile.PotRegion = rect;
            _profileManager.SaveProfile();
            RefreshInspectors();
            MessageBox.Show($"팟 금액 영역이 저장되었습니다.\n(X: {rect.X:P0}, Y: {rect.Y:P0}, W: {rect.Width:P0}, H: {rect.Height:P0})", "영역 설정 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        overlay.Show();
    }

    // ==========================================
    // ⏱️ [신설] 1초 주기 자동 갱신 엔진 (Periodic Polling)
    // ==========================================
    private void PeriodicTimer_Tick(object? sender, EventArgs e)
    {
        ExecutePeriodicUpdate();
    }

    private void PeriodicUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_periodicTimer == null) return;
        if (PeriodicUpdateCheck.IsChecked == true)
        {
            _periodicTimer.Start();
            PeriodicPulseDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            PeriodicStatusText.Text = "⏱️ 1초 주기 자동 갱신 동작 중 (보드·내카드·확률)";
            PeriodicStatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        }
        else
        {
            _periodicTimer.Stop();
            PeriodicPulseDot.Fill = new SolidColorBrush(Color.FromRgb(107, 114, 128));
            PeriodicStatusText.Text = "⏸️ 1초 자동 갱신 일시정지 (수동 또는 턴 감지)";
            PeriodicStatusText.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
        }
    }

    public void ExecutePeriodicUpdate()
    {
        try
        {
            if (_turnDetector == null) return;
            using var fullBmp = _turnDetector.CaptureCurrentTarget();
            if (fullBmp == null) return;

            var profile = _profileManager.CurrentProfile;

            // 1. Crop current live regions for hash change detection
            using var heroBmp = ScreenCaptureService.CropRegion(fullBmp, profile.HeroCardsRegion.X, profile.HeroCardsRegion.Y, profile.HeroCardsRegion.Width, profile.HeroCardsRegion.Height);
            using var boardBmp = ScreenCaptureService.CropRegion(fullBmp, profile.BoardCardsRegion.X, profile.BoardCardsRegion.Y, profile.BoardCardsRegion.Width, profile.BoardCardsRegion.Height);
            using var potBmp = ScreenCaptureService.CropRegion(fullBmp, profile.PotRegion.X, profile.PotRegion.Y, profile.PotRegion.Width, profile.PotRegion.Height);

            ulong currentHash = ComputeRegionsSampleHash(heroBmp, boardBmp, potBmp);
            bool regionsChanged = (currentHash != _lastRegionsHash);

            var result = CardRecognitionEngine.RecognizeTableState(fullBmp, profile);

            // Always update Live Previews with current frame so viewer is real-time
            RefreshInspectorsWithFrame(fullBmp, result);

            string currentCardKey = $"{string.Join(",", result.HeroCards)}|{string.Join(",", result.BoardCards)}|{result.PotAmount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";
            string timeStr = DateTime.Now.ToString("HH:mm:ss");

            // ⭐ 실시간 영역에 시각적 변화가 없거나 카드/팟 인식 결과가 동일한 경우 무거운 몬테카를로 계산 패스
            if (!regionsChanged && currentCardKey == _lastCardStateKey && _lastCardStateKey != "")
            {
                PeriodicStatusText.Text = $"⏱️ 1초 주기 감시 중 [{timeStr}] (영역 변화 없음 - 계산 패스)";
                PeriodicStatusText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                return;
            }

            _lastRegionsHash = currentHash;
            _lastCardStateKey = currentCardKey;

            // 2. Update Hero Cards
            if (result.HeroCards.Count >= 2)
            {
                _currentHeroCards = result.HeroCards;
                HeroCardStatusText.Text = $"내 카드: {string.Join(" ", _currentHeroCards.Select(c => c.DisplayText))} (인식 성공)";
                HeroCardStatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            }
            else if (result.HeroCards.Count == 1)
            {
                _currentHeroCards.Clear();
                HeroCardStatusText.Text = $"내 카드 1장 감지: {result.HeroCards[0].DisplayText} (영역 보정 권장)";
                HeroCardStatusText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            }
            else
            {
                _currentHeroCards.Clear();
                HeroCardStatusText.Text = result.StatusMessage;
                HeroCardStatusText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            }

            // 3. Update Board Cards (Texas Hold'em: Flop 3, Turn 4, River 5, Preflop 0)
            if (result.BoardCards.Count >= 3)
            {
                _currentBoardCards = result.BoardCards;
            }
            else
            {
                _currentBoardCards.Clear();
            }

            // 3.5 Update Pot Size if recognized (> 0)
            if (result.PotAmount > 0)
            {
                PotSizeInput.Text = result.PotAmount % 1 != 0 
                    ? result.PotAmount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) 
                    : (result.PotAmount < 1000 ? result.PotAmount.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : result.PotAmount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
            }

            // 4. Render Cards and Run Analysis
            RenderCards();
            RunAnalysis();

            // 5. Update Status Timestamp
            PeriodicStatusText.Text = $"⚡ 1초 주기 갱신 완료 [{timeStr}] (카드/팟 변화 ➔ 계산 완료)";
            PeriodicStatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        }
        catch (Exception ex)
        {
            LogStartup($"[ExecutePeriodicUpdate Error] {ex}");
        }
    }

    private static ulong ComputeRegionsSampleHash(Bitmap heroBmp, Bitmap boardBmp, Bitmap potBmp)
    {
        ulong hash = 14695981039346656037UL;
        SampleBmp(heroBmp, ref hash);
        SampleBmp(boardBmp, ref hash);
        SampleBmp(potBmp, ref hash);
        return hash;

        static void SampleBmp(Bitmap bmp, ref ulong h)
        {
            if (bmp == null || bmp.Width < 4 || bmp.Height < 4) return;
            int stepX = Math.Max(1, bmp.Width / 8);
            int stepY = Math.Max(1, bmp.Height / 8);
            for (int y = stepY / 2; y < bmp.Height; y += stepY)
            {
                for (int x = stepX / 2; x < bmp.Width; x += stepX)
                {
                    var c = bmp.GetPixel(x, y);
                    ulong q = (ulong)((c.R >> 3) << 10 | (c.G >> 3) << 5 | (c.B >> 3));
                    h = (h ^ q) * 1099511628211UL;
                }
            }
        }
    }

    // ==========================================
    // ⭐ [강화] 실시간 4대 영역 뷰어 갱신 기능
    // ==========================================
    private void RefreshInspectors_Click(object sender, RoutedEventArgs e)
    {
        RefreshInspectors();
    }

    private void RefreshInspectors()
    {
        try
        {
            if (_turnDetector == null) return;
            using var fullBmp = _turnDetector.CaptureCurrentTarget();
            if (fullBmp == null) return;
            RefreshInspectorsWithFrame(fullBmp);
        }
        catch (Exception ex)
        {
            LogStartup($"[RefreshInspectors Error] {ex}");
        }
    }

    private void RefreshInspectorsWithFrame(Bitmap fullBmp, CardRecognitionEngine.RecognitionResult? recogResult = null)
    {
        var profile = _profileManager.CurrentProfile;

        // 1. Hero Crop
        using (var heroBmp = ScreenCaptureService.CropRegion(fullBmp, profile.HeroCardsRegion.X, profile.HeroCardsRegion.Y, profile.HeroCardsRegion.Width, profile.HeroCardsRegion.Height))
        {
            HeroPreviewImage.Source = BitmapToBitmapSource(heroBmp);
            var (detected, diag) = CardRecognitionEngine.DetectHeroCardsWithDiag(heroBmp);
            HeroInspectText.Text = detected.Count > 0 ? $"감지: {string.Join(" ", detected)}" : $"미감지 ({diag})";
            HeroInspectText.Foreground = detected.Count >= 2 ? new SolidColorBrush(Color.FromRgb(52, 211, 153)) : new SolidColorBrush(Color.FromRgb(248, 113, 113));
        }

        // 2. Board Crop
        using (var boardBmp = ScreenCaptureService.CropRegion(fullBmp, profile.BoardCardsRegion.X, profile.BoardCardsRegion.Y, profile.BoardCardsRegion.Width, profile.BoardCardsRegion.Height))
        {
            BoardPreviewImage.Source = BitmapToBitmapSource(boardBmp);
            var detected = recogResult?.BoardCards ?? CardRecognitionEngine.DetectBoardCards(boardBmp);
            BoardInspectText.Text = detected.Count >= 3 ? $"보드 ({detected.Count}장): {string.Join(" ", detected)}" : "보드 비어있음 (프리플랍)";
            BoardInspectText.Foreground = detected.Count >= 3 ? new SolidColorBrush(Color.FromRgb(52, 211, 153)) : new SolidColorBrush(Color.FromRgb(148, 163, 184));
        }

        // 3. Timer Crop
        using (var timerBmp = ScreenCaptureService.CropRegion(fullBmp, profile.TimerGaugeRegion.X, profile.TimerGaugeRegion.Y, profile.TimerGaugeRegion.Width, profile.TimerGaugeRegion.Height))
        {
            GaugePreviewImage.Source = BitmapToBitmapSource(timerBmp);
            TimerInspectText.Text = "타이머 영역 실시간";
        }

        // 4. Pot Crop
        using (var potBmp = ScreenCaptureService.CropRegion(fullBmp, profile.PotRegion.X, profile.PotRegion.Y, profile.PotRegion.Width, profile.PotRegion.Height))
        {
            PotPreviewImage.Source = BitmapToBitmapSource(potBmp);
            var potResult = recogResult != null && recogResult.PotAmount > 0
                ? new PotRecognitionEngine.PotResult(recogResult.PotAmount, "", 1.0)
                : PotRecognitionEngine.RecognizePot(potBmp);

            if (potResult.Amount > 0)
            {
                string displayAmount = potResult.Amount % 1 != 0 
                    ? potResult.Amount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) 
                    : (potResult.Amount < 1000 ? potResult.Amount.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : potResult.Amount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
                string unitSuffix = potResult.RawText.Contains("bb", StringComparison.OrdinalIgnoreCase) ? " BB" : "";
                PotInspectText.Text = $"팟: {displayAmount}{unitSuffix} (인식 완료)";
                PotInspectText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            }
            else
            {
                PotInspectText.Text = "팟 감시 중 (수동 입력값 유지)";
                PotInspectText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            }
        }
    }

    // ==========================================
    // ⭐ [신설] 카드 즉시 다시 읽기 기능
    // ==========================================
    private void RereadCards_Click(object sender, RoutedEventArgs e)
    {
        ExecutePeriodicUpdate();
    }

    private void Sensitivity_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_turnDetector == null) return;
        _turnDetector.Threshold = SensitivityComboBox.SelectedIndex switch
        {
            0 => 0.010, // 1.0% 매우 민감
            1 => 0.020, // 2.0% 민감 (기본)
            2 => 0.035, // 3.5% 보통
            3 => 0.050, // 5.0% 안정
            _ => 0.020
        };
    }

    private void OnTurnSampleUpdated(Bitmap? previewBmp, double score, double threshold, bool isTriggered)
    {
        Dispatcher.Invoke(() =>
        {
            ActivityScoreText.Text = $"활동도: {score * 100:F1}% / 기준: {threshold * 100:F1}%";
            ActivityProgressBar.Maximum = Math.Max(10, threshold * 150);
            ActivityProgressBar.Value = Math.Min(ActivityProgressBar.Maximum, score * 100);

            if (isTriggered)
            {
                ActivityProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153)); // Emerald
            }
            else
            {
                ActivityProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // Sky Blue
            }

            if (previewBmp != null)
            {
                try
                {
                    GaugePreviewImage.Source = BitmapToBitmapSource(previewBmp);
                }
                catch { }
                finally
                {
                    previewBmp.Dispose();
                }
            }
        });
    }

    private static BitmapSource? BitmapToBitmapSource(Bitmap? bmp)
    {
        if (bmp == null) return null;
        IntPtr hBmp = IntPtr.Zero;
        try
        {
            hBmp = bmp.GetHbitmap();
            return Imaging.CreateBitmapSourceFromHBitmap(
                hBmp,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions()
            );
        }
        catch (Exception ex)
        {
            LogStartup($"[BitmapToBitmapSource Error] {ex.Message}");
            return null;
        }
        finally
        {
            if (hBmp != IntPtr.Zero)
            {
                DeleteObject(hBmp);
            }
        }
    }

    private void OnTurnStarted(Bitmap frame)
    {
        Dispatcher.Invoke(() =>
        {
            TurnStatusBorder.Background = new SolidColorBrush(Color.FromRgb(6, 78, 59));
            TurnPulseDot.Fill = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            TurnStatusText.Text = "🔔 내 턴 감지됨! (즉시 분석 완료)";
            TurnStatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            GaugePercentText.Text = "내 턴 진행 중";

            // Vision recognition
            var result = CardRecognitionEngine.RecognizeTableState(frame, _profileManager.CurrentProfile);
            if (result.HeroCards.Count >= 2)
            {
                _currentHeroCards = result.HeroCards;
                HeroCardStatusText.Text = $"내 카드: {string.Join(" ", _currentHeroCards.Select(c => c.DisplayText))} (자동 인식)";
                HeroCardStatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            }
            else
            {
                _currentHeroCards.Clear();
                HeroCardStatusText.Text = result.StatusMessage;
            }

            if (result.BoardCards.Count >= 3)
            {
                _currentBoardCards = result.BoardCards;
            }
            else
            {
                _currentBoardCards.Clear();
            }

            if (result.PotAmount > 0)
            {
                PotSizeInput.Text = result.PotAmount % 1 != 0 
                    ? result.PotAmount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) 
                    : (result.PotAmount < 1000 ? result.PotAmount.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : result.PotAmount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
            }

            RenderCards();
            RunAnalysis();
            RefreshInspectorsWithFrame(frame, result);

            frame.Dispose();
        });
    }

    private void OnTurnEnded()
    {
        Dispatcher.Invoke(() =>
        {
            TurnStatusBorder.Background = new SolidColorBrush(Color.FromRgb(31, 41, 55));
            TurnPulseDot.Fill = new SolidColorBrush(Color.FromRgb(107, 114, 128));
            TurnStatusText.Text = "대기 중 (하단 중앙 타이머 감시 중)";
            TurnStatusText.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            GaugePercentText.Text = "감시 중";
        });
    }

    private void RunAnalysis()
    {
        double potSize = PotRecognitionEngine.ParseNormalizedNumber(PotSizeInput.Text);
        if (potSize <= 0) potSize = 2000;

        double callAmount = PotRecognitionEngine.ParseNormalizedNumber(CallAmountInput.Text);
        if (callAmount <= 0) callAmount = 400;

        Position pos = Position.BTN;
        if (PositionComboBox.SelectedItem is ComboBoxItem item && Enum.TryParse<Position>(item.Content.ToString(), out var parsedPos))
        {
            pos = parsedPos;
        }

        // 1. Run Monte Carlo simulation (5,000 iterations in ~15ms)
        var sim = MonteCarloSimulator.Run(_currentHeroCards, _currentBoardCards, opponentCount: 1, simulations: 5000);

        // 2. Evaluate GTO Action
        var advice = GtoStrategyEngine.EvaluateAction(_currentHeroCards, _currentBoardCards, sim, potSize, callAmount, pos);

        // 3. Update UI Elements
        ActionTypeText.Text = advice.ActionNameKr;
        ActionTypeText.Foreground = (Brush)new BrushConverter().ConvertFromString(advice.ActionColorHex)!;
        ActionBadgeBorder.BorderBrush = (Brush)new BrushConverter().ConvertFromString(advice.ActionColorHex)!;

        // EV Chip
        string evStr = advice.ExpectedValue % 1 != 0 
            ? advice.ExpectedValue.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) 
            : (Math.Abs(advice.ExpectedValue) < 1000 ? advice.ExpectedValue.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : advice.ExpectedValue.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        EvChipText.Text = $"EV {(advice.ExpectedValue >= 0 ? "+" : "")}{evStr}";
        if (advice.ExpectedValue >= 0)
        {
            EvChipBorder.Background = new SolidColorBrush(Color.FromRgb(6, 78, 59));
            EvChipText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        }
        else
        {
            EvChipBorder.Background = new SolidColorBrush(Color.FromRgb(127, 29, 29));
            EvChipText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
        }

        BetSizeText.Text = advice.BetSizingRecommendation;
        HeroHandTitleText.Text = $"[{sim.HeroCurrentHand}]";

        // Progress Bars & Percentages
        EquityText.Text = $"{sim.Equity:F1}%";
        EquityProgressBar.Value = Math.Clamp(sim.Equity, 0, 100);

        PotOddsText.Text = $"{advice.PotOdds:F1}%";
        PotOddsProgressBar.Value = Math.Clamp(advice.PotOdds, 0, 100);

        WinRateText.Text = $"{sim.WinRate:F1}%";
        TieRateText.Text = $"{sim.TieRate:F1}%";
        LossRateText.Text = $"{sim.LossRate:F1}%";
        OutsText.Text = $"{sim.Outs}개";

        // Tactical Description
        BoardTextureText.Text = sim.BoardTexture;
        TacticalReasonText.Text = $"{advice.SummaryReason}\n\n{advice.TacticalDetails}";
    }

    private void RenderCards()
    {
        HeroCardsPanel.Children.Clear();
        if (_currentHeroCards.Count >= 2)
        {
            foreach (var card in _currentHeroCards)
            {
                HeroCardsPanel.Children.Add(CreateCardControl(card, 52, 70));
            }
        }
        else
        {
            HeroCardsPanel.Children.Add(CreateEmptyCardSlot(52, 70));
            HeroCardsPanel.Children.Add(CreateEmptyCardSlot(52, 70));
        }

        BoardCardsPanel.Children.Clear();
        for (int i = 0; i < 5; i++)
        {
            if (i < _currentBoardCards.Count)
            {
                BoardCardsPanel.Children.Add(CreateCardControl(_currentBoardCards[i], 42, 60));
            }
            else
            {
                BoardCardsPanel.Children.Add(CreateEmptyCardSlot(42, 60));
            }
        }
    }

    private Border CreateCardControl(Card card, double width, double height)
    {
        var cardBrush = (Brush)new BrushConverter().ConvertFromString(card.SuitColorHex)!;
        var border = new Border
        {
            Width = width,
            Height = height,
            Background = cardBrush, // 카드 바탕이 해당 색상 (파랑, 빨강, 초록, 회색)
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(3, 0, 3, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Opacity = 0.35 }
        };

        var grid = new Grid { Margin = new Thickness(width > 50 ? 4 : 3) };
        var textBrush = Brushes.White; // 숫자와 문양은 항상 흰색!

        // 1. Top-Left: 작은 숫자 + 바로 아래 작은 문양 기호
        var topLeftStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        var smallRankText = new TextBlock
        {
            Text = card.RankDisplay,
            FontSize = width > 50 ? 15 : 12,
            FontWeight = FontWeights.ExtraBold,
            Foreground = textBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var smallSuitText = new TextBlock
        {
            Text = card.SuitSymbol,
            FontSize = width > 50 ? 13 : 10,
            FontWeight = FontWeights.Bold,
            Foreground = textBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0)
        };

        topLeftStack.Children.Add(smallRankText);
        topLeftStack.Children.Add(smallSuitText);
        grid.Children.Add(topLeftStack);

        // 2. Bottom-Right: 비교적 크게 표시되는 우측 하단 숫자 (실제 게임 화면과 1:1 일치)
        var largeRankText = new TextBlock
        {
            Text = card.RankDisplay,
            FontSize = width > 50 ? 32 : 24,
            FontWeight = FontWeights.ExtraBold,
            Foreground = textBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, width > 50 ? 2 : 1, width > 50 ? -2 : -2)
        };

        grid.Children.Add(largeRankText);
        border.Child = grid;

        return border;
    }

    private Border CreateEmptyCardSlot(double width, double height)
    {
        var border = new Border
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.FromRgb(26, 34, 48)),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(3, 0, 3, 0),
            BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            BorderThickness = new Thickness(1)
        };

        var text = new TextBlock
        {
            Text = "+",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        border.Child = text;
        return border;
    }

    private void ChangeHeroCards_Click(object sender, RoutedEventArgs e)
    {
        var picker = new CardPickerWindow(_currentHeroCards, 2, selected =>
        {
            if (selected.Count >= 2)
            {
                _currentHeroCards = selected.Take(2).ToList();
                HeroCardStatusText.Text = $"내 카드 직접 지정: {string.Join(" ", _currentHeroCards.Select(c => c.DisplayText))}";
                HeroCardStatusText.Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250));
                RenderCards();
                RunAnalysis();
            }
        });
        picker.Owner = this;
        picker.ShowDialog();
    }

    private void ChangeBoardCards_Click(object sender, RoutedEventArgs e)
    {
        var picker = new CardPickerWindow(_currentBoardCards, 5, selected =>
        {
            _currentBoardCards = selected;
            RenderCards();
            RunAnalysis();
        });
        picker.Owner = this;
        picker.ShowDialog();
    }

    private void Platform_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || PlatformComboBox == null) return;

        if (PlatformComboBox.SelectedIndex == 0)
        {
            _profileManager.CurrentProfile = ProfileManager.GetNetmarblePreset();
        }
        else if (PlatformComboBox.SelectedIndex == 1)
        {
            _profileManager.CurrentProfile = ProfileManager.GetWinjoyPreset();
        }
        else
        {
            _profileManager.CurrentProfile = ProfileManager.GetCustomPreset();
        }

        InitTurnDetector();
        RefreshInspectors();
    }

    private void AutoDetect_Changed(object sender, RoutedEventArgs e)
    {
        if (_turnDetector == null) return;
        if (AutoDetectCheck.IsChecked == true)
        {
            _turnDetector.StartMonitoring();
        }
        else
        {
            _turnDetector.StopMonitoring();
            OnTurnEnded();
        }
    }

    private void ManualCapture_Click(object sender, RoutedEventArgs e)
    {
        _turnDetector?.ForceTrigger();
    }

    private void Calibration_Click(object sender, RoutedEventArgs e)
    {
        var calib = new CalibrationWindow(_profileManager.CurrentProfile, updated =>
        {
            _profileManager.CurrentProfile = updated;
            _profileManager.SaveProfile();
            InitTurnDetector();
            RefreshInspectors();
        });
        calib.Owner = this;
        calib.ShowDialog();
    }

    private void PotSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            RunAnalysis();
        }
    }

    private void DragWindow()
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            ReleaseCapture();
            SendMessage(helper.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
        catch
        {
            try { DragMove(); } catch { }
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragWindow();
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragWindow();
        }
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        TopmostBtn.Foreground = Topmost ? (Brush)new BrushConverter().ConvertFromString("#38BDF8")! : Brushes.Gray;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _periodicTimer.Stop();
        _turnDetector?.Dispose();
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("PokerAssistant") != null;
        }
        catch
        {
            return false;
        }
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            if (enable)
            {
                string exePath = Environment.ProcessPath ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PokerAssistant.exe");
                key.SetValue("PokerAssistant", $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue("PokerAssistant", false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"시작 프로그램 등록 설정 중 오류가 발생했습니다:\n{ex.Message}", "자동 실행 설정", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        SetAutoStart(AutoStartCheck.IsChecked == true);
    }
}