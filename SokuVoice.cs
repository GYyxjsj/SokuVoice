// SokuVoice - 東方非想天則 1.10a 外部语音托盘启动器
// 纯托盘 + 设置(音量/监控周期/玩家模式/系统音开关) + 实时调试窗口
// 编译: csc /platform:x86 /target:winexe /out:SokuVoice.exe SokuVoice.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SokuVoice
{
    // ===================== Win32 =====================
    internal static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr h, IntPtr baseAddr, byte[] buf, int size, out int read);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetPrivateProfileString(string sec, string key, string def, StringBuilder ret, int size, string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool WritePrivateProfileString(string sec, string key, string val, string path);

        [DllImport("winmm.dll")]
        public static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")]
        public static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")]
        public static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")]
        public static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")]
        public static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")]
        public static extern int waveOutClose(IntPtr h);
        [DllImport("winmm.dll")]
        public static extern int waveOutSetVolume(IntPtr h, int vol);
    }

    // ===================== 日志 =====================
    internal static class Log
    {
        public static readonly string FilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SokuVoice.log");
        private static readonly object sync = new object();
        private static StreamWriter writer;

        public static void Init()
        {
            try
            {
                writer = new StreamWriter(FilePath, false, Encoding.UTF8);
                writer.AutoFlush = true;
                writer.WriteLine("==== SokuVoice 日志 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");
            }
            catch { writer = null; }
        }

        public static void W(string msg)
        {
            StreamWriter w = writer;
            if (w == null) return;
            try { lock (sync) { w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg); } }
            catch { }
        }

        public static void Close()
        {
            lock (sync) { if (writer != null) { try { writer.Dispose(); } catch { } writer = null; } }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    // ===================== WAV 解析 =====================
    internal sealed class WavFile
    {
        public WAVEFORMATEX fmt;
        public byte[] data;

        public static bool TryLoad(string path, out WavFile w)
        {
            w = null;
            try
            {
                byte[] all = File.ReadAllBytes(path);
                if (all.Length < 44) return false;
                if (BitConverter.ToInt32(all, 0) != 0x46464952) return false; // RIFF
                int pos = 12;
                WAVEFORMATEX fmt = new WAVEFORMATEX();
                bool gotFmt = false;
                byte[] data = null;
                while (pos + 8 <= all.Length)
                {
                    int id = BitConverter.ToInt32(all, pos);
                    int sz = BitConverter.ToInt32(all, pos + 4);
                    int body = pos + 8;
                    if (sz < 0 || body > all.Length) break;
                    if (id == 0x20746D66) // "fmt "
                    {
                        if (body + 16 > all.Length) break;
                        fmt.wFormatTag = BitConverter.ToUInt16(all, body);
                        fmt.nChannels = BitConverter.ToUInt16(all, body + 2);
                        fmt.nSamplesPerSec = BitConverter.ToUInt32(all, body + 4);
                        fmt.nAvgBytesPerSec = BitConverter.ToUInt32(all, body + 8);
                        fmt.nBlockAlign = BitConverter.ToUInt16(all, body + 12);
                        fmt.wBitsPerSample = BitConverter.ToUInt16(all, body + 14);
                        fmt.cbSize = 0;
                        gotFmt = true;
                    }
                    else if (id == 0x61746164) // "data"
                    {
                        int take = Math.Min(sz, all.Length - body);
                        if (take > 0) { data = new byte[take]; Array.Copy(all, body, data, 0, take); }
                    }
                    pos = body + sz + (sz & 1);
                }
                if (!gotFmt || data == null || fmt.wFormatTag != 1) return false;
                w = new WavFile();
                w.fmt = fmt;
                w.data = data;
                return true;
            }
            catch { return false; }
        }
    }

    // ===================== 音频 =====================
    internal static class Audio
    {
        public static int PctToVol(int pct)
        {
            if (pct < 0) pct = 0; if (pct > 100) pct = 100;
            int v = (int)(pct / 100.0 * 65535.0);
            return v | (v << 16);
        }

        public static void OneShot(string path, int vol)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Log.W("缺失音频: " + path); return; }
            Log.W("播放: " + Path.GetFileName(path));
            Thread t = new Thread(delegate() { PlayOnce(path, vol); });
            t.IsBackground = true;
            t.Start();
        }

        // 依次播完再放下一条(挂合 P1→P2)，不重叠
        public static void OneShotSequence(string[] paths, int vol)
        {
            string names = "";
            for (int i = 0; i < paths.Length; i++)
                if (!string.IsNullOrEmpty(paths[i]) && File.Exists(paths[i]))
                    names += (names.Length > 0 ? " → " : "") + Path.GetFileName(paths[i]);
            if (names.Length == 0) { Log.W("挂合无可用音频"); return; }
            Log.W("顺序播放: " + names);
            Thread t = new Thread(delegate()
            {
                for (int i = 0; i < paths.Length; i++)
                    if (!string.IsNullOrEmpty(paths[i]) && File.Exists(paths[i])) PlayOnce(paths[i], vol);
            });
            t.IsBackground = true;
            t.Start();
        }

        private static void PlayOnce(string path, int vol)
        {
            WavFile w;
            if (!WavFile.TryLoad(path, out w)) { Log.W("WAV解析失败: " + Path.GetFileName(path)); return; }
            IntPtr h;
            int err = Native.waveOutOpen(out h, -1, ref w.fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (err != 0) { Log.W("waveOutOpen失败 err=" + err + " " + Path.GetFileName(path)); return; }
            Native.waveOutSetVolume(h, PctToVol(vol));
            IntPtr data = Marshal.AllocHGlobal(w.data.Length);
            Marshal.Copy(w.data, 0, data, w.data.Length);
            IntPtr hdr = Marshal.AllocHGlobal(32);
            for (int i = 0; i < 32; i++) Marshal.WriteByte(hdr, i, 0);
            Marshal.WriteIntPtr(hdr, 0, data);
            Marshal.WriteInt32(hdr, 4, w.data.Length);
            Native.waveOutPrepareHeader(h, hdr, 32);
            Native.waveOutWrite(h, hdr, 32);
            while ((Marshal.ReadInt32(hdr, 16) & 1) == 0) Thread.Sleep(5); // WHDR_DONE
            Native.waveOutReset(h);
            Native.waveOutUnprepareHeader(h, hdr, 32);
            Native.waveOutClose(h);
            Marshal.FreeHGlobal(hdr);
            Marshal.FreeHGlobal(data);
        }
    }

    // 可循环、可中断的声道（用于 P1/P2 对战动作语音）
    // 每次 Set/Stop 都让 gen 自增，旧线程发现自己已过期就退出，不会误杀新语音、也不会阻塞监控线程
    internal sealed class LoopChannel
    {
        private readonly object sync = new object();
        private IntPtr curH = IntPtr.Zero;
        private int gen;
        public string Current { get; private set; }
        public bool IsLoop { get; private set; }

        public void Set(string path, bool loop, int vol)
        {
            Stop();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Log.W("缺失循环音频: " + path); return; }
            string name = Path.GetFileName(path);
            Log.W((loop ? "循环: " : "声道: ") + name);
            int my;
            lock (sync) { my = gen; Current = path; IsLoop = loop; }
            Thread t = new Thread(delegate() { Run(path, loop, vol, my, name); });
            t.IsBackground = true;
            t.Start();
        }

        private bool Alive(int my) { lock (sync) { return gen == my; } }

        private void Run(string path, bool loop, int vol, int my, string name)
        {
            WavFile w;
            if (!WavFile.TryLoad(path, out w)) { Log.W("循环WAV解析失败: " + name); return; }
            if (!Alive(my)) return;
            IntPtr h;
            int err = Native.waveOutOpen(out h, -1, ref w.fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (err != 0) { Log.W("循环waveOutOpen失败 err=" + err + " " + name); return; }
            lock (sync) { if (gen == my) curH = h; }
            Native.waveOutSetVolume(h, Audio.PctToVol(vol));
            IntPtr data = Marshal.AllocHGlobal(w.data.Length);
            Marshal.Copy(w.data, 0, data, w.data.Length);
            IntPtr hdr = Marshal.AllocHGlobal(32);
            for (int i = 0; i < 32; i++) Marshal.WriteByte(hdr, i, 0);
            Marshal.WriteIntPtr(hdr, 0, data);
            Marshal.WriteInt32(hdr, 4, w.data.Length);
            Native.waveOutPrepareHeader(h, hdr, 32);
            bool started = false;
            do
            {
                if (!Alive(my)) break;
                // 只清 WHDR_DONE(1)，保留 WHDR_PREPARED(0x10)，否则 waveOutWrite 拒绝播放
                Marshal.WriteInt32(hdr, 16, Marshal.ReadInt32(hdr, 16) & ~1);
                int we = Native.waveOutWrite(h, hdr, 32);
                if (we != 0) { Log.W("声道写入失败 err=" + we + " " + name); break; }
                if (!started) { started = true; Log.W("声道开始 " + name); }
                while (Alive(my) && (Marshal.ReadInt32(hdr, 16) & 1) == 0) Thread.Sleep(5);
                if (!Alive(my)) break;
            } while (loop);
            Native.waveOutReset(h);
            Native.waveOutUnprepareHeader(h, hdr, 32);
            lock (sync) { if (curH == h) curH = IntPtr.Zero; }
            Native.waveOutClose(h);
            Marshal.FreeHGlobal(hdr);
            Marshal.FreeHGlobal(data);
            if (started) Log.W("声道结束 " + name);
            lock (sync) { if (gen == my) { Current = null; IsLoop = false; } }
        }

        public void Stop()
        {
            IntPtr h;
            lock (sync)
            {
                gen++;
                h = curH; curH = IntPtr.Zero;
                Current = null; IsLoop = false;
                // 立即掐断正在播放的音频（句柄由工作线程负责关闭）
                if (h != IntPtr.Zero) Native.waveOutReset(h);
            }
        }

        public void SetVolume(int vol)
        {
            lock (sync) { if (curH != IntPtr.Zero) Native.waveOutSetVolume(curH, Audio.PctToVol(vol)); }
        }
    }

    // ===================== 设置 =====================
    internal sealed class Settings
    {
        public string IniPath;
        public int Volume = 80;          // 0-100
        public int PollMs = 20;          // 监控周期
        public int PlayerMode = 0;       // 0=双人 1=仅P1 2=仅P2
        public bool SystemSounds = true; // 系统音开关
        public bool NetplayRestrict = true; // 热帯限制(网战仅己方)
        public bool Enabled = true;
        public string VoiceDir = @"..\th123v";
        public string ProcName = "th123";

        // 相对路径基于 exe 所在目录解析；绝对路径则原样使用
        public string VoiceRoot
        {
            get { return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, VoiceDir)); }
        }

        public Settings()
        {
            IniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SokuVoice.ini");
            Load();
            if (!File.Exists(IniPath)) Save();
        }

        private string Get(string key, string def)
        {
            StringBuilder sb = new StringBuilder(1024);
            Native.GetPrivateProfileString("SokuVoice", key, def, sb, sb.Capacity, IniPath);
            return sb.ToString();
        }
        private int GetInt(string key, int def)
        {
            int v; return int.TryParse(Get(key, def.ToString()), out v) ? v : def;
        }
        private bool GetBool(string key, bool def)
        {
            return Get(key, def ? "1" : "0") == "1";
        }

        public void Load()
        {
            Volume = GetInt("Volume", 80);
            PollMs = GetInt("PollMs", 20);
            PlayerMode = GetInt("PlayerMode", 0);
            SystemSounds = GetBool("SystemSounds", true);
            NetplayRestrict = GetBool("NetplayRestrict", true);
            Enabled = GetBool("Enabled", true);
            VoiceDir = Get("VoiceDir", @"..\th123v");
            ProcName = Get("ProcName", "th123");
            if (PollMs < 1) PollMs = 1; if (PollMs > 500) PollMs = 500;
            if (Volume < 0) Volume = 0; if (Volume > 100) Volume = 100;
        }

        public void Save()
        {
            Native.WritePrivateProfileString("SokuVoice", "Volume", Volume.ToString(), IniPath);
            Native.WritePrivateProfileString("SokuVoice", "PollMs", PollMs.ToString(), IniPath);
            Native.WritePrivateProfileString("SokuVoice", "PlayerMode", PlayerMode.ToString(), IniPath);
            Native.WritePrivateProfileString("SokuVoice", "SystemSounds", SystemSounds ? "1" : "0", IniPath);
            Native.WritePrivateProfileString("SokuVoice", "NetplayRestrict", NetplayRestrict ? "1" : "0", IniPath);
            Native.WritePrivateProfileString("SokuVoice", "Enabled", Enabled ? "1" : "0", IniPath);
            Native.WritePrivateProfileString("SokuVoice", "VoiceDir", VoiceDir, IniPath);
            Native.WritePrivateProfileString("SokuVoice", "ProcName", ProcName, IniPath);
        }
    }

    // ===================== 内存读取 =====================
    internal struct Snapshot
    {
        public bool ok;
        public int scene, mode, time;
        public int battleMgr, p1obj, p2obj;
        public int p1char, p2char;
        public short p1act, p2act;
        public byte p1wins, p2wins;
        public short p1hp, p2hp;
        public int p1a, p2a, p2aAlt; //  live A button (1=fresh press)
    }

    internal sealed class GameReader
    {
        private IntPtr hProc = IntPtr.Zero;
        public int Base = 0x400000;
        public int Pid = 0;
        public bool Attached = false;

        public bool TryAttach(string name)
        {
            Process[] ps;
            try { ps = Process.GetProcessesByName(name); } catch { return false; }
            if (ps.Length == 0) return false;
            Process p = ps[0];
            try { Base = p.MainModule.BaseAddress.ToInt32(); } catch { Base = 0x400000; }
            hProc = Native.OpenProcess(0x0410, false, p.Id); // VM_READ|QUERY_INFORMATION
            if (hProc == IntPtr.Zero) return false;
            Pid = p.Id;
            Attached = true;
            return true;
        }

        public void Detach()
        {
            if (hProc != IntPtr.Zero) Native.CloseHandle(hProc);
            hProc = IntPtr.Zero;
            Attached = false;
            Pid = 0;
        }

        private int StaticAddr(int va) { return Base + (va - 0x400000); }

        private bool ReadMem(int addr, byte[] buf)
        {
            int read;
            return Native.ReadProcessMemory(hProc, (IntPtr)addr, buf, buf.Length, out read) && read == buf.Length;
        }
        // 静态地址读取：返回成败，用于判定连接健康(仅静态地址失败才算断线)
        private bool ReadStaticIntEx(int va, out int val)
        {
            byte[] b = new byte[4];
            bool ok = ReadMem(StaticAddr(va), b);
            val = ok ? BitConverter.ToInt32(b, 0) : 0;
            return ok;
        }
        private int ReadAbsInt(int a) { byte[] b = new byte[4]; if (!ReadMem(a, b)) return 0; return BitConverter.ToInt32(b, 0); }
        private short ReadAbsShort(int a) { byte[] b = new byte[2]; if (!ReadMem(a, b)) return 0; return BitConverter.ToInt16(b, 0); }
        private byte ReadAbsByte(int a) { byte[] b = new byte[1]; if (!ReadMem(a, b)) return 0; return b[0]; }

        public short ReadAbsShortPublic(int a) { return ReadAbsShort(a); }

        public Snapshot Read()
        {
            Snapshot s = new Snapshot();
            bool ok = true;
            ok = ReadStaticIntEx(0x8A0044, out s.scene) & ok;
            ok = ReadStaticIntEx(0x898690, out s.mode) & ok;
            ok = ReadStaticIntEx(0x8985D8, out s.time) & ok;
            ok = ReadStaticIntEx(0x899D10, out s.p1char) & ok;
            ok = ReadStaticIntEx(0x899D30, out s.p2char) & ok;
            ok = ReadStaticIntEx(0x89A288, out s.p1a) & ok;   // 输入簇0x89A248 + 0x40 = P1.A (SokuAPI确认)
            ok = ReadStaticIntEx(0x89A2A8, out s.p2a) & ok;   // P2.A 候选: 紧邻P1 (+0x60)
            ok = ReadStaticIntEx(0x89A2C8, out s.p2aAlt) & ok; // P2.A 候选: 0x40步长 (+0x80)
            ok = ReadStaticIntEx(0x8985E4, out s.battleMgr) & ok;
            // battleMgr 派生指针在菜单/场景切换时可能过期，读取失败按0处理，不影响连接健康
            if (s.battleMgr != 0)
            {
                s.p1obj = ReadAbsInt(s.battleMgr + 0x0C);
                s.p2obj = ReadAbsInt(s.battleMgr + 0x10);
                if (s.p1obj != 0)
                {
                    s.p1act = ReadAbsShort(s.p1obj + 0x13C);
                    s.p1wins = ReadAbsByte(s.p1obj + 0x573);
                    s.p1hp = ReadAbsShort(s.p1obj + 0x184);
                }
                if (s.p2obj != 0)
                {
                    s.p2act = ReadAbsShort(s.p2obj + 0x13C);
                    s.p2wins = ReadAbsByte(s.p2obj + 0x573);
                    s.p2hp = ReadAbsShort(s.p2obj + 0x184);
                }
            }
            s.ok = ok;
            return s;
        }
    }

    // ===================== 语音引擎 =====================
    internal sealed class VoiceEngine
    {
        private readonly Settings cfg;
        private readonly GameReader reader = new GameReader();
        private readonly LoopChannel chP1 = new LoopChannel();
        private readonly LoopChannel chP2 = new LoopChannel();
        private readonly LoopChannel chSel1 = new LoopChannel();
        private readonly LoopChannel chSel2 = new LoopChannel();
        private Thread thread;
        private volatile bool running;
        private readonly Random rng = new Random();

        // 状态
        private bool prevBattleActive = false;
        private int prevTime = 0;
        private int prevScene = -1, prevMode = -1;       // 场景/模式变化日志
        private int logA1 = -1, logA2 = -1, logA2Alt = -1; // 选角A值变化日志
        private int lastWinSum = 0;
        private int announcedRound = 0;
        private bool kakeaiDone = false;
        private bool matchEndHandled = false;
        private bool inSelect = false;
        private int selChar = -1;                 // 己方最近高亮角色
        private int selC1 = -1, selC2 = -1;       // 双方最近角色值(用于退回重选判定)
        private readonly bool[] palPick = new bool[3]; // [1]=P1 [2]=P2 已选中(.2)
        private readonly bool[] palConf = new bool[3]; // [1]=P1 [2]=P2 已确认(.1)
        private string palLogPath = null;         // PaletteShare 日志(确认信号), 没有则为空
        private long palLogPos = 0;
        private bool palLogWarned = false;
        private bool palResolveLogged = false;
        private int prevA = 0;                    // 本机输入A上一帧值(边沿检测)
        private int lastP1Act = -1, lastP2Act = -1;

        // 调试输出
        public Snapshot Last;
        public string Status = "未连接";
        public string PlayingP1 = "";
        public string PlayingP2 = "";
        public bool DbBattleActive = false;
        public bool DbAllowP1 = false, DbAllowP2 = false;
        public int DbLastP1Act = -1, DbLastP2Act = -1;
        public string DbFileP1 = "", DbFileP2 = "";
        public bool DbInSelect = false;
        public int DbA1 = 0, DbA2 = 0, DbA2Alt = 0;
        public string DbSelEvent = "";
        public volatile bool GameExited = false;
        private bool hasAttached = false;
        private int goneCount = 0;
        private int readFailStreak = 0;
        private int lastBeatMs = 0;
        public int ReaderBase { get { return reader.Base; } }
        public int ReaderPid { get { return reader.Pid; } }
        public bool ReaderAttached { get { return reader.Attached; } }

        public VoiceEngine(Settings cfg) { this.cfg = cfg; }

        public void Start()
        {
            if (running) return;
            Log.W("配置: 音量=" + cfg.Volume + " 周期=" + cfg.PollMs + "ms 玩家模式=" + cfg.PlayerMode
                + " 系统音=" + cfg.SystemSounds + " 网战限制=" + cfg.NetplayRestrict + " 启用=" + cfg.Enabled);
            Log.W("语音目录: " + cfg.VoiceRoot + " (存在=" + Directory.Exists(cfg.VoiceRoot) + ")");
            running = true;
            thread = new Thread(new ThreadStart(Loop));
            thread.IsBackground = true;
            thread.Start();
        }
        public void Stop()
        {
            running = false;
            if (thread != null && thread.IsAlive) thread.Join(1000);
            chP1.Stop(); chP2.Stop(); chSel1.Stop(); chSel2.Stop();
            reader.Detach();
        }

        // 进程名被改掉后旧句柄失效: 强制拆连接，让 Tick 立刻按新进程名重找
        public void ForgetProcess()
        {
            reader.Detach();
            hasAttached = false; goneCount = 0; GameExited = false;
            ResetState();
            Status = "等待进程 " + cfg.ProcName + " ...";
        }

        public void ApplyVolume()
        {
            chP1.SetVolume(cfg.Volume);
            chP2.SetVolume(cfg.Volume);
            chSel1.SetVolume(cfg.Volume);
            chSel2.SetVolume(cfg.Volume);
        }

        private void Loop()
        {
            while (running)
            {
                try { Tick(); }
                catch (Exception ex) { Status = "错误: " + ex.Message; }
                // 选角时用高频轮询：A 键轻点可能只有 1~2 帧，25ms 周期会整个漏掉
                int ms = DbInSelect ? 2 : cfg.PollMs;
                if (ms < 1) ms = 1;
                Thread.Sleep(ms);
            }
        }

        private void ResetState()
        {
            prevBattleActive = false; prevTime = 0; lastWinSum = 0;
            announcedRound = 0; kakeaiDone = false; matchEndHandled = false;
            prevScene = -1; prevMode = -1; logA1 = -1; logA2 = -1; logA2Alt = -1;
            inSelect = false; selChar = -1; selC1 = -1; selC2 = -1;
            ClearSelFlags(1); ClearSelFlags(2);
            prevA = 0;
            lastP1Act = -1; lastP2Act = -1;
            chP1.Stop(); chP2.Stop(); chSel1.Stop(); chSel2.Stop();
            PlayingP1 = ""; PlayingP2 = "";
        }

        private void Tick()
        {
            if (!reader.Attached)
            {
                if (!reader.TryAttach(cfg.ProcName))
                {
                    ResetState(); Last = new Snapshot();
                    if (hasAttached && !ProcessExists(cfg.ProcName))
                    {
                        goneCount++;
                        if (goneCount == 1) Log.W("进程已关闭, 等待重新启动");
                        if (goneCount >= 3)
                        {
                            if (!GameExited) Log.W("进程已退出，继续等待重新启动");
                            GameExited = true; Status = "等待进程 " + cfg.ProcName + " ...";
                            reader.Detach(); hasAttached = false; goneCount = 0; // 游戏关掉后托盘常驻，等下次启动自动重连
                        }
                        else Status = "确认进程退出中...";
                    }
                    else { goneCount = 0; Status = "等待进程 " + cfg.ProcName + " ..."; }
                    return;
                }
                hasAttached = true; goneCount = 0; GameExited = false;
                Status = "已连接 PID " + reader.Pid;
                Log.W("连接 PID=" + reader.Pid + " base=0x" + reader.Base.ToString("X8"));
                ResetState();
                ResolvePaletteLog();
            }
            Snapshot s = reader.Read();
            Last = s;
            if (!s.ok)
            {
                // 仅静态地址读取失败才到这里(进程内存不可读，通常正在退出)。不立即拆连接，避免重连刷屏
                readFailStreak++;
                if (hasAttached && !ProcessExists(cfg.ProcName))
                {
                    goneCount++;
                    if (goneCount >= 3) { GameExited = true; Status = "进程已退出"; }
                    else Status = "确认进程退出中...";
                }
                else
                {
                    if (readFailStreak == 1) Log.W("读取失败(进程可能正在退出)");
                    Status = "读取异常，重试...";
                    if (readFailStreak >= 40) { reader.Detach(); readFailStreak = 0; } // 约1秒后重挂，防句柄失效
                }
                return;
            }
            readFailStreak = 0;
            goneCount = 0;

            int nowMs = Environment.TickCount;
            if (nowMs - lastBeatMs >= 10000 || nowMs < lastBeatMs)
            {
                lastBeatMs = nowMs;
                Log.W("心跳 场景=" + s.scene + " 模式=" + s.mode + " A=" + s.p1a + " char=" + s.p1char + "/" + s.p2char);
            }

            if (s.scene != prevScene || s.mode != prevMode)
            {
                Log.W("场景 " + prevScene + "→" + s.scene + "  模式 " + prevMode + "→" + s.mode
                    + "  time=" + s.time + "  P1char=" + s.p1char + " P2char=" + s.p2char);
                if (IsBattleScene(prevScene) && !IsBattleScene(s.scene) && s.scene != 6)
                {
                    // 场景6是回合间/开赛前的读盘，不能当"离场"，否则第2回合会重播op1
                    // 退出对战(回选角/VS/菜单)才算这一场结束，下一场回合1要能重新播报
                    announcedRound = 0; kakeaiDone = false; matchEndHandled = false; lastWinSum = 0;
                    Log.W("离开对战, 回合状态重置");
                }
                prevScene = s.scene; prevMode = s.mode;
            }

            if (!cfg.Enabled) { DbInSelect = false; chP1.Stop(); chP2.Stop(); chSel1.Stop(); chSel2.Stop(); PlayingP1 = ""; PlayingP2 = ""; Status = "已暂停 (PID " + reader.Pid + ")"; return; }

            bool battleScene = IsBattleScene(s.scene);
            bool battleActive = battleScene && s.battleMgr != 0 && s.p1obj != 0;

            HandleSelect(s);
            HandleBattleEvents(s, battleActive);
            HandleMotion(s, battleActive);

            prevBattleActive = battleActive;
            Status = "运行中 PID " + reader.Pid + "  场景=" + s.scene + " 模式=" + s.mode;
        }

        private static bool ProcessExists(string name)
        {
            try { return Process.GetProcessesByName(name).Length > 0; }
            catch { return true; } // 查询异常时视为仍在，避免误退出
        }

        private static bool IsBattleScene(int sc) { return sc == 5 || sc == 13 || sc == 14 || sc == 15; }
        private static bool IsSelectScene(int sc) { return sc == 3 || sc == 8 || sc == 9; }

        private bool ModeAllows(int player)
        {
            if (cfg.PlayerMode == 1) return player == 1;
            if (cfg.PlayerMode == 2) return player == 2;
            return true;
        }
        // 网战：4=VSSERVER(己方在左侧=P1)  5=VSCLIENT(己方在右侧=P2)  6=观战
        // 实测日志: 以 join 进来打满一整场都是 模式=5, 而 PaletteShare 记的是 side=1(P2)，
        // 所以 5 是客户端、己方在右；4 才是房主、己方在左
        private static bool IsNetplay(int mode) { return mode == 4 || mode == 5 || mode == 6; }
        private static int OwnSide(int mode)
        {
            if (mode == 4) return 1;
            if (mode == 5) return 2;
            return 0;
        }

        // ---- 角色选择 / 卡组决定 ----
        // 包里实际编号与 readme 相反: キャラ選時 sys/[c].1.wav ; デッキ決定時 sys/[c].2.wav
        // P1: 第一次A(进入调色板) → .1；P2 没有本机输入可读，P1/P2 的 .1/.2 都再从
        //     PaletteShare 日志取一遍(palette step → .1, confirmed → .2)；
        //     P1 已经由按A播过就跳过，不会重复。
        // 没装 PaletteShare 才退回「第二次A=确认」(仅 P1)。
        // 滑动浏览不发声; 离开选角场景不发声
        // 己方判定: 网战 4=房主→左侧P1, 5=客户端→右侧P2, 6=观战; 离线按玩家模式(默认P1)
        // 本机输入统一读 0x89A288 (输入簇里本机设备的那份, 网战P2时同样有效)
        private void HandleSelect(Snapshot s)
        {
            bool sel = IsSelectScene(s.scene);
            DbInSelect = sel; DbA1 = s.p1a; DbA2 = s.p2a; DbA2Alt = s.p2aAlt;
            bool sys = cfg.SystemSounds;
            bool net = IsNetplay(s.mode);
            int side = net ? OwnSide(s.mode) : (cfg.PlayerMode == 2 ? 2 : 1);
            int myChar = side == 2 ? s.p2char : s.p1char;
            bool canPlay = (side == 1 || side == 2); // 观战(0)不播

            if (sel)
            {
                if (!inSelect)
                {
                    inSelect = true;
                    selChar = myChar; selC1 = s.p1char; selC2 = s.p2char;
                    ClearSelFlags(1); ClearSelFlags(2);
                    if (palLogPath == null) ResolvePaletteLog(); // 挂上时游戏可能还没写日志，进选角再找一次
                    prevA = s.p1a;
                    logA1 = -1; logA2 = -1; logA2Alt = -1;
                    Log.W("进入选角 scene=" + s.scene + " 模式=" + s.mode + " 己方=P" + side
                        + " 我方char=" + myChar + " P1char=" + s.p1char + " P2char=" + s.p2char
                        + " 系统音=" + sys);
                }
                // A值变化日志(诊断用): 仅在值变化时记录，不刷帧
                if (s.p1a != logA1 || s.p2a != logA2 || s.p2aAlt != logA2Alt)
                {
                    Log.W("选角A值 本机=" + s.p1a + " P2=" + s.p2a + " alt=" + s.p2aAlt
                        + "  char P1=" + s.p1char + " P2=" + s.p2char);
                    logA1 = s.p1a; logA2 = s.p2a; logA2Alt = s.p2aAlt;
                }
                // 滑动浏览不发声；选中/确认后又改角色 = 退回重选，重置该方状态
                if (s.p1char != selC1)
                {
                    Log.W("P1 角色 " + selC1 + "→" + s.p1char + (s.p1char == myChar ? " (己方)" : ""));
                    selC1 = s.p1char;
                    if (palPick[1] || palConf[1]) { Log.W("P1 退回重选, 重置确认状态"); ClearSelFlags(1); }
                }
                if (s.p2char != selC2)
                {
                    Log.W("P2 角色 " + selC2 + "→" + s.p2char + (s.p2char == myChar ? " (己方)" : ""));
                    selC2 = s.p2char;
                    if (palPick[2] || palConf[2]) { Log.W("P2 退回重选, 重置确认状态"); ClearSelFlags(2); }
                }
                selChar = myChar;
                // 第一次A = 选中角色(进入调色板阶段) → .2
                // A 按住时数值逐帧递增, 用 0→非0 / 数值回落 判按下
                bool edge = (s.p1a != 0 && (prevA == 0 || prevA > s.p1a));
                if (edge && canPlay)
                {
                    if (!palPick[side])
                    {
                        palPick[side] = true;
                        if (sys) PlaySelectVoice(side, selChar, false);
                        DbSelEvent = "P" + side + " 选中角色 .1 (char=" + selChar + ")";
                    }
                    else if (palLogPath == null && !palConf[side])
                    {
                        // 没有 PaletteShare 时退回原作两段式: 第二次A = 卡组确认
                        palConf[side] = true;
                        if (sys) PlaySelectVoice(side, selChar, true);
                        DbSelEvent = "P" + side + " 卡组确认 .2 (char=" + selChar + ", 按A)";
                    }
                    else DbSelEvent = "P" + side + " 后续A(忽略)";
                    Log.W(DbSelEvent + " [A=" + s.p1a + " prev=" + prevA + "]");
                }
                prevA = s.p1a;

                // 双方选中/确认都从 PaletteShare 日志取：P2(CPU/对手)没有本机输入可读
                if (palLogPath != null) PollPaletteLog(sys);
            }
            else if (inSelect)
            {
                Log.W("离开选角 scene=" + s.scene + " 选中=P1:" + palPick[1] + "/P2:" + palPick[2]
                    + " 确认=P1:" + palConf[1] + "/P2:" + palConf[2]);
                // 离开选角(回主菜单等)不补播卡组音
                inSelect = false;
            }
        }

        // 找游戏目录下的 PaletteShare 日志；找到就用它的 confirmed 行做确认信号
        private void ResolvePaletteLog()
        {
            palLogPath = null; palLogPos = 0;
            try
            {
                Process p = Process.GetProcessById(reader.Pid);
                string dir = Path.GetDirectoryName(p.MainModule.FileName);
                string cand = Path.Combine(dir, "modules", "PaletteShare", "PaletteShare.log");
                if (File.Exists(cand))
                {
                    palLogPath = cand;
                    palLogPos = new FileInfo(cand).Length; // 只认挂上之后的新行
                    palLogWarned = false;
                    Log.W("确认信号: 监听 " + cand);
                }
                else if (!palResolveLogged) { palResolveLogged = true; Log.W("未找到 PaletteShare 日志, 选角确认退回按A次数"); }
            }
            catch (Exception ex) { if (!palResolveLogged) { palResolveLogged = true; Log.W("解析 PaletteShare 日志失败: " + ex.Message); } }
        }

        private void ClearSelFlags(int p) { palPick[p] = false; palConf[p] = false; }

        // PaletteShare 选角 UI 会写两类行:
        //   ... picker: palette step for side=0 char=6, 0 custom palette(s), ...
        //   ... picker: confirmed side=0 char=6 (vanilla), game slot 4
        // side=0 左侧(P1), side=1 右侧(P2)。palette step = 角色已锁定(进调色板) → .1
        // confirmed = 卡组/调色板确认 → .2。P2(CPU/对手)没有本机输入可读，只能靠这个日志
        private void PollPaletteLog(bool sys)
        {
            try
            {
                FileInfo fi = new FileInfo(palLogPath);
                if (!fi.Exists) return;
                long len = fi.Length;
                if (len < palLogPos) palLogPos = 0; // 日志被轮换
                if (len == palLogPos) return;
                byte[] buf = new byte[(int)Math.Min(len - palLogPos, 16384)];
                int n;
                using (FileStream fs = new FileStream(palLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(palLogPos, SeekOrigin.Begin);
                    n = fs.Read(buf, 0, buf.Length);
                }
                if (n <= 0) return;
                string text = Encoding.UTF8.GetString(buf, 0, n);
                int cut = text.LastIndexOf('\n');
                if (cut < 0) return; // 半行还没写完，下次再读
                palLogPos += Encoding.UTF8.GetByteCount(text.Substring(0, cut + 1));
                foreach (string raw in text.Substring(0, cut + 1).Split('\n'))
                {
                    string line = raw.Trim();
                    bool confirm;
                    if (line.IndexOf("picker: palette step for side=", StringComparison.Ordinal) >= 0) confirm = false;
                    else if (line.IndexOf("picker: confirmed side=", StringComparison.Ordinal) >= 0) confirm = true;
                    else continue;
                    int sd, ch;
                    if (!ParseSideChar(line, out sd, out ch)) continue;
                    int p = sd + 1;
                    if (p < 1 || p > 2) continue;
                    if (confirm)
                    {
                        if (palConf[p]) continue;
                        palConf[p] = true; palPick[p] = true;
                        if (sys) PlaySelectVoice(p, ch, true);
                        Log.W("P" + p + " 卡组确认 .2 (char=" + ch + ", 游戏确认)");
                    }
                    else
                    {
                        if (palPick[p]) continue;
                        palPick[p] = true;
                        if (sys) PlaySelectVoice(p, ch, false);
                        Log.W("P" + p + " 选中角色 .1 (char=" + ch + ", 游戏)");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!palLogWarned) { palLogWarned = true; Log.W("读取 PaletteShare 日志失败: " + ex.Message); }
            }
        }

        private static bool ParseSideChar(string line, out int side, out int ch)
        {
            side = -1; ch = -1;
            int i = line.IndexOf("side=", StringComparison.Ordinal);
            if (i < 0) return false;
            i += 5;
            int v = 0, d = 0;
            while (i < line.Length && line[i] >= '0' && line[i] <= '9') { v = v * 10 + (line[i] - '0'); i++; d++; }
            if (d == 0) return false;
            side = v;
            int j = line.IndexOf("char=", i, StringComparison.Ordinal);
            if (j < 0) return false;
            j += 5; v = 0; d = 0;
            while (j < line.Length && line[j] >= '0' && line[j] <= '9') { v = v * 10 + (line[j] - '0'); j++; d++; }
            if (d == 0) return false;
            ch = v;
            return true;
        }
        private void PlaySelectVoice(int player, int c, bool deck)
        {
            if (c < 0 || c > 19) { Log.W("选角语音跳过: 非法角色 c=" + c); return; }
            // 实测包里编号和 readme 写的是反的：选中角色= .1.wav，卡组确认= .2.wav
            string f = Path.Combine(cfg.VoiceRoot, "sys", c + (deck ? ".2.wav" : ".1.wav"));
            // 每个玩家一条可打断声道：新语音切断上一句，与原作单声道行为一致，避免多音叠糊
            (player == 1 ? chSel1 : chSel2).Set(f, false, cfg.Volume);
        }

        // ---- 回合播报 / 挂合 / 一本 / 胜负 ----
        private void HandleBattleEvents(Snapshot s, bool battleActive)
        {
            int winSum = s.p1wins + s.p2wins;
            int round = winSum + 1;
            if (winSum < lastWinSum) matchEndHandled = false; // 新比赛重置

            // 回合开始：刚进入对战，或计时器回落(新回合从0重计)。旧版要求上一回合时间>120，
            // 早KO的回合会漏掉 op2/op3；现在只要比上一帧小就算重开(回合号没变时不会重复播)
            bool timeReset = battleActive && prevBattleActive && s.time < prevTime;
            bool roundStart = battleActive && (!prevBattleActive || timeReset);

            if (battleActive && !prevBattleActive)
            {
                Log.W("战斗开始 场景=" + s.scene + " time=" + s.time + " battleMgr=0x" + s.battleMgr.ToString("X")
                    + " p1obj=0x" + s.p1obj.ToString("X") + " 回合=" + round + " 已播回合=" + announcedRound);
                // 中途接管(托盘后启动/断线重连)：比赛早已进行中，不补播当前回合的 op，
                // 也不把场上已有胜场当成本帧新增，否则会误响 op2/win.wav
                if (s.time >= 120) announcedRound = round;
                lastWinSum = winSum;
            }

            if (roundStart && round != announcedRound && s.p1wins < 2 && s.p2wins < 2)
            {
                announcedRound = round;
                kakeaiDone = false;
                string op = round <= 1 ? "op1.wav" : (round == 2 ? "op2.wav" : "op3.wav");
                Log.W("回合" + round + " 开始 (time=" + s.time + " winSum=" + winSum + ") → " + op + " 系统音=" + cfg.SystemSounds + " 观战=" + (s.scene == 15));
                if (cfg.SystemSounds && s.scene != 15) // 观战不播报
                    PlaySys(op);
            }

            // 回合结束：胜场增加 → 一本；达到2胜 → 决着(音效统一在 HandleMatchEnd 里按顺序播)
            if (battleActive && winSum > lastWinSum)
            {
                bool final = (s.p1wins >= 2 || s.p2wins >= 2);
                Log.W("胜场增加 " + lastWinSum + "→" + winSum + " (P1=" + s.p1wins + " P2=" + s.p2wins + ")"
                    + (final ? " → 决着" : " → win.wav"));
                if (final) HandleMatchEnd(s);
                else if (cfg.SystemSounds) PlaySys("win.wav");
            }

            // 挂合（开局对白），每回合一次；P1 播完再播 P2
            if (battleActive && !kakeaiDone && s.time >= 20 && s.time <= 60)
            {
                kakeaiDone = true;
                Log.W("挂合 time=" + s.time + " P1char=" + s.p1char + " P2char=" + s.p2char + " 顺序 P1→P2");
                PlayKakeaiPair(s.p1char, s.p2char, s.p2char, s.p1char);
            }

            if (battleActive) lastWinSum = winSum;
            prevTime = s.time;
        }

        private void HandleMatchEnd(Snapshot s)
        {
            if (matchEndHandled) return;
            int winner = s.p1wins >= 2 ? 1 : (s.p2wins >= 2 ? 2 : 0);
            if (winner == 0) return;
            matchEndHandled = true;
            short wact = winner == 1 ? s.p1act : s.p2act;
            Log.W("决着 胜方=P" + winner + " (P1wins=" + s.p1wins + " P2wins=" + s.p2wins + ") 胜方act=" + wact + " → win2" + (wact >= 500 ? "/tere" : ""));
            int wc = winner == 1 ? s.p1char : s.p2char;
            int lc = winner == 1 ? s.p2char : s.p1char;
            // 一本 → 决着 → (テレッテー) → 胜方台词 → 负方台词，顺序播，不叠在一起
            List<string> seq = new List<string>();
            if (cfg.SystemSounds)
            {
                seq.Add(Path.Combine(cfg.VoiceRoot, "sys", "win.wav"));
                seq.Add(Path.Combine(cfg.VoiceRoot, "sys", "win2.wav"));
                if (wact >= 500) seq.Add(Path.Combine(cfg.VoiceRoot, "sys", "tere.wav"));
            }
            seq.Add(FindWinLine(wc));
            seq.Add(FindLoseLine(lc));
            Audio.OneShotSequence(seq.ToArray(), cfg.Volume);
        }

        private void PlaySys(string name)
        {
            Audio.OneShot(Path.Combine(cfg.VoiceRoot, "sys", name), cfg.Volume);
        }
        // 挂合文件: [c]-[oc] → [c]--[0~4] 随机 → [c]- 兜底
        private string FindKakeai(int c, int oc)
        {
            if (c < 0 || c > 19) return null;
            string d = Path.Combine(cfg.VoiceRoot, c.ToString());
            string f = Path.Combine(d, c + "-" + oc + ".wav");
            if (File.Exists(f)) return f;
            List<string> rnd = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                string r = Path.Combine(d, c + "--" + i + ".wav");
                if (File.Exists(r)) rnd.Add(r);
            }
            if (rnd.Count > 0) return rnd[rng.Next(rnd.Count)];
            string common = Path.Combine(d, c + "-.wav");
            return File.Exists(common) ? common : null;
        }
        private void PlayKakeaiPair(int c1, int oc1, int c2, int oc2)
        {
            Audio.OneShotSequence(new string[] { FindKakeai(c1, oc1), FindKakeai(c2, oc2) }, cfg.Volume);
        }
        // readme: 从 0 开始按顺序找，遇到缺口就停，随机挑一个
        private string FindWinLine(int c)
        {
            if (c < 0 || c > 19) return null;
            string d = Path.Combine(cfg.VoiceRoot, c.ToString());
            List<string> found = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                string f = Path.Combine(d, c + "_win" + i + ".wav");
                if (File.Exists(f)) found.Add(f); else break;
            }
            return found.Count > 0 ? found[rng.Next(found.Count)] : null;
        }
        private string FindLoseLine(int c)
        {
            if (c < 0 || c > 19) return null;
            string f = Path.Combine(cfg.VoiceRoot, c.ToString(), c + "_lose.wav");
            return File.Exists(f) ? f : null;
        }

        // ---- 对战动作语音 ----
        private void HandleMotion(Snapshot s, bool battleActive)
        {
            DbBattleActive = battleActive;
            if (!battleActive)
            {
                chP1.Stop(); chP2.Stop(); lastP1Act = -1; lastP2Act = -1;
                DbAllowP1 = false; DbAllowP2 = false; DbFileP1 = ""; DbFileP2 = "";
                return;
            }
            bool allowP1, allowP2;
            if (IsNetplay(s.mode) && cfg.NetplayRestrict)
            {
                // 网战: 只播己方(4→P1, 5→P2, 观战→无)
                int own = OwnSide(s.mode);
                allowP1 = own == 1; allowP2 = own == 2;
            }
            else { allowP1 = ModeAllows(1); allowP2 = ModeAllows(2); }
            DbAllowP1 = allowP1; DbAllowP2 = allowP2;
            ProcessMotion(chP1, allowP1, 1, s.p1char, s.p1act, s.p2char, s.p2act, s.p1obj, ref lastP1Act);
            ProcessMotion(chP2, allowP2, 2, s.p2char, s.p2act, s.p1char, s.p1act, s.p2obj, ref lastP2Act);
            DbLastP1Act = lastP1Act; DbLastP2Act = lastP2Act;
        }

        private void ProcessMotion(LoopChannel ch, bool allow, int player, int c, short act, int oc, short om, int obj, ref int lastAct)
        {
            if (!allow) { ch.Stop(); lastAct = -1; SetDbFile(player, "(此玩家被禁用)"); return; }
            if (act == lastAct) return;
            lastAct = act;
            string file = LookupVoice(c, act, oc, om, obj);
            Log.W("动作 P" + player + " c=" + c + " act=" + act + " oc=" + oc + " om=" + om
                + " → " + (file == null ? "无文件" : Path.GetFileName(file)));
            if (file == null)
            {
                SetDbFile(player, "(无文件 act=" + act + ")");
                if (ch.IsLoop) ch.Stop(); // 仅停移动循环；已播的一次性语音继续播完
                return;
            }
            string name = Path.GetFileName(file);
            SetDbFile(player, name);
            bool loop = (act == 4 || act == 5 || act == 200);
            if (loop)
            {
                ch.Set(file, true, cfg.Volume);
            }
            else
            {
                if (ch.IsLoop) ch.Stop(); // 离开移动循环
                Audio.OneShot(file, cfg.Volume); // 独立播完整段，动作取消/切换不打断
            }
            SetPlaying(player, name);
        }
        private void SetDbFile(int player, string v) { if (player == 1) DbFileP1 = v; else DbFileP2 = v; }
        private void SetPlaying(int player, string v) { if (player == 1) PlayingP1 = v; else PlayingP2 = v; }

        private string LookupVoice(int c, short act, int oc, short om, int obj)
        {
            if (c < 0 || c > 19 || act < 0) return null;
            string d = Path.Combine(cfg.VoiceRoot, c.ToString());
            // 反魂蝶 (ゆゆこ 619)
            if (c == 7 && act == 619)
            {
                int count = 0;
                if (obj != 0) { count = ReadObjShort(obj + 0x892); }
                string f = Path.Combine(d, "7_619_" + count + ".wav");
                if (File.Exists(f)) return f;
                string p = Path.Combine(d, "7_619.wav");
                return File.Exists(p) ? p : null;
            }
            string f1 = Path.Combine(d, c + "_" + act + "_" + oc + "_" + om + ".wav");
            if (File.Exists(f1)) return f1;
            string f2 = Path.Combine(d, c + "_" + act + "_" + om + ".wav");
            if (File.Exists(f2)) return f2;
            string f3 = Path.Combine(d, c + "_" + act + "_" + oc + "_.wav");
            if (File.Exists(f3)) return f3;
            string f4 = Path.Combine(d, c + "_" + act + ".wav");
            if (File.Exists(f4)) return f4;
            return null;
        }

        private short ReadObjShort(int addr)
        {
            // 通过 reader 重新读取绝对地址（复用其句柄）
            return reader.ReadAbsShortPublic(addr);
        }
    }

    // ===================== 设置窗口 =====================
    internal sealed class SettingsForm : Form
    {
        private readonly Settings cfg;
        private readonly VoiceEngine engine;
        private TrackBar volBar;
        private Label volLabel;
        private NumericUpDown pollNum;
        private TextBox dirBox;
        private TextBox procBox;

        public SettingsForm(Settings cfg, VoiceEngine engine)
        {
            this.cfg = cfg; this.engine = engine;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "SokuVoice 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(460, 250);
            Font = new Font("Microsoft YaHei", 9F);

            Label lv = new Label(); lv.Text = "音量"; lv.Location = new Point(16, 20); lv.AutoSize = true;
            volBar = new TrackBar(); volBar.Minimum = 0; volBar.Maximum = 100; volBar.TickFrequency = 10;
            volBar.Value = cfg.Volume; volBar.Location = new Point(70, 12); volBar.Width = 280;
            volLabel = new Label(); volLabel.Text = cfg.Volume + "%"; volLabel.Location = new Point(360, 20); volLabel.AutoSize = true;
            volBar.ValueChanged += delegate { volLabel.Text = volBar.Value + "%"; };

            Label lp = new Label(); lp.Text = "监控周期(ms)"; lp.Location = new Point(16, 62); lp.AutoSize = true;
            pollNum = new NumericUpDown(); pollNum.Minimum = 1; pollNum.Maximum = 500; pollNum.Value = cfg.PollMs;
            pollNum.Location = new Point(120, 58); pollNum.Width = 80;
            Label lpn = new Label(); lpn.Text = "越小越灵敏，越占CPU"; lpn.Location = new Point(210, 62); lpn.AutoSize = true; lpn.ForeColor = Color.Gray;

            Label ld = new Label(); ld.Text = "语音目录"; ld.Location = new Point(16, 100); ld.AutoSize = true;
            dirBox = new TextBox(); dirBox.Text = cfg.VoiceDir; dirBox.Location = new Point(90, 96); dirBox.Width = 280;
            Button browse = new Button(); browse.Text = "浏览..."; browse.Location = new Point(378, 94); browse.Width = 66;
            browse.Click += delegate {
                FolderBrowserDialog fbd = new FolderBrowserDialog();
                fbd.SelectedPath = dirBox.Text;
                if (fbd.ShowDialog() == DialogResult.OK) dirBox.Text = fbd.SelectedPath;
            };

            Label lpr = new Label(); lpr.Text = "进程名"; lpr.Location = new Point(16, 138); lpr.AutoSize = true;
            procBox = new TextBox(); procBox.Text = cfg.ProcName; procBox.Location = new Point(90, 134); procBox.Width = 160;
            Label lprn = new Label(); lprn.Text = "不含 .exe"; lprn.Location = new Point(258, 138); lprn.AutoSize = true; lprn.ForeColor = Color.Gray;

            Button save = new Button(); save.Text = "保存"; save.Location = new Point(280, 195); save.Width = 80;
            save.Click += delegate {
                string oldProc = cfg.ProcName;
                cfg.Volume = volBar.Value;
                cfg.PollMs = (int)pollNum.Value;
                cfg.VoiceDir = dirBox.Text.Trim();
                cfg.ProcName = procBox.Text.Trim();
                cfg.Save();
                engine.ApplyVolume();
                if (cfg.ProcName != oldProc) engine.ForgetProcess();
                DialogResult = DialogResult.OK;
                Close();
            };
            Button cancel = new Button(); cancel.Text = "取消"; cancel.Location = new Point(366, 195); cancel.Width = 78;
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.AddRange(new Control[] { lv, volBar, volLabel, lp, pollNum, lpn, ld, dirBox, browse, lpr, procBox, lprn, save, cancel });
            AcceptButton = save; CancelButton = cancel;
        }
    }

    // ===================== 调试窗口 =====================
    internal sealed class DebugForm : Form
    {
        private readonly VoiceEngine engine;
        private Label lab;
        private System.Windows.Forms.Timer timer;

        public DebugForm(VoiceEngine engine)
        {
            this.engine = engine;
            Text = "SokuVoice 实时调试";
            ClientSize = new Size(470, 400);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Consolas", 9F);
            lab = new Label(); lab.Dock = DockStyle.Fill; lab.TextAlign = ContentAlignment.TopLeft;
            Controls.Add(lab);
            timer = new System.Windows.Forms.Timer(); timer.Interval = 100;
            timer.Tick += delegate { RefreshInfo(); };
            timer.Start();
            FormClosed += delegate { timer.Stop(); };
            RefreshInfo();
        }

        private void RefreshInfo()
        {
            Snapshot s = engine.Last;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("状态: " + engine.Status);
            sb.AppendLine("Base: 0x" + engine.ReaderBase.ToString("X8") + "  PID: " + engine.ReaderPid);
            sb.AppendLine("----------------------------------------");
            sb.AppendLine("scene = " + s.scene + "   mode = " + s.mode + "   time = " + s.time);
            sb.AppendLine("battleMgr = 0x" + s.battleMgr.ToString("X8"));
            sb.AppendLine("P1 obj = 0x" + s.p1obj.ToString("X8"));
            sb.AppendLine("P2 obj = 0x" + s.p2obj.ToString("X8"));
            sb.AppendLine("----------------------------------------");
            sb.AppendLine("P1 char=" + s.p1char + " act=" + s.p1act + " wins=" + s.p1wins + " hp=" + s.p1hp);
            sb.AppendLine("P2 char=" + s.p2char + " act=" + s.p2act + " wins=" + s.p2wins + " hp=" + s.p2hp);
            sb.AppendLine("----------------------------------------");
            sb.AppendLine("对战激活=" + engine.DbBattleActive + "  允许P1=" + engine.DbAllowP1 + " 允许P2=" + engine.DbAllowP2);
            sb.AppendLine("P1 上次act=" + engine.DbLastP1Act + "  查找: " + engine.DbFileP1);
            sb.AppendLine("P2 上次act=" + engine.DbLastP2Act + "  查找: " + engine.DbFileP2);
            sb.AppendLine("播放P1: " + engine.PlayingP1);
            sb.AppendLine("播放P2: " + engine.PlayingP2);
            sb.AppendLine("----------------------------------------");
            sb.AppendLine("选人激活=" + engine.DbInSelect + "  A1=" + engine.DbA1 + "  A2=" + engine.DbA2 + " (alt=" + engine.DbA2Alt + ")");
            sb.AppendLine("选人事件: " + engine.DbSelEvent);
            sb.AppendLine();
            sb.AppendLine("场景: 3=角色选 5/13/14=对战 15=观战");
            sb.AppendLine("模式: 2=VS电脑 3=VS玩家 4=客 5=主 6=观战 8=练习");
            lab.Text = sb.ToString();
        }
    }

    // ===================== 托盘 =====================
    internal sealed class TrayContext : ApplicationContext
    {
        private readonly Settings cfg;
        private readonly VoiceEngine engine;
        private readonly NotifyIcon tray;
        private SettingsForm settingsForm;
        private DebugForm debugForm;
        private System.Windows.Forms.Timer exitTimer;

        private ToolStripMenuItem miEnabled;
        private ToolStripMenuItem miSys;
        private ToolStripMenuItem miNet;
        private ToolStripMenuItem miBoth, miP1, miP2;

        public TrayContext()
        {
            cfg = new Settings();
            engine = new VoiceEngine(cfg);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei", 9F);

            miEnabled = new ToolStripMenuItem("启用语音");
            miEnabled.Checked = cfg.Enabled;
            miEnabled.Click += delegate { cfg.Enabled = !cfg.Enabled; miEnabled.Checked = cfg.Enabled; cfg.Save(); };
            menu.Items.Add(miEnabled);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem modeMenu = new ToolStripMenuItem("玩家模式");
            miBoth = new ToolStripMenuItem("双人 (P1+P2)");
            miP1 = new ToolStripMenuItem("仅 P1");
            miP2 = new ToolStripMenuItem("仅 P2");
            miBoth.Click += delegate { SetMode(0); };
            miP1.Click += delegate { SetMode(1); };
            miP2.Click += delegate { SetMode(2); };
            modeMenu.DropDownItems.AddRange(new ToolStripItem[] { miBoth, miP1, miP2 });
            menu.Items.Add(modeMenu);
            UpdateModeChecks();

            miSys = new ToolStripMenuItem("系统音 (角色选/回合/一本/胜负)");
            miSys.Checked = cfg.SystemSounds;
            miSys.Click += delegate { cfg.SystemSounds = !cfg.SystemSounds; miSys.Checked = cfg.SystemSounds; cfg.Save(); };
            menu.Items.Add(miSys);

            miNet = new ToolStripMenuItem("热帯限制 (网战仅己方)");
            miNet.Checked = cfg.NetplayRestrict;
            miNet.Click += delegate { cfg.NetplayRestrict = !cfg.NetplayRestrict; miNet.Checked = cfg.NetplayRestrict; cfg.Save(); };
            menu.Items.Add(miNet);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miTest = new ToolStripMenuItem("播放测试音");
            miTest.Click += delegate { Audio.OneShot(Path.Combine(cfg.VoiceRoot, "sys", "op1.wav"), cfg.Volume); };
            menu.Items.Add(miTest);

            ToolStripMenuItem miSettings = new ToolStripMenuItem("设置...");
            miSettings.Click += delegate { ShowSettings(); };
            menu.Items.Add(miSettings);

            ToolStripMenuItem miDebug = new ToolStripMenuItem("调试窗口...");
            miDebug.Click += delegate { ShowDebug(); };
            menu.Items.Add(miDebug);

            ToolStripMenuItem miLog = new ToolStripMenuItem("打开日志");
            miLog.Click += delegate {
                try { Process.Start("notepad.exe", Log.FilePath); }
                catch (Exception ex) { MessageBox.Show("无法打开日志: " + ex.Message); }
            };
            menu.Items.Add(miLog);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { Exit(); };
            menu.Items.Add(miExit);

            tray = new NotifyIcon();
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { tray.Icon = SystemIcons.Application; }
            if (tray.Icon == null) tray.Icon = SystemIcons.Application;
            tray.Text = "SokuVoice";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowSettings(); };

            engine.Start();

            exitTimer = new System.Windows.Forms.Timer();
            exitTimer.Interval = 500;
            // 游戏退出后 3 秒内没重新连上就跟着退出(SokuLauncher 下次开游戏会自动拉起)
            int goneTicks = 0;
            exitTimer.Tick += delegate {
                if (engine.GameExited)
                {
                    goneTicks++;
                    tray.Text = "SokuVoice (游戏已退出)";
                    if (goneTicks >= 6) Exit();
                }
                else { goneTicks = 0; tray.Text = "SokuVoice"; }
            };
            exitTimer.Start();
        }

        private void SetMode(int m)
        {
            cfg.PlayerMode = m; cfg.Save(); UpdateModeChecks();
        }
        private void UpdateModeChecks()
        {
            miBoth.Checked = cfg.PlayerMode == 0;
            miP1.Checked = cfg.PlayerMode == 1;
            miP2.Checked = cfg.PlayerMode == 2;
        }

        private void ShowSettings()
        {
            if (settingsForm == null || settingsForm.IsDisposed)
            {
                settingsForm = new SettingsForm(cfg, engine);
                settingsForm.FormClosed += delegate { settingsForm = null; };
                settingsForm.Show();
            }
            else settingsForm.Activate();
        }
        private void ShowDebug()
        {
            if (debugForm == null || debugForm.IsDisposed)
            {
                debugForm = new DebugForm(engine);
                debugForm.FormClosed += delegate { debugForm = null; };
                debugForm.Show();
            }
            else debugForm.Activate();
        }

        private bool exited = false;
        private void Exit()
        {
            if (exited) return;
            exited = true;
            Log.W("退出 SokuVoice");
            if (exitTimer != null) exitTimer.Stop();
            engine.Stop();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // SokuLauncher 会自动拉起本程序，手动再开一份就会双份播放/双份读内存，这里只允许一个实例
            bool created;
            using (Mutex single = new Mutex(true, "SokuVoice.SingleInstance.7F3A", out created))
            {
                if (!created) return;
                Log.Init();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
                Log.Close();
            }
        }
    }
}