using System;
using System.IO;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace PokerAssistant.Vision;

/// <summary>
/// 실전 포커 클라이언트에서 카드 인식 실패 시 원본 프레임을 debug_dumps/ 에 자동 저장합니다.
/// 누적된 덤프 이미지를 바탕으로 모델 보강 훈련에 활용할 수 있습니다.
/// </summary>
public static class ScreenDumpService
{
    private static readonly string DumpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_dumps");
    private static int _dumpCounter = 0;
    private static bool _enabled = true;

    /// <summary>
    /// 덤프 기능 활성화/비활성화
    /// </summary>
    public static bool IsEnabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// 인식 실패 프레임을 저장합니다.
    /// </summary>
    /// <param name="frame">캡처된 전체 화면 비트맵</param>
    /// <param name="reason">실패 이유 (파일명에 포함)</param>
    public static void DumpFrame(System.Drawing.Bitmap frame, string reason = "")
    {
        if (!_enabled || frame == null) return;

        try
        {
            Directory.CreateDirectory(DumpDir);

            int id = System.Threading.Interlocked.Increment(ref _dumpCounter);
            string sanitized = SanitizeFileName(reason);
            string filename = $"{DateTime.Now:yyyyMMdd_HHmmss}_{id:D4}_{sanitized}.png";
            string path = Path.Combine(DumpDir, filename);

            frame.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch { /* 덤프 실패는 무시 */ }
    }

    /// <summary>
    /// OpenCV Mat 이미지를 저장합니다.
    /// </summary>
    public static void DumpMat(Mat mat, string tag)
    {
        if (!_enabled || mat == null || mat.Empty()) return;

        try
        {
            Directory.CreateDirectory(DumpDir);

            int id = System.Threading.Interlocked.Increment(ref _dumpCounter);
            string filename = $"{DateTime.Now:yyyyMMdd_HHmmss}_{id:D4}_{SanitizeFileName(tag)}.png";
            string path = Path.Combine(DumpDir, filename);

            Cv2.ImWrite(path, mat);
        }
        catch { }
    }

    /// <summary>
    /// 현재 저장된 덤프 파일 개수를 반환합니다.
    /// </summary>
    public static int GetDumpCount()
    {
        try
        {
            if (!Directory.Exists(DumpDir)) return 0;
            return Directory.GetFiles(DumpDir, "*.png").Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 덤프 디렉토리 경로를 반환합니다.
    /// </summary>
    public static string GetDumpDirectory() => DumpDir;

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "frame";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length > 40 ? name[..40] : name;
    }
}
