using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Roshikey.UnityAutoBackup
{
    [InitializeOnLoad]
    internal static class BackupRunner
    {
        public static bool IsRunning { get; private set; }
        public static float CurrentProgress => Mathf.Clamp01(_progress);
        public static string CurrentStatus => _status ?? "";
        public static event Action Finished;

        /// <summary>백업이 끝나면 Unity를 종료할 예정인지</summary>
        public static bool QuitPending { get; private set; }
        static bool _allowQuit;

        static volatile float _progress;
        static volatile string _status;
        static volatile bool _done;
        static bool _resultSuccess, _resultCanceled;
        static string _resultMessage;

        static CancellationTokenSource _cts;
        static int _progressId = -1;
        static bool _reloadLocked;

        static BackupRunner()
        {
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorApplication.wantsToQuit += OnWantsToQuit;
        }

        // ───────────────────────── 트리거 ─────────────────────────

        static void OnSceneSaved(Scene scene)
        {
            var s = BackupSettings.Instance;
            if (!s.backupOnSceneSave || IsRunning) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            if (s.lastBackupUtcTicks > 0)
            {
                var since = DateTime.UtcNow - new DateTime(s.lastBackupUtcTicks, DateTimeKind.Utc);
                if (since.TotalMinutes < s.minIntervalMinutes) return;
            }

            string sceneName = scene.name;
            // 저장이 완전히 끝난 다음 프레임에 시작
            EditorApplication.delayCall += () => StartBackup("씬 저장: " + sceneName);
        }

        static bool OnWantsToQuit()
        {
            if (_allowQuit) return true;

            // 이미 백업이 돌고 있을 때
            if (IsRunning)
            {
                if (QuitPending)
                {
                    bool now = EditorUtility.DisplayDialog("Unity Auto Backup",
                        "종료 전 백업이 진행 중입니다. 끝나면 자동으로 종료됩니다.\n\n백업을 취소하고 바로 종료할까요?",
                        "취소하고 바로 종료", "기다리기");
                    if (!now) return false;
                    CancelAndQuit();
                    return true;
                }

                int choice = EditorUtility.DisplayDialogComplex("Unity Auto Backup",
                    "백업이 진행 중입니다.", "끝나면 종료", "기다리기", "취소하고 종료");
                if (choice == 0) { QuitPending = true; ShowWindow(); return false; }
                if (choice == 2) { CancelAndQuit(); return true; }
                return false;
            }

            var s = BackupSettings.Instance;
            if (!s.backupOnQuit) return true;

            // 방금 백업했으면 또 할 필요 없음
            if (s.lastBackupUtcTicks > 0 &&
                (DateTime.UtcNow - new DateTime(s.lastBackupUtcTicks, DateTimeKind.Utc)).TotalMinutes < 1)
                return true;

            // 저장 안 된 씬이 있으면 먼저 물어보기 (취소 누르면 종료도 취소)
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
            AssetDatabase.SaveAssets();

            if (!StartBackup("종료 전 백업")) return true;
            QuitPending = true;
            ShowWindow();
            return false;
        }

        static void ShowWindow()
        {
            EditorApplication.delayCall += () => UnityAutoBackupWindow.Open();
        }

        /// <summary>백업을 취소하고 즉시 종료</summary>
        public static void CancelAndQuit()
        {
            Cancel();
            QuitPending = false;
            _allowQuit = true;
            if (_reloadLocked) { EditorApplication.UnlockReloadAssemblies(); _reloadLocked = false; }
        }

        /// <summary>종료 예약만 취소 (백업은 계속)</summary>
        public static void AbortQuit() => QuitPending = false;

        static void QuitNow()
        {
            EditorApplication.delayCall += RequestQuit;
        }

        /// <summary>
        /// Unity 종료 요청. Unity엔 일반 종료 API가 없어서 File > Exit 메뉴를 실행해 정상 종료 과정을 거침.
        /// 메뉴가 없으면(macOS 등) 저장 확인 후 강제 종료.
        /// </summary>
        public static void RequestQuit()
        {
            _allowQuit = true;
            if (EditorApplication.ExecuteMenuItem("File/Exit")) return;

            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                AssetDatabase.SaveAssets();
                EditorApplication.Exit(0);
            }
            else
            {
                _allowQuit = false;
            }
        }

        // ───────────────────────── 공개 API ─────────────────────────

        public static bool StartBackup(string reason)
        {
            if (IsRunning) return false;

            var s = BackupSettings.Instance;
            string root = BackupSettings.ProjectRoot;
            string name = BackupSettings.ProjectName;
            string folder = s.ResolvedBackupFolder;
            var excludes = s.excludedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim().Replace('\\', '/').Trim('/'))
                .ToList();
            var mode = (ZipMode)Mathf.Clamp(s.compressionMode, 0, 2);
            var storeExts = new HashSet<string>(
                (s.storeExtensions ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().TrimStart('.', '*').ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);
            int threads = s.threadCount > 0 ? s.threadCount : Math.Max(1, SystemInfo.processorCount - 1);
            int max = s.maxBackups;

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Unity Auto Backup] 백업 폴더를 만들 수 없습니다: {folder}\n{e.Message}");
                return false;
            }

            IsRunning = true;
            _done = false;
            _progress = 0f;
            _status = "파일 목록 수집 중...";
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _progressId = Progress.Start("Unity Auto Backup", reason);
            Progress.RegisterCancelCallback(_progressId, () => { _cts?.Cancel(); return true; });

            // 백업 도중 스크립트 리컴파일(도메인 리로드)로 스레드가 죽지 않게 잠금
            EditorApplication.LockReloadAssemblies();
            _reloadLocked = true;

            Task.Run(() => Run(root, name, folder, excludes, mode, storeExts, threads, max, token));
            EditorApplication.update += Poll;
            return true;
        }

        public static void Cancel() => _cts?.Cancel();

        static readonly Regex DateSuffix = new Regex(@"_\d{8}_\d{6}(_\d+)?\.zip$", RegexOptions.IgnoreCase);

        public static List<FileInfo> GetBackups(string folder, string projectName)
        {
            var list = new List<FileInfo>();
            try
            {
                if (!Directory.Exists(folder)) return list;
                foreach (var f in new DirectoryInfo(folder).GetFiles(projectName + "_*.zip"))
                {
                    // "Foo" 프로젝트가 "Foo_Bar" 프로젝트 백업을 건드리지 않도록 정확히 매칭
                    string rest = f.Name.Substring(projectName.Length);
                    if (DateSuffix.IsMatch(rest) && DateSuffix.Match(rest).Index == 0) list.Add(f);
                }
            }
            catch { /* 무시 */ }
            return list.OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        }

        // ───────────────────────── 메인 스레드 폴링 ─────────────────────────

        static void Poll()
        {
            if (_progressId >= 0 && Progress.Exists(_progressId))
                Progress.Report(_progressId, CurrentProgress, CurrentStatus);

            if (!_done) return;

            EditorApplication.update -= Poll;
            if (_reloadLocked)
            {
                EditorApplication.UnlockReloadAssemblies();
                _reloadLocked = false;
            }

            if (_progressId >= 0 && Progress.Exists(_progressId))
            {
                var st = _resultSuccess ? Progress.Status.Succeeded
                       : _resultCanceled ? Progress.Status.Canceled
                       : Progress.Status.Failed;
                Progress.Finish(_progressId, st);
            }
            _progressId = -1;

            if (_resultSuccess)
            {
                var s = BackupSettings.Instance;
                s.lastBackupUtcTicks = DateTime.UtcNow.Ticks;
                s.Save();
                Debug.Log("[Unity Auto Backup] " + _resultMessage);
            }
            else if (_resultCanceled) Debug.Log("[Unity Auto Backup] " + _resultMessage);
            else Debug.LogError("[Unity Auto Backup] " + _resultMessage);

            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            Finished?.Invoke();

            if (QuitPending)
            {
                QuitPending = false;
                if (_resultSuccess || _resultCanceled)
                {
                    QuitNow();
                }
                else if (EditorUtility.DisplayDialog("Unity Auto Backup",
                             "종료 전 백업에 실패했습니다. (자세한 내용은 콘솔 참고)\n그래도 종료할까요?",
                             "종료", "종료 안 함"))
                {
                    QuitNow();
                }
            }
        }

        static void Complete(bool success, bool canceled, string message)
        {
            _resultSuccess = success;
            _resultCanceled = canceled;
            _resultMessage = message;
            _done = true;
        }

        // ───────────────────────── 백그라운드 작업 ─────────────────────────

        static void Run(string root, string name, string folder, List<string> excludes,
                        ZipMode mode, HashSet<string> storeExts, int threads, int max, CancellationToken token)
        {
            string tmpPath = null;
            try
            {
                var files = CollectFiles(root, folder, excludes, token);
                long total = files.Sum(f => f.Length);
                var sources = files.Select(f => new ZipSource
                {
                    FullPath = f.FullName,
                    EntryName = Relative(root, f.FullName),
                    Length = f.Length,
                    LastWriteTime = f.LastWriteTime,
                }).ToList();

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string finalPath = Path.Combine(folder, $"{name}_{stamp}.zip");
                for (int n = 2; File.Exists(finalPath); n++)
                    finalPath = Path.Combine(folder, $"{name}_{stamp}_{n}.zip");
                tmpPath = finalPath + ".tmp";

                int count = sources.Count;
                var started = DateTime.UtcNow;
                var zr = FastZip.Create(tmpPath, sources, mode, storeExts, threads,
                    done => _progress = total > 0 ? (float)((double)done / total) : 1f,
                    (i, rel) => _status = $"({i + 1}/{count}) {rel}",
                    token);
                int skipped = zr.Skipped;
                double secs = (DateTime.UtcNow - started).TotalSeconds;

                File.Move(tmpPath, finalPath);
                tmpPath = null;

                _status = "오래된 백업 정리 중...";
                Rotate(folder, name, max);

                long size = new FileInfo(finalPath).Length;
                string msg = $"백업 완료: {Path.GetFileName(finalPath)} ({FormatSize(size)}, 파일 {zr.Written}개, {secs:0.0}초";
                if (skipped > 0) msg += $", 읽기 실패로 건너뜀 {skipped}개";
                Complete(true, false, msg + ")\n" + finalPath);
            }
            catch (OperationCanceledException)
            {
                Complete(false, true, "백업이 취소되었습니다.");
            }
            catch (Exception e)
            {
                Complete(false, false, "백업 실패: " + e);
            }
            finally
            {
                if (tmpPath != null) TryDelete(tmpPath);
            }
        }

        static List<FileInfo> CollectFiles(string root, string backupFolder, List<string> excludes, CancellationToken token)
        {
            var result = new List<FileInfo>();
            string backupFull = Path.GetFullPath(backupFolder).TrimEnd('\\', '/');
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));

            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                FileSystemInfo[] items;
                try { items = dir.GetFileSystemInfos(); }
                catch { continue; }

                foreach (var item in items)
                {
                    string rel = Relative(root, item.FullName);
                    if (IsExcluded(rel, excludes)) continue;

                    if (item is DirectoryInfo d)
                    {
                        // 심볼릭 링크/정션은 무한 루프 방지를 위해 건너뜀
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (string.Equals(d.FullName.TrimEnd('\\', '/'), backupFull, StringComparison.OrdinalIgnoreCase)) continue;
                        stack.Push(d);
                    }
                    else if (item is FileInfo f)
                    {
                        if (f.Name.EndsWith(".zip.tmp", StringComparison.OrdinalIgnoreCase)) continue;
                        result.Add(f);
                    }
                }
            }
            return result;
        }

        static bool IsExcluded(string rel, List<string> excludes)
        {
            foreach (var ex in excludes)
            {
                if (ex.StartsWith("*."))
                {
                    if (rel.EndsWith(ex.Substring(1), StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (string.Equals(rel, ex, StringComparison.OrdinalIgnoreCase) ||
                         rel.StartsWith(ex + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        static void Rotate(string folder, string name, int max)
        {
            // 강제 종료 등으로 남은 임시 파일 정리
            try
            {
                foreach (var t in new DirectoryInfo(folder).GetFiles(name + "_*.zip.tmp"))
                    if ((DateTime.Now - t.LastWriteTime).TotalHours > 1) TryDelete(t.FullName);
            }
            catch { /* 무시 */ }

            if (max <= 0) return;
            foreach (var old in GetBackups(folder, name).Skip(max))
                TryDelete(old.FullName);
        }

        static string Relative(string root, string full) =>
            full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 무시 */ }
        }

        public static string FormatSize(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.##} {u[i]}";
        }
    }
}
