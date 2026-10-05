using System;
using System.IO;
using System.Text.Json;

namespace PokerAssistant.Config;

public record RectF(float X, float Y, float Width, float Height)
{
    public static RectF Zero => new(0, 0, 0, 0);
}

public class GameProfile
{
    public string Name { get; set; } = "기본 프로필";
    public string TargetWindowKeyword { get; set; } = "";
    
    // Normalized coordinates (0.0f - 1.0f) relative to game window
    // Standard bottom-center seating for Hero & Action Gauge
    public RectF HeroCardsRegion { get; set; } = new(0.35f, 0.62f, 0.30f, 0.24f);
    public RectF BoardCardsRegion { get; set; } = new(0.25f, 0.34f, 0.50f, 0.18f);
    // Bottom-center region (하단 중앙: X 24%~76%, Y 68%~96%) for timer gauge and action controls
    public RectF TimerGaugeRegion { get; set; } = new(0.24f, 0.68f, 0.52f, 0.28f);
    public RectF ActionButtonsRegion { get; set; } = new(0.50f, 0.82f, 0.46f, 0.16f);
    public RectF PotRegion { get; set; } = new(0.38f, 0.22f, 0.24f, 0.08f);

    // Turn Detection sensitivity
    public float TurnSaturationThreshold { get; set; } = 0.25f;
    public float TurnBrightnessChangeThreshold { get; set; } = 0.10f;
}

public class ProfileManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PokerAssistant",
        "profiles.json"
    );

    public GameProfile CurrentProfile { get; set; }

    public ProfileManager()
    {
        CurrentProfile = GetWinjoyPreset(); // Default to Winjoy / WPL
    }

    public static GameProfile GetNetmarblePreset()
    {
        return new GameProfile
        {
            Name = "넷마블 포커 (Netmarble)",
            TargetWindowKeyword = "netmarble;넷마블;포커;골드윙",
            HeroCardsRegion = new(0.35f, 0.62f, 0.30f, 0.24f),
            BoardCardsRegion = new(0.25f, 0.34f, 0.50f, 0.18f),
            // 타겟 윈도우 하단 중앙 기준 타이머 및 액션 감시 (X: 24%~76%, Y: 68%~96%)
            TimerGaugeRegion = new(0.24f, 0.68f, 0.52f, 0.28f),
            ActionButtonsRegion = new(0.50f, 0.82f, 0.46f, 0.16f),
            PotRegion = new(0.38f, 0.22f, 0.24f, 0.08f)
        };
    }

    public static GameProfile GetWinjoyPreset()
    {
        return new GameProfile
        {
            Name = "윈조이 포커 (Winjoy / WPL)",
            TargetWindowKeyword = "wpl;wplpoker;winjoy;윈조이;엠게임;포커",
            HeroCardsRegion = new(0.35f, 0.62f, 0.30f, 0.24f),
            BoardCardsRegion = new(0.25f, 0.34f, 0.50f, 0.18f),
            // 타겟 윈도우 하단 중앙 기준 타이머 및 액션 감시 (X: 24%~76%, Y: 68%~96%)
            TimerGaugeRegion = new(0.24f, 0.68f, 0.52f, 0.28f),
            ActionButtonsRegion = new(0.50f, 0.82f, 0.46f, 0.16f),
            PotRegion = new(0.38f, 0.22f, 0.24f, 0.08f)
        };
    }

    public static GameProfile GetCustomPreset()
    {
        return new GameProfile
        {
            Name = "사용자 정의 (Custom)",
            TargetWindowKeyword = "",
            HeroCardsRegion = new(0.35f, 0.62f, 0.30f, 0.24f),
            BoardCardsRegion = new(0.25f, 0.34f, 0.50f, 0.18f),
            TimerGaugeRegion = new(0.24f, 0.68f, 0.52f, 0.28f),
            ActionButtonsRegion = new(0.50f, 0.82f, 0.46f, 0.16f),
            PotRegion = new(0.38f, 0.22f, 0.24f, 0.08f)
        };
    }

    public void SaveProfile()
    {
        try
        {
            string? dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string json = JsonSerializer.Serialize(CurrentProfile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }

    public void LoadProfile()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<GameProfile>(json);
                if (loaded != null) CurrentProfile = loaded;
            }
        }
        catch { }
    }
}
