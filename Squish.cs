// Squish - a small offline front end for 7-Zip.
// Each folder (or file) in the queue becomes its own lossless .7z archive.
// Build with build.bat (uses the C# compiler that ships with Windows, so C# 5 syntax only).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace Squish
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }

    enum JobState { Pending, Working, Done, Failed, Cancelled }

    class Job
    {
        public string Source;
        public List<string> Sources;    // set when several items are packed into one archive
        public string Target;           // that archive's path, chosen by the user
        public string Title { get { return Sources != null ? Path.GetFileNameWithoutExtension(Target) : Path.GetFileName(Source); } }
        public bool IsDir;
        public long Size = -1;      // -1 while the folder is still being measured
        public long Packed = -1;    // real archive size, once finished
        public long Est = -1;       // predicted archive size
        public bool Estimating, EstFailed;
        public string Archive;      // the archive, or its first volume
        public List<string> Parts;  // every file that makes up the archive (more than one when split into volumes)
        public JobState State;
        public bool Verified;
        public int Pct;
        public string Note;         // one-line status shown on the card
        public string Detail;       // verification report or 7-Zip error output, shown on double-click
    }

    class Options
    {
        public string Pack;         // format, level and method switches
        public string Finish;       // volumes, self-extracting, encryption mode
        public string Secret;       // the password switch, also needed to test the archive
        public string Filters;      // include/exclude switches, also applied when hashing the originals
        public string Extension;    // ".7z", ".zip", ".tar" or ".exe"
        public string Summary;      // shown in the verification report
        public string OutDir;       // null = next to the original
        public bool LowPriority;
    }

    struct FileEntry
    {
        public string Path;
        public long Size;
    }

    // ------------------------------------------------------------------ Look

    // The typeface (Jost, SIL Open Font License) is compiled into the exe, so nothing is installed or downloaded.
    static class Fonts
    {
        static readonly PrivateFontCollection embedded = new PrivateFontCollection();

        public static void Load()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using (Stream s = asm.GetManifestResourceStream(name))
                    {
                        var data = new byte[s.Length];
                        for (int got = 0, n; got < data.Length && (n = s.Read(data, got, data.Length - got)) > 0; got += n) { }
                        // Both GDI+ and plain GDI text drawing need the font, and the memory must outlive them.
                        IntPtr mem = Marshal.AllocCoTaskMem(data.Length);
                        Marshal.Copy(data, 0, mem, data.Length);
                        embedded.AddMemoryFont(mem, data.Length);
                        uint count = 0;
                        Native.AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, ref count);
                    }
                }
                catch { }
            }
        }

        public static Font Get(string family, float size)
        {
            foreach (FontFamily f in embedded.Families) if (f.Name == family) return new Font(f, size);
            return new Font("Segoe UI", size - 0.75f);
        }
    }

    static class Theme
    {
        public static bool Dark;
        public static Color Bg, Card, Raised, Border, Text, Muted, Accent, Good, Warn, Bad;
        public static Color BgTop, BgBottom, Accent2;   // gradient ends

        static Theme() { SetDark(true); }

        // Dark is a deep purple gradient with violet-to-pink accents; light is warm cream-to-peach with orange accents.
        public static void SetDark(bool dark)
        {
            Dark = dark;
            if (dark)
            {
                BgTop = Color.FromArgb(46, 21, 92); Bg = Color.FromArgb(26, 15, 54); BgBottom = Color.FromArgb(13, 9, 28);
                Card = Color.FromArgb(40, 27, 78); Raised = Color.FromArgb(58, 41, 108); Border = Color.FromArgb(84, 64, 146);
                Text = Color.FromArgb(245, 240, 255); Muted = Color.FromArgb(180, 168, 220);
                Accent = Color.FromArgb(146, 100, 255); Accent2 = Color.FromArgb(222, 104, 250);
                Good = Color.FromArgb(98, 228, 170); Warn = Color.FromArgb(255, 198, 110); Bad = Color.FromArgb(255, 128, 146);
            }
            else
            {
                BgTop = Color.FromArgb(255, 251, 244); Bg = Color.FromArgb(255, 243, 228); BgBottom = Color.FromArgb(255, 225, 194);
                Card = Color.FromArgb(255, 255, 255); Raised = Color.FromArgb(253, 238, 221); Border = Color.FromArgb(242, 215, 188);
                Text = Color.FromArgb(52, 34, 22); Muted = Color.FromArgb(142, 113, 93);
                Accent = Color.FromArgb(240, 108, 28); Accent2 = Color.FromArgb(255, 164, 46);
                Good = Color.FromArgb(22, 146, 80); Warn = Color.FromArgb(194, 118, 6); Bad = Color.FromArgb(212, 62, 62);
            }
        }

        public static Color[] Palette() { return new[] { Bg, Card, Raised, Border, Text, Muted, Accent, Good, Warn, Bad }; }

        // Follows Windows' "app mode" setting until the user picks one.
        public static bool SystemDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return !(v is int) || (int)v == 0;
                }
            }
            catch { return true; }
        }

        public const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        public static float Scale = 1;
        public static Font Font, Bold, Small, Title, Icon, IconSmall, IconBig;

        public static void Init(float scale)
        {
            Scale = scale;
            Fonts.Load();
            Font = Fonts.Get("Jost*", 10.5f);
            Bold = Fonts.Get("Jost* Medium", 11.25f);
            Small = Fonts.Get("Jost*", 9.75f);
            Title = Fonts.Get("Jost* Semi", 18f);
            Icon = new Font("Segoe MDL2 Assets", 15f);
            IconSmall = new Font("Segoe MDL2 Assets", 9f);
            IconBig = new Font("Segoe MDL2 Assets", 30f);
        }

        public static int S(int v) { return (int)Math.Round(v * Scale); }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, Rectangle r, int radius, Color c)
        {
            using (GraphicsPath p = Round(r, radius))
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        // Left-to-right gradient fill, used for anything "selected" or primary.
        public static void FillGradient(Graphics g, Rectangle r, int radius, Color from, Color to)
        {
            if (r.Width < 1 || r.Height < 1) return;
            using (GraphicsPath p = Round(r, radius))
            using (var b = new LinearGradientBrush(new Rectangle(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), from, to, 0f)) g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, Rectangle r, int radius, Color c, bool dashed)
        {
            r.Width--; r.Height--;
            using (GraphicsPath p = Round(r, radius))
            using (var pen = new Pen(c, Math.Max(1f, Scale)))
            {
                if (dashed) pen.DashPattern = new[] { 5f, 4f };
                g.DrawPath(pen, p);
            }
        }

        // Title bar in the same mode, tinted to match the top of the window where Windows allows it.
        public static void StyleTitleBar(IntPtr handle)
        {
            int dark = Dark ? 1 : 0, caption = BgTop.R | BgTop.G << 8 | BgTop.B << 16;
            Native.DwmSetWindowAttribute(handle, 20, ref dark, 4);
            Native.DwmSetWindowAttribute(handle, 35, ref caption, 4);
        }

        static bool OnCard(Control c)
        {
            for (Control p = c.Parent; p != null; p = p.Parent) if (p is CardPanel) return true;
            return false;
        }

        // The flat colour closest to what is behind a control (for blending disabled states).
        public static Color Behind(Control c) { return OnCard(c) ? Card : Bg; }

        // Paints what is behind a control: the card colour inside a card, otherwise that control's
        // slice of the window-wide background gradient, so the gradient runs unbroken under everything.
        public static void PaintBackdrop(Graphics g, Control c)
        {
            Form f = c.FindForm();
            if (OnCard(c) || f == null || !f.IsHandleCreated || !c.IsHandleCreated)
            {
                using (var b = new SolidBrush(Behind(c))) g.FillRectangle(b, c.ClientRectangle);
                return;
            }
            Point origin = f.PointToClient(c.PointToScreen(Point.Empty));
            Rectangle window = f.ClientRectangle;
            window.Offset(-origin.X, -origin.Y);
            if (window.Width < 1 || window.Height < 1) return;
            using (var b = new LinearGradientBrush(window, BgTop, BgBottom, 62f))
            {
                b.InterpolationColors = new ColorBlend { Colors = new[] { BgTop, Bg, BgBottom }, Positions = new[] { 0f, 0.45f, 1f } };
                b.WrapMode = WrapMode.TileFlipXY;
                g.FillRectangle(b, c.ClientRectangle);
            }
        }
    }

    // The Squish mark: an archive box pressed flat between two arrows, on a gradient that runs from
    // the dark theme's violet and pink into the light theme's orange. make-icon.ps1 draws the same shape for the .ico.
    class LogoMark : Control
    {
        public LogoMark()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.PaintBackdrop(e.Graphics, this);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float n = Math.Min(Width, Height) - 1;
            var r = new RectangleF(0, 0, n, n);

            using (GraphicsPath tile = Rounded(r, n * 0.26f))
            using (var b = new LinearGradientBrush(r, Color.Empty, Color.Empty, 45f))
            {
                b.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(132, 92, 255), Color.FromArgb(226, 100, 244), Color.FromArgb(255, 156, 66) },
                    Positions = new[] { 0f, 0.55f, 1f }
                };
                e.Graphics.FillPath(b, tile);
            }
            using (var pen = new Pen(Color.White, Math.Max(1.4f, n * 0.085f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                e.Graphics.DrawLines(pen, new[] { new PointF(n * 0.36f, n * 0.20f), new PointF(n * 0.50f, n * 0.32f), new PointF(n * 0.64f, n * 0.20f) });
                e.Graphics.DrawLines(pen, new[] { new PointF(n * 0.36f, n * 0.80f), new PointF(n * 0.50f, n * 0.68f), new PointF(n * 0.64f, n * 0.80f) });
            }
            using (GraphicsPath box = Rounded(new RectangleF(n * 0.17f, n * 0.41f, n * 0.66f, n * 0.18f), n * 0.07f))
                e.Graphics.FillPath(Brushes.White, box);
            using (var slot = new Pen(Color.FromArgb(150, 96, 240), n * 0.05f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                e.Graphics.DrawLine(slot, n * 0.43f, n * 0.50f, n * 0.57f, n * 0.50f);
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Layout panels that let the window gradient show through.
    class Table : TableLayoutPanel
    {
        public Table() { DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintBackdrop(e.Graphics, this); }
    }

    class Flow : FlowLayoutPanel
    {
        public Flow() { DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintBackdrop(e.Graphics, this); }
    }

    class FlatButton : Button
    {
        public bool Primary;
        bool hover, down;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        Color Shade(Color c, Color back)
        {
            if (!Enabled) return Theme.Mix(c, back, 0.65);
            if (down) return Theme.Mix(c, Color.Black, 0.18);
            return hover ? Theme.Mix(c, Primary ? Color.White : Theme.Text, Primary ? 0.14 : 0.08) : c;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color back = Theme.Behind(this);
            Theme.PaintBackdrop(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (Primary) Theme.FillGradient(g, ClientRectangle, Theme.S(9), Shade(Theme.Accent, back), Shade(Theme.Accent2, back));
            else
            {
                Theme.Fill(g, ClientRectangle, Theme.S(9), Shade(Theme.Raised, back));
                if (Enabled) Theme.Stroke(g, ClientRectangle, Theme.S(9), Theme.Border, false);
            }
            if (Focused && ShowFocusCues) Theme.Stroke(g, ClientRectangle, Theme.S(9), Theme.Text, false);

            Color text = !Enabled ? Theme.Mix(Theme.Muted, back, 0.45) : Primary ? Color.White : Theme.Text;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, Theme.Line | TextFormatFlags.HorizontalCenter);
        }
    }

    // A row of mutually exclusive choices, used instead of drop-down lists.
    class Segmented : Control
    {
        string[] items = new string[0];
        int[] edges = new int[1];
        int selected, hover = -1;

        public event EventHandler SelectedIndexChanged;

        public Segmented()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
        }

        public string[] Items
        {
            set
            {
                items = value;
                edges = new int[items.Length + 1];
                for (int i = 0; i < items.Length; i++)
                    edges[i + 1] = edges[i] + TextRenderer.MeasureText(items[i], Theme.Font).Width + Theme.S(20);
                Size = new Size(edges[items.Length], Theme.S(32));
                Invalidate();
            }
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value == selected || value < 0 || value >= items.Length) return;
                selected = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        int IndexAt(int x)
        {
            for (int i = 0; i < items.Length; i++) if (x < edges[i + 1]) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.X);
            if (i != hover) { hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) SelectedIndex = IndexAt(e.X); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.PaintBackdrop(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Fill(g, ClientRectangle, Theme.S(9), Theme.Raised);
            for (int i = 0; i < items.Length; i++)
            {
                var r = new Rectangle(edges[i], 0, edges[i + 1] - edges[i], Height);
                Color text = i == hover && Enabled ? Theme.Text : Theme.Muted;
                if (i == selected)
                {
                    double dim = Enabled ? 0 : 0.6;
                    Theme.FillGradient(g, Rectangle.Inflate(r, -Theme.S(3), -Theme.S(3)), Theme.S(7),
                        Theme.Mix(Theme.Accent, Theme.Raised, dim), Theme.Mix(Theme.Accent2, Theme.Raised, dim));
                    text = Enabled ? Color.White : Theme.Muted;
                }
                else if (!Enabled) text = Theme.Mix(Theme.Muted, Theme.Raised, 0.5);
                TextRenderer.DrawText(g, items[i], Theme.Font, r, text, Theme.Line | TextFormatFlags.HorizontalCenter);
            }
        }
    }

    // A check box drawn as a switch.
    class Toggle : CheckBox
    {
        public Toggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
            AutoSize = true;
        }

        public override Size GetPreferredSize(Size proposed)
        {
            return new Size(Theme.S(48) + TextRenderer.MeasureText(Text, Theme.Font).Width, Theme.S(26));
        }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color back = Theme.Behind(this);
            Theme.PaintBackdrop(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int h = Theme.S(18), w = Theme.S(34), knob = h - Theme.S(6);
            var track = new Rectangle(0, (Height - h) / 2, w, h);
            double dim = Enabled ? 0 : 0.6;
            if (Checked) Theme.FillGradient(g, track, h, Theme.Mix(Theme.Accent, back, dim), Theme.Mix(Theme.Accent2, back, dim));
            else
            {
                Theme.Fill(g, track, h, Theme.Mix(Theme.Raised, back, dim));
                Theme.Stroke(g, track, h, Theme.Border, false);
            }
            int x = Checked ? track.Right - knob - Theme.S(3) : track.X + Theme.S(3);
            using (var b = new SolidBrush(Checked && Enabled ? Color.White : Theme.Muted))
                g.FillEllipse(b, x, track.Y + (h - knob) / 2f - 0.5f, knob, knob);

            var text = new Rectangle(w + Theme.S(9), 0, Width - w - Theme.S(9), Height);
            TextRenderer.DrawText(g, Text, Theme.Font, text, Enabled ? Theme.Text : Theme.Muted, Theme.Line);
        }
    }

    class Meter : Control
    {
        int value;
        Color color = Theme.Accent;

        public Meter()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void Remap(Dictionary<Color, Color> map)
        {
            Color n;
            if (map.TryGetValue(color, out n)) color = n;
        }

        public void Set(int percent, Color c)
        {
            value = Math.Max(0, Math.Min(100, percent));
            color = c;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.PaintBackdrop(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Fill(g, ClientRectangle, Height, Theme.Mix(Theme.Raised, Theme.Border, 0.35));
            if (value == 0) return;
            var bar = new Rectangle(0, 0, Math.Max(Height, Width * value / 100), Height);
            Theme.FillGradient(g, bar, Height, color, color == Theme.Accent ? Theme.Accent2 : color);
        }
    }

    class CardPanel : Panel
    {
        public CardPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Card;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Theme.PaintBackdrop(e.Graphics, this);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Fill(e.Graphics, ClientRectangle, Theme.S(14), Theme.Card);
            Theme.Stroke(e.Graphics, ClientRectangle, Theme.S(14), Theme.Mix(Theme.Card, Theme.Border, 0.55), false);
        }
    }

    // ------------------------------------------------------- Advanced settings

    // The compression recipe. The main window's simple controls only set Level and Dict; the Advanced
    // dialog can set everything, mirroring 7-Zip's own "Add to Archive" options. Empty strings mean "auto".
    class Config
    {
        public bool Custom;             // false = the simple controls on the main window decide
        public string Format = "7z";
        public int Level = 9;
        public string Method = "", Dict = "", Word = "", Solid = "", Threads = "", Memory = "", Volume = "", Encryption = "";
        public bool Sfx, Shared, EncryptNames;
        public string Password = "";    // never written to disk
        public string Extra = "";       // raw 7-Zip switches

        public static readonly string[] Formats = { "7z", "zip", "tar" };
        public static readonly int[] Levels = { 0, 1, 3, 5, 7, 9 };
        public static readonly string[] LevelNames = { "Store", "Fastest", "Fast", "Normal", "Maximum", "Ultra" };
        public static readonly string[] Solids = { "", "off", "16m", "64m", "256m", "1g", "4g", "16g", "64g", "on" };
        public static readonly string[] Memories = { "", "p20", "p40", "p60", "p80" };
        public static readonly string[] Volumes = { "", "10m", "100m", "700m", "1g", "2g", "4092m", "4480m", "8128m", "23040m" };
        public static readonly string[] VolumeNames = { "No", "10 MB", "100 MB", "700 MB (CD)", "1 GB", "2 GB", "4092 MB (FAT32)", "4480 MB (DVD)", "8128 MB (DVD DL)", "23040 MB (Blu-ray)" };

        // Switches that would break the app's promises or its plumbing. -sdel deletes the originals.
        static readonly string[] Blocked = { "-sdel", "-so", "-si", "-bs", "-bb", "-bd", "-scc", "-scs", "-sa", "-sfx", "-p", "-t", "-v", "-ao", "-u", "-o", "-ad", "-y" };

        public Config Clone() { return (Config)MemberwiseClone(); }

        public static string[] Methods(string format)
        {
            if (format == "7z") return new[] { "LZMA2", "LZMA", "PPMd", "BZip2" };
            if (format == "zip") return new[] { "Deflate", "Deflate64", "BZip2", "LZMA", "PPMd" };
            return new string[0];
        }

        // The method actually used: none for tar or Store, otherwise the chosen one or the format's default.
        public string EffectiveMethod
        {
            get
            {
                string[] m = Methods(Format);
                return m.Length == 0 || Level == 0 ? "" : Array.IndexOf(m, Method) >= 0 ? Method : m[0];
            }
        }

        public static string[] Dicts(string method, long ramMb)
        {
            var list = new List<string> { "" };
            if (method == "LZMA" || method == "LZMA2")
            {
                // LZMA needs roughly 11x the dictionary in RAM while compressing.
                foreach (string d in new[] { "64k", "256k", "1m", "2m", "4m", "8m", "16m", "32m", "64m", "128m", "256m", "512m", "1024m", "1536m" })
                    if (Megabytes(d) * 12 <= ramMb * 0.6) list.Add(d);
            }
            else if (method == "PPMd")
            {
                foreach (string d in new[] { "1m", "4m", "16m", "32m", "64m", "128m", "256m", "512m", "1024m" })
                    if (Megabytes(d) <= ramMb * 0.5) list.Add(d);
            }
            else if (method == "BZip2") list.AddRange(new[] { "100k", "300k", "500k", "700k", "900k" });
            return list.ToArray();
        }

        public static string[] Words(string method)
        {
            if (method == "LZMA" || method == "LZMA2") return new[] { "", "8", "12", "16", "24", "32", "48", "64", "96", "128", "192", "256", "273" };
            if (method == "Deflate") return new[] { "", "8", "12", "16", "24", "32", "48", "64", "96", "128", "192", "256", "258" };
            if (method == "Deflate64") return new[] { "", "8", "12", "16", "24", "32", "48", "64", "96", "128", "192", "256", "257" };
            if (method == "PPMd") return new[] { "", "2", "3", "4", "5", "6", "7", "8", "10", "12", "16", "24", "32" };
            return new[] { "" };
        }

        public static string[] ThreadChoices()
        {
            var list = new List<string> { "" };
            int cores = Environment.ProcessorCount;
            for (int n = 1; n < cores; n *= 2) list.Add(n.ToString());
            list.Add(cores.ToString());
            return list.ToArray();
        }

        static double Megabytes(string size)
        {
            double n = double.Parse(size.Substring(0, size.Length - 1));
            return size.EndsWith("k") ? n / 1024 : size.EndsWith("g") ? n * 1024 : n;
        }

        public static string SizeName(string size)
        {
            if (size == "") return "Auto";
            return size.Substring(0, size.Length - 1) + (size.EndsWith("k") ? " KB" : size.EndsWith("g") ? " GB" : " MB");
        }

        // Clears choices that do not belong to the current format and method (after a change, or from an old settings file).
        public void Normalize(long ramMb)
        {
            if (Array.IndexOf(Formats, Format) < 0) Format = "7z";
            if (Array.IndexOf(Levels, Level) < 0) Level = 9;
            Method = EffectiveMethod;
            if (Array.IndexOf(Dicts(Method, ramMb), Dict) < 0) Dict = "";
            if (Array.IndexOf(Words(Method), Word) < 0) Word = "";
            if (Format != "7z" || Array.IndexOf(Solids, Solid) < 0) Solid = "";
            if (Array.IndexOf(ThreadChoices(), Threads) < 0) Threads = "";
            if (Array.IndexOf(Memories, Memory) < 0) Memory = "";
            if (Array.IndexOf(Volumes, Volume) < 0) Volume = "";
            if (Format != "7z") { Sfx = false; EncryptNames = false; }
            if (Format == "tar") Password = "";
            Encryption = Format == "zip" && Encryption == "ZipCrypto" ? "ZipCrypto" : "";
        }

        // Format, level and method switches. Also used for the size prediction, so nothing secret goes here.
        public string Core()
        {
            var sb = new StringBuilder("-t" + Format + (Format == "tar" ? "" : " -mx=" + Level));
            string m = EffectiveMethod;
            if (Format == "7z")
            {
                if (!Custom) { if (Dict != "") sb.Append(" -md=" + Dict); }
                else if (m != "")
                {
                    sb.Append(" -m0=" + m);
                    if (Dict != "") sb.Append((m == "PPMd" ? ":mem=" : ":d=") + Dict);
                    if (Word != "") sb.Append((m == "PPMd" ? ":o=" : ":fb=") + Word);
                }
                if (Solid != "") sb.Append(" -ms=" + Solid);
            }
            else if (Format == "zip" && m != "")
            {
                sb.Append(" -mm=" + m);
                if (Dict != "") sb.Append((m == "PPMd" ? " -mmem=" : " -md=") + Dict);
                if (Word != "") sb.Append((m == "PPMd" ? " -mo=" : " -mfb=") + Word);
            }
            if (Threads != "") sb.Append(" -mmt=" + Threads);
            if (Memory != "") sb.Append(" -mmemuse=" + Memory);
            foreach (string t in Tokens(Extra)) if (!IsFilter(t)) sb.Append(" " + t);
            return sb.ToString();
        }

        // Switches that shape the output file rather than the compression.
        public string Finish()
        {
            string s = "";
            if (Format == "7z" && Sfx) s += " -sfx";
            else if (Volume != "") s += " -v" + Volume;
            if (Shared) s += " -ssw";
            if (Password != "" && Format == "7z" && EncryptNames) s += " -mhe=on";
            if (Password != "" && Format == "zip") s += " -mem=" + (Encryption == "ZipCrypto" ? "ZipCrypto" : "AES256");
            return s;
        }

        // 7-Zip splits its command line on quotes only, so a password may hold anything except a double quote.
        public string Secret() { return Password != "" && Format != "tar" ? " \"-p" + Password + "\"" : ""; }

        // Include/exclude switches also apply when the originals are hashed, so the lossless check compares like with like.
        public string Filters()
        {
            string s = "";
            foreach (string t in Tokens(Extra)) if (IsFilter(t)) s += " " + t;
            return s;
        }

        public string Extension() { return Format == "7z" && Sfx ? ".exe" : "." + Format; }

        public string Describe()
        {
            var parts = new List<string> { Format == "7z" && Sfx ? "self-extracting 7z" : Format };
            if (Format != "tar") parts.Add(LevelNames[Math.Max(0, Array.IndexOf(Levels, Level))] + (EffectiveMethod != "" ? " " + EffectiveMethod : ""));
            if (Dict != "") parts.Add(SizeName(Dict) + " dictionary");
            if (Solid != "") parts.Add(Solid == "off" ? "non-solid" : Solid == "on" ? "solid" : SizeName(Solid) + " solid blocks");
            if (Volume != "" && !(Format == "7z" && Sfx)) parts.Add("split " + SizeName(Volume));
            if (Password != "" && Format != "tar") parts.Add("encrypted");
            if (Extra.Trim() != "") parts.Add("extra switches");
            return string.Join(", ", parts);
        }

        public string Preview()
        {
            return "7z a " + Core() + Finish() + (Secret() != "" ? " -p\u2022\u2022\u2022\u2022" : "") + Filters();
        }

        // Returns what is wrong with the raw switches, or null if they are acceptable.
        public static string Check(string extra)
        {
            foreach (string t in Tokens(extra))
            {
                if (!t.StartsWith("-")) return "\"" + t + "\" is not a switch. Parameters must be 7-Zip switches that start with a dash.";
                string low = t.ToLowerInvariant();
                if (low.StartsWith("-sdel")) return "-sdel deletes the original files after compressing. Squish never touches your originals, so it is not allowed.";
                foreach (string b in Blocked)
                    if (low.StartsWith(b) && !IsFilter(t)) return t + " can't be used here. Squish sets that itself (use the options in this window instead).";
            }
            return null;
        }

        static bool IsFilter(string token)
        {
            string low = token.ToLowerInvariant();
            return low.StartsWith("-x") || low.StartsWith("-i") || low == "-r" || low == "-r0" || low == "-r-";
        }

        // Splits on spaces, keeping quoted parts together.
        static List<string> Tokens(string text)
        {
            var list = new List<string>();
            var cur = new StringBuilder();
            bool quoted = false;
            foreach (char ch in text ?? "")
            {
                if (ch == '"') quoted = !quoted;
                if (char.IsWhiteSpace(ch) && !quoted) { if (cur.Length > 0) list.Add(cur.ToString()); cur.Length = 0; }
                else cur.Append(ch);
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list;
        }

        public string[] ToLines()
        {
            return new[]
            {
                "adv.custom=" + (Custom ? 1 : 0), "adv.format=" + Format, "adv.level=" + Level, "adv.method=" + Method,
                "adv.dict=" + Dict, "adv.word=" + Word, "adv.solid=" + Solid, "adv.threads=" + Threads, "adv.memory=" + Memory,
                "adv.volume=" + Volume, "adv.encryption=" + Encryption, "adv.sfx=" + (Sfx ? 1 : 0), "adv.shared=" + (Shared ? 1 : 0),
                "adv.names=" + (EncryptNames ? 1 : 0), "adv.extra=" + Extra
            };
        }

        public static Config FromSettings(Dictionary<string, string> cfg, long ramMb)
        {
            var c = new Config();
            string v; int n;
            c.Custom = cfg.TryGetValue("adv.custom", out v) && v == "1";
            if (cfg.TryGetValue("adv.format", out v)) c.Format = v;
            if (cfg.TryGetValue("adv.level", out v) && int.TryParse(v, out n)) c.Level = n;
            if (cfg.TryGetValue("adv.method", out v)) c.Method = v;
            if (cfg.TryGetValue("adv.dict", out v)) c.Dict = v;
            if (cfg.TryGetValue("adv.word", out v)) c.Word = v;
            if (cfg.TryGetValue("adv.solid", out v)) c.Solid = v;
            if (cfg.TryGetValue("adv.threads", out v)) c.Threads = v;
            if (cfg.TryGetValue("adv.memory", out v)) c.Memory = v;
            if (cfg.TryGetValue("adv.volume", out v)) c.Volume = v;
            if (cfg.TryGetValue("adv.encryption", out v)) c.Encryption = v;
            c.Sfx = cfg.TryGetValue("adv.sfx", out v) && v == "1";
            c.Shared = cfg.TryGetValue("adv.shared", out v) && v == "1";
            c.EncryptNames = cfg.TryGetValue("adv.names", out v) && v == "1";
            if (cfg.TryGetValue("adv.extra", out v) && Check(v) == null) c.Extra = v;
            c.Normalize(ramMb);
            return c;
        }
    }

    // A themed drop-down list.
    class Dropdown : Control
    {
        string[] labels = { "" }, values = { "" };
        int index;
        bool hover;

        public event EventHandler Changed;

        public Dropdown()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(Theme.S(230), Theme.S(32));
            Cursor = Cursors.Hand;
        }

        public string Value { get { return values[index]; } }

        public void SetItems(string[] names, string[] vals, string selected)
        {
            labels = names; values = vals;
            index = Math.Max(0, Array.IndexOf(vals, selected));
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || labels.Length < 2) return;
            var list = new PopupList(labels, index, Width);
            var host = new ToolStripControlHost(list) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = list.Size };
            var drop = new ToolStripDropDown { Padding = new Padding(1), Margin = Padding.Empty, BackColor = Theme.Border, AutoSize = false, Size = new Size(list.Width + 2, list.Height + 2) };
            drop.Items.Add(host);
            list.Picked += delegate(int i)
            {
                drop.Close();
                if (i == index) return;
                index = i;
                Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
            };
            drop.Show(this, new Point(0, Height + Theme.S(3)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color back = Theme.Behind(this);
            Theme.PaintBackdrop(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool live = Enabled && labels.Length > 1;
            Theme.Fill(g, ClientRectangle, Theme.S(9), live ? (hover ? Theme.Mix(Theme.Raised, Theme.Text, 0.08) : Theme.Raised) : Theme.Mix(Theme.Raised, back, 0.6));
            if (live) Theme.Stroke(g, ClientRectangle, Theme.S(9), Theme.Border, false);
            Color text = live ? Theme.Text : Theme.Mix(Theme.Muted, back, 0.35);
            TextRenderer.DrawText(g, labels[index], Theme.Font, new Rectangle(Theme.S(12), 0, Width - Theme.S(44), Height), text, Theme.Line);
            if (live) TextRenderer.DrawText(g, "\uE70D", Theme.IconSmall, new Rectangle(Width - Theme.S(34), 0, Theme.S(28), Height), Theme.Muted, Theme.Line | TextFormatFlags.HorizontalCenter);
        }
    }

    // The list a Dropdown opens.
    class PopupList : Control
    {
        readonly string[] items;
        readonly int selected, rowH = Theme.S(30);
        int hover = -1;

        public event Action<int> Picked;

        public PopupList(string[] items, int selected, int width)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            this.items = items; this.selected = selected;
            Size = new Size(width - 2, items.Length * rowH + Theme.S(8));
        }

        int RowAt(int y)
        {
            int i = (y - Theme.S(4)) / rowH;
            return y >= Theme.S(4) && i < items.Length ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = RowAt(e.Y);
            if (i != hover) { hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int i = RowAt(e.Y);
            if (i >= 0 && Picked != null) Picked(i);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < items.Length; i++)
            {
                var r = new Rectangle(Theme.S(4), Theme.S(4) + i * rowH, Width - Theme.S(8), rowH);
                if (i == selected) Theme.FillGradient(g, r, Theme.S(7), Theme.Accent, Theme.Accent2);
                else if (i == hover) Theme.Fill(g, r, Theme.S(7), Theme.Raised);
                TextRenderer.DrawText(g, items[i], Theme.Font, new Rectangle(r.X + Theme.S(10), r.Y, r.Width - Theme.S(14), r.Height), i == selected ? Color.White : Theme.Text, Theme.Line);
            }
        }
    }

    // A themed single-line text box.
    class TextField : Control
    {
        public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.None };

        public TextField()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Box.Font = Theme.Font;
            Box.BackColor = Theme.Raised;
            Box.ForeColor = Theme.Text;
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Controls.Add(Box);
            Size = new Size(Theme.S(230), Theme.S(32));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            Box.SetBounds(Theme.S(11), (Height - Box.Height) / 2, Math.Max(10, Width - Theme.S(22)), Box.Height);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Box.BackColor = Enabled ? Theme.Raised : Theme.Mix(Theme.Raised, Theme.Behind(this), 0.6);
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.PaintBackdrop(e.Graphics, this);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Fill(e.Graphics, ClientRectangle, Theme.S(9), Box.BackColor);
            if (Enabled) Theme.Stroke(e.Graphics, ClientRectangle, Theme.S(9), Box.Focused ? Theme.Accent : Theme.Border, false);
        }
    }

    // The Advanced dialog: the choices from 7-Zip's "Add to Archive" window, in the app's theme.
    class AdvancedForm : Form
    {
        public Config Result;

        readonly Config c;
        readonly long ramMb;
        readonly bool sfxAvailable;
        readonly ToolTip tips = new ToolTip();
        Dropdown ddFormat, ddLevel, ddMethod, ddDict, ddWord, ddSolid, ddThreads, ddMemory, ddVolume, ddEncryption;
        TextField txtPassword, txtExtra;
        Toggle togNames, togShow, togSfx, togShared;
        Label lblPreview;
        bool loading;

        public AdvancedForm(Config start, long ramMb, bool sfxAvailable)
        {
            c = start.Clone();
            c.Custom = true;
            this.ramMb = ramMb;
            this.sfxAvailable = sfxAvailable;
            c.Normalize(ramMb);

            Text = "Advanced settings";
            Font = Theme.Font;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(Theme.S(860), work.Width - Theme.S(24)), Math.Min(Theme.S(552), work.Height - Theme.S(48)));

            Build();
            Fill();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.StyleTitleBar(Handle); }
        protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintBackdrop(e.Graphics, this); }

        static int S(int v) { return Theme.S(v); }

        void Build()
        {
            var root = new Table { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(S(18), S(14), S(18), S(16)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(new Label
            {
                Text = "7-Zip's Add to Archive options. Whatever you pick, the archive is still verified lossless with SHA-256.",
                AutoSize = false, Dock = DockStyle.Fill, Height = S(26), AutoEllipsis = true, UseMnemonic = false, ForeColor = Theme.Muted, Margin = new Padding(S(2), 0, 0, S(10))
            }, 0, 0);

            var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(S(18), S(12), S(18), S(12)), Margin = new Padding(0) };
            var grid = new Table { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 11 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 10; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Controls.Add(grid);
            root.Controls.Add(card, 0, 1);

            // Left: how to compress
            ddFormat = Add(grid, 0, 0, "Archive format", "7z compresses best. zip opens everywhere without extra software. tar only bundles files, with no compression.");
            ddLevel = Add(grid, 0, 1, "Compression level", "How hard 7-Zip works. Every level is lossless.");
            ddMethod = Add(grid, 0, 2, "Compression method", "LZMA2 is the usual choice. PPMd is strong on plain text. BZip2 and Deflate are older and more widely supported.");
            ddDict = Add(grid, 0, 3, "Dictionary size", "How far back the compressor looks for repeated data. Bigger can compress more and needs more RAM.");
            ddWord = Add(grid, 0, 4, "Word size", "How long a match the compressor searches for (the model order for PPMd). Bigger is slower and can compress slightly more.");
            ddSolid = Add(grid, 0, 5, "Solid block size", "7z only. Files in the same block are compressed together, which shrinks more but makes extracting one file slower.");
            ddThreads = Add(grid, 0, 6, "CPU threads", "How many processor threads 7-Zip may use.");
            ddMemory = Add(grid, 0, 7, "Memory limit", "The most RAM 7-Zip may use while compressing. It lowers the thread count to stay under it.");

            // Right: what comes out
            ddVolume = Add(grid, 2, 0, "Split into volumes", "Cuts the archive into pieces of this size: name.001, name.002 and so on.");
            grid.Controls.Add(NewLabel("Password", S(26)), 2, 1);
            txtPassword = new TextField { Dock = DockStyle.Fill, Margin = new Padding(0, S(4), 0, S(4)) };
            txtPassword.Box.UseSystemPasswordChar = true;
            txtPassword.Box.TextChanged += delegate { Changed(); };
            tips.SetToolTip(txtPassword.Box, "Leave empty for no encryption. The password is never saved, and there is no way to recover it.");
            grid.Controls.Add(txtPassword, 3, 1);
            ddEncryption = Add(grid, 2, 2, "Encryption", "AES-256 is strong. ZipCrypto is weak and only useful for very old unzip programs.");

            togNames = NewToggle("Encrypt file names too", "7z only. Without the password, nobody can even see what the archive contains.");
            togShow = NewToggle("Show password", "");
            togSfx = NewToggle("Self-extracting archive (.exe)", sfxAvailable ? "7z only. Makes a program that unpacks itself, so the other person does not need 7-Zip." : "Not available: this copy of 7-Zip has no 7z.sfx module.");
            togShared = NewToggle("Include files open in other programs", "Also reads files that another program has open for writing.");
            Toggle[] toggles = { togNames, togShow, togSfx, togShared };
            for (int i = 0; i < toggles.Length; i++)
            {
                grid.Controls.Add(toggles[i], 2, 3 + i);
                grid.SetColumnSpan(toggles[i], 2);
            }

            grid.Controls.Add(NewLabel("Parameters", 0), 0, 8);
            txtExtra = new TextField { Dock = DockStyle.Fill, Margin = new Padding(0, S(4), 0, S(4)) };
            txtExtra.Box.TextChanged += delegate { Changed(); };
            tips.SetToolTip(txtExtra.Box, "Extra 7-Zip switches, for example  -xr!*.tmp  to leave out temp files, or  -mqs=on .\r\nInclude and exclude switches are applied to the lossless check as well.");
            grid.Controls.Add(txtExtra, 1, 8);
            grid.SetColumnSpan(txtExtra, 3);

            lblPreview = new Label { Dock = DockStyle.Fill, AutoSize = false, Height = S(24), AutoEllipsis = true, UseMnemonic = false, Font = Theme.Small, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, S(4), 0, 0) };
            grid.Controls.Add(lblPreview, 0, 9);
            grid.SetColumnSpan(lblPreview, 4);

            // Buttons
            var bottom = new Table { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, S(14), 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            FlatButton simple = NewButton("Use simple settings", false, delegate
            {
                Read();
                Result = c.Clone();
                Result.Custom = false;
                Result.Password = "";
                DialogResult = DialogResult.OK;
            });
            simple.Anchor = AnchorStyles.Left;
            tips.SetToolTip(simple, "Go back to the Compression and Dictionary choices on the main window.");
            var actions = new Flow { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
            FlatButton cancel = NewButton("Cancel", false, delegate { DialogResult = DialogResult.Cancel; });
            FlatButton ok = NewButton("OK", true, delegate { Accept(); });
            actions.Controls.AddRange(new Control[] { cancel, ok });
            bottom.Controls.Add(simple, 0, 0);
            bottom.Controls.Add(actions, 1, 0);
            root.Controls.Add(bottom, 0, 2);
            AcceptButton = ok;
            CancelButton = cancel;

            foreach (Control l in new Control[] { root, grid })
                foreach (Control child in l.Controls) if (child is Label) child.BackColor = Color.Transparent;
        }

        Label NewLabel(string text, int leftGap)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Muted, Margin = new Padding(leftGap, 0, S(14), 0) };
        }

        Dropdown Add(Table grid, int column, int row, string label, string tip)
        {
            grid.Controls.Add(NewLabel(label, column == 0 ? 0 : S(26)), column, row);
            var d = new Dropdown { Margin = new Padding(0, S(4), 0, S(4)) };
            if (column > 0) d.Dock = DockStyle.Fill;
            d.Changed += delegate { Changed(); };
            tips.SetToolTip(d, tip);
            grid.Controls.Add(d, column + 1, row);
            return d;
        }

        Toggle NewToggle(string text, string tip)
        {
            var t = new Toggle { Text = text, Margin = new Padding(S(26), S(5), 0, S(5)) };
            t.CheckedChanged += delegate { Changed(); };
            if (tip != "") tips.SetToolTip(t, tip);
            return t;
        }

        FlatButton NewButton(string text, bool primary, EventHandler click)
        {
            var b = new FlatButton { Text = text, Primary = primary, Font = primary ? Theme.Bold : Theme.Font, Margin = new Padding(S(8), 0, 0, 0) };
            b.Size = new Size(Math.Max(S(104), TextRenderer.MeasureText(text, b.Font).Width + S(34)), S(38));
            b.Click += click;
            return b;
        }

        // Puts the settings into the controls.
        void Fill()
        {
            loading = true;
            txtPassword.Box.Text = c.Password;
            txtExtra.Box.Text = c.Extra;
            togNames.Checked = c.EncryptNames;
            togSfx.Checked = c.Sfx && sfxAvailable;
            togShared.Checked = c.Shared;
            ddFormat.SetItems(Config.Formats, Config.Formats, c.Format);
            ddLevel.SetItems(Config.LevelNames, Array.ConvertAll(Config.Levels, delegate(int l) { return l.ToString(); }), c.Level.ToString());
            ddSolid.SetItems(Array.ConvertAll(Config.Solids, delegate(string s) { return s == "off" ? "Non-solid" : s == "on" ? "Solid" : Config.SizeName(s); }), Config.Solids, c.Solid);
            string[] threads = Config.ThreadChoices();
            ddThreads.SetItems(Array.ConvertAll(threads, delegate(string s) { return s == "" ? "Auto" : s; }), threads, c.Threads);
            ddMemory.SetItems(Array.ConvertAll(Config.Memories, delegate(string s) { return s == "" ? "Auto" : s.Substring(1) + "% of RAM"; }), Config.Memories, c.Memory);
            ddVolume.SetItems(Config.VolumeNames, Config.Volumes, c.Volume);
            loading = false;
            Dependents();
        }

        // Reads the controls back into the settings.
        void Read()
        {
            c.Format = ddFormat.Value;
            c.Level = int.Parse(ddLevel.Value);
            c.Method = ddMethod.Value;
            c.Dict = ddDict.Value;
            c.Word = ddWord.Value;
            c.Solid = ddSolid.Value;
            c.Threads = ddThreads.Value;
            c.Memory = ddMemory.Value;
            c.Volume = ddVolume.Value;
            c.Encryption = ddEncryption.Value;
            c.Password = txtPassword.Box.Text;
            c.Extra = txtExtra.Box.Text.Trim();
            c.EncryptNames = togNames.Checked;
            c.Sfx = togSfx.Checked;
            c.Shared = togShared.Checked;
        }

        void Changed()
        {
            if (loading) return;
            Read();
            Dependents();
        }

        // The method, dictionary, word and encryption lists depend on the format and method, as in 7-Zip.
        void Dependents()
        {
            loading = true;
            c.Normalize(ramMb);
            string[] methods = Config.Methods(c.Format);
            if (methods.Length == 0 || c.Level == 0) ddMethod.SetItems(new[] { c.Level == 0 && methods.Length > 0 ? "None (store)" : "None" }, new[] { "" }, "");
            else ddMethod.SetItems(methods, methods, c.Method);

            string[] dicts = Config.Dicts(c.Method, ramMb);
            ddDict.SetItems(c.Method == "Deflate" ? new[] { "32 KB" } : c.Method == "Deflate64" ? new[] { "64 KB" } : Array.ConvertAll(dicts, Config.SizeName), dicts, c.Dict);
            string[] words = Config.Words(c.Method);
            ddWord.SetItems(Array.ConvertAll(words, delegate(string s) { return s == "" ? "Auto" : s; }), words, c.Word);

            if (c.Format == "zip") ddEncryption.SetItems(new[] { "AES-256", "ZipCrypto (weak)" }, new[] { "", "ZipCrypto" }, c.Encryption);
            else ddEncryption.SetItems(new[] { c.Format == "7z" ? "AES-256" : "Not available" }, new[] { "" }, "");

            ddLevel.Enabled = c.Format != "tar";
            ddSolid.Enabled = c.Format == "7z";
            ddVolume.Enabled = !(c.Format == "7z" && c.Sfx);
            txtPassword.Enabled = togShow.Enabled = c.Format != "tar";
            ddEncryption.Enabled = c.Format == "zip" && c.Password != "";
            togNames.Enabled = c.Format == "7z" && c.Password != "";
            togSfx.Enabled = c.Format == "7z" && sfxAvailable;
            if (togSfx.Checked != c.Sfx) togSfx.Checked = c.Sfx;
            if (togNames.Checked != c.EncryptNames) togNames.Checked = c.EncryptNames;
            txtPassword.Box.UseSystemPasswordChar = !togShow.Checked;

            lblPreview.Text = c.Preview();
            tips.SetToolTip(lblPreview, c.Preview());
            loading = false;
        }

        void Accept()
        {
            Read();
            string problem = Config.Check(c.Extra);
            if (problem == null && c.Password.Contains("\"")) problem = "The password can't contain a double quote (\"), because 7-Zip's command line cannot carry one.";
            if (problem != null)
            {
                MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            c.Normalize(ramMb);
            Result = c;
            DialogResult = DialogResult.OK;
        }
    }

    // The queue: one card per item, with sizes, the prediction, progress and the verification result.
    class QueueView : Panel
    {
        public List<Job> Jobs;
        public event Action<Job> Activated;
        public event Action<Job, int> ButtonClicked;    // one of the Btn* values

        public const int BtnRemove = 0, BtnDelete = 1, BtnShow = 2;
        static readonly string[] Glyphs = { "\uE711", "\uE74D", "\uE838" };
        static readonly string[] Tips = { "Remove from the list", "Delete this archive (to the Recycle Bin)", "Show the archive in its folder" };

        readonly int rowH = Theme.S(68), gap = Theme.S(8);
        readonly ToolTip tip = new ToolTip();
        int hoverRow = -1, hoverBtn = -1;

        // Drag-and-drop feedback: an animated overlay while files are held over the window.
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 16 };
        readonly Stopwatch dragClock = new Stopwatch(), leftClock = new Stopwatch();
        bool dragging, dragLeft;
        string dragText = "";

        public QueueView()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            AutoScroll = true;
            BackColor = Theme.Bg;
            // Moving between controls fires Leave then Enter, so only end the overlay once the drag has really left.
            anim.Tick += delegate { if (dragLeft && leftClock.ElapsedMilliseconds > 120) EndDrag(); else Invalidate(); };
        }

        public void DragHover(string text)
        {
            dragLeft = false;
            if (text != null) dragText = text;
            if (dragging) return;
            dragging = true;
            dragClock.Restart();
            anim.Start();
            Invalidate();
        }

        public void DragAway() { dragLeft = true; leftClock.Restart(); }

        public void EndDrag()
        {
            dragging = dragLeft = false;
            anim.Stop();
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyScrollTheme();
        }

        public void ApplyScrollTheme() { Native.SetWindowTheme(Handle, Theme.Dark ? "DarkMode_Explorer" : "Explorer", null); }

        protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintBackdrop(e.Graphics, this); }

        public void Relayout()
        {
            AutoScrollMinSize = new Size(0, Jobs.Count * (rowH + gap));
            Invalidate();
        }

        Rectangle RowRect(int i) { return new Rectangle(0, i * (rowH + gap) + AutoScrollPosition.Y, ClientSize.Width, rowH); }

        // Finished items get Show and Delete buttons; every idle item can be removed from the list.
        static int ButtonCount(Job j) { return j.State == JobState.Working ? 0 : j.State == JobState.Done ? 3 : 1; }

        static Rectangle ButtonRect(Rectangle row, int i)
        {
            return new Rectangle(row.Right - Theme.S(40) - i * Theme.S(34), row.Y + (row.Height - Theme.S(30)) / 2, Theme.S(30), Theme.S(30));
        }

        int RowAt(Point p)
        {
            int i = (p.Y - AutoScrollPosition.Y) / (rowH + gap);
            return i >= 0 && i < Jobs.Count && RowRect(i).Contains(p) ? i : -1;
        }

        int ButtonAt(int row, Point p)
        {
            if (row < 0) return -1;
            Rectangle r = RowRect(row);
            for (int i = 0; i < ButtonCount(Jobs[row]); i++) if (ButtonRect(r, i).Contains(p)) return i;
            return -1;
        }

        protected override void OnScroll(ScrollEventArgs e) { base.OnScroll(e); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int row = RowAt(e.Location), btn = ButtonAt(row, e.Location);
            if (row == hoverRow && btn == hoverBtn) return;
            hoverRow = row; hoverBtn = btn;
            Cursor = btn >= 0 ? Cursors.Hand : Cursors.Default;
            if (btn >= 0) tip.Show(Tips[btn], this, e.X + Theme.S(12), e.Y + Theme.S(18), 3000); else tip.Hide(this);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e) { hoverRow = hoverBtn = -1; tip.Hide(this); Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int row = RowAt(e.Location), btn = ButtonAt(row, e.Location);
            if (btn < 0 || e.Button != MouseButtons.Left || ButtonClicked == null) return;
            hoverRow = hoverBtn = -1;
            tip.Hide(this);
            ButtonClicked(Jobs[row], btn);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int row = RowAt(e.Location);
            if (row >= 0 && ButtonAt(row, e.Location) < 0 && Activated != null) Activated(Jobs[row]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Jobs.Count == 0 && !dragging) PaintEmpty(g);
            for (int i = 0; i < Jobs.Count; i++)
            {
                Rectangle r = RowRect(i);
                if (r.Bottom >= 0 && r.Top <= Height) PaintRow(g, r, Jobs[i], i == hoverRow);
            }
            if (dragging) PaintDrop(g);
        }

        void PaintDrop(Graphics g)
        {
            double t = dragClock.Elapsed.TotalSeconds, fade = Math.Min(1, t / 0.18);
            int a = (int)(255 * fade);
            var r = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            Theme.Fill(g, r, Theme.S(14), Color.FromArgb((int)(240 * fade), Theme.Mix(Theme.Bg, Theme.Accent, 0.10)));

            // Marching dashed border
            using (GraphicsPath p = Theme.Round(Rectangle.Inflate(r, -Theme.S(1), -Theme.S(1)), Theme.S(14)))
            using (var pen = new Pen(Color.FromArgb(a, Theme.Accent), Theme.S(2)) { DashPattern = new[] { 4f, 3f }, DashOffset = (float)(-t * 6) })
                g.DrawPath(pen, p);

            // Pulsing rings behind a bouncing drop icon
            int cx = r.Width / 2, cy = r.Height / 2 - Theme.S(34), disc = Theme.S(40);
            for (int k = 0; k < 2; k++)
            {
                double ph = (t / 1.4 + k * 0.5) % 1.0;
                int rad = disc + (int)(ph * Theme.S(56));
                using (var pen = new Pen(Color.FromArgb((int)((1 - ph) * 120 * fade), Theme.Accent), Theme.S(2)))
                    g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
            }
            float scale = 1f + 0.06f * (float)Math.Sin(t * 5);
            int d = (int)(disc * scale);
            using (var b = new LinearGradientBrush(new Rectangle(cx - d, cy - d, d * 2, d * 2), Color.FromArgb(a, Theme.Accent), Color.FromArgb(a, Theme.Accent2), 60f))
                g.FillEllipse(b, cx - d, cy - d, d * 2, d * 2);
            int bounce = (int)(Math.Sin(t * 5) * Theme.S(4));
            TextRenderer.DrawText(g, "\uE896", Theme.IconBig, new Rectangle(cx - disc, cy - disc + bounce, disc * 2, disc * 2), Color.White, Theme.Line | TextFormatFlags.HorizontalCenter);

            TextRenderer.DrawText(g, "Drop it like it's hot", Theme.Title, new Rectangle(0, cy + disc + Theme.S(22), r.Width, Theme.S(38)), Theme.Text, Theme.Line | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, dragText, Theme.Font, new Rectangle(0, cy + disc + Theme.S(60), r.Width, Theme.S(22)), Theme.Muted, Theme.Line | TextFormatFlags.HorizontalCenter);
        }

        void PaintEmpty(Graphics g)
        {
            var r = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            Theme.Stroke(g, r, Theme.S(14), Theme.Border, true);
            int y = r.Height / 2 - Theme.S(70);
            TextRenderer.DrawText(g, "\uE8B7", Theme.IconBig, new Rectangle(0, y, r.Width, Theme.S(56)), Theme.Accent, Theme.Line | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "Drop files or folders here", Theme.Title, new Rectangle(0, y + Theme.S(58), r.Width, Theme.S(38)), Theme.Text, Theme.Line | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "Each item becomes its own .7z, or turn on All in one archive to pack them together.",
                Theme.Font, new Rectangle(0, y + Theme.S(98), r.Width, Theme.S(22)), Theme.Muted, Theme.Line | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "The size is predicted before you start, and every archive is verified lossless.",
                Theme.Font, new Rectangle(0, y + Theme.S(120), r.Width, Theme.S(22)), Theme.Muted, Theme.Line | TextFormatFlags.HorizontalCenter);
        }

        void PaintRow(Graphics g, Rectangle r, Job j, bool hovered)
        {
            Theme.Fill(g, r, Theme.S(14), hovered ? Theme.Mix(Theme.Card, Theme.Raised, 0.5) : Theme.Card);
            Theme.Stroke(g, r, Theme.S(14), Theme.Mix(Theme.Card, Theme.Border, 0.55), false);

            Color accent = j.State == JobState.Done ? Theme.Good : j.State == JobState.Failed ? Theme.Bad : Theme.Accent;
            TextRenderer.DrawText(g, j.Sources != null ? "\uE7B8" : j.IsDir ? "\uE8B7" : "\uE7C3", Theme.Icon, new Rectangle(r.X + Theme.S(12), r.Y, Theme.S(36), r.Height), accent, Theme.Line | TextFormatFlags.HorizontalCenter);

            // Right side: size, predicted or real result
            string sizes, saved = ""; Color savedColor = Theme.Muted;
            if (j.Size < 0) sizes = "Measuring...";
            else if (j.Packed >= 0)
            {
                sizes = MainForm.Fmt(j.Size) + "  \u2192  " + MainForm.Fmt(j.Packed);
                saved = MainForm.Smaller(j.Size, j.Packed, false);
                savedColor = Theme.Good;
            }
            else if (j.Est >= 0)
            {
                sizes = MainForm.Fmt(j.Size) + "  \u2192  \u2248 " + MainForm.Fmt(j.Est);
                saved = "predicted " + MainForm.Smaller(j.Size, j.Est, true);
            }
            else
            {
                sizes = MainForm.Fmt(j.Size);
                saved = j.Estimating ? "predicting size..." : "";
            }
            int rightW = Theme.S(250), right = r.Right - Theme.S(j.State == JobState.Done ? 116 : 48) - rightW;
            TextRenderer.DrawText(g, sizes, Theme.Bold, new Rectangle(right, r.Y + Theme.S(10), rightW, Theme.S(24)), Theme.Text, Theme.Line | TextFormatFlags.Right);
            TextRenderer.DrawText(g, saved, Theme.Small, new Rectangle(right, r.Y + Theme.S(34), rightW, Theme.S(20)), savedColor, Theme.Line | TextFormatFlags.Right);

            // Left side: name and status
            string status; Color statusColor = Theme.Muted;
            switch (j.State)
            {
                case JobState.Working: status = j.Note ?? "Starting..."; statusColor = Theme.Accent; break;
                case JobState.Done: status = j.Note; statusColor = j.Verified ? Theme.Good : Theme.Muted; break;
                case JobState.Failed: status = "Failed: " + j.Note; statusColor = Theme.Bad; break;
                case JobState.Cancelled: status = "Cancelled"; break;
                default: status = j.Sources != null ? j.Sources.Count + " items" : j.Source; break;
            }
            int left = r.X + Theme.S(56), leftW = right - left - Theme.S(10);
            TextRenderer.DrawText(g, j.Title, Theme.Bold, new Rectangle(left, r.Y + Theme.S(10), leftW, Theme.S(24)), Theme.Text, Theme.Line);
            TextRenderer.DrawText(g, status, Theme.Small, new Rectangle(left, r.Y + Theme.S(34), leftW, Theme.S(20)), statusColor,
                j.State == JobState.Pending ? (Theme.Line & ~TextFormatFlags.EndEllipsis) | TextFormatFlags.PathEllipsis : Theme.Line);

            if (j.State == JobState.Working)
            {
                var track = new Rectangle(left, r.Bottom - Theme.S(9), r.Right - Theme.S(16) - left, Theme.S(4));
                Theme.Fill(g, track, track.Height, Theme.Raised);
                if (j.Pct > 0) Theme.Fill(g, new Rectangle(track.X, track.Y, Math.Max(track.Height, track.Width * j.Pct / 100), track.Height), track.Height, Theme.Accent);
            }
            else
            {
                for (int i = 0; i < ButtonCount(j); i++)
                {
                    if (i == BtnRemove && !hovered) continue;
                    Rectangle c = ButtonRect(r, i);
                    bool over = hovered && i == hoverBtn;
                    Color tint = i == BtnShow ? Theme.Accent : Theme.Bad;
                    if (over) Theme.Fill(g, c, Theme.S(7), Theme.Mix(tint, Theme.Card, 0.75));
                    else if (i != BtnRemove) Theme.Fill(g, c, Theme.S(7), Theme.Raised);
                    TextRenderer.DrawText(g, Glyphs[i], Theme.IconSmall, c, over ? tint : i == BtnRemove ? Theme.Muted : Theme.Text, Theme.Line | TextFormatFlags.HorizontalCenter);
                }
            }
        }
    }

    // Predicts the archive size by building a real .7z from a small, evenly spread sample of the item,
    // with the same settings as the real run. Each sampled piece keeps its file name and extension, so
    // 7-Zip picks the same filters (e.g. the x86 filter for .exe/.dll) and groups files the same way.
    static class Estimator
    {
        const int Pieces = 32, PieceSize = 2 << 20;

        // Returns archive size / original size, or -1 if it could not be measured.
        public static double Ratio(string sevenZip, List<FileEntry> files, string switches, string extension, Action<Process> started, Func<bool> stop)
        {
            var starts = new long[files.Count];
            long total = 0;
            for (int i = 0; i < files.Count; i++) { starts[i] = total; total += files[i].Size; }
            if (total == 0) return 0;
            long wholeLimit = (long)Pieces * PieceSize;   // items this small are compressed in full

            string tmp = Path.Combine(Path.GetTempPath(), "Squish-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string dir = Path.Combine(tmp, "sample");
            Directory.CreateDirectory(dir);
            try
            {
                var buf = new byte[1 << 16];
                long fed = 0;
                int n = 0;
                if (total <= wholeLimit)
                {
                    foreach (FileEntry f in files) fed += CopyRange(f.Path, 0, long.MaxValue, Target(dir, n++, f.Path), buf);
                }
                else
                {
                    for (int c = 0; c < Pieces; c++)
                    {
                        if (stop()) return -1;
                        long pos = Math.Max(0, (long)((c + 0.5) * total / Pieces) - PieceSize / 2);
                        int i = Array.BinarySearch(starts, pos);
                        if (i < 0) i = ~i - 1;
                        while (i > 0 && starts[i] == starts[i - 1]) i--;   // skip back over empty files
                        long offset = pos - starts[i], need = PieceSize;
                        for (; need > 0 && i < files.Count; i++, offset = 0)
                        {
                            if (files[i].Size <= offset) continue;
                            long got = CopyRange(files[i].Path, offset, need, Target(dir, n++, files[i].Path), buf);
                            need -= Math.Min(need, files[i].Size - offset);
                            fed += got;
                        }
                    }
                }
                if (fed == 0 || stop()) return fed == 0 ? 0 : -1;

                string archive = Path.Combine(tmp, "sample" + extension);
                var psi = new ProcessStartInfo(sevenZip, "a " + switches + " -bso0 -bse0 -bsp0 -y \"" + archive + "\" \"" + dir + "\\*\"")
                {
                    UseShellExecute = false, CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                    started(p);
                    p.WaitForExit();
                    if (p.ExitCode != 0 || !File.Exists(archive)) return -1;
                }
                return Math.Min(1.02, (double)new FileInfo(archive).Length / fed);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        static string Target(string dir, int n, string source)
        {
            return Path.Combine(dir, n.ToString("D5") + "_" + Path.GetFileName(source));
        }

        // Copies part of a file into a new file. Unreadable files are skipped.
        static long CopyRange(string path, long offset, long count, string to, byte[] buf)
        {
            long done = 0;
            FileStream f;
            try { f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); } catch { return 0; }
            using (f)
            using (var o = new FileStream(to, FileMode.Create, FileAccess.Write))
            {
                try { f.Seek(offset, SeekOrigin.Begin); } catch { return 0; }
                while (done < count)
                {
                    int n;
                    try { n = f.Read(buf, 0, (int)Math.Min(buf.Length, count - done)); } catch { break; }
                    if (n <= 0) break;
                    o.Write(buf, 0, n);
                    done += n;
                }
            }
            return done;
        }
    }

    // ------------------------------------------------------------- The app

    class MainForm : Form
    {
        const string AppName = "Squish";

        static readonly string[] LevelNames = { "Fast", "Normal", "Maximum", "Ultra" };
        static readonly int[] LevelMx = { 3, 5, 7, 9 };
        static readonly string[] LevelHints =
        {
            "Quickest. Bigger archives.",
            "A good balance of speed and size.",
            "Smaller archives, noticeably slower.",
            "Smallest archives. Can take hours on very large folders."
        };
        static readonly Regex ProgressRx = new Regex(@"^\s*(\d{1,3})%(?:\s+\d+)?(?:\s+\S(?=\s))?\s*(.*)$", RegexOptions.Compiled);
        static readonly Regex InfoRx = new Regex(@"^(SHA256 for data and names|SHA256 for data|Files|Folders|Size):\s*(\S+)", RegexOptions.Compiled);
        static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
        static readonly string PortableSettings = Path.Combine(AppDir, "Squish.ini");
        static readonly string UserSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Squish\Squish.ini");

        readonly string[] startupPaths;
        readonly List<Job> jobs = new List<Job>();
        readonly List<int> dictChoices = new List<int>();
        readonly ToolTip tips = new ToolTip();
        readonly AutoResetEvent estWake = new AutoResetEvent(false);

        QueueView queue;
        Segmented segLevel, segDict, segOut;
        Toggle togLowPrio, togCombine;
        FlatButton btnAdvanced;
        Label lblDictHint;
        Config adv = new Config();      // what the Advanced dialog last set
        long ramMb;
        FlatButton btnBrowse, btnClear, btnTheme, btnStart, btnPause, btnCancel;
        Meter meter;
        Label lblLevelHint, lblOut, lblSummary, lblStatus, lblFile;

        string sevenZip, outDir = "";
        bool running, paused, closeWhenDone;
        int started, total, estGen;
        volatile bool cancel;
        volatile Process current, estProc;
        Stopwatch phaseClock = new Stopwatch();

        public MainForm(string[] args)
        {
            startupPaths = args;
            float scale;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            Theme.Init(scale);
            Dictionary<string, string> cfg = LoadSettings();
            string mode;
            Theme.SetDark(cfg.TryGetValue("theme", out mode) ? mode != "light" : Theme.SystemDark());

            Text = AppName;
            Font = Theme.Font;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            // Fit small laptop screens and high scaling settings.
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(S(900), work.Width - S(24)), Math.Min(S(680), work.Height - S(48)));
            MinimumSize = new Size(Math.Min(S(780), work.Width), Math.Min(S(600), work.Height));
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            HookDrop(this);
            ClearLabelBackgrounds(this);
            ApplySettings(cfg);

            string saved;
            cfg.TryGetValue("sevenzip", out saved);
            sevenZip = Find7z(saved);
            ShowIdleStatus();
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            UpdateUi();
        }

        static int S(int v) { return Theme.S(v); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBar();
        }

        protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintBackdrop(e.Graphics, this); }

        // The gradient spans the window, so everything repaints when the window changes size.
        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(true); }

        // Labels are see-through so the gradient (or card) shows behind their text.
        static void ClearLabelBackgrounds(Control c)
        {
            if (c is Label) c.BackColor = Color.Transparent;
            foreach (Control child in c.Controls) ClearLabelBackgrounds(child);
        }

        void ApplyTitleBar() { Theme.StyleTitleBar(Handle); }

        void ToggleTheme()
        {
            Color[] before = Theme.Palette();
            Theme.SetDark(!Theme.Dark);
            Color[] after = Theme.Palette();
            var map = new Dictionary<Color, Color>();
            for (int i = 0; i < before.Length; i++) map[before[i]] = after[i];

            SuspendLayout();
            Recolor(this, map);
            meter.Remap(map);
            queue.ApplyScrollTheme();
            ApplyTitleBar();
            UpdateThemeButton();
            ResumeLayout();
            Invalidate(true);
            SaveSettings();
        }

        static void Recolor(Control c, Dictionary<Color, Color> map)
        {
            Color n;
            if (map.TryGetValue(c.BackColor, out n)) c.BackColor = n;
            if (map.TryGetValue(c.ForeColor, out n)) c.ForeColor = n;
            foreach (Control child in c.Controls) Recolor(child, map);
        }

        void UpdateThemeButton()
        {
            btnTheme.Text = Theme.Dark ? "\uE706" : "\uE708";   // sun in dark mode, moon in light mode
            tips.SetToolTip(btnTheme, Theme.Dark ? "Switch to light mode" : "Switch to dark mode");
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Remove size-prediction samples left behind if the app was ever force-closed mid-prediction.
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    foreach (string d in Directory.GetDirectories(Path.GetTempPath(), "Squish-*"))
                        if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddMinutes(-10)) try { Directory.Delete(d, true); } catch { }
                }
                catch { }
            });
            new Thread(EstimatorLoop) { IsBackground = true }.Start();
            AddPaths(startupPaths);
        }

        // ---------------------------------------------------------------- UI

        void BuildUi()
        {
            var root = NewTable(1, 6);
            root.Dock = DockStyle.Fill;
            root.AutoSize = false;
            root.Padding = new Padding(S(18), S(14), S(18), S(16));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // Header
            var header = NewTable(3, 1);
            header.Dock = DockStyle.Fill;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(new LogoMark { Size = new Size(S(48), S(48)), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, S(12), 0) }, 0, 0);
            var titles = NewTable(1, 2);
            titles.Controls.Add(new Label { Text = AppName, Font = Theme.Title, AutoSize = true, Margin = new Padding(0) }, 0, 0);
            titles.Controls.Add(new Label { Text = "Lossless .7z archives. Works offline, nothing to install.", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(S(2), 0, 0, 0) }, 0, 1);
            var adders = NewFlow();
            adders.Anchor = AnchorStyles.Right;
            btnClear = NewButton("Clear", delegate { RemoveWhere(delegate(Job j) { return true; }); });
            btnTheme = NewButton("", delegate { ToggleTheme(); });
            btnTheme.Font = new Font("Segoe MDL2 Assets", 12f);
            btnTheme.Size = new Size(S(40), S(34));
            adders.Controls.AddRange(new Control[]
            {
                NewButton("Add Folder...", delegate { AddPaths(Shell.PickFolders(this, "Choose folders to compress", true)); }),
                NewButton("Add Files...", delegate { AddFilesClick(); }),
                btnClear,
                btnTheme
            });
            UpdateThemeButton();
            header.Controls.Add(titles, 1, 0);
            header.Controls.Add(adders, 2, 0);
            root.Controls.Add(header, 0, 0);

            // Queue
            queue = new QueueView { Jobs = jobs, Dock = DockStyle.Fill, Margin = new Padding(0, S(14), 0, S(12)) };
            queue.ButtonClicked += delegate(Job j, int button)
            {
                if (button == QueueView.BtnRemove) RemoveWhere(delegate(Job x) { return x == j; });
                else if (button == QueueView.BtnDelete) DeleteArchive(j);
                else ShowArchive(j);
            };
            queue.Activated += RowActivated;
            root.Controls.Add(queue, 0, 1);

            // Options
            var card = new CardPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(16), S(10), S(16), S(10)), Margin = new Padding(0) };
            var opts = NewTable(3, 4);
            opts.Dock = DockStyle.Top;
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            segLevel = NewSegmented(LevelNames);
            segLevel.SelectedIndexChanged += delegate { LevelChanged(); };
            lblLevelHint = NewHint("");
            tips.SetToolTip(segLevel, "How hard 7-Zip works. Every level is lossless; higher levels are smaller and slower.");

            long ramKb;
            if (!Native.GetPhysicallyInstalledSystemMemory(out ramKb) || ramKb <= 0) ramKb = 8L * 1024 * 1024;
            var dictNames = new List<string>();
            ramMb = ramKb / 1024;
            foreach (int mb in new[] { 0, 64, 128, 256, 512, 1024 })
            {
                // LZMA2 needs roughly 11x the dictionary in RAM while compressing.
                if (mb * 12L > ramKb / 1024 * 0.6) continue;
                dictChoices.Add(mb);
                dictNames.Add(mb == 0 ? "Auto" : mb + " MB");
            }
            segDict = NewSegmented(dictNames.ToArray());
            tips.SetToolTip(segDict, "How far back 7-Zip looks for repeated data. Auto picks a size from the level.\r\nBigger uses about 11x this much RAM while compressing.");

            segOut = NewSegmented(new[] { "Next to the original", "Choose a folder" });
            segOut.SelectedIndexChanged += delegate { OutModeChanged(); };
            lblOut = NewHint("");
            btnBrowse = NewButton("Browse...", delegate { BrowseOut(); });
            btnBrowse.Margin = new Padding(S(8), S(4), 0, S(4));
            var outRow = NewTable(2, 1);
            outRow.Dock = DockStyle.Fill;
            outRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            outRow.Controls.Add(lblOut, 0, 0);
            outRow.Controls.Add(btnBrowse, 1, 0);

            opts.Controls.Add(NewLabel("Compression"), 0, 0);
            opts.Controls.Add(segLevel, 1, 0);
            opts.Controls.Add(lblLevelHint, 2, 0);
            opts.Controls.Add(NewLabel("Dictionary"), 0, 1);
            opts.Controls.Add(segDict, 1, 1);
            lblDictHint = NewHint("");
            opts.Controls.Add(lblDictHint, 2, 1);
            opts.Controls.Add(NewLabel("Save to"), 0, 2);
            opts.Controls.Add(segOut, 1, 2);
            opts.Controls.Add(outRow, 2, 2);

            var toggles = NewFlow();
            toggles.Margin = new Padding(0, S(6), 0, 0);
            var verified = new Label
            {
                Text = "\u2714  Verified lossless with SHA-256, always",
                AutoSize = true, ForeColor = Theme.Good, Anchor = AnchorStyles.Left, Margin = new Padding(0, S(3), S(24), 0)
            };
            togLowPrio = new Toggle { Text = "Keep PC responsive", Margin = new Padding(0) };
            tips.SetToolTip(verified, "After compressing, the archive is fully decompressed and every file is hashed with SHA-256,\r\nthen compared with a fresh hash of the original files. If anything differs, or a file could not\r\nbe read, the archive is removed and the item is marked as failed. This cannot be turned off.");
            tips.SetToolTip(togLowPrio, "7-Zip runs at low priority and gives way to other programs.");
            togCombine = new Toggle { Text = "All in one archive", Margin = new Padding(0, 0, S(24), 0) };
            tips.SetToolTip(togCombine, "On: everything in the queue is packed into a single .7z, which you name when you press Start.\r\nOff: each item becomes its own archive.");
            togLowPrio.Margin = new Padding(0, 0, S(24), 0);
            btnAdvanced = NewButton("Advanced...", delegate { AdvancedClick(); });
            btnAdvanced.Margin = new Padding(0);
            btnAdvanced.Height = S(30);
            tips.SetToolTip(btnAdvanced, "Everything 7-Zip offers: format, method, word size, solid blocks, threads,\r\nvolumes, password and raw switches. The lossless check still always runs.");
            toggles.Controls.AddRange(new Control[] { verified, togCombine, togLowPrio, btnAdvanced });
            opts.Controls.Add(toggles, 0, 3);
            opts.SetColumnSpan(toggles, 3);
            card.Controls.Add(opts);
            root.Controls.Add(card, 0, 2);

            // Progress and actions
            lblSummary = new Label { Dock = DockStyle.Fill, AutoSize = false, Height = S(24), AutoEllipsis = true, UseMnemonic = false, Font = Theme.Bold, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, S(12), 0, S(4)) };
            tips.SetToolTip(lblSummary, "Predicted by compressing an evenly spread sample of each item with the same settings.\r\n" +
                                        "Usually within about 5%. Folders of many similar small files often end up smaller than predicted.");
            root.Controls.Add(lblSummary, 0, 3);
            meter = new Meter { Dock = DockStyle.Fill, Height = S(8), Margin = new Padding(0, S(2), 0, S(8)) };
            root.Controls.Add(meter, 0, 4);

            var bottom = NewTable(2, 1);
            bottom.Dock = DockStyle.Fill;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var texts = NewTable(1, 2);
            texts.Dock = DockStyle.Fill;
            texts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lblStatus = new Label { Dock = DockStyle.Fill, AutoSize = false, Height = S(22), AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
            lblFile = new Label { Dock = DockStyle.Fill, AutoSize = false, Height = S(20), AutoEllipsis = true, UseMnemonic = false, Font = Theme.Small, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
            texts.Controls.Add(lblStatus, 0, 0);
            texts.Controls.Add(lblFile, 0, 1);
            var actions = NewFlow();
            btnStart = NewButton("Start", delegate { StartClick(); });
            btnStart.Primary = true;
            btnStart.Font = Theme.Bold;
            btnStart.Size = new Size(S(130), S(40));
            btnPause = NewButton("Pause", delegate { PauseClick(); });
            btnCancel = NewButton("Cancel", delegate { CancelClick(); });
            btnPause.Height = btnCancel.Height = S(40);
            actions.Controls.AddRange(new Control[] { btnPause, btnCancel, btnStart });
            bottom.Controls.Add(texts, 0, 0);
            bottom.Controls.Add(actions, 1, 0);
            root.Controls.Add(bottom, 0, 5);
        }

        // Files can be dropped anywhere on the window; the queue shows the drop overlay.
        void HookDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += delegate(object s, DragEventArgs e)
            {
                var paths = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
                e.Effect = paths != null ? DragDropEffects.Copy : DragDropEffects.None;
                if (paths != null) queue.DragHover(DropText(paths));
            };
            c.DragOver += delegate(object s, DragEventArgs e)
            {
                bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
                e.Effect = files ? DragDropEffects.Copy : DragDropEffects.None;
                if (files) queue.DragHover(null);
            };
            c.DragLeave += delegate { queue.DragAway(); };
            c.DragDrop += delegate(object s, DragEventArgs e)
            {
                queue.EndDrag();
                AddPaths(e.Data.GetData(DataFormats.FileDrop) as string[]);
            };
            foreach (Control child in c.Controls) HookDrop(child);
        }

        static string DropText(string[] paths)
        {
            int dirs = 0, files = 0;
            foreach (string p in paths) { if (Directory.Exists(p)) dirs++; else files++; }
            var parts = new List<string>();
            if (dirs > 0) parts.Add(dirs + (dirs == 1 ? " folder" : " folders"));
            if (files > 0) parts.Add(files + (files == 1 ? " file" : " files"));
            return "Release to add " + string.Join(" and ", parts) + " to the queue";
        }

        static TableLayoutPanel NewTable(int columns, int rows)
        {
            return new Table { ColumnCount = columns, RowCount = rows, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        }

        static FlowLayoutPanel NewFlow()
        {
            return new Flow { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        }

        static FlatButton NewButton(string text, EventHandler click)
        {
            var b = new FlatButton { Text = text, Font = Theme.Font, Margin = new Padding(S(8), 0, 0, 0) };
            b.Size = new Size(Math.Max(S(96), TextRenderer.MeasureText(text, Theme.Font).Width + S(34)), S(34));
            b.Click += click;
            return b;
        }

        static Segmented NewSegmented(string[] items)
        {
            return new Segmented { Items = items, Margin = new Padding(0, S(4), S(14), S(4)) };
        }

        static Label NewLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Muted, Margin = new Padding(0, 0, S(16), 0) };
        }

        static Label NewHint(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Font = Theme.Small, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
        }

        void UpdateUi()
        {
            bool canStart = jobs.Exists(delegate(Job j) { return j.State != JobState.Done && j.State != JobState.Working; });
            btnStart.Enabled = !running && canStart;
            btnPause.Enabled = running && !cancel;
            btnPause.Text = paused ? "Resume" : "Pause";
            btnCancel.Enabled = running && !cancel;
            btnClear.Enabled = jobs.Count > 0;
            segLevel.Enabled = segDict.Enabled = !running && !adv.Custom;
            segOut.Enabled = togLowPrio.Enabled = togCombine.Enabled = btnAdvanced.Enabled = !running;
            btnBrowse.Visible = segOut.SelectedIndex == 1;
            btnBrowse.Enabled = !running;
            lblOut.Text = segOut.SelectedIndex == 1 ? outDir : "Each archive is saved beside its original.";
            lblLevelHint.Text = adv.Custom ? "Advanced: " + adv.Describe() : LevelHints[segLevel.SelectedIndex];
            lblDictHint.Text = adv.Custom ? "Change these in Advanced, or go back to simple there." : "Bigger can shrink more, but needs far more RAM.";
            UpdateSummary();
        }

        void UpdateSummary()
        {
            long size = 0, result = 0;
            bool predicted = false, waiting = false;
            foreach (Job j in jobs)
            {
                long r = j.Packed >= 0 ? j.Packed : j.Est;
                if (j.Size < 0 || r < 0) { waiting = true; continue; }
                if (j.Packed < 0) predicted = true;
                size += j.Size; result += r;
            }
            string s = jobs.Count == 0 ? "" : jobs.Count + (jobs.Count == 1 ? " item" : " items");
            if (size > 0) s += "   \u00B7   " + Fmt(size) + "  \u2192  " + (predicted ? "\u2248 " : "") + Fmt(result) + "   \u00B7   " + (predicted ? "predicted " : "") + Smaller(size, result, predicted);
            if (waiting && !running) s += "   \u00B7   predicting...";
            lblSummary.Text = s;
        }

        void ShowIdleStatus()
        {
            lblStatus.Text = sevenZip != null ? "Ready." : "7-Zip was not found. Press Start to locate 7z.exe.";
            lblFile.Text = sevenZip != null ? "Using " + sevenZip : "";
        }

        // ------------------------------------------------------------- Queue

        void AddPaths(IEnumerable<string> paths)
        {
            if (paths == null) return;
            bool wholeDrive = false;
            foreach (string raw in paths)
            {
                string p;
                try { p = Path.GetFullPath(raw).TrimEnd('\\'); } catch { continue; }
                if (p.Length <= 2) { wholeDrive = true; continue; }
                bool dir = Directory.Exists(p);
                if (!dir && !File.Exists(p)) continue;
                if (jobs.Exists(delegate(Job j) { return string.Equals(j.Source, p, StringComparison.OrdinalIgnoreCase); })) continue;

                var job = new Job { Source = p, IsDir = dir };
                jobs.Add(job);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    long size = 0;
                    try { size = job.IsDir ? Walk(job.Source, null) : new FileInfo(job.Source).Length; } catch { }
                    Ui(delegate { job.Size = size; queue.Invalidate(); UpdateSummary(); estWake.Set(); });
                });
            }
            queue.Relayout();
            UpdateUi();
            if (wholeDrive)
                MessageBox.Show(this, "A whole drive can't be added. Pick a folder inside it instead.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void AddFilesClick()
        {
            using (var d = new OpenFileDialog { Title = "Choose files to compress", Multiselect = true, Filter = "All files|*.*" })
                if (d.ShowDialog(this) == DialogResult.OK) AddPaths(d.FileNames);
        }

        // The item being compressed right now always stays in the list.
        void RemoveWhere(Predicate<Job> match)
        {
            jobs.RemoveAll(delegate(Job j) { return j.State != JobState.Working && match(j); });
            queue.Relayout();
            UpdateUi();
        }

        void RowActivated(Job j)
        {
            bool hasArchive = j.Archive != null && File.Exists(j.Archive);
            if (!string.IsNullOrEmpty(j.Detail))
            {
                string text = j.Detail.Length > 3000 ? j.Detail.Substring(0, 3000) + "\r\n..." : j.Detail;
                if (j.State != JobState.Done || !hasArchive)
                {
                    MessageBox.Show(this, text, j.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (MessageBox.Show(this, text + "\r\n\r\nShow the archive in Explorer?", j.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            }
            if (hasArchive) ShowArchive(j);
        }

        void ShowArchive(Job j)
        {
            if (j.Archive != null && File.Exists(j.Archive)) Process.Start("explorer.exe", "/select,\"" + j.Archive + "\"");
            else ArchiveGone(j);
        }

        // Deletes a finished archive from inside the app. The original files are never touched.
        void DeleteArchive(Job j)
        {
            if (j.State != JobState.Done) return;
            if (j.Archive == null || !File.Exists(j.Archive)) { ArchiveGone(j); return; }
            List<string> parts = (j.Parts ?? new List<string> { j.Archive }).FindAll(File.Exists);
            long size = 0;
            foreach (string p in parts) size += new FileInfo(p).Length;
            string msg = "Delete this archive?\r\n\r\n" + j.Archive + (parts.Count > 1 ? "\r\nand its " + (parts.Count - 1) + " other volume" + (parts.Count > 2 ? "s" : "") : "") + "\r\n" + Fmt(size) +
                         "\r\n\r\nIt goes to the Recycle Bin (drives without one delete it permanently).\r\nYour original files are not touched.";
            if (MessageBox.Show(this, msg, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try { foreach (string p in parts) FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The archive could not be deleted.\r\n\r\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ResetJob(j);
            lblStatus.Text = "Archive deleted. Its contents are back in the queue if you want to compress them again.";
        }

        void ArchiveGone(Job j)
        {
            MessageBox.Show(this, "That archive is no longer there. It may have been moved or deleted.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ResetJob(j);
        }

        // An all-in-one archive that is deleted, cancelled or failed goes back to being separate items.
        void Explode(Job j)
        {
            jobs.Remove(j);
            AddPaths(j.Sources);
        }

        void ResetJob(Job j)
        {
            if (j.Sources != null) { Explode(j); estWake.Set(); return; }
            j.State = JobState.Pending; j.Archive = j.Note = j.Detail = null; j.Packed = -1; j.Verified = false;
            queue.Invalidate();
            UpdateUi();
            estWake.Set();
        }

        // ------------------------------------------------------------ Actions

        // The recipe in effect: the Advanced one, or one built from the two simple controls.
        Config Current()
        {
            if (adv.Custom) return adv;
            int mb = dictChoices[segDict.SelectedIndex];
            return new Config { Level = LevelMx[segLevel.SelectedIndex], Dict = mb > 0 ? mb + "m" : "" };
        }

        // Self-extracting archives need 7-Zip's 7z.sfx module next to 7z.exe.
        bool SfxAvailable { get { return sevenZip != null && File.Exists(Path.Combine(Path.GetDirectoryName(sevenZip), "7z.sfx")); } }

        void AdvancedClick()
        {
            using (var d = new AdvancedForm(Current(), ramMb, SfxAvailable))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                adv = d.Result;
            }
            LevelChanged();
            SaveSettings();
        }

        void LevelChanged()
        {
            // Predictions depend on the level, so start them over.
            estGen++;
            foreach (Job j in jobs) { j.Est = -1; j.EstFailed = false; }
            KillQuietly(estProc);
            queue.Invalidate();
            UpdateUi();
            estWake.Set();
        }

        void OutModeChanged()
        {
            if (segOut.SelectedIndex == 1 && !Directory.Exists(outDir) && !BrowseOut()) { segOut.SelectedIndex = 0; return; }
            UpdateUi();
        }

        bool BrowseOut()
        {
            string[] picked = Shell.PickFolders(this, "Choose where to save the archives", false);
            if (picked.Length == 0) return false;
            outDir = picked[0];
            UpdateUi();
            return true;
        }

        bool Locate7z()
        {
            MessageBox.Show(this, "7-Zip was not found on this PC.\r\n\r\nInstall 7-Zip, or point to 7z.exe in the next window.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            using (var d = new OpenFileDialog { Title = "Locate 7z.exe", Filter = "7z.exe|7z.exe" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                sevenZip = d.FileName;
                return true;
            }
        }

        // Uses the predictions to warn before a drive fills up half way through.
        bool EnoughSpace(Options o)
        {
            var need = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (Job j in jobs)
            {
                if (j.State != JobState.Pending || j.Est < 0) continue;
                string drive = Path.GetPathRoot(j.Target ?? o.OutDir ?? j.Source);
                long sum;
                need.TryGetValue(drive, out sum);
                need[drive] = sum + j.Est;
            }
            foreach (KeyValuePair<string, long> n in need)
            {
                long free;
                try { free = new DriveInfo(n.Key).AvailableFreeSpace; } catch { continue; }
                if (free >= n.Value) continue;
                string msg = "Drive " + n.Key + " has " + Fmt(free) + " free, but the archives are predicted to need about " + Fmt(n.Value) + ".\r\n\r\nStart anyway?";
                if (MessageBox.Show(this, msg, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
            }
            return true;
        }

        void StartClick()
        {
            if (running) return;
            if ((sevenZip == null || !File.Exists(sevenZip)) && !Locate7z()) return;

            Config recipe = Current();
            if (recipe.Sfx && !SfxAvailable)
            {
                MessageBox.Show(this, "A self-extracting archive needs 7-Zip's 7z.sfx file next to 7z.exe, and it is missing. Turn that option off in Advanced.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var o = new Options
            {
                Pack = recipe.Core(), Finish = recipe.Finish(), Secret = recipe.Secret(), Filters = recipe.Filters(),
                Extension = recipe.Extension(), Summary = recipe.Describe(), LowPriority = togLowPrio.Checked
            };
            if (segOut.SelectedIndex == 1)
            {
                if (!Directory.Exists(outDir) && !BrowseOut()) return;
                o.OutDir = Path.GetFullPath(outDir);
            }

            foreach (Job j in jobs.FindAll(delegate(Job x) { return x.State == JobState.Failed || x.State == JobState.Cancelled; }))
            {
                if (j.Sources != null) { Explode(j); continue; }
                j.State = JobState.Pending; j.Note = j.Detail = j.Archive = null; j.Packed = -1;
            }
            if ((togCombine.Checked && !Combine(o)) || !EnoughSpace(o)) { queue.Invalidate(); UpdateUi(); return; }

            SaveSettings();
            cancel = false; paused = false; running = true; started = 0;
            estGen++;
            KillQuietly(estProc);
            meter.Set(0, Theme.Accent);
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);
            new Thread(delegate() { Work(o); }) { IsBackground = true }.Start();
            queue.Invalidate();
            UpdateUi();
        }

        // Replaces the waiting items with one job that packs them all into a single archive the user names.
        bool Combine(Options o)
        {
            List<Job> waiting = jobs.FindAll(delegate(Job j) { return j.State == JobState.Pending; });
            if (waiting.Count < 2) return true;     // a single item is just a normal archive

            string parent = Path.GetDirectoryName(waiting[0].Source);
            bool together = waiting.TrueForAll(delegate(Job j) { return string.Equals(Path.GetDirectoryName(j.Source), parent, StringComparison.OrdinalIgnoreCase); });
            string name = together ? Path.GetFileName(parent) : "";
            string target;
            using (var d = new SaveFileDialog
            {
                Title = "Save " + waiting.Count + " items as one archive", Filter = "Archive|*" + o.Extension, DefaultExt = o.Extension.Substring(1), AddExtension = true,
                InitialDirectory = o.OutDir ?? parent, FileName = (string.IsNullOrEmpty(name) ? "Archive" : name) + o.Extension, OverwritePrompt = true
            })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                target = d.FileName;
            }
            if (!target.EndsWith(o.Extension, StringComparison.OrdinalIgnoreCase)) target += o.Extension;
            if (waiting.Exists(delegate(Job j) { return j.IsDir && target.StartsWith(j.Source + "\\", StringComparison.OrdinalIgnoreCase); }))
            {
                MessageBox.Show(this, "The archive can't be saved inside one of the folders being compressed. Pick another place.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            var all = new Job { Source = target, Target = target, IsDir = true, Sources = waiting.ConvertAll(delegate(Job j) { return j.Source; }) };
            if (waiting.TrueForAll(delegate(Job j) { return j.Size >= 0; })) { all.Size = 0; foreach (Job j in waiting) all.Size += j.Size; }
            else ThreadPool.QueueUserWorkItem(delegate
            {
                long size = 0;
                foreach (string p in all.Sources)
                    try { size += Directory.Exists(p) ? Walk(p, null) : new FileInfo(p).Length; } catch { }
                Ui(delegate { all.Size = size; queue.Invalidate(); UpdateSummary(); });
            });
            if (waiting.TrueForAll(delegate(Job j) { return j.Est >= 0; })) { all.Est = 0; foreach (Job j in waiting) all.Est += j.Est; }
            jobs.RemoveAll(waiting.Contains);
            jobs.Add(all);
            queue.Relayout();
            return true;
        }

        void PauseClick()
        {
            Process p = current;
            if (p == null) return;
            try
            {
                if (paused) { Native.NtResumeProcess(p.Handle); phaseClock.Start(); }
                else { Native.NtSuspendProcess(p.Handle); phaseClock.Stop(); }
                paused = !paused;
                if (paused) lblStatus.Text = "Paused";
            }
            catch { }
            UpdateUi();
        }

        void CancelClick()
        {
            cancel = true;
            KillQuietly(current);
            paused = false;
            lblStatus.Text = "Stopping...";
            UpdateUi();
        }

        static void KillQuietly(Process p)
        {
            if (p != null) try { p.Kill(); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running)
            {
                // Let the worker clean up the half-written archive, then close.
                e.Cancel = true;
                if (!cancel && MessageBox.Show(this, "Compression is still running. Stop it and exit?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                closeWhenDone = true;
                CancelClick();
            }
            else
            {
                SaveSettings();
                KillQuietly(estProc);
            }
            base.OnFormClosing(e);
        }

        // --------------------------------------------------- Size prediction

        // Predicts each item's archive size in the background (see Estimator).
        void EstimatorLoop()
        {
            while (true)
            {
                estWake.WaitOne();
                while (true)
                {
                    object[] next;
                    try { next = (object[])Invoke(new Func<object[]>(NextEstimate)); } catch { return; }
                    if (next == null) break;
                    var job = (Job)next[0];
                    int gen = (int)next[3];
                    double ratio = -1;
                    try
                    {
                        var files = new List<FileEntry>();
                        if (job.IsDir) Walk(job.Source, files);
                        else files.Add(new FileEntry { Path = job.Source, Size = new FileInfo(job.Source).Length });
                        ratio = Estimator.Ratio(sevenZip, files, (string)next[1], (string)next[2],
                            delegate(Process p) { estProc = p; }, delegate { return gen != estGen; });
                    }
                    catch { }
                    estProc = null;
                    Ui(delegate
                    {
                        job.Estimating = false;
                        if (gen == estGen)
                        {
                            if (ratio >= 0) job.Est = (long)(job.Size * ratio); else job.EstFailed = true;
                        }
                        queue.Invalidate();
                        UpdateSummary();
                    });
                }
            }
        }

        object[] NextEstimate()
        {
            if (running || sevenZip == null) return null;
            Job job = jobs.Find(delegate(Job j) { return j.Sources == null && j.Size >= 0 && j.Packed < 0 && j.Est < 0 && !j.EstFailed && j.State != JobState.Working; });
            if (job == null) return null;
            job.Estimating = true;
            queue.Invalidate();
            Config recipe = Current();
            return new object[] { job, recipe.Core(), "." + recipe.Format, estGen };
        }


        // ------------------------------------------------------------- Worker

        void Work(Options o)
        {
            int ok = 0, verified = 0, bad = 0;
            long before = 0, after = 0;
            while (!cancel)
            {
                Job job = (Job)Invoke(new Func<Job>(TakeNext));
                if (job == null) break;

                JobState state; string note, detail; bool proven;
                ProcessJob(job, o, out state, out note, out detail, out proven);
                long packed = -1;
                if (state == JobState.Done)
                {
                    try { packed = 0; foreach (string p in job.Parts) packed += new FileInfo(p).Length; } catch { packed = -1; }
                    ok++;
                    if (proven) verified++;
                    if (job.Size > 0 && packed >= 0) { before += job.Size; after += packed; }
                }
                else if (state == JobState.Failed) bad++;

                Ui(delegate
                {
                    job.State = state; job.Note = note; job.Detail = detail; job.Packed = packed; job.Verified = proven;
                    queue.Invalidate();
                    UpdateSummary();
                });
            }
            Ui(delegate { Finished(ok, verified, bad, before, after); });
        }

        Job TakeNext()
        {
            Job next = jobs.Find(delegate(Job j) { return j.State == JobState.Pending; });
            if (next == null) return null;
            started++;
            total = started + jobs.FindAll(delegate(Job j) { return j.State == JobState.Pending; }).Count - 1;
            next.State = JobState.Working; next.Note = null; next.Pct = 0;
            queue.Invalidate();
            return next;
        }

        void ProcessJob(Job job, Options o, out JobState state, out string note, out string detail, out bool proven)
        {
            state = JobState.Failed; note = null; detail = null; proven = false;
            string listFile = null;
            try
            {
                // What 7-Zip packs: one path, or for an all-in-one archive a list file naming every item.
                string archive, items;
                if (job.Sources != null)
                {
                    archive = job.Target;
                    Directory.CreateDirectory(Path.GetDirectoryName(archive));
                    foreach (string old in Parts(archive)) File.Delete(old);    // replacing it was confirmed in the save dialog
                    listFile = Path.Combine(Path.GetTempPath(), "Squish-list-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
                    File.WriteAllLines(listFile, job.Sources.ToArray(), new UTF8Encoding(false));
                    items = " -scsUTF-8 \"@" + listFile + "\"";
                }
                else
                {
                    string dir = o.OutDir ?? Path.GetDirectoryName(job.Source);
                    string name = job.IsDir ? job.Title : Path.GetFileNameWithoutExtension(job.Source);
                    Directory.CreateDirectory(dir);
                    archive = Path.Combine(dir, name + o.Extension);
                    for (int n = 2; Parts(archive).Count > 0; n++) archive = Path.Combine(dir, name + " (" + n + ")" + o.Extension);
                    if (job.IsDir && archive.StartsWith(job.Source + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        note = "the output folder is inside the folder being compressed";
                        return;
                    }
                    items = " \"" + job.Source + "\"";
                }
                job.Archive = archive;

                // Every format, method and level offered is lossless, and every archive is proven so before it is kept:
                // step 1 compresses, step 2 decompresses the archive and hashes each file, step 3 hashes the originals.
                const string common = " -bsp1 -bse2 -sccUTF-8";
                const string steps = " of 3";
                string err;
                int code = Run7z("a " + o.Pack + o.Finish + o.Secret + o.Filters + " -bso0 -y" + common + " \"" + archive + "\"" + items,
                                 job, "Step 1" + steps + "  \u00B7  Compressing", o, null, out err);
                if (cancel || code >= 2)
                {
                    DeleteParts(archive);
                    if (cancel) { state = JobState.Cancelled; return; }
                    note = Explain(code, err); detail = err;
                    return;
                }
                if (code == 1)
                {
                    // Some files could not be read, so the archive would be incomplete. An incomplete copy is never kept.
                    DeleteParts(archive);
                    note = "some files could not be read, so the archive was removed (double-click for details)";
                    detail = "Lossless check FAILED. 7-Zip could not read some files, so the archive would have been incomplete. " +
                             "It was removed and your original files were not touched.\r\n\r\n" +
                             "Close any program that may be using these files and try again.\r\n\r\n" + err;
                    return;
                }

                List<string> parts = Parts(archive);
                if (parts.Count == 0) { note = "7-Zip did not produce an archive"; detail = err; return; }

                var packed = new Dictionary<string, string>();
                code = Run7z("t -scrcSHA256 -bso1" + common + o.Secret + " \"" + parts[0] + "\"", job, "Step 2" + steps + "  \u00B7  Decompressing and hashing the archive", o, packed, out err);
                if (cancel) { DeleteParts(archive); state = JobState.Cancelled; return; }
                if (code != 0 || !packed.ContainsKey("SHA256 for data"))
                {
                    DeleteParts(archive);
                    note = "the archive did not pass 7-Zip's integrity test and was removed"; detail = err;
                    return;
                }

                var original = new Dictionary<string, string>();
                code = Run7z("h -scrcSHA256 -bso1" + common + o.Filters + items, job, "Step 3" + steps + "  \u00B7  Hashing the originals to compare", o, original, out err);
                if (cancel) { DeleteParts(archive); state = JobState.Cancelled; return; }
                string difference = code != 0 ? "The original files could not all be read again.\r\n" + err : Compare(original, packed);
                if (difference != null)
                {
                    DeleteParts(archive);
                    note = "the archive does not match the originals and was removed (double-click for details)";
                    detail = "Lossless check FAILED. The archive was removed and your original files were not touched.\r\n\r\n" + difference +
                             "\r\n\r\nThis usually means files changed while they were being compressed (is a program still using or saving them?).";
                    return;
                }

                proven = true;
                state = JobState.Done;
                job.Parts = parts;
                job.Archive = parts[0];
                string files;
                note = "Verified lossless  \u00B7  SHA-256 matches the originals" + (packed.TryGetValue("Files", out files) ? " (" + long.Parse(files).ToString("N0") + (files == "1" ? " file)" : " files)") : "");
                detail = "VERIFIED LOSSLESS\r\n\r\n" +
                         "The archive was fully decompressed and every file hashed with SHA-256. The original files " +
                         "were then hashed the same way. Contents, file names, file and folder counts and total size are identical.\r\n\r\n" +
                         Describe(packed) + "\r\n\r\nSettings: " + o.Summary + "\r\nArchive: " + parts[0] + (parts.Count > 1 ? "  (" + parts.Count + " volumes)" : "");
            }
            catch (Exception ex)
            {
                note = ex.Message;
            }
            finally
            {
                if (listFile != null) try { File.Delete(listFile); } catch { }
            }
        }

        static string Compare(Dictionary<string, string> original, Dictionary<string, string> packed)
        {
            foreach (string key in new[] { "SHA256 for data", "Size", "SHA256 for data and names", "Files", "Folders" })
            {
                string a, b;
                bool hasA = original.TryGetValue(key, out a), hasB = packed.TryGetValue(key, out b);
                bool required = key == "SHA256 for data" || key == "Size";
                if ((hasA && hasB && a != b) || (required && !(hasA && hasB)))
                    return "Originals:\r\n" + Describe(original) + "\r\n\r\nArchive:\r\n" + Describe(packed);
            }
            return null;
        }

        static string Describe(Dictionary<string, string> info)
        {
            var sb = new StringBuilder();
            string v;
            if (info.TryGetValue("Files", out v)) sb.AppendLine("Files: " + v);
            if (info.TryGetValue("Folders", out v)) sb.AppendLine("Folders: " + v);
            if (info.TryGetValue("Size", out v)) sb.AppendLine("Size: " + v + " bytes");
            if (info.TryGetValue("SHA256 for data", out v)) sb.AppendLine("SHA-256 of contents:\r\n  " + v);
            if (info.TryGetValue("SHA256 for data and names", out v)) sb.AppendLine("SHA-256 of contents and names:\r\n  " + v);
            return sb.ToString().TrimEnd();
        }

        // Runs 7-Zip, feeding its progress to the window. Summary lines (hashes, counts) are collected into info.
        int Run7z(string args, Job job, string phase, Options o, Dictionary<string, string> info, out string err)
        {
            var psi = new ProcessStartInfo(sevenZip, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            var errors = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                p.Start();
                if (o.LowPriority) try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                p.BeginErrorReadLine();
                current = p;
                if (cancel) KillQuietly(p);

                phaseClock = Stopwatch.StartNew();
                Report(job, phase, 0, "");
                var line = new StringBuilder();
                long lastShown = 0;
                int ch;
                // 7-Zip redraws its progress line with carriage returns, so split on those too.
                while ((ch = p.StandardOutput.Read()) >= 0)
                {
                    if (ch != '\r' && ch != '\n') { if (ch != '\b') line.Append((char)ch); continue; }
                    if (line.Length == 0) continue;
                    string text = line.ToString();
                    line.Length = 0;
                    Match m = ProgressRx.Match(text);
                    if (!m.Success)
                    {
                        if (info != null && (m = InfoRx.Match(text)).Success) info[m.Groups[1].Value] = m.Groups[2].Value;
                        continue;
                    }
                    if (phaseClock.ElapsedMilliseconds - lastShown < 200) continue;
                    lastShown = phaseClock.ElapsedMilliseconds;
                    Report(job, phase, Math.Min(100, int.Parse(m.Groups[1].Value)), m.Groups[2].Value.Trim());
                }
                p.WaitForExit();
                current = null;
                lock (errors) err = errors.ToString().Trim();
                return p.ExitCode;
            }
        }

        void Report(Job job, string phase, int pct, string file)
        {
            TimeSpan elapsed = phaseClock.Elapsed;
            Ui(delegate
            {
                if (cancel || paused) return;
                meter.Set(pct, Theme.Accent);
                job.Pct = pct;
                job.Note = phase + "  \u00B7  " + pct + "%";
                queue.Invalidate();
                string s = "Item " + started + " of " + total + "  \u00B7  " + job.Title + "  \u00B7  " + phase.Split('\u00B7')[0].Trim() + "  \u00B7  " + pct + "%  \u00B7  " + Clock(elapsed) + " elapsed";
                if (pct >= 2 && pct < 100 && elapsed.TotalSeconds >= 10)
                    s += ", about " + Clock(TimeSpan.FromSeconds(elapsed.TotalSeconds * (100 - pct) / pct)) + " left";
                lblStatus.Text = s;
                lblFile.Text = file;
                Text = pct + "% - " + AppName;
            });
        }

        void Finished(int ok, int verified, int bad, long before, long after)
        {
            bool stopped = cancel;
            running = false; paused = false; cancel = false;
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            Text = AppName;
            meter.Set(stopped ? 0 : 100, bad > 0 ? Theme.Warn : Theme.Good);

            string s = (stopped ? "Stopped. " : "Finished. ") + ok + (ok == 1 ? " archive" : " archives") + " created";
            if (ok > 0 && verified == ok) s += ", all verified lossless";
            else if (verified > 0) s += ", " + verified + " verified lossless";
            if (bad > 0) s += ", " + bad + " failed";
            lblStatus.Text = s + ".";
            lblFile.Text = before > 0 ? Fmt(before) + "  \u2192  " + Fmt(after) + "  (" + Smaller(before, after, false) + ")   \u00B7   Double-click an item for its verification report." : "";
            queue.Invalidate();
            UpdateUi();
            estWake.Set();

            if (closeWhenDone) { Close(); return; }
            SystemSounds.Asterisk.Play();
            if (ActiveForm != this) Native.FlashWindow(Handle, true);
        }

        void Ui(MethodInvoker action)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        static string Explain(int code, string err)
        {
            if (code == 8) return "not enough memory - pick a smaller dictionary or a lower level";
            foreach (string l in (err ?? "").Split('\n'))
            {
                string t = l.Trim();
                if (t.Length > 0 && !t.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)) return t;
            }
            return "7-Zip stopped with error code " + code;
        }

        // An archive is one file, or name.001, name.002 and so on when it was split into volumes.
        static List<string> Parts(string archive)
        {
            var parts = new List<string>();
            if (File.Exists(archive)) parts.Add(archive);
            for (int i = 1; File.Exists(archive + "." + i.ToString("D3")); i++) parts.Add(archive + "." + i.ToString("D3"));
            return parts;
        }

        static void DeleteParts(string archive)
        {
            foreach (string p in Parts(archive)) TryDelete(p);
        }

        static void TryDelete(string path)
        {
            for (int i = 0; i < 5; i++)
            {
                try { File.Delete(path); return; }
                catch { Thread.Sleep(200); }
            }
        }

        // ------------------------------------------------------------ Helpers

        // Returns the total size of a folder, optionally collecting every file in it.
        public static long Walk(string dir, List<FileEntry> into)
        {
            long sum = 0;
            var todo = new Stack<string>();
            todo.Push(dir);
            while (todo.Count > 0)
            {
                try
                {
                    foreach (FileSystemInfo e in new DirectoryInfo(todo.Pop()).EnumerateFileSystemInfos())
                    {
                        var f = e as FileInfo;
                        if (f == null)
                        {
                            if ((e.Attributes & FileAttributes.ReparsePoint) == 0) todo.Push(e.FullName);
                            continue;
                        }
                        sum += f.Length;
                        if (into != null) into.Add(new FileEntry { Path = f.FullName, Size = f.Length });
                    }
                }
                catch { }
            }
            return sum;
        }

        public static string Fmt(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return v.ToString(i == 0 || v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00") + " " + units[i];
        }

        public static string Smaller(long before, long after, bool approximate)
        {
            if (before <= 0) return "";
            return (approximate ? "\u2248 " : "") + Math.Max(0, 100 - 100.0 * after / before).ToString("0") + "% smaller";
        }

        static string Clock(TimeSpan t)
        {
            return t.TotalHours >= 1
                ? (int)t.TotalHours + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                : t.Minutes + ":" + t.Seconds.ToString("00");
        }

        static string Find7z(string saved)
        {
            var candidates = new List<string>();
            // A copy shipped next to the app comes first, so the portable folder works on any PC.
            candidates.Add(Path.Combine(AppDir, @"7-Zip\7z.exe"));
            candidates.Add(Path.Combine(AppDir, "7z.exe"));
            if (!string.IsNullOrEmpty(saved)) candidates.Add(saved);
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\7-Zip"))
                        {
                            string dir = key == null ? null : key.GetValue("Path") as string;
                            if (dir != null) candidates.Add(Path.Combine(dir, "7z.exe"));
                        }
                    }
                    catch { }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"7-Zip\7z.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"7-Zip\7z.exe"));
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim(), "7z.exe"));

            foreach (string c in candidates)
                try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
            return null;
        }

        // ----------------------------------------------------------- Settings

        static Dictionary<string, string> LoadSettings()
        {
            var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string l in File.ReadAllLines(File.Exists(PortableSettings) ? PortableSettings : UserSettings))
                {
                    int eq = l.IndexOf('=');
                    if (eq > 0) cfg[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return cfg;
        }

        void ApplySettings(Dictionary<string, string> cfg)
        {
            string v; int n;
            if (cfg.TryGetValue("outdir", out v)) outDir = v;
            segLevel.SelectedIndex = cfg.TryGetValue("level", out v) && int.TryParse(v, out n) ? n : LevelMx.Length - 1;
            segDict.SelectedIndex = cfg.TryGetValue("dict", out v) && int.TryParse(v, out n) ? dictChoices.IndexOf(n) : 0;
            if (cfg.TryGetValue("outmode", out v) && v == "1" && Directory.Exists(outDir)) segOut.SelectedIndex = 1;
            togLowPrio.Checked = !cfg.TryGetValue("lowprio", out v) || v != "0";
            togCombine.Checked = cfg.TryGetValue("combine", out v) && v == "1";
            adv = Config.FromSettings(cfg, ramMb);
        }

        void SaveSettings()
        {
            string[] lines =
            {
                "level=" + segLevel.SelectedIndex,
                "dict=" + dictChoices[segDict.SelectedIndex],
                "outmode=" + segOut.SelectedIndex,
                "outdir=" + outDir,
                "lowprio=" + (togLowPrio.Checked ? 1 : 0),
                "combine=" + (togCombine.Checked ? 1 : 0),
                "theme=" + (Theme.Dark ? "dark" : "light"),
                "sevenzip=" + sevenZip
            };
            var all = new List<string>(lines);
            all.AddRange(adv.ToLines());    // never includes the password
            lines = all.ToArray();
            // Settings travel with the app; if its folder is read-only they go to the user profile instead.
            try { File.WriteAllLines(PortableSettings, lines); return; } catch { }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UserSettings));
                File.WriteAllLines(UserSettings, lines);
            }
            catch { }
        }
    }

    static class Native
    {
        public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool FlashWindow(IntPtr hwnd, bool invert);
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        [DllImport("kernel32.dll")] public static extern bool GetPhysicallyInstalledSystemMemory(out long totalKb);
        [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr process);
        [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr process);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("gdi32.dll")] public static extern IntPtr AddFontMemResourceEx(IntPtr font, uint size, IntPtr reserved, ref uint count);
    }

    // The modern Explorer-style folder picker (the built-in WinForms one is the old tree dialog).
    static class Shell
    {
        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_ALLOWMULTISELECT = 0x200;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        public static string[] PickFolders(IWin32Window owner, string title, bool multi)
        {
            var found = new List<string>();
            var dialog = (IFileOpenDialog)new FileOpenDialogRCW();
            try
            {
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | (multi ? FOS_ALLOWMULTISELECT : 0));
                dialog.SetTitle(title);
                if (dialog.Show(owner.Handle) != 0) return new string[0];

                IShellItemArray items;
                dialog.GetResults(out items);
                uint count;
                items.GetCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    IShellItem item; string path;
                    items.GetItemAt(i, out item);
                    item.GetDisplayName(SIGDN_FILESYSPATH, out path);
                    if (!string.IsNullOrEmpty(path)) found.Add(path);
                }
            }
            catch (COMException) { }
            finally { Marshal.ReleaseComObject(dialog); }
            return found.ToArray();
        }

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filterSpec);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IShellItemArray items);
            void GetSelectedItems(out IShellItemArray items);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemArray
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetPropertyStore(int flags, ref Guid iid, out IntPtr result);
            void GetPropertyDescriptionList(IntPtr keyType, ref Guid iid, out IntPtr result);
            void GetAttributes(int flags, uint mask, out uint attributes);
            void GetCount(out uint count);
            void GetItemAt(uint index, out IShellItem item);
            void EnumItems(out IntPtr enumerator);
        }
    }
}
