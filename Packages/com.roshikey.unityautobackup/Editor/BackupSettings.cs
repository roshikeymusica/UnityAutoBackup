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
        public bool backupOnQuit = true;
        public int minIntervalMinutes = 10;
        public int maxBackups = 20;
        /// <summary>0 = 압축 안 함, 1 = 빠른 압축, 2 = 최대 압축</summary>
        public int compressionMode = 1;
        /// <summary>압축 스레드 수. 0 = 자동(코어 수 - 1)</summary>
        public int threadCount = 0;
        /// <summary>이미 압축된 형식이라 다시 압축해도 안 줄어드는 확장자 → 그냥 저장</summary>
        public string storeExtensions = "png, jpg, jpeg, webp, exr, zip, 7z, rar, gz, unitypackage, mp3, ogg, m4a, aac, flac, opus, mp4, webm, mov, mkv, avi, ktx2";
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
