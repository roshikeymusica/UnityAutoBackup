using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Roshikey.UnityAutoBackup
{
    internal class UnityAutoBackupWindow : EditorWindow
    {
        Vector2 _scroll;
        List<FileInfo> _backups = new List<FileInfo>();
        string _excludeText;

        [MenuItem("Tools/Unity Auto Backup/백업 창 열기", priority = 0)]
        static void Open() => GetWindow<UnityAutoBackupWindow>("Unity Auto Backup");

        [MenuItem("Tools/Unity Auto Backup/지금 백업", priority = 1)]
        static void BackupNow()
        {
            AssetDatabase.SaveAssets();
            BackupRunner.StartBackup("수동 백업");
        }

        [MenuItem("Tools/Unity Auto Backup/지금 백업", true)]
        static bool ValidateBackupNow() => !BackupRunner.IsRunning;

        void OnEnable()
        {
            _excludeText = string.Join("\n", BackupSettings.Instance.excludedPaths);
            RefreshList();
            BackupRunner.Finished += OnBackupFinished;
        }

        void OnDisable() => BackupRunner.Finished -= OnBackupFinished;

        void OnBackupFinished()
        {
            RefreshList();
            Repaint();
        }

        void OnInspectorUpdate()
        {
            if (BackupRunner.IsRunning) Repaint();
        }

        void RefreshList()
        {
            _backups = BackupRunner.GetBackups(BackupSettings.Instance.ResolvedBackupFolder, BackupSettings.ProjectName);
        }

        void OnGUI()
        {
            var s = BackupSettings.Instance;
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // ── 상태 ──
            EditorGUILayout.LabelField("상태", EditorStyles.boldLabel);
            if (BackupRunner.IsRunning)
            {
                Rect r = GUILayoutUtility.GetRect(18, 20, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(r, BackupRunner.CurrentProgress, $"백업 중... {BackupRunner.CurrentProgress * 100f:0}%");
                EditorGUILayout.LabelField(BackupRunner.CurrentStatus, EditorStyles.miniLabel);
                if (GUILayout.Button("취소")) BackupRunner.Cancel();
            }
            else
            {
                var last = s.LastBackupLocal;
                EditorGUILayout.LabelField("마지막 백업", last.HasValue ? last.Value.ToString("yyyy-MM-dd HH:mm:ss") : "없음");
                if (GUILayout.Button("지금 백업", GUILayout.Height(32))) BackupNow();
            }

            EditorGUILayout.Space(10);

            // ── 설정 ──
            EditorGUILayout.LabelField("설정", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            s.backupOnSceneSave = EditorGUILayout.Toggle("씬 저장 시 자동 백업", s.backupOnSceneSave);
            using (new EditorGUI.DisabledScope(!s.backupOnSceneSave))
            {
                s.minIntervalMinutes = Mathf.Max(0, EditorGUILayout.IntField(
                    new GUIContent("최소 간격(분)", "마지막 백업 후 이 시간이 지나야 씬 저장 시 다시 백업합니다. 0 = 매번"),
                    s.minIntervalMinutes));
            }
            s.maxBackups = Mathf.Max(0, EditorGUILayout.IntField(
                new GUIContent("최대 보관 개수", "넘으면 오래된 것부터 삭제. 0 = 무제한"), s.maxBackups));
            s.fastestCompression = EditorGUILayout.Toggle(
                new GUIContent("빠른 압축", "끄면 용량은 조금 줄지만 훨씬 느려집니다"), s.fastestCompression);

            EditorGUILayout.BeginHorizontal();
            s.backupFolder = EditorGUILayout.TextField("백업 폴더", s.backupFolder);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string picked = EditorUtility.OpenFolderPanel("백업 폴더 선택", s.ResolvedBackupFolder, "");
                if (!string.IsNullOrEmpty(picked))
                {
                    s.backupFolder = picked;
                    GUI.FocusControl(null);
                    GUI.changed = true;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (string.IsNullOrWhiteSpace(s.backupFolder))
                EditorGUILayout.HelpBox("비워두면 프로젝트 옆 폴더에 저장:\n" + BackupSettings.DefaultBackupFolder, MessageType.None);

            string assetsDir = Path.GetFullPath(Application.dataPath);
            if (s.ResolvedBackupFolder.StartsWith(assetsDir, StringComparison.OrdinalIgnoreCase))
                EditorGUILayout.HelpBox("백업 폴더가 Assets 안에 있으면 Unity가 zip을 임포트하고 백업끼리 중첩됩니다. 프로젝트 밖을 추천합니다.", MessageType.Warning);

            EditorGUILayout.LabelField("제외 목록 (한 줄에 하나 · 프로젝트 기준 경로 또는 *.확장자)", EditorStyles.miniBoldLabel);
            _excludeText = EditorGUILayout.TextArea(_excludeText, GUILayout.MinHeight(90));

            if (EditorGUI.EndChangeCheck())
            {
                s.excludedPaths = _excludeText
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
                s.Save();
                RefreshList();
            }

            EditorGUILayout.Space(10);

            // ── 백업 목록 ──
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"백업 목록 ({_backups.Count}개)", EditorStyles.boldLabel);
            if (GUILayout.Button("새로고침", GUILayout.Width(70))) RefreshList();
            if (GUILayout.Button("폴더 열기", GUILayout.Width(70)))
            {
                Directory.CreateDirectory(s.ResolvedBackupFolder);
                Application.OpenURL("file:///" + s.ResolvedBackupFolder.Replace('\\', '/'));
            }
            EditorGUILayout.EndHorizontal();

            if (_backups.Count == 0)
            {
                EditorGUILayout.LabelField("아직 백업이 없습니다.", EditorStyles.miniLabel);
            }
            else
            {
                long totalSize = 0;
                foreach (var f in _backups)
                {
                    if (!f.Exists) continue;
                    totalSize += f.Length;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(f.Name);
                    EditorGUILayout.LabelField(BackupRunner.FormatSize(f.Length), GUILayout.Width(80));
                    if (GUILayout.Button("보기", GUILayout.Width(45))) EditorUtility.RevealInFinder(f.FullName);
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.LabelField("총 용량: " + BackupRunner.FormatSize(totalSize), EditorStyles.miniLabel);
            }

            EditorGUILayout.EndScrollView();
        }
    }
}
