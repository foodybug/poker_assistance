using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PokerAssistant.Config;
using PokerAssistant.Vision;

namespace PokerAssistant.UI;

public partial class RegionSelectorOverlay : Window
{
    private readonly string _targetRegionName;
    private readonly IntPtr _targetHwnd;
    private readonly Action<RectF> _onRegionSelected;

    private Point _startPoint;
    private bool _isDragging = false;

    public RegionSelectorOverlay(string targetRegionName, IntPtr targetHwnd, Color boxColor, Action<RectF> onRegionSelected)
    {
        InitializeComponent();
        _targetRegionName = targetRegionName;
        _targetHwnd = targetHwnd;
        _onRegionSelected = onRegionSelected;

        InstructionTitle.Text = $"🎯 [{_targetRegionName}]을 마우스로 드래그하여 지정하세요";
        InstructionTitle.Foreground = new SolidColorBrush(boxColor);
        SelectionBox.Stroke = new SolidColorBrush(boxColor);
        SelectionBox.Fill = new SolidColorBrush(Color.FromArgb(50, boxColor.R, boxColor.G, boxColor.B));
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _isDragging = true;
            _startPoint = e.GetPosition(OverlayCanvas);

            Canvas.SetLeft(SelectionBox, _startPoint.X);
            Canvas.SetTop(SelectionBox, _startPoint.Y);
            SelectionBox.Width = 0;
            SelectionBox.Height = 0;
            SelectionBox.Visibility = Visibility.Visible;
            CaptureMouse();
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            Point currentPoint = e.GetPosition(OverlayCanvas);

            double left = Math.Min(_startPoint.X, currentPoint.X);
            double top = Math.Min(_startPoint.Y, currentPoint.Y);
            double width = Math.Abs(currentPoint.X - _startPoint.X);
            double height = Math.Abs(currentPoint.Y - _startPoint.Y);

            Canvas.SetLeft(SelectionBox, left);
            Canvas.SetTop(SelectionBox, top);
            SelectionBox.Width = width;
            SelectionBox.Height = height;
        }
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();

            Point endPoint = e.GetPosition(OverlayCanvas);
            double selLeft = Math.Min(_startPoint.X, endPoint.X);
            double selTop = Math.Min(_startPoint.Y, endPoint.Y);
            double selWidth = Math.Abs(endPoint.X - _startPoint.X);
            double selHeight = Math.Abs(endPoint.Y - _startPoint.Y);

            if (selWidth < 10 || selHeight < 10)
            {
                SelectionBox.Visibility = Visibility.Collapsed;
                return;
            }

            // Convert WPF coordinates to physical screen pixels for 100% DPI accuracy
            Point p1 = PointToScreen(new Point(selLeft, selTop));
            Point p2 = PointToScreen(new Point(selLeft + selWidth, selTop + selHeight));
            double physLeft = Math.Min(p1.X, p2.X);
            double physTop = Math.Min(p1.Y, p2.Y);
            double physWidth = Math.Abs(p2.X - p1.X);
            double physHeight = Math.Abs(p2.Y - p1.Y);

            RectF relativeRect;

            if (_targetHwnd != IntPtr.Zero && ScreenCaptureService.GetWindowBounds(_targetHwnd, out var winRect) && winRect.Width > 0 && winRect.Height > 0)
            {
                float rx = (float)(physLeft - winRect.Left) / winRect.Width;
                float ry = (float)(physTop - winRect.Top) / winRect.Height;
                float rw = (float)physWidth / winRect.Width;
                float rh = (float)physHeight / winRect.Height;

                // Clamp to [0, 1]
                rx = Math.Clamp(rx, 0f, 1f);
                ry = Math.Clamp(ry, 0f, 1f);
                rw = Math.Clamp(rw, 0.01f, 1f - rx);
                rh = Math.Clamp(rh, 0.01f, 1f - ry);

                relativeRect = new RectF(rx, ry, rw, rh);
            }
            else
            {
                // Full Desktop mode (DPI aware)
                var pOrigin = PointToScreen(new Point(0, 0));
                var pMax = PointToScreen(new Point(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
                double totalPhysW = Math.Max(1, pMax.X - pOrigin.X);
                double totalPhysH = Math.Max(1, pMax.Y - pOrigin.Y);

                float rx = (float)(physLeft / totalPhysW);
                float ry = (float)(physTop / totalPhysH);
                float rw = (float)(physWidth / totalPhysW);
                float rh = (float)(physHeight / totalPhysH);

                rx = Math.Clamp(rx, 0f, 1f);
                ry = Math.Clamp(ry, 0f, 1f);
                rw = Math.Clamp(rw, 0.01f, 1f - rx);
                rh = Math.Clamp(rh, 0.01f, 1f - ry);

                relativeRect = new RectF(rx, ry, rw, rh);
            }

            _onRegionSelected?.Invoke(relativeRect);
            Close();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
