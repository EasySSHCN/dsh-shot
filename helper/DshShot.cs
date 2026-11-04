// DSH screenshot overlay core - compiled in-process by Add-Type (no standalone exe).
// C# 5 / .NET Framework 4.x / WinForms.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

public class DshShot
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    static bool dpiDone;
    public static int PaintTicks;

    static void EnsureDpi()
    {
        if (dpiDone) return;
        dpiDone = true;
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }
        catch (Exception) { }
        try { SetProcessDPIAware(); } catch (Exception) { }
    }

    public static Rectangle VirtualScreen()
    {
        EnsureDpi();
        return SystemInformation.VirtualScreen;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT rect, int size);

    /// <summary>Window under the cursor, in overlay client coordinates (Doubao's TryRecognizeWindow).</summary>
    public static bool TryHoverWindow(Rectangle vs, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        POINT pt;
        if (!GetCursorPos(out pt)) return false;
        IntPtr h = WindowFromPoint(pt);
        if (h == IntPtr.Zero) return false;
        IntPtr root = GetAncestor(h, 2 /* GA_ROOT */);
        if (root == IntPtr.Zero) root = h;

        StringBuilder cls = new StringBuilder(256);
        GetClassName(root, cls, cls.Capacity);
        string c = cls.ToString();
        if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;

        RECT r;
        int hr = DwmGetWindowAttribute(root, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out r, Marshal.SizeOf(typeof(RECT)));
        if (hr != 0) return false;

        Rectangle screen = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        Rectangle clipped = Rectangle.Intersect(screen, vs);
        if (clipped.Width < 48 || clipped.Height < 48) return false;
        bounds = new Rectangle(clipped.X - vs.X, clipped.Y - vs.Y, clipped.Width, clipped.Height);
        return true;
    }

    public static Bitmap Grab(Rectangle r)
    {
        Bitmap bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);
        }
        return bmp;
    }

    /// <summary>
    /// Hides the host application's own top-level windows for the duration of one capture, so the
    /// screenshot shows the user's work instead of the chat window (Doubao's hide_window /
    /// exclude_chat equivalent). The target app is the parent process of this PowerShell host;
    /// shell processes are refused so the taskbar/desktop can never be hidden.
    /// </summary>
    public static class AppWindows
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int SW_HIDE = 0;
        const int SW_SHOW = 5;
        const uint TH32CS_SNAPPROCESS = 0x2;

        static readonly string[] NeverHide =
        {
            "explorer.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe",
            "windowsterminal.exe", "services.exe", "wininit.exe", "winlogon.exe",
        };

        static Dictionary<uint, uint> ParentMap()
        {
            var map = new Dictionary<uint, uint>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                PROCESSENTRY32 pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (Process32FirstW(snap, ref pe))
                {
                    do { map[pe.th32ProcessID] = pe.th32ParentProcessID; }
                    while (Process32NextW(snap, ref pe));
                }
            }
            finally { CloseHandle(snap); }
            return map;
        }

        static string ExeNameOf(uint pid)
        {
            try
            {
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById((int)pid))
                    return p.ProcessName + ".exe";
            }
            catch (Exception) { return null; }
        }

        static string HostImageName()
        {
            try
            {
                Dictionary<uint, uint> parents = ParentMap();
                uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                uint parent;
                if (!parents.TryGetValue(pid, out parent)) return null;
                if (parent == 0 || parent == pid) return null;
                string name = ExeNameOf(parent);
                if (name == null) return null;
                if (Array.IndexOf(NeverHide, name.ToLowerInvariant()) >= 0) return null;
                return name;
            }
            catch (Exception) { return null; }
        }

        public static void Hide(List<IntPtr> hidden)
        {
            string host = HostImageName();
            if (host == null) return;
            IntPtr prevFg = GetForegroundWindow();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                if (GetWindowTextLength(h) == 0) return true;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                string name = ExeNameOf(pid);
                if (name == null) return true;
                if (!string.Equals(name, host, StringComparison.OrdinalIgnoreCase)) return true;
                ShowWindow(h, SW_HIDE);
                hidden.Add(h);
                return true;
            }, IntPtr.Zero);
            if (hidden.Count > 0)
            {
                try { DwmFlush(); } catch (Exception) { }
                System.Threading.Thread.Sleep(140);
                lastForeground = prevFg;
            }
        }

        static IntPtr lastForeground = IntPtr.Zero;

        public static void Restore(List<IntPtr> hidden)
        {
            foreach (IntPtr h in hidden) ShowWindow(h, SW_SHOW);
            hidden.Clear();
            if (lastForeground != IntPtr.Zero)
            {
                try { SetForegroundWindow(lastForeground); } catch (Exception) { }
                lastForeground = IntPtr.Zero;
            }
        }
    }

    // selftest: capture only
    public static string CaptureOnly(string outPath)
    {
        Rectangle vs = VirtualScreen();
        DateTime t0 = DateTime.UtcNow;
        List<IntPtr> hidden = new List<IntPtr>();
        Bitmap bmp;
        try
        {
            AppWindows.Hide(hidden);
            bmp = Grab(vs);
        }
        finally { AppWindows.Restore(hidden); }

        using (bmp)
        {
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            bmp.Save(outPath, ImageFormat.Png);
            return "{\"ok\":true,\"path\":\"" + Esc(outPath) + "\",\"width\":" + bmp.Width +
                   ",\"height\":" + bmp.Height + ",\"ms\":" + (int)(DateTime.UtcNow - t0).TotalMilliseconds + "}";
        }
    }

    public static string Run(string outPath, int sx, int sy, int sw, int sh,
                             int autoAcceptMs, string proofPath, int proofMs, string banner)
    {
        Rectangle vs = VirtualScreen();
        DateTime t0 = DateTime.UtcNow;
        List<IntPtr> hidden = new List<IntPtr>();
        Bitmap frozen;
        Rectangle hover = Rectangle.Empty;
        bool hasHover = false;
        try
        {
            AppWindows.Hide(hidden);
            frozen = Grab(vs);
            // Resolve the hover target while our own windows are hidden, so the preset
            // rectangle describes the same world the frozen bitmap captured.
            if (sw <= 0) hasHover = TryHoverWindow(vs, out hover);
        }
        finally { AppWindows.Restore(hidden); }

        int captureMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
        using (frozen)
        {
            using (Overlay form = new Overlay(frozen, vs, banner))
            {
                if (sw > 0 && sh > 0) form.Script(new Rectangle(sx, sy, sw, sh), autoAcceptMs);
                else if (hasHover) form.Preset(hover);
                if (!string.IsNullOrEmpty(proofPath)) form.Proof(proofPath, proofMs);
                Timer guard = new Timer();
                guard.Interval = 60000;
                guard.Tick += delegate { guard.Stop(); form.CancelNow(); };
                guard.Start();

                Application.Run(form);
                guard.Stop();

                if (form.Accepted && form.Sel.Width > 1 && form.Sel.Height > 1)
                {
                    Rectangle sel = form.Sel;
                    string dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    using (Bitmap crop = frozen.Clone(sel, PixelFormat.Format32bppArgb))
                    {
                        // Composite annotations (stored in absolute overlay coordinates) onto the crop.
                        using (Graphics g = Graphics.FromImage(crop))
                        {
                            DrawShapes(g, crop, form.Shapes, new Point(-sel.X, -sel.Y));
                        }
                        crop.Save(outPath, ImageFormat.Png);
                        TryCopyToClipboard(crop);
                    }
                    return "{\"ok\":true,\"path\":\"" + Esc(outPath) + "\",\"width\":" + sel.Width +
                           ",\"height\":" + sel.Height + ",\"rect\":[" + sel.X + "," + sel.Y + "," + sel.Width + "," + sel.Height +
                           "],\"action\":\"" + form.Action +
                           "\",\"captureMs\":" + captureMs + ",\"totalMs\":" + (int)(DateTime.UtcNow - t0).TotalMilliseconds + "}";
                }
                return "{\"ok\":false,\"error\":\"cancelled\"}";
            }
        }
    }

    // repaint cost measurement
    public static string Bench(int frames)
    {
        Rectangle vs = VirtualScreen();
        using (Bitmap frozen = Grab(vs))
        using (Overlay form = new Overlay(frozen, vs, "BENCH"))
        {
            form.Script(new Rectangle(400, 200, 1200, 700), 0);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
            bool vis = form.Visible; bool hnd = form.IsHandleCreated; int ticksBefore = PaintTicks;
            DateTime t0 = DateTime.UtcNow;
            for (int i = 0; i < frames; i++)
            {
                form.Refresh();
                Application.DoEvents();
            }
            double avg = (DateTime.UtcNow - t0).TotalMilliseconds / Math.Max(1, frames);
            form.Close();
            return "{\"ok\":true,\"visible\":" + (vis ? 1 : 0) + ",\"handle\":" + (hnd ? 1 : 0) + ",\"paintsBefore\":" + ticksBefore + ",\"frames\":" + frames + ",\"paints\":" + PaintTicks + ",\"avgRepaintMs\":" + avg.ToString("0.0", CultureInfo.InvariantCulture) + "}";
        }
    }

    static string Esc(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>One annotation stroke: kind, point list (absolute overlay coords), color, width.</summary>
    public class Shape
    {
        public string Kind; // "rect" | "arrow" | "pen" | "mosaic"
        public List<Point> Pts;
        public Color Col;
        public float Width;

        public Rectangle Bounds()
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (Point p in Pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            if (minX > maxX) return Rectangle.Empty;
            return new Rectangle(minX, minY, maxX - minX, maxY - minY);
        }
    }

    /// <summary>
    /// Renders annotation shapes onto <paramref name="g"/>. Mosaic pixels come from
    /// <paramref name="src"/> (the frozen frame when painting the overlay, the crop when
    /// compositing the final PNG); every shape is translated by <paramref name="off"/>,
    /// so callers pass the selection offset when drawing into a cropped bitmap.
    /// </summary>
    static void DrawShapes(Graphics g, Bitmap src, List<Shape> shapes, Point off)
    {
        if (shapes == null || shapes.Count == 0) return;
        foreach (Shape s in shapes)
        {
            if (s == null || s.Pts == null || s.Pts.Count == 0) continue;
            using (Pen p = new Pen(s.Col, s.Width))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                if (s.Kind == "rect")
                {
                    Point a = new Point(s.Pts[0].X + off.X, s.Pts[0].Y + off.Y);
                    Point b = s.Pts[s.Pts.Count - 1];
                    b = new Point(b.X + off.X, b.Y + off.Y);
                    g.DrawRectangle(p, Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                                    Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                }
                else if (s.Kind == "arrow")
                {
                    Point a = new Point(s.Pts[0].X + off.X, s.Pts[0].Y + off.Y);
                    Point b = s.Pts[s.Pts.Count - 1];
                    b = new Point(b.X + off.X, b.Y + off.Y);
                    p.EndCap = LineCap.NoAnchor;
                    g.DrawLine(p, a, b);
                    double ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
                    double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    double head = Math.Min(22.0, Math.Max(10.0, len * 0.25)) + s.Width * 1.5;
                    Point h1 = new Point((int)Math.Round(b.X - head * Math.Cos(ang - 0.45)),
                                         (int)Math.Round(b.Y - head * Math.Sin(ang - 0.45)));
                    Point h2 = new Point((int)Math.Round(b.X - head * Math.Cos(ang + 0.45)),
                                         (int)Math.Round(b.Y - head * Math.Sin(ang + 0.45)));
                    g.DrawLine(p, b, h1);
                    g.DrawLine(p, b, h2);
                }
                else if (s.Kind == "pen")
                {
                    if (s.Pts.Count == 1)
                    {
                        using (SolidBrush b = new SolidBrush(s.Col))
                        {
                            g.FillEllipse(b, s.Pts[0].X + off.X - s.Width / 2f,
                                          s.Pts[0].Y + off.Y - s.Width / 2f, s.Width, s.Width);
                        }
                    }
                    else
                    {
                        List<Point> pts = new List<Point>(s.Pts.Count);
                        foreach (Point q in s.Pts) pts.Add(new Point(q.X + off.X, q.Y + off.Y));
                        g.DrawLines(p, pts.ToArray());
                    }
                }
                else if (s.Kind == "mosaic")
                {
                    Rectangle r = s.Bounds();
                    r = new Rectangle(r.X + off.X, r.Y + off.Y, r.Width + 1, r.Height + 1);
                    Rectangle srcR = Rectangle.Intersect(new Rectangle(0, 0, src.Width, src.Height), r);
                    if (srcR.Width <= 0 || srcR.Height <= 0) continue;
                    int sw = Math.Max(1, srcR.Width / 12), sh = Math.Max(1, srcR.Height / 12);
                    using (Bitmap small = new Bitmap(sw, sh))
                    using (Graphics sg = Graphics.FromImage(small))
                    {
                        sg.DrawImage(src, new Rectangle(0, 0, sw, sh), srcR, GraphicsUnit.Pixel);
                        InterpolationMode old = g.InterpolationMode;
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(small, srcR);
                        g.InterpolationMode = old;
                        g.PixelOffsetMode = PixelOffsetMode.Default;
                    }
                }
            }
        }
    }

    /// <summary>Copy to clipboard like Doubao's Apply: best effort, retried while another app holds it.</summary>
    static bool TryCopyToClipboard(Bitmap bmp)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetImage(bmp);
                return true;
            }
            catch (Exception)
            {
                System.Threading.Thread.Sleep(60);
            }
        }
        return false;
    }

    class Overlay : Form
    {
        // ---- annotation toolbar -------------------------------------------------
        static readonly string[] ToolKinds = { "rect", "arrow", "pen", "mosaic", "undo", "copy", "chat", "translate", "ocr", "ok", "cancel" };
        const int TB_BTN_W = 44, TB_BTN_H = 36, TB_PAD = 3;
        static readonly int TB_W = ToolKinds.Length * TB_BTN_W + (ToolKinds.Length + 1) * TB_PAD;
        const int TB_H = TB_BTN_H + 2 * TB_PAD;
        static readonly Color InkColor = Color.FromArgb(255, 235, 40, 40);
        const float InkWidth = 3f;

        readonly Bitmap frozen;
        readonly Rectangle vs;
        readonly string banner;
        public Rectangle Sel { get; set; }
        public bool Accepted { get; set; }
        /// <summary>Apply action chosen on the toolbar: session (default) | copy | translate | ocr.</summary>
        public string Action { get; set; }
        public List<Shape> Shapes { get; private set; }
        string mode;                 // "select" | "rect" | "arrow" | "pen" | "mosaic"
        Rectangle toolbarRect;
        int hoverBtn = -1;
        bool hasSel, dragging, drawing;
        Point dragStart;
        Shape cur;
        Timer acceptTimer, proofTimer;

        public Overlay(Bitmap b, Rectangle v, string bannerText)
        {
            frozen = b; vs = v; banner = bannerText;
            Shapes = new List<Shape>();
            mode = "select";
            Action = "session";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = vs;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            BackColor = Color.Black;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processIdOut);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        /// <summary>
        /// The daemon is a long-running background process, so Windows' foreground lock denies it
        /// focus when the overlay appears: the window is visible/TopMost but keyboard input stays
        /// in the user's app (Enter/Esc would never reach us). Attach to the foreground thread's
        /// input queue, then take foreground+focus — the standard snipping-tool workaround.
        /// </summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
                uint me = GetCurrentThreadId();
                bool attached = false;
                if (fgThread != 0 && fgThread != me) attached = AttachThreadInput(fgThread, me, true);
                try
                {
                    BringWindowToTop(Handle);
                    SetForegroundWindow(Handle);
                    SetFocus(Handle);
                    Activate();
                }
                finally { if (attached) AttachThreadInput(fgThread, me, false); }
            }
            catch (Exception) { }
        }

        public void Script(Rectangle r, int autoAcceptMs)
        {
            Sel = r; hasSel = true;
            acceptTimer = new Timer();
            acceptTimer.Interval = Math.Max(400, autoAcceptMs);
            acceptTimer.Tick += delegate
            {
                acceptTimer.Stop();
                if (hasSel && Sel.Width > 1) Accepted = true;
                Close();
            };
            acceptTimer.Start();
        }

        /// <summary>Pre-select a rectangle (the window under the cursor) without starting a drag.</summary>
        public void Preset(Rectangle r)
        {
            Sel = r;
            hasSel = true;
        }

        public void Proof(string path, int delayMs)
        {
            proofTimer = new Timer();
            proofTimer.Interval = Math.Max(100, delayMs);
            proofTimer.Tick += delegate
            {
                proofTimer.Stop();
                using (Bitmap b = Grab(vs))
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    b.Save(path, ImageFormat.Png);
                }
            };
            proofTimer.Start();
        }

        public void CancelNow()
        {
            Accepted = false;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintTicks++;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImageUnscaled(frozen, 0, 0);

            Rectangle sel = hasSel ? Sel : Rectangle.Empty;
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
            {
                if (!hasSel || sel.Width <= 0 || sel.Height <= 0)
                {
                    g.FillRectangle(dim, 0, 0, Width, Height);
                }
                else
                {
                    g.FillRectangle(dim, 0, 0, Width, sel.Top);
                    g.FillRectangle(dim, 0, sel.Bottom, Width, Height - sel.Bottom);
                    g.FillRectangle(dim, 0, sel.Top, sel.Left, sel.Height);
                    g.FillRectangle(dim, sel.Right, sel.Top, Width - sel.Right, sel.Height);
                    g.DrawImage(frozen, sel, sel, GraphicsUnit.Pixel);
                }
            }

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(235, 220, 0, 200)))
            using (Font f = new Font("Segoe UI", 14f, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(banner, f);
                g.FillRectangle(bg, 24, 24, sz.Width + 24, sz.Height + 16);
                g.DrawString(banner, f, Brushes.White, 36, 30);
            }

            bool toolbarAbove = false;
            if (hasSel && sel.Width > 0 && sel.Height > 0)
            {
                toolbarAbove = LayoutToolbar(sel);
                using (Pen p = new Pen(Color.FromArgb(0, 170, 255), 2f))
                {
                    g.DrawRectangle(p, sel.Left, sel.Top, sel.Width - 1, sel.Height - 1);
                }
                string label = sel.Width + " x " + sel.Height;
                using (Font f2 = new Font("Consolas", 11f, FontStyle.Bold))
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(220, 0, 0, 0)))
                {
                    SizeF sz2 = g.MeasureString(label, f2);
                    float ly;
                    if (toolbarAbove) ly = sel.Top + 4;                 // toolbar owns the space above
                    else ly = sel.Top - sz2.Height - 8;
                    if (ly < 4) ly = sel.Bottom + 8;
                    g.FillRectangle(tb, sel.Left, ly, sz2.Width + 10, sz2.Height + 4);
                    g.DrawString(label, f2, Brushes.White, sel.Left + 5, ly + 2);
                }
            }

            // committed annotations
            DrawShapes(g, frozen, Shapes, Point.Empty);
            // live stroke preview while the user is dragging one
            if (drawing && cur != null)
            {
                List<Shape> live = new List<Shape>(1);
                live.Add(cur);
                DrawShapes(g, frozen, live, Point.Empty);
            }

            if (hasSel && sel.Width > 0 && sel.Height > 0 && toolbarRect.Width > 0)
            {
                DrawToolbar(g);
            }

            using (Font f3 = new Font("Segoe UI", 10f))
            {
                string hint = mode == "select"
                    ? "drag to select   Enter = send to session   toolbar: annotate | copy/session/translate/OCR   Esc = cancel"
                    : "drag inside selection to draw   click tool again = re-select   Esc = cancel";
                g.DrawString(hint, f3, Brushes.White, 26, 78);
            }
        }

        /// <summary>Places the toolbar under the selection (above when out of room). Returns true when it sits above.</summary>
        bool LayoutToolbar(Rectangle sel)
        {
            int x = sel.Left + (sel.Width - TB_W) / 2;
            if (x < 2) x = 2;
            if (x + TB_W > Width - 2) x = Width - TB_W - 2;
            int y = sel.Bottom + 10;
            bool above = false;
            if (y + TB_H > Height - 2)
            {
                y = sel.Top - TB_H - 10;
                above = true;
                if (y < 2) y = 2;
            }
            Rectangle r = new Rectangle(x, y, TB_W, TB_H);
            if (r != toolbarRect) toolbarRect = r;
            return above;
        }

        void DrawToolbar(Graphics g)
        {
            using (SolidBrush back = new SolidBrush(Color.FromArgb(225, 24, 24, 28)))
            using (SolidBrush hot = new SolidBrush(Color.FromArgb(235, 60, 60, 70)))
            using (SolidBrush active = new SolidBrush(Color.FromArgb(235, 0, 110, 190)))
            using (Pen line = new Pen(Color.FromArgb(200, 90, 90, 100)))
            {
                g.FillRectangle(back, toolbarRect);
                g.DrawRectangle(line, toolbarRect.X, toolbarRect.Y, toolbarRect.Width - 1, toolbarRect.Height - 1);
                for (int i = 0; i < ToolKinds.Length; i++)
                {
                    Rectangle b = ToolButtonRect(i);
                    if (i == hoverBtn) g.FillRectangle(hot, b);
                    else if (ToolKinds[i] == mode) g.FillRectangle(active, b);
                    DrawToolIcon(g, i, b);
                }
            }
        }

        Rectangle ToolButtonRect(int i)
        {
            return new Rectangle(toolbarRect.X + TB_PAD + i * (TB_BTN_W + TB_PAD),
                                 toolbarRect.Y + TB_PAD, TB_BTN_W, TB_BTN_H);
        }

        int ToolButtonAt(int x, int y)
        {
            for (int i = 0; i < ToolKinds.Length; i++)
            {
                if (ToolButtonRect(i).Contains(x, y)) return i;
            }
            return -1;
        }

        void DrawToolIcon(Graphics g, int i, Rectangle b)
        {
            string k = ToolKinds[i];
            Color c = k == mode ? Color.White
                    : k == "ok" ? Color.FromArgb(120, 230, 120)
                    : k == "cancel" ? Color.FromArgb(240, 100, 100)
                    : (k == "copy" || k == "chat" || k == "translate" || k == "ocr") ? Color.FromArgb(185, 215, 255)
                    : Color.Gainsboro;
            using (Pen p = new Pen(c, 2f))
            using (SolidBrush br = new SolidBrush(c))
            {
                int cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
                if (k == "copy" || k == "chat" || k == "translate" || k == "ocr")
                {
                    // function buttons carry two-character text labels
                    string label = k == "copy" ? "复制" : k == "chat" ? "会话" : k == "translate" ? "翻译" : "识别";
                    using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                    {
                        SizeF sz = g.MeasureString(label, f);
                        g.DrawString(label, f, br, b.X + (b.Width - sz.Width) / 2, b.Y + (b.Height - sz.Height) / 2);
                    }
                    return;
                }
                switch (i)
                {
                    case 0: // rectangle
                        g.DrawRectangle(p, cx - 10, cy - 7, 20, 14);
                        break;
                    case 1: // arrow
                        g.DrawLine(p, cx - 10, cy + 7, cx + 9, cy - 7);
                        g.DrawLine(p, cx + 9, cy - 7, cx - 1, cy - 6);
                        g.DrawLine(p, cx + 9, cy - 7, cx + 8, cy + 1);
                        break;
                    case 2: // brush / pen
                        g.DrawLine(p, cx - 9, cy + 8, cx + 7, cy - 8);
                        g.FillPolygon(br, new Point[] {
                            new Point(cx + 7, cy - 8), new Point(cx + 10, cy - 5), new Point(cx + 5, cy - 3) });
                        g.FillRectangle(br, cx - 11, cy + 7, 4, 3);
                        break;
                    case 3: // mosaic: 3x3 pixel grid
                        for (int gx = -1; gx <= 1; gx++)
                            for (int gy = -1; gy <= 1; gy++)
                                g.FillRectangle(br, cx + gx * 7 - 3, cy + gy * 7 - 3, 6, 6);
                        break;
                    case 4: // undo: curved arrow
                        g.DrawArc(p, cx - 9, cy - 8, 18, 16, 0, 250);
                        g.FillPolygon(br, new Point[] {
                            new Point(cx - 11, cy - 8), new Point(cx - 1, cy - 11), new Point(cx - 3, cy - 2) });
                        break;
                    case 9: // check
                        g.DrawLines(p, new Point[] {
                            new Point(cx - 9, cy + 1), new Point(cx - 2, cy + 8), new Point(cx + 10, cy - 8) });
                        break;
                    case 10: // X
                        g.DrawLine(p, cx - 8, cy - 8, cx + 8, cy + 8);
                        g.DrawLine(p, cx + 8, cy - 8, cx - 8, cy + 8);
                        break;
                }
            }
        }

        void ToolbarAction(int i)
        {
            string k = ToolKinds[i];
            if (k == "undo")
            {
                if (Shapes.Count > 0) Shapes.RemoveAt(Shapes.Count - 1);
                Invalidate();
                return;
            }
            if (k == "ok")
            {
                if (hasSel && Sel.Width > 1 && Sel.Height > 1) { Action = "session"; Accepted = true; Close(); }
                return;
            }
            if (k == "cancel") { CancelNow(); return; }
            if (k == "copy" || k == "chat" || k == "translate" || k == "ocr")
            {
                // function buttons apply immediately with the chosen action
                if (hasSel && Sel.Width > 1 && Sel.Height > 1)
                {
                    Action = k == "chat" ? "session" : k;
                    Accepted = true;
                    Close();
                }
                return;
            }
            // drawing tools: clicking the active tool returns to select mode
            mode = (mode == k) ? "select" : k;
            Invalidate();
        }

        bool HitToolbar(MouseEventArgs e)
        {
            if (!hasSel || toolbarRect.Width <= 0) return false;
            int i = ToolButtonAt(e.X, e.Y);
            if (i < 0) return false;
            ToolbarAction(i);
            return true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { CancelNow(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (HitToolbar(e)) return;

            // click outside the current selection always restarts framing
            bool insideSel = hasSel && Sel.Contains(e.Location);
            if (mode != "select" && insideSel)
            {
                drawing = true;
                cur = new Shape();
                cur.Kind = mode;
                cur.Col = InkColor;
                cur.Width = InkWidth;
                cur.Pts = new List<Point>();
                cur.Pts.Add(e.Location);
            }
            else
            {
                mode = "select";
                dragging = true;
                dragStart = e.Location;
                hasSel = true;
                Sel = new Rectangle(e.Location, Size.Empty);
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drawing && cur != null)
            {
                if (cur.Kind == "pen") cur.Pts.Add(e.Location);
                else if (cur.Pts.Count < 2) cur.Pts.Add(e.Location);  // rect/arrow/mosaic: two-point drag
                else cur.Pts[1] = e.Location;
                Invalidate();
                return;
            }
            if (dragging)
            {
                int x = Math.Min(dragStart.X, e.X), y = Math.Min(dragStart.Y, e.Y);
                Sel = new Rectangle(x, y, Math.Abs(dragStart.X - e.X), Math.Abs(dragStart.Y - e.Y));
                Invalidate();
                return;
            }
            int h = (hasSel && toolbarRect.Width > 0) ? ToolButtonAt(e.X, e.Y) : -1;
            if (h != hoverBtn)
            {
                hoverBtn = h;
                Cursor = h >= 0 ? Cursors.Hand : Cursors.Cross;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (drawing)
            {
                drawing = false;
                if (cur != null && cur.Pts.Count >= 2 &&
                    (cur.Kind != "pen" || cur.Pts.Count > 2))
                {
                    Shapes.Add(cur);
                }
                cur = null;
                Invalidate();
                return;
            }
            dragging = false;
            if (Sel.Width < 3 || Sel.Height < 3) hasSel = false;
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (HitToolbar(e)) return;
            if (hasSel && Sel.Width > 1 && mode == "select") { Accepted = true; Close(); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { CancelNow(); return; }
            if (e.KeyCode == Keys.Enter && hasSel && Sel.Width > 1) { Accepted = true; Close(); return; }
            if (e.Control && e.KeyCode == Keys.A) { hasSel = true; Sel = new Rectangle(0, 0, Width, Height); Invalidate(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (acceptTimer != null) { acceptTimer.Stop(); acceptTimer.Dispose(); }
            if (proofTimer != null) { proofTimer.Stop(); proofTimer.Dispose(); }
            base.Dispose(disposing);
        }
    }
}






