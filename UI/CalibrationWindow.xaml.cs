using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PokerAssistant.Config;
using PokerAssistant.Vision;

namespace PokerAssistant.UI;

public partial class CalibrationWindow : Window
{
    private readonly GameProfile _profile;
    private readonly Action<GameProfile> _onSaved;
    private Bitmap? _currentCapture;

    public CalibrationWindow(GameProfile profile, Action<GameProfile> onSaved)
    {
        InitializeComponent();
        _profile = profile;
        _onSaved = onSaved;

        LoadSlidersFromProfile();
        CapturePokerWindow();
    }

    private void LoadSlidersFromProfile()
    {
        HeroXSlider.Value = _profile.HeroCardsRegion.X;
        HeroYSlider.Value = _profile.HeroCardsRegion.Y;
        HeroWSlider.Value = _profile.HeroCardsRegion.Width;
        HeroHSlider.Value = _profile.HeroCardsRegion.Height;

        BoardXSlider.Value = _profile.BoardCardsRegion.X;
        BoardYSlider.Value = _profile.BoardCardsRegion.Y;
        BoardWSlider.Value = _profile.BoardCardsRegion.Width;
        BoardHSlider.Value = _profile.BoardCardsRegion.Height;

        TimerXSlider.Value = _profile.TimerGaugeRegion.X;
        TimerYSlider.Value = _profile.TimerGaugeRegion.Y;
        TimerWSlider.Value = _profile.TimerGaugeRegion.Width;
        TimerHSlider.Value = _profile.TimerGaugeRegion.Height;
    }

    private void CapturePokerWindow()
    {
        _currentCapture?.Dispose();

        var windows = ScreenCaptureService.FindWindows(_profile.TargetWindowKeyword);
        if (windows.Count > 0)
        {
            _currentCapture = ScreenCaptureService.CaptureWindow(windows[0].Hwnd);
        }

        if (_currentCapture == null)
        {
            int screenW = (int)SystemParameters.PrimaryScreenWidth;
            int screenH = (int)SystemParameters.PrimaryScreenHeight;
            if (screenW <= 0) screenW = 1920;
            if (screenH <= 0) screenH = 1080;
            var bounds = new System.Drawing.Rectangle(0, 0, screenW, screenH);
            _currentCapture = ScreenCaptureService.CaptureDesktopRegion(bounds);
        }

        if (_currentCapture != null)
        {
            var hBitmap = _currentCapture.GetHbitmap();
            try
            {
                var wpfBitmap = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions()
                );
                CapturedImage.Source = wpfBitmap;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }

        UpdateBoxes();
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    private void UpdateBoxes()
    {
        if (PreviewCanvas.ActualWidth <= 0 || PreviewCanvas.ActualHeight <= 0) return;

        double cw = PreviewCanvas.ActualWidth;
        double ch = PreviewCanvas.ActualHeight;

        CapturedImage.Width = cw;
        CapturedImage.Height = ch;

        // Hero
        Canvas.SetLeft(HeroBox, HeroXSlider.Value * cw);
        Canvas.SetTop(HeroBox, HeroYSlider.Value * ch);
        HeroBox.Width = Math.Max(5, HeroWSlider.Value * cw);
        HeroBox.Height = Math.Max(5, HeroHSlider.Value * ch);

        // Board
        Canvas.SetLeft(BoardBox, BoardXSlider.Value * cw);
        Canvas.SetTop(BoardBox, BoardYSlider.Value * ch);
        BoardBox.Width = Math.Max(5, BoardWSlider.Value * cw);
        BoardBox.Height = Math.Max(5, BoardHSlider.Value * ch);

        // Timer
        Canvas.SetLeft(TimerBox, TimerXSlider.Value * cw);
        Canvas.SetTop(TimerBox, TimerYSlider.Value * ch);
        TimerBox.Width = Math.Max(5, TimerWSlider.Value * cw);
        TimerBox.Height = Math.Max(5, TimerHSlider.Value * ch);
    }

    private void Sliders_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateBoxes();
    }

    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateBoxes();
    }

    private void RefreshCapture_Click(object sender, RoutedEventArgs e)
    {
        CapturePokerWindow();
    }

    private void NetmarblePreset_Click(object sender, RoutedEventArgs e)
    {
        var preset = ProfileManager.GetNetmarblePreset();
        ApplyPreset(preset);
    }

    private void WinjoyPreset_Click(object sender, RoutedEventArgs e)
    {
        var preset = ProfileManager.GetWinjoyPreset();
        ApplyPreset(preset);
    }

    private void ApplyPreset(GameProfile p)
    {
        HeroXSlider.Value = p.HeroCardsRegion.X;
        HeroYSlider.Value = p.HeroCardsRegion.Y;
        HeroWSlider.Value = p.HeroCardsRegion.Width;
        HeroHSlider.Value = p.HeroCardsRegion.Height;

        BoardXSlider.Value = p.BoardCardsRegion.X;
        BoardYSlider.Value = p.BoardCardsRegion.Y;
        BoardWSlider.Value = p.BoardCardsRegion.Width;
        BoardHSlider.Value = p.BoardCardsRegion.Height;

        TimerXSlider.Value = p.TimerGaugeRegion.X;
        TimerYSlider.Value = p.TimerGaugeRegion.Y;
        TimerWSlider.Value = p.TimerGaugeRegion.Width;
        TimerHSlider.Value = p.TimerGaugeRegion.Height;

        UpdateBoxes();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _profile.HeroCardsRegion = new((float)HeroXSlider.Value, (float)HeroYSlider.Value, (float)HeroWSlider.Value, (float)HeroHSlider.Value);
        _profile.BoardCardsRegion = new((float)BoardXSlider.Value, (float)BoardYSlider.Value, (float)BoardWSlider.Value, (float)BoardHSlider.Value);
        _profile.TimerGaugeRegion = new((float)TimerXSlider.Value, (float)TimerYSlider.Value, (float)TimerWSlider.Value, (float)TimerHSlider.Value);

        _onSaved?.Invoke(_profile);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _currentCapture?.Dispose();
        base.OnClosed(e);
    }
}
