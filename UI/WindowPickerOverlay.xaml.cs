using System;
using System.Windows;
using System.Windows.Input;
using PokerAssistant.Vision;

namespace PokerAssistant.UI;

public partial class WindowPickerOverlay : Window
{
    private readonly Action<IntPtr> _onPicked;

    public WindowPickerOverlay(Action<IntPtr> onPicked)
    {
        InitializeComponent();
        _onPicked = onPicked;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var screenPt = PointToScreen(e.GetPosition(this));
        Hide(); // Hide self before probing window underneath

        IntPtr hwnd = ScreenCaptureService.GetRootWindowFromPoint((int)screenPt.X, (int)screenPt.Y);
        _onPicked?.Invoke(hwnd);
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
