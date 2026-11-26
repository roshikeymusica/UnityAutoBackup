using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
// UnityEngine.CompressionLevel 과 이름이 겹쳐서 별칭 사용
using ZipLevel = System.IO.Compression.CompressionLevel;

namespace Roshikey.UnityAutoBackup
{
    [InitializeOnLoad]
    internal static class BackupRunner
    {
        public static bool IsRunning { get; private set; }
        public static float CurrentProgress => Mathf.Clamp01(_progress);
        public static string CurrentStatus => _status ?? "";
        public static event Action Finished;

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
            if (!IsRunning) return true;
            bool quit = EditorUtility.DisplayDialog("Unity Auto Backup",
                "백업이 진행 중입니다. 백업을 취소하고 종료할까요?", "취소하고 종료", "기다리기");
            if (quit) Cancel();
            return quit;
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
            var level = s.fastestCompression ? ZipLevel.Fastest : ZipLevel.Optimal;
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

            Task.Run(() => Run(root, name, folder, excludes, level, max, token));
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
                        ZipLevel level, int max, CancellationToken token)
        {
            string tmpPath = null;
            try
            {
                var files = CollectFiles(root, folder, excludes, token);
                long total = files.Sum(f => f.Length);

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string finalPath = Path.Combine(folder, $"{name}_{stamp}.zip");
                for (int n = 2; File.Exists(finalPath); n++)
                    finalPath = Path.Combine(folder, $"{name}_{stamp}_{n}.zip");
                tmpPath = finalPath + ".tmp";

                long done = 0;
                int skipped = 0;
                var buffer = new byte[1 << 20];

                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    for (int i = 0; i < files.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var fi = files[i];
                        string rel = Relative(root, fi.FullName);
                        _status = $"({i + 1}/{files.Count}) {rel}";

                        FileStream src;
                        try
                        {
                            // 다른 프로그램이 열고 있는 파일도 최대한 읽기
                            src = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read,
                                                 FileShare.ReadWrite | FileShare.Delete);
                        }
                        catch (Exception)
                        {
                            skipped++;
                            done += fi.Length;
                            continue;
                        }

                        using (src)
                        {
                            var entry = zip.CreateEntry(rel, level);
                            try { entry.LastWriteTime = fi.LastWriteTime; } catch { /* 1980년 이전 날짜 등 */ }
                            using (var dst = entry.Open())
                            {
                                try
                                {
                                    int read;
                                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                                    {
                                        token.ThrowIfCancellationRequested();
                                        dst.Write(buffer, 0, read);
                                        done += read;
                                        _progress = total > 0 ? (float)((double)done / total) : 1f;
                                    }
                                }
                                catch (IOException) { skipped++; }
                            }
                        }
                    }
                }

                File.Move(tmpPath, finalPath);
                tmpPath = null;

                _status = "오래된 백업 정리 중...";
                Rotate(folder, name, max);

                long size = new FileInfo(finalPath).Length;
                string msg = $"백업 완료: {Path.GetFileName(finalPath)} ({FormatSize(size)}, 파일 {files.Count - skipped}개";
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
