using System;
using System.IO;
using System.Text.Json;

namespace Compare
{
    public class PathSettings
    {
        public string LastDirA { get; set; } = "";
        public string LastDirB { get; set; } = "";

        internal static string? SettingsPathOverride { get; set; }

        private static string SettingsFilePath => SettingsPathOverride ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Compare", "lastpaths.json");

        // 예전 이름(FolderCompareWPF, 2026-11-16 Compare 로 바꿈)의 설정: 새 파일이 없으면 이것을 읽고, 저장은 새 위치에
        private static string FormerSettingsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FolderCompareWPF", "lastpaths.json");

        public static PathSettings Load()
        {
            try
            {
                string path = SettingsFilePath;
                if (!File.Exists(path) && SettingsPathOverride is null && File.Exists(FormerSettingsFilePath)) path = FormerSettingsFilePath;
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<PathSettings>(json);
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // 설정 파일 손상 등 - 기본값 사용
            }
            return new PathSettings();
        }

        public void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsFilePath);
                if (dir != null) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(this);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // 저장 실패 시 무시 (앱 동작에 영향 없도록)
            }
        }
    }
}
