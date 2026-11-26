using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Roshikey.UnityAutoBackup
{
    /// <summary>
    /// 사용자별 설정. UserSettings/UnityAutoBackupSettings.json 에 저장됨 (보통 git에 안 올라감).
    /// </summary>
    [Serializable]
    internal class BackupSettings
    {
        public bool backupOnSceneSave = true;
        public int minIntervalMinutes = 10;
        public int maxBackups = 20;
        public bool fastestCompression = true;
        public string backupFolder = "";
        public List<string> excludedPaths = new List<string>
        {
            "Library", "Temp", "Logs", "obj", "Build", "Builds",
            ".git", ".vs", ".idea", ".vscode"
        };
        public long lastBackupUtcTicks;

        static BackupSettings _instance;

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static string ProjectName => new DirectoryInfo(ProjectRoot).Name;
        static string FilePath => Path.Combine(ProjectRoot, "UserSettings", "UnityAutoBackupSettings.json");

        public static string DefaultBackupFolder =>
            Path.Combine(Path.GetDirectoryName(ProjectRoot) ?? ProjectRoot, ProjectName + "_Backups");

        public string ResolvedBackupFolder =>
            string.IsNullOrWhiteSpace(backupFolder) ? DefaultBackupFolder : Path.GetFullPath(backupFolder.Trim());

        public DateTime? LastBackupLocal =>
            lastBackupUtcTicks <= 0 ? (DateTime?)null : new DateTime(lastBackupUtcTicks, DateTimeKind.Utc).ToLocalTime();

        public static BackupSettings Instance
        {
            get
            {
                if (_instance == null) Load();
                return _instance;
            }
        }

        static void Load()
        {
            _instance = new BackupSettings();
            try
            {
                if (File.Exists(FilePath))
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(FilePath), _instance);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Unity Auto Backup] 설정을 불러오지 못해 기본값을 씁니다: " + e.Message);
            }
            if (_instance.excludedPaths == null) _instance.excludedPaths = new List<string>();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Unity Auto Backup] 설정 저장 실패: " + e.Message);
            }
        }
    }
}
