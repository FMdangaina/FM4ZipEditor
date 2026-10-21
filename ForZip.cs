using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Fm4Zip
{
    public delegate bool ProgressFn(double fraction, string text);

    public static class Crc32
    {
        static readonly uint[] T = Make();
        static uint[] Make()
        {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }
        public static uint Of(byte[] b)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < b.Length; i++) crc = T[(crc ^ b[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }

    // Wrapper for xcompress32.dll (XMem LZX, ZIP method 21 used by Forza Motorsport)
    public static class XMem
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateFn(int codec, int param, int flags, out IntPtr ctx);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CodeFn(IntPtr ctx, byte[] dst, ref int dstLen, byte[] src, int srcLen);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void DestroyFn(IntPtr ctx);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        static extern IntPtr GetProcAddress(IntPtr h, string name);

        const int LZX = 1;
        static CreateFn createComp, createDecomp;
        static CodeFn comp, decomp;
        static DestroyFn destroyComp, destroyDecomp;
        static readonly object Lock = new object();

        public static bool Available { get; private set; }
        public static string Error { get; private set; }

        // Looks for the dll next to the exe, otherwise uses the one embedded as a resource.
        public static void Init()
        {
            try
            {
                string path = null;
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xcompress32.dll");
                if (File.Exists(local)) path = local;
                else
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    using (Stream s = asm.GetManifestResourceStream("xcompress32.dll"))
                    {
                        if (s == null) throw new FileNotFoundException("xcompress32.dll not found (neither next to the exe nor embedded).");
                        string dir = Path.Combine(Path.GetTempPath(), "FM4ZipEditor");
                        Directory.CreateDirectory(dir);
                        path = Path.Combine(dir, "xcompress32.dll");
                        if (!File.Exists(path) || new FileInfo(path).Length != s.Length)
                        {
                            try
                            {
                                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                                    s.CopyTo(fs);
                            }
                            catch (IOException) { /* già in uso da un'altra istanza: va bene */ }
                        }
                    }
                }
                if (IntPtr.Size != 4)
                    throw new BadImageFormatException("The program must run as 32-bit to use xcompress32.dll.");
                IntPtr h = LoadLibraryW(path);
                if (h == IntPtr.Zero)
                    throw new DllNotFoundException("Unable to load xcompress32.dll (error " + Marshal.GetLastWin32Error() + ").");
                createComp = Get<CreateFn>(h, "XMemCreateCompressionContext");
                comp = Get<CodeFn>(h, "XMemCompress");
                destroyComp = Get<DestroyFn>(h, "XMemDestroyCompressionContext");
                createDecomp = Get<CreateFn>(h, "XMemCreateDecompressionContext");
                decomp = Get<CodeFn>(h, "XMemDecompress");
                destroyDecomp = Get<DestroyFn>(h, "XMemDestroyDecompressionContext");
                Available = true;
            }
            catch (Exception ex)
            {
                Available = false;
                Error = ex.Message;
            }
        }

        static T Get<T>(IntPtr h, string name) where T : class
        {
            IntPtr p = GetProcAddress(h, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException("Function " + name + " not found in xcompress32.dll");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        public static byte[] Compress(byte[] data)
        {
            if (!Available) throw new InvalidOperationException("XMem not available: " + Error);
            lock (Lock)
            {
                IntPtr ctx;
                int r = createComp(LZX, 0, 0, out ctx);
                if (r != 0) throw new InvalidOperationException("XMemCreateCompressionContext: " + r);
                try
                {
                    int dstLen = checked(data.Length * 2 + 4096);
                    byte[] dst = new byte[dstLen];
                    r = comp(ctx, dst, ref dstLen, data, data.Length);
                    if (r != 0) throw new InvalidOperationException("XMemCompress: " + r);
                    byte[] res = new byte[dstLen];
                    Buffer.BlockCopy(dst, 0, res, 0, dstLen);
                    return res;
                }
                finally { destroyComp(ctx); }
            }
        }

        public static byte[] Decompress(byte[] data, int uncompSize)
        {
            if (!Available) throw new InvalidOperationException("XMem not available: " + Error);
            lock (Lock)
            {
                IntPtr ctx;
                int r = createDecomp(LZX, 0, 0, out ctx);
                if (r != 0) throw new InvalidOperationException("XMemCreateDecompressionContext: " + r);
                try
                {
                    byte[] dst = new byte[uncompSize];
                    int dstLen = uncompSize;
                    r = decomp(ctx, dst, ref dstLen, data, data.Length);
                    if (r != 0) throw new InvalidOperationException("XMemDecompress: " + r);
                    if (dstLen != uncompSize)
                    {
                        byte[] res = new byte[dstLen];
                        Buffer.BlockCopy(dst, 0, res, 0, dstLen);
                        return res;
                    }
                    return dst;
                }
                finally { destroyDecomp(ctx); }
            }
        }
    }

    public class ZEntry
    {
        public string Name;
        public ushort VerMade, VerNeed, Flags, Method, DosTime, DosDate, DiskNum, IntAttr;
        public uint Crc, CompSize, UncompSize, ExtAttr;
        public byte[] LocalExtra = new byte[0];
        public byte[] CdExtra = new byte[0];
        public byte[] Comment = new byte[0];
        public long LocalOffset, DataOffset;

        public bool IsDir { get { return Name.EndsWith("/"); } }

        public DateTime Modified
        {
            get
            {
                if (DosDate == 0) return DateTime.MinValue;
                try
                {
                    return new DateTime(((DosDate >> 9) & 0x7F) + 1980, Math.Max(1, (DosDate >> 5) & 0xF),
                        Math.Max(1, DosDate & 0x1F), Math.Min(23, (DosTime >> 11) & 0x1F),
                        Math.Min(59, (DosTime >> 5) & 0x3F), Math.Min(59, (DosTime & 0x1F) * 2));
                }
                catch { return DateTime.MinValue; }
            }
        }

        public ZEntry Clone() { return (ZEntry)MemberwiseClone(); }

        public static void ToDos(DateTime d, out ushort date, out ushort time)
        {
            if (d.Year < 1980) d = new DateTime(1980, 1, 1);
            if (d.Year > 2107) d = new DateTime(2107, 12, 31);
            date = (ushort)(((d.Year - 1980) << 9) | (d.Month << 5) | d.Day);
            time = (ushort)((d.Hour << 11) | (d.Minute << 5) | (d.Second / 2));
        }
    }

    // Item of the rewrite plan: either an existing entry (copied as is, possibly renamed)
    // or a new file from disk to compress.
    public class PlanItem
    {
        public ZEntry Src;
        public string Name;       // new name (or name of the new file)
        public string FilePath;   // for new files
        public int Method = -1;   // for new files: 0, 8, 21
    }

    public class ZArchive
    {
        public string Path;
        public List<ZEntry> Entries = new List<ZEntry>();
        byte[] eocdComment = new byte[0];

        public static void CreateEmpty(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] eocd = new byte[22];
                eocd[0] = 0x50; eocd[1] = 0x4B; eocd[2] = 5; eocd[3] = 6;
                fs.Write(eocd, 0, eocd.Length);
            }
        }

        public static ZArchive Open(string path)
        {
            ZArchive a = new ZArchive();
            a.Path = path;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (BinaryReader br = new BinaryReader(fs))
            {
                long size = fs.Length;
                if (size < 22) throw new InvalidDataException("The file is not a valid ZIP archive.");
                int tail = (int)Math.Min(size, 65536 + 22);
                fs.Seek(size - tail, SeekOrigin.Begin);
                byte[] tb = br.ReadBytes(tail);
                int pos = -1;
                for (int i = tb.Length - 22; i >= 0; i--)
                    if (tb[i] == 0x50 && tb[i + 1] == 0x4B && tb[i + 2] == 5 && tb[i + 3] == 6) { pos = i; break; }
                if (pos < 0) throw new InvalidDataException("The file is not a valid ZIP archive (end of central directory not found).");

                int total = BitConverter.ToUInt16(tb, pos + 10);
                uint cdOff = BitConverter.ToUInt32(tb, pos + 16);
                int cl = BitConverter.ToUInt16(tb, pos + 20);
                int avail = Math.Max(0, Math.Min(cl, tb.Length - pos - 22));
                a.eocdComment = new byte[avail];
                Buffer.BlockCopy(tb, pos + 22, a.eocdComment, 0, avail);

                long eocdAbs = size - tail + pos;
                if (cdOff > eocdAbs) throw new InvalidDataException("Corrupted ZIP archive.");
                fs.Seek(cdOff, SeekOrigin.Begin);
                byte[] cd = br.ReadBytes((int)(eocdAbs - cdOff));

                int p = 0;
                for (int n = 0; n < total; n++)
                {
                    if (p + 46 > cd.Length || BitConverter.ToUInt32(cd, p) != 0x02014B50) break;
                    ZEntry e = new ZEntry();
                    e.VerMade = BitConverter.ToUInt16(cd, p + 4);
                    e.VerNeed = BitConverter.ToUInt16(cd, p + 6);
                    e.Flags = BitConverter.ToUInt16(cd, p + 8);
                    e.Method = BitConverter.ToUInt16(cd, p + 10);
                    e.DosTime = BitConverter.ToUInt16(cd, p + 12);
                    e.DosDate = BitConverter.ToUInt16(cd, p + 14);
                    e.Crc = BitConverter.ToUInt32(cd, p + 16);
                    e.CompSize = BitConverter.ToUInt32(cd, p + 20);
                    e.UncompSize = BitConverter.ToUInt32(cd, p + 24);
                    int nl = BitConverter.ToUInt16(cd, p + 28);
                    int el = BitConverter.ToUInt16(cd, p + 30);
                    int cm = BitConverter.ToUInt16(cd, p + 32);
                    e.DiskNum = BitConverter.ToUInt16(cd, p + 34);
                    e.IntAttr = BitConverter.ToUInt16(cd, p + 36);
                    e.ExtAttr = BitConverter.ToUInt32(cd, p + 38);
                    e.LocalOffset = BitConverter.ToUInt32(cd, p + 42);
                    e.Name = Encoding.UTF8.GetString(cd, p + 46, nl).Replace('\\', '/');
                    e.CdExtra = Slice(cd, p + 46 + nl, el);
                    e.Comment = Slice(cd, p + 46 + nl + el, cm);

                    fs.Seek(e.LocalOffset + 26, SeekOrigin.Begin);
                    int lnl = br.ReadUInt16();
                    int lel = br.ReadUInt16();
                    fs.Seek(e.LocalOffset + 30 + lnl, SeekOrigin.Begin);
                    e.LocalExtra = br.ReadBytes(lel);
                    e.DataOffset = e.LocalOffset + 30 + lnl + lel;
                    a.Entries.Add(e);
                    p += 46 + nl + el + cm;
                }
            }
            return a;
        }

        static byte[] Slice(byte[] b, int off, int len)
        {
            byte[] r = new byte[len];
            Buffer.BlockCopy(b, off, r, 0, len);
            return r;
        }

        public byte[] ReadRaw(ZEntry e)
        {
            using (FileStream fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(e.DataOffset, SeekOrigin.Begin);
                byte[] b = new byte[e.CompSize];
                int got = 0;
                while (got < b.Length)
                {
                    int r = fs.Read(b, got, b.Length - got);
                    if (r <= 0) throw new EndOfStreamException("Truncated archive.");
                    got += r;
                }
                return b;
            }
        }

        public byte[] Read(ZEntry e)
        {
            byte[] raw = ReadRaw(e);
            switch (e.Method)
            {
                case 0: return raw;
                case 8:
                    using (DeflateStream ds = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress))
                    {
                        MemoryStream ms = new MemoryStream((int)e.UncompSize);
                        ds.CopyTo(ms);
                        return ms.ToArray();
                    }
                case 21:
                    return XMem.Decompress(raw, (int)e.UncompSize);
                default:
                    throw new NotSupportedException("Compression method " + e.Method + " is not supported.");
            }
        }

        public void Extract(ZEntry e, string destFile)
        {
            string dir = System.IO.Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            byte[] data = Read(e);
            File.WriteAllBytes(destFile, data);
            DateTime m = e.Modified;
            if (m != DateTime.MinValue) { try { File.SetLastWriteTime(destFile, m); } catch { } }
        }

        // Returns null if the file is intact, otherwise the error message.
        public string Test(ZEntry e)
        {
            try
            {
                byte[] d = Read(e);
                if (d.Length != e.UncompSize) return "wrong size";
                if (Crc32.Of(d) != e.Crc) return "wrong CRC";
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static string MethodName(int m)
        {
            if (m == 0) return "Stored";
            if (m == 8) return "Deflate";
            if (m == 21) return "XMem LZX";
            return "Method " + m;
        }

        // Rewrites the whole archive following the plan: temp file, then replace.
        public void Rewrite(List<PlanItem> plan, ProgressFn prog)
        {
            long total = 0;
            foreach (PlanItem pi in plan)
                total += pi.Src != null ? pi.Src.CompSize : new FileInfo(pi.FilePath).Length;
            if (total < 1) total = 1;
            long done = 0;

            // new files get the same "version" fields as the entries already present
            ushort verMade = 10, verNeed = 10;
            if (Entries.Count > 0) { verMade = Entries[0].VerMade; verNeed = Entries[0].VerNeed; }

            string tmp = Path + ".tmp";
            List<ZEntry> result = new List<ZEntry>();
            try
            {
                using (FileStream inp = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (FileStream outp = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[1 << 20];
                    foreach (PlanItem pi in plan)
                    {
                        string label = pi.Name ?? pi.Src.Name;
                        if (prog != null && !prog(done / (double)total, label)) throw new OperationCanceledException();

                        ZEntry ne;
                        byte[] newData = null;
                        if (pi.Src != null)
                        {
                            ne = pi.Src.Clone();
                            if (pi.Name != null) ne.Name = pi.Name;
                        }
                        else
                        {
                            byte[] raw = File.ReadAllBytes(pi.FilePath);
                            ne = new ZEntry();
                            ne.Name = pi.Name;
                            ne.VerMade = verMade; ne.VerNeed = verNeed;
                            ne.Crc = Crc32.Of(raw);
                            ne.UncompSize = (uint)raw.Length;
                            int m = pi.Method;
                            if (raw.Length == 0) m = 0;
                            if (m == 21) newData = XMem.Compress(raw);
                            else if (m == 8) newData = DeflateRaw(raw);
                            else { m = 0; newData = raw; }
                            ne.Method = (ushort)m;
                            ne.CompSize = (uint)newData.Length;
                            ushort dd, dt;
                            ZEntry.ToDos(File.GetLastWriteTime(pi.FilePath), out dd, out dt);
                            ne.DosDate = dd; ne.DosTime = dt;
                            ne.CdExtra = new byte[0];
                            ne.LocalExtra = new byte[0];
                            ne.Comment = new byte[0];
                        }
                        ne.Flags = (ushort)(ne.Flags & ~0x08);
                        byte[] nameB = Encoding.UTF8.GetBytes(ne.Name);
                        if (pi.Src == null || pi.Name != null)
                        {
                            bool ascii = true;
                            foreach (byte b in nameB) if (b > 127) { ascii = false; break; }
                            if (!ascii) ne.Flags |= 0x800;
                        }

                        ne.LocalOffset = outp.Position;
                        byte[] lh = new byte[30];
                        Put32(lh, 0, 0x04034B50);
                        Put16(lh, 4, ne.VerNeed);
                        Put16(lh, 6, ne.Flags);
                        Put16(lh, 8, ne.Method);
                        Put16(lh, 10, ne.DosTime);
                        Put16(lh, 12, ne.DosDate);
                        Put32(lh, 14, ne.Crc);
                        Put32(lh, 18, ne.CompSize);
                        Put32(lh, 22, ne.UncompSize);
                        Put16(lh, 26, (ushort)nameB.Length);
                        Put16(lh, 28, (ushort)ne.LocalExtra.Length);
                        outp.Write(lh, 0, lh.Length);
                        outp.Write(nameB, 0, nameB.Length);
                        outp.Write(ne.LocalExtra, 0, ne.LocalExtra.Length);
                        ne.DataOffset = outp.Position;

                        if (newData != null)
                        {
                            outp.Write(newData, 0, newData.Length);
                            done += pi.FilePath != null ? new FileInfo(pi.FilePath).Length : 0;
                        }
                        else
                        {
                            inp.Seek(pi.Src.DataOffset, SeekOrigin.Begin);
                            long rem = pi.Src.CompSize;
                            while (rem > 0)
                            {
                                int r = inp.Read(buf, 0, (int)Math.Min(buf.Length, rem));
                                if (r <= 0) throw new EndOfStreamException("Truncated archive.");
                                outp.Write(buf, 0, r);
                                rem -= r;
                            }
                            done += pi.Src.CompSize;
                        }
                        result.Add(ne);
                    }

                    long cdStart = outp.Position;
                    MemoryStream cd = new MemoryStream();
                    foreach (ZEntry e in result)
                    {
                        byte[] nameB = Encoding.UTF8.GetBytes(e.Name);
                        byte[] extra = BuildCdExtra(e);
                        byte[] h = new byte[46];
                        Put32(h, 0, 0x02014B50);
                        Put16(h, 4, e.VerMade);
                        Put16(h, 6, e.VerNeed);
                        Put16(h, 8, e.Flags);
                        Put16(h, 10, e.Method);
                        Put16(h, 12, e.DosTime);
                        Put16(h, 14, e.DosDate);
                        Put32(h, 16, e.Crc);
                        Put32(h, 20, e.CompSize);
                        Put32(h, 24, e.UncompSize);
                        Put16(h, 28, (ushort)nameB.Length);
                        Put16(h, 30, (ushort)extra.Length);
                        Put16(h, 32, (ushort)e.Comment.Length);
                        Put16(h, 34, e.DiskNum);
                        Put16(h, 36, e.IntAttr);
                        Put32(h, 38, e.ExtAttr);
                        Put32(h, 42, (uint)e.LocalOffset);
                        cd.Write(h, 0, h.Length);
                        cd.Write(nameB, 0, nameB.Length);
                        cd.Write(extra, 0, extra.Length);
                        cd.Write(e.Comment, 0, e.Comment.Length);
                        e.CdExtra = extra;
                    }
                    byte[] cdb = cd.ToArray();
                    outp.Write(cdb, 0, cdb.Length);

                    byte[] eo = new byte[22];
                    Put32(eo, 0, 0x06054B50);
                    Put16(eo, 8, (ushort)result.Count);
                    Put16(eo, 10, (ushort)result.Count);
                    Put32(eo, 12, (uint)cdb.Length);
                    Put32(eo, 16, (uint)cdStart);
                    Put16(eo, 20, (ushort)eocdComment.Length);
                    outp.Write(eo, 0, eo.Length);
                    outp.Write(eocdComment, 0, eocdComment.Length);
                }

                File.Delete(Path);
                File.Move(tmp, Path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
            Entries = result;
        }

        // Forza's central directory carries an extra field 0x1123 with the absolute data offset:
        // it must be recomputed on every rewrite. Other extra fields are preserved.
        static byte[] BuildCdExtra(ZEntry e)
        {
            MemoryStream ms = new MemoryStream();
            byte[] ex = e.CdExtra ?? new byte[0];
            int p = 0;
            while (p + 4 <= ex.Length)
            {
                ushort id = BitConverter.ToUInt16(ex, p);
                int len = BitConverter.ToUInt16(ex, p + 2);
                if (p + 4 + len > ex.Length) break;
                if (id != 0x1123) ms.Write(ex, p, 4 + len);
                p += 4 + len;
            }
            byte[] f = new byte[8];
            Put16(f, 0, 0x1123);
            Put16(f, 2, 4);
            Put32(f, 4, (uint)e.DataOffset);
            ms.Write(f, 0, 8);
            return ms.ToArray();
        }

        static byte[] DeflateRaw(byte[] raw)
        {
            MemoryStream ms = new MemoryStream();
            using (DeflateStream ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
                ds.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        static void Put16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static void Put32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
    }
}
