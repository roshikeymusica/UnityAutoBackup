using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace Roshikey.UnityAutoBackup
{
    internal enum ZipMode { Store = 0, Fast = 1, Optimal = 2 }

    internal sealed class ZipSource
    {
        public string FullPath;
        public string EntryName;
        public long Length;
        public DateTime LastWriteTime;
    }

    internal sealed class FastZipResult
    {
        public int Written;
        public int Skipped;
    }

    /// <summary>
    /// 멀티스레드 zip 작성기.
    /// 작은 파일은 여러 스레드가 동시에 압축하고, 쓰기는 한 스레드가 순서대로 처리.
    /// 큰 파일(32MB 초과)은 메모리 절약을 위해 쓰기 스레드가 바로 스트리밍.
    /// Zip64 지원(4GB 초과 파일/아카이브, 65535개 초과 파일), 한글 파일명(UTF-8) 지원.
    /// </summary>
    internal static class FastZip
    {
        const long InlineThreshold = 32L << 20;   // 이보다 큰 파일은 스트리밍
        const long MemoryBudget = 256L << 20;     // 동시에 메모리에 올려둘 최대 바이트
        const uint Max32 = 0xFFFFFFFF;

        sealed class Prepared
        {
            public bool Failed;
            public bool Inline;
            public ushort Method;
            public uint Crc;
            public long USize;
            public byte[] Data;
            public int DataLength;
            public long Reserved;
        }

        sealed class CdEntry
        {
            public byte[] Name;
            public ushort Method;
            public uint DosTime;
            public uint Crc;
            public long CSize;
            public long USize;
            public long Offset;
            public bool ForceZip64;
        }

        public static FastZipResult Create(string outPath, IList<ZipSource> files, ZipMode mode,
            HashSet<string> storeExtensions, int threads, Action<long> onBytes, Action<int, string> onFile,
            CancellationToken token)
        {
            int n = files.Count;
            var slots = new Prepared[n];
            var lk = new object();
            int nextIndex = 0;
            int writerIndex = 0;
            long inflight = 0;
            bool stop = false;
            Exception fatal = null;
            var level = mode == ZipMode.Optimal ? CompressionLevel.Optimal : CompressionLevel.Fastest;

            Func<ZipSource, bool> shouldStore = src =>
            {
                if (mode == ZipMode.Store) return true;
                string ext = Path.GetExtension(src.EntryName);
                return ext.Length > 1 && storeExtensions.Contains(ext.Substring(1).ToLowerInvariant());
            };

            ThreadStart workerBody = () =>
            {
                while (true)
                {
                    int idx;
                    long reserve;
                    lock (lk)
                    {
                        if (stop || nextIndex >= n) return;
                        idx = nextIndex++;
                        var src0 = files[idx];
                        bool inline0 = src0.Length > InlineThreshold;
                        reserve = inline0 ? 0 : Math.Max(src0.Length, 1);
                        // 메모리 예산 초과 시 대기 (단, 쓰기 스레드가 기다리는 맨 앞 파일은 항상 진행)
                        while (!stop && reserve > 0 && inflight + reserve > MemoryBudget && idx != writerIndex)
                            Monitor.Wait(lk, 100);
                        if (stop) return;
                        inflight += reserve;
                    }

                    Prepared p;
                    try
                    {
                        p = Prepare(files[idx], shouldStore(files[idx]), level, token);
                    }
                    catch (OperationCanceledException)
                    {
                        lock (lk) { stop = true; Monitor.PulseAll(lk); }
                        return;
                    }
                    catch (Exception e)
                    {
                        lock (lk) { fatal = e; stop = true; Monitor.PulseAll(lk); }
                        return;
                    }
                    p.Reserved = reserve;

                    lock (lk)
                    {
                        slots[idx] = p;
                        Monitor.PulseAll(lk);
                    }
                }
            };

            int workerCount = Math.Max(1, threads);
            var workers = new List<Thread>();
            for (int i = 0; i < workerCount; i++)
            {
                var t = new Thread(workerBody) { IsBackground = true, Name = "UnityAutoBackup Zip " + i };
                workers.Add(t);
                t.Start();
            }

            var result = new FastZipResult();
            var cd = new List<CdEntry>(n);
            long bytesDone = 0;
            var utf8 = new UTF8Encoding(false);

            try
            {
                using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
                using (var bw = new BinaryWriter(fs, utf8, true))
                {
                    var streamBuf = new byte[1 << 20];

                    for (int i = 0; i < n; i++)
                    {
                        Prepared p;
                        lock (lk)
                        {
                            while ((p = slots[i]) == null)
                            {
                                if (fatal != null) throw new IOException("압축 스레드 오류", fatal);
                                token.ThrowIfCancellationRequested();
                                if (stop) throw new OperationCanceledException();
                                Monitor.Wait(lk, 100);
                            }
                            slots[i] = null;
                        }

                        var src = files[i];
                        if (onFile != null) onFile(i, src.EntryName);

                        if (p.Failed)
                        {
                            result.Skipped++;
                            bytesDone += src.Length;
                        }
                        else if (p.Inline)
                        {
                            if (WriteInline(fs, bw, src, shouldStore(src), level, streamBuf, cd, utf8, token,
                                    b => { if (onBytes != null) onBytes(bytesDone + b); }))
                                result.Written++;
                            else
                                result.Skipped++;
                            bytesDone += src.Length;
                        }
                        else
                        {
                            var e = new CdEntry
                            {
                                Name = utf8.GetBytes(src.EntryName),
                                Method = p.Method,
                                DosTime = ToDos(src.LastWriteTime),
                                Crc = p.Crc,
                                CSize = p.DataLength,
                                USize = p.USize,
                                Offset = fs.Position,
                            };
                            WriteLocalHeader(bw, e, false);
                            fs.Write(p.Data, 0, p.DataLength);
                            cd.Add(e);
                            result.Written++;
                            bytesDone += src.Length;
                        }

                        lock (lk)
                        {
                            inflight -= p.Reserved;
                            writerIndex = i + 1;
                            Monitor.PulseAll(lk);
                        }
                        if (onBytes != null) onBytes(bytesDone);
                        token.ThrowIfCancellationRequested();
                    }

                    WriteCentralDirectory(fs, bw, cd);
                }
            }
            finally
            {
                lock (lk) { stop = true; Monitor.PulseAll(lk); }
                foreach (var t in workers) t.Join(5000);
            }

            return result;
        }

        // ───────────── 작업 스레드: 파일 하나 읽고 압축 ─────────────

        static Prepared Prepare(ZipSource src, bool store, CompressionLevel level, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (src.Length > InlineThreshold) return new Prepared { Inline = true };

            byte[] raw;
            int total = 0;
            try
            {
                using (var f = new FileStream(src.FullPath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
                {
                    long len = Math.Min(f.Length, InlineThreshold);
                    raw = new byte[len];
                    while (total < len)
                    {
                        int r = f.Read(raw, total, (int)(len - total));
                        if (r <= 0) break;
                        total += r;
                    }
                }
            }
            catch (IOException) { return new Prepared { Failed = true }; }
            catch (UnauthorizedAccessException) { return new Prepared { Failed = true }; }

            uint crc = Crc32.Update(0, raw, 0, total);

            if (!store && total > 0)
            {
                var ms = new MemoryStream(total / 2 + 64);
                using (var ds = new DeflateStream(ms, level, true))
                    ds.Write(raw, 0, total);
                // 압축해도 안 줄어들면 그냥 원본 저장
                if (ms.Length < total)
                    return new Prepared { Method = 8, Crc = crc, USize = total, Data = ms.GetBuffer(), DataLength = (int)ms.Length };
            }
            return new Prepared { Method = 0, Crc = crc, USize = total, Data = raw, DataLength = total };
        }

        // ───────────── 쓰기 스레드: 큰 파일 스트리밍 ─────────────

        static bool WriteInline(FileStream fs, BinaryWriter bw, ZipSource src, bool store, CompressionLevel level,
            byte[] buf, List<CdEntry> cd, Encoding utf8, CancellationToken token, Action<long> progress)
        {
            FileStream input;
            try
            {
                input = new FileStream(src.FullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }

            using (input)
            {
                var e = new CdEntry
                {
                    Name = utf8.GetBytes(src.EntryName),
                    Method = (ushort)(store ? 0 : 8),
                    DosTime = ToDos(src.LastWriteTime),
                    Offset = fs.Position,
                    ForceZip64 = true,
                };
                WriteLocalHeader(bw, e, true);
                long dataStart = fs.Position;

                uint crc = 0;
                long usize = 0;
                Stream sink = store ? (Stream)new NonClosing(fs) : new DeflateStream(new NonClosing(fs), level, false);
                using (sink)
                {
                    int r;
                    while ((r = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        crc = Crc32.Update(crc, buf, 0, r);
                        sink.Write(buf, 0, r);
                        usize += r;
                        progress(usize);
                    }
                }
                long end = fs.Position;
                e.Crc = crc;
                e.USize = usize;
                e.CSize = end - dataStart;

                // 로컬 헤더의 CRC와 Zip64 크기 값을 나중에 채워넣기
                fs.Position = e.Offset + 14;
                bw.Write(e.Crc);
                fs.Position = e.Offset + 30 + e.Name.Length + 4;
                bw.Write(e.USize);
                bw.Write(e.CSize);
                fs.Position = end;

                cd.Add(e);
                return true;
            }
        }

        // ───────────── zip 구조 쓰기 ─────────────

        static void WriteLocalHeader(BinaryWriter bw, CdEntry e, bool zip64)
        {
            bw.Write(0x04034b50u);
            bw.Write((ushort)(zip64 ? 45 : 20));
            bw.Write((ushort)0x0800);           // UTF-8 파일명
            bw.Write(e.Method);
            bw.Write(e.DosTime);
            bw.Write(e.Crc);
            bw.Write(zip64 ? Max32 : (uint)e.CSize);
            bw.Write(zip64 ? Max32 : (uint)e.USize);
            bw.Write((ushort)e.Name.Length);
            bw.Write((ushort)(zip64 ? 20 : 0));
            bw.Write(e.Name);
            if (zip64)
            {
                bw.Write((ushort)0x0001);
                bw.Write((ushort)16);
                bw.Write(e.USize);
                bw.Write(e.CSize);
            }
        }

        static void WriteCentralDirectory(FileStream fs, BinaryWriter bw, List<CdEntry> cd)
        {
            long cdStart = fs.Position;
            foreach (var e in cd)
            {
                bool zU = e.ForceZip64 || e.USize >= Max32;
                bool zC = e.ForceZip64 || e.CSize >= Max32;
                bool zO = e.Offset >= Max32;
                int extra = (zU ? 8 : 0) + (zC ? 8 : 0) + (zO ? 8 : 0);
                if (extra > 0) extra += 4;

                bw.Write(0x02014b50u);
                bw.Write((ushort)45);            // made by: MS-DOS, 4.5
                bw.Write((ushort)(extra > 0 ? 45 : 20));
                bw.Write((ushort)0x0800);
                bw.Write(e.Method);
                bw.Write(e.DosTime);
                bw.Write(e.Crc);
                bw.Write(zC ? Max32 : (uint)e.CSize);
                bw.Write(zU ? Max32 : (uint)e.USize);
                bw.Write((ushort)e.Name.Length);
                bw.Write((ushort)extra);
                bw.Write((ushort)0);             // comment
                bw.Write((ushort)0);             // disk
                bw.Write((ushort)0);             // internal attr
                bw.Write(0u);                    // external attr
                bw.Write(zO ? Max32 : (uint)e.Offset);
                bw.Write(e.Name);
                if (extra > 0)
                {
                    bw.Write((ushort)0x0001);
                    bw.Write((ushort)(extra - 4));
                    if (zU) bw.Write(e.USize);
                    if (zC) bw.Write(e.CSize);
                    if (zO) bw.Write(e.Offset);
                }
            }
            long cdSize = fs.Position - cdStart;
            long count = cd.Count;
            bool z64 = count >= 0xFFFF || cdStart >= Max32 || cdSize >= Max32;

            if (z64)
            {
                long z64Pos = fs.Position;
                bw.Write(0x06064b50u);
                bw.Write(44L);
                bw.Write((ushort)45);
                bw.Write((ushort)45);
                bw.Write(0u);
                bw.Write(0u);
                bw.Write(count);
                bw.Write(count);
                bw.Write(cdSize);
                bw.Write(cdStart);

                bw.Write(0x07064b50u);
                bw.Write(0u);
                bw.Write(z64Pos);
                bw.Write(1u);
            }

            bw.Write(0x06054b50u);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((ushort)(count >= 0xFFFF ? 0xFFFF : count));
            bw.Write((ushort)(count >= 0xFFFF ? 0xFFFF : count));
            bw.Write(cdSize >= Max32 ? Max32 : (uint)cdSize);
            bw.Write(cdStart >= Max32 ? Max32 : (uint)cdStart);
            bw.Write((ushort)0);
        }

        static uint ToDos(DateTime t)
        {
            if (t.Year < 1980) t = new DateTime(1980, 1, 1);
            if (t.Year > 2107) t = new DateTime(2107, 12, 31, 23, 59, 58);
            uint time = (uint)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
            uint date = (uint)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
            return (date << 16) | time;
        }

        /// <summary>DeflateStream을 닫아도 아래 FileStream은 안 닫히게 하는 래퍼</summary>
        sealed class NonClosing : Stream
        {
            readonly Stream _s;
            public NonClosing(Stream s) { _s = s; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _s.Flush();
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => _s.Write(b, o, c);
        }
    }

    internal static class Crc32
    {
        static readonly uint[] T = BuildTable();

        static uint[] BuildTable()
        {
            var t = new uint[8 * 256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            for (int i = 0; i < 256; i++)
                for (int k = 1; k < 8; k++)
                    t[k * 256 + i] = (t[(k - 1) * 256 + i] >> 8) ^ t[t[(k - 1) * 256 + i] & 0xFF];
            return t;
        }

        /// <summary>slice-by-8 CRC32. 이전 결과를 crc로 넘기면 이어서 계산.</summary>
        public static uint Update(uint crc, byte[] b, int off, int len)
        {
            crc = ~crc;
            while (len >= 8)
            {
                uint one = crc ^ (uint)(b[off] | b[off + 1] << 8 | b[off + 2] << 16 | b[off + 3] << 24);
                uint two = (uint)(b[off + 4] | b[off + 5] << 8 | b[off + 6] << 16 | b[off + 7] << 24);
                crc = T[7 * 256 + (one & 0xFF)] ^ T[6 * 256 + ((one >> 8) & 0xFF)] ^
                      T[5 * 256 + ((one >> 16) & 0xFF)] ^ T[4 * 256 + (one >> 24)] ^
                      T[3 * 256 + (two & 0xFF)] ^ T[2 * 256 + ((two >> 8) & 0xFF)] ^
                      T[256 + ((two >> 16) & 0xFF)] ^ T[two >> 24];
                off += 8;
                len -= 8;
            }
            while (len-- > 0) crc = T[(crc ^ b[off++]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }
}
