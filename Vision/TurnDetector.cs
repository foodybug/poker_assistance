using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using PokerAssistant.Config;

namespace PokerAssistant.Vision;

public class TurnDetector : IDisposable
{
    private readonly GameProfile _profile;
    private CancellationTokenSource? _cts;
    private bool _isMyTurn = false;
    private DateTime _lastTurnTriggerTime = DateTime.MinValue;
    private IntPtr _targetHwnd = IntPtr.Zero;

    // Frame difference buffer for motion/change detection
    private byte[]? _previousSample;
    private int _prevWidth = 0;
    private int _prevHeight = 0;

    public event Action<Bitmap>? TurnStarted;
    public event Action? TurnEnded;
    public event Action<string>? StatusMessageUpdated;
    public event Action<Bitmap?, double, double, bool>? TurnSampleUpdated;

    public double Threshold { get; set; } = 0.03; // Default 3.0% sensitivity
    public bool IsMonitoring => _cts != null && !_cts.IsCancellationRequested;
    public bool IsMyTurn => _isMyTurn;
    public IntPtr TargetHwnd => _targetHwnd;

    public TurnDetector(GameProfile profile)
    {
        _profile = profile;
    }

    public void SetTargetWindow(IntPtr hWnd)
    {
        _targetHwnd = hWnd;
        _previousSample = null;
    }

    public void StartMonitoring()
    {
        if (IsMonitoring) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            StatusMessageUpdated?.Invoke("자동 턴 모니터링 활성화됨 (게이지/타이머 감시 중)");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(140, token);
                    CheckTurnState();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    StatusMessageUpdated?.Invoke($"모니터링 알림: {ex.Message}");
                }
            }
        }, token);
    }

    public void StopMonitoring()
    {
        _cts?.Cancel();
        _cts = null;
        _isMyTurn = false;
        _previousSample = null;
        StatusMessageUpdated?.Invoke("모니터링 중지됨");
    }

    public void ForceTrigger()
    {
        Bitmap? frame = CaptureCurrentTarget();
        if (frame != null)
        {
            _isMyTurn = true;
            _lastTurnTriggerTime = DateTime.Now;
            TurnStarted?.Invoke(frame);
        }
    }

    public Bitmap? CaptureCurrentTarget()
    {
        if (_targetHwnd != IntPtr.Zero)
        {
            var bmp = ScreenCaptureService.CaptureWindow(_targetHwnd);
            if (bmp != null) return bmp;
        }

        // Auto find if not set
        if (!string.IsNullOrEmpty(_profile.TargetWindowKeyword))
        {
            var windows = ScreenCaptureService.FindWindows(_profile.TargetWindowKeyword);
            if (windows.Count > 0)
            {
                _targetHwnd = windows[0].Hwnd;
                var bmp = ScreenCaptureService.CaptureWindow(_targetHwnd);
                if (bmp != null) return bmp;
            }
        }

        // Fallback to active desktop screen
        int screenW = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
        int screenH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
        if (screenW <= 0) screenW = 1920;
        if (screenH <= 0) screenH = 1080;
        var bounds = new Rectangle(0, 0, screenW, screenH);
        return ScreenCaptureService.CaptureDesktopRegion(bounds);
    }

    private void CheckTurnState()
    {
        using var fullBmp = CaptureCurrentTarget();
        if (fullBmp == null || fullBmp.Width < 100 || fullBmp.Height < 100) return;

        // Check Timer Gauge Region
        using var gaugeBmp = ScreenCaptureService.CropRegion(
            fullBmp,
            _profile.TimerGaugeRegion.X,
            _profile.TimerGaugeRegion.Y,
            _profile.TimerGaugeRegion.Width,
            _profile.TimerGaugeRegion.Height
        );

        // Check Action Button Region
        using var buttonsBmp = ScreenCaptureService.CropRegion(
            fullBmp,
            _profile.ActionButtonsRegion.X,
            _profile.ActionButtonsRegion.Y,
            _profile.ActionButtonsRegion.Width,
            _profile.ActionButtonsRegion.Height
        );

        // 1. Color saturation & brightness score
        double gaugeColorScore = EvaluateColorActivity(gaugeBmp);
        double buttonColorScore = EvaluateColorActivity(buttonsBmp);

        // 2. Motion / Frame Difference score
        double motionScore = EvaluateMotionDifference(gaugeBmp);

        // Combined max activity score
        double combinedScore = Math.Max(motionScore, Math.Max(gaugeColorScore, buttonColorScore));

        bool turnActive = combinedScore >= Threshold;

        // Clone small preview for UI display
        Bitmap? previewClone = null;
        try
        {
            previewClone = (Bitmap)gaugeBmp.Clone();
        }
        catch { }

        TurnSampleUpdated?.Invoke(previewClone, combinedScore, Threshold, turnActive);

        if (turnActive)
        {
            // Debounce: Trigger when entering turn state or long turn
            if (!_isMyTurn || (DateTime.Now - _lastTurnTriggerTime).TotalSeconds > 7)
            {
                _isMyTurn = true;
                _lastTurnTriggerTime = DateTime.Now;

                var captureClone = (Bitmap)fullBmp.Clone();
                StatusMessageUpdated?.Invoke($"내 턴 감지됨! (활동도: {combinedScore:P1}) - 즉시 분석 실행");
                TurnStarted?.Invoke(captureClone);
            }
        }
        else
        {
            if (_isMyTurn)
            {
                _isMyTurn = false;
                StatusMessageUpdated?.Invoke("상대 턴 / 대기 중");
                TurnEnded?.Invoke();
            }
        }
    }

    private unsafe double EvaluateMotionDifference(Bitmap bmp)
    {
        if (bmp.Width == 0 || bmp.Height == 0) return 0;

        BitmapData data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb
        );

        int changedPixels = 0;
        int sampledTotal = 0;

        try
        {
            int w = bmp.Width;
            int h = bmp.Height;
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;

            int sampleSize = (w / 2) * (h / 2);
            byte[] currentSample = new byte[sampleSize];
            int idx = 0;

            for (int y = 0; y < h; y += 2)
            {
                byte* row = scan0 + (y * stride);
                for (int x = 0; x < w; x += 2)
                {
                    if (idx >= sampleSize) break;
                    byte b = row[x * 4];
                    byte g = row[x * 4 + 1];
                    byte r = row[x * 4 + 2];

                    // Grayscale intensity
                    byte gray = (byte)((r * 30 + g * 59 + b * 11) / 100);
                    currentSample[idx] = gray;

                    if (_previousSample != null && idx < _previousSample.Length)
                    {
                        sampledTotal++;
                        if (Math.Abs(gray - _previousSample[idx]) > 20)
                        {
                            changedPixels++;
                        }
                    }
                    idx++;
                }
            }

            _previousSample = currentSample;
            _prevWidth = w;
            _prevHeight = h;

            return sampledTotal > 0 ? (double)changedPixels / sampledTotal : 0;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private unsafe double EvaluateColorActivity(Bitmap bmp)
    {
        if (bmp.Width == 0 || bmp.Height == 0) return 0;

        BitmapData data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb
        );

        int activeCount = 0;
        int sampledTotal = 0;

        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;

            for (int y = 0; y < bmp.Height; y += 2)
            {
                byte* row = scan0 + (y * stride);
                for (int x = 0; x < bmp.Width; x += 2)
                {
                    sampledTotal++;
                    byte b = row[x * 4];
                    byte g = row[x * 4 + 1];
                    byte r = row[x * 4 + 2];

                    int max = Math.Max(r, Math.Max(g, b));
                    int min = Math.Min(r, Math.Min(g, b));
                    int delta = max - min;

                    // Vivid colors (Yellow, Orange, Green, Cyan, Red) typical of action timer & buttons
                    if (max > 100 && delta > 30)
                    {
                        activeCount++;
                    }
                }
            }

            return sampledTotal > 0 ? (double)activeCount / sampledTotal : 0;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}
