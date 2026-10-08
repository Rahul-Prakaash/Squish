// Squish - a small offline front end for 7-Zip.
// Each folder (or file) in the queue becomes its own lossless .7z archive.
// Build with build.bat (uses the C# compiler that ships with Windows, so C# 5 syntax only).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
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
        public bool IsDir;
        public long Size = -1;      // -1 while the folder is still being measured
        public long Packed = -1;    // real archive size, once finished
        public long Est = -1;       // predicted archive size
        public bool Estimating, EstFailed;
        public string Archive;
        public JobState State;
        public bool Verified;
        public int Pct;
        public string Note;         // one-line status shown on the card
        public string Detail;       // verification report or 7-Zip error output, shown on double-click
    }

    class Options
    {
        public int Mx;
        public int DictMb;          // 0 = let 7-Zip decide
        public string OutDir;       // null = next to the original
        public bool LowPriority;
    }

    struct FileEntry
    {
        public string Path;
        public long Size;
    }

    // ------------------------------------------------------------------ Look

    static class Theme
    {
        public static bool Dark;
        public static Color Bg, Card, Raised, Border, Text, Muted, Accent, Good, Warn, Bad;
        public static readonly Color Hot = Color.FromArgb(255, 146, 72), HotDeep = Color.FromArgb(236, 72, 84);

        static Theme() { SetDark(true); }

        public static void SetDark(bool dark)
        {
            Dark = dark;
            if (dark)
            {
                Bg = Color.FromArgb(17, 19, 24); Card = Color.FromArgb(27, 30, 37); Raised = Color.FromArgb(39, 43, 53);
                Border = Color.FromArgb(55, 60, 73); Text = Color.FromArgb(233, 236, 241); Muted = Color.FromArgb(146, 154, 170);
                Accent = Color.FromArgb(92, 139, 255); Good = Color.FromArgb(78, 203, 131); Warn = Color.FromArgb(229, 170, 66); Bad = Color.FromArgb(244, 104, 104);
            }
            else
            {
                Bg = Color.FromArgb(243, 244, 247); Card = Color.FromArgb(255, 255, 255); Raised = Color.FromArgb(232, 235, 241);
                Border = Color.FromArgb(210, 215, 224); Text = Color.FromArgb(24, 27, 34); Muted = Color.FromArgb(98, 106, 122);
                Accent = Color.FromArgb(58, 106, 240); Good = Color.FromArgb(22, 145, 80); Warn = Color.FromArgb(190, 120, 10); Bad = Color.FromArgb(210, 58, 58);
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
            Font = new Font("Segoe UI", 9.75f);
            Bold = new Font("Segoe UI Semibold", 10.5f);
            Small = new Font("Segoe UI", 9f);
            Title = new Font("Segoe UI Semibold", 16f);
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

        public static Color Behind(Control c) { return c.Parent != null ? c.Parent.BackColor : Bg; }
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

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color back = Theme.Behind(this);
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill = Primary ? Theme.Accent : Theme.Raised;
            if (!Enabled) fill = Theme.Mix(fill, back, 0.65);
            else if (down) fill = Theme.Mix(fill, Color.Black, 0.18);
            else if (hover) fill = Theme.Mix(fill, Theme.Text, 0.08);
            Theme.Fill(g, ClientRectangle, Theme.S(7), fill);
            if (!Primary && Enabled) Theme.Stroke(g, ClientRectangle, Theme.S(7), Theme.Border, false);
            if (Focused && ShowFocusCues) Theme.Stroke(g, ClientRectangle, Theme.S(7), Theme.Text, false);

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
            Color back = Theme.Behind(this);
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Fill(g, ClientRectangle, Theme.S(8), Theme.Raised);
            for (int i = 0; i < items.Length; i++)
            {
                var r = new Rectangle(edges[i], 0, edges[i + 1] - edges[i], Height);
                Color text = i == hover && Enabled ? Theme.Text : Theme.Muted;
                if (i == selected)
                {
                    Theme.Fill(g, Rectangle.Inflate(r, -Theme.S(3), -Theme.S(3)), Theme.S(6), Enabled ? Theme.Accent : Theme.Mix(Theme.Accent, Theme.Raised, 0.6));
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
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int h = Theme.S(18), w = Theme.S(34), knob = h - Theme.S(6);
            var track = new Rectangle(0, (Height - h) / 2, w, h);
            Color fill = Checked ? Theme.Accent : Theme.Raised;
            if (!Enabled) fill = Theme.Mix(fill, back, 0.6);
            Theme.Fill(g, track, h, fill);
            if (!Checked) Theme.Stroke(g, track, h, Theme.Border, false);
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
            g.Clear(Theme.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Fill(g, ClientRectangle, Height, Theme.Raised);
            if (value > 0) Theme.Fill(g, new Rectangle(0, 0, Math.Max(Height, Width * value / 100), Height), Height, color);
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
            e.Graphics.Clear(Theme.Bg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Fill(e.Graphics, ClientRectangle, Theme.S(12), Theme.Card);
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
            Theme.Fill(g, r, Theme.S(14), Color.FromArgb((int)(240 * fade), Theme.Mix(Theme.Bg, Theme.Hot, 0.10)));

            // Marching dashed border
            using (GraphicsPath p = Theme.Round(Rectangle.Inflate(r, -Theme.S(1), -Theme.S(1)), Theme.S(14)))
            using (var pen = new Pen(Color.FromArgb(a, Theme.Hot), Theme.S(2)) { DashPattern = new[] { 4f, 3f }, DashOffset = (float)(-t * 6) })
                g.DrawPath(pen, p);

            // Pulsing rings behind a bouncing drop icon
            int cx = r.Width / 2, cy = r.Height / 2 - Theme.S(34), disc = Theme.S(40);
            for (int k = 0; k < 2; k++)
            {
                double ph = (t / 1.4 + k * 0.5) % 1.0;
                int rad = disc + (int)(ph * Theme.S(56));
                using (var pen = new Pen(Color.FromArgb((int)((1 - ph) * 120 * fade), Theme.Hot), Theme.S(2)))
                    g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
            }
            float scale = 1f + 0.06f * (float)Math.Sin(t * 5);
            int d = (int)(disc * scale);
            using (var b = new LinearGradientBrush(new Rectangle(cx - d, cy - d, d * 2, d * 2), Color.FromArgb(a, Theme.Hot), Color.FromArgb(a, Theme.HotDeep), 60f))
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
            TextRenderer.DrawText(g, "Each item becomes its own .7z archive, with the final size predicted before you start.",
                Theme.Font, new Rectangle(0, y + Theme.S(98), r.Width, Theme.S(22)), Theme.Muted, Theme.Line | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "Lossless: every archive is checked against the original files with SHA-256.",
                Theme.Font, new Rectangle(0, y + Theme.S(120), r.Width, Theme.S(22)), Theme.Muted, Theme.Line | TextFormatFlags.HorizontalCenter);
        }

        void PaintRow(Graphics g, Rectangle r, Job j, bool hovered)
        {
            Theme.Fill(g, r, Theme.S(12), hovered ? Theme.Mix(Theme.Card, Theme.Raised, 0.5) : Theme.Card);

            Color accent = j.State == JobState.Done ? Theme.Good : j.State == JobState.Failed ? Theme.Bad : Theme.Accent;
            TextRenderer.DrawText(g, j.IsDir ? "\uE8B7" : "\uE7C3", Theme.Icon, new Rectangle(r.X + Theme.S(12), r.Y, Theme.S(36), r.Height), accent, Theme.Line | TextFormatFlags.HorizontalCenter);

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
                default: status = j.Source; break;
            }
            int left = r.X + Theme.S(56), leftW = right - left - Theme.S(10);
            TextRenderer.DrawText(g, Path.GetFileName(j.Source), Theme.Bold, new Rectangle(left, r.Y + Theme.S(10), leftW, Theme.S(24)), Theme.Text, Theme.Line);
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
        public static double Ratio(string sevenZip, List<FileEntry> files, int mx, int dictMb, Action<Process> started, Func<bool> stop)
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

                string archive = Path.Combine(tmp, "sample.7z");
                var psi = new ProcessStartInfo(sevenZip, "a -t7z -mx=" + mx + (dictMb > 0 ? " -md=" + dictMb + "m" : "") +
                                                         " -bso0 -bse0 -bsp0 -y -- \"" + archive + "\" \"" + dir + "\\*\"")
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
        Toggle togLowPrio;
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
            // Fit small laptop screens and high scaling settings.
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(S(900), work.Width - S(24)), Math.Min(S(680), work.Height - S(48)));
            MinimumSize = new Size(Math.Min(S(780), work.Width), Math.Min(S(600), work.Height));
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            HookDrop(this);
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

        // Title bar in the same mode, tinted to match the window where Windows allows it.
        void ApplyTitleBar()
        {
            int dark = Theme.Dark ? 1 : 0, caption = Theme.Bg.R | Theme.Bg.G << 8 | Theme.Bg.B << 16;
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            Native.DwmSetWindowAttribute(Handle, 35, ref caption, 4);
        }

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
            var header = NewTable(2, 1);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var titles = NewTable(1, 2);
            titles.Controls.Add(new Label { Text = AppName, Font = Theme.Title, AutoSize = true, Margin = new Padding(0) }, 0, 0);
            titles.Controls.Add(new Label { Text = "Lossless .7z archives, powered by 7-Zip. Works offline, nothing to install.", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(S(2), 0, 0, 0) }, 0, 1);
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
            header.Controls.Add(titles, 0, 0);
            header.Controls.Add(adders, 1, 0);
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
            opts.Controls.Add(NewHint("Bigger can shrink large folders further, but needs far more RAM."), 2, 1);
            opts.Controls.Add(NewLabel("Save to"), 0, 2);
            opts.Controls.Add(segOut, 1, 2);
            opts.Controls.Add(outRow, 2, 2);

            var toggles = NewFlow();
            toggles.Margin = new Padding(0, S(6), 0, 0);
            var verified = new Label
            {
                Text = "\u2714  Every archive is verified lossless with SHA-256 before it is kept, at every level",
                AutoSize = true, ForeColor = Theme.Good, Anchor = AnchorStyles.Left, Margin = new Padding(0, S(3), S(26), 0)
            };
            togLowPrio = new Toggle { Text = "Keep PC responsive", Margin = new Padding(0) };
            tips.SetToolTip(verified, "After compressing, the archive is fully decompressed and every file is hashed with SHA-256,\r\nthen compared with a fresh hash of the original files. If anything differs, or a file could not\r\nbe read, the archive is removed and the item is marked as failed. This cannot be turned off.");
            tips.SetToolTip(togLowPrio, "7-Zip runs at low priority and gives way to other programs.");
            toggles.Controls.AddRange(new Control[] { verified, togLowPrio });
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
            return new TableLayoutPanel { ColumnCount = columns, RowCount = rows, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        }

        static FlowLayoutPanel NewFlow()
        {
            return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
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
            segLevel.Enabled = segDict.Enabled = segOut.Enabled = togLowPrio.Enabled = !running;
            btnBrowse.Visible = segOut.SelectedIndex == 1;
            btnBrowse.Enabled = !running;
            lblOut.Text = segOut.SelectedIndex == 1 ? outDir : "Each archive is saved beside its original.";
            lblLevelHint.Text = LevelHints[segLevel.SelectedIndex];
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
                    MessageBox.Show(this, text, Path.GetFileName(j.Source), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (MessageBox.Show(this, text + "\r\n\r\nShow the archive in Explorer?", Path.GetFileName(j.Source), MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
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
            string msg = "Delete this archive?\r\n\r\n" + j.Archive + "\r\n" + Fmt(new FileInfo(j.Archive).Length) +
                         "\r\n\r\nIt goes to the Recycle Bin (drives without one delete it permanently).\r\nYour original files are not touched.";
            if (MessageBox.Show(this, msg, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try { FileSystem.DeleteFile(j.Archive, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The archive could not be deleted.\r\n\r\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ResetJob(j);
            lblStatus.Text = "Archive deleted. " + Path.GetFileName(j.Source) + " is back in the queue if you want to compress it again.";
        }

        void ArchiveGone(Job j)
        {
            MessageBox.Show(this, "That archive is no longer there. It may have been moved or deleted.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ResetJob(j);
        }

        void ResetJob(Job j)
        {
            j.State = JobState.Pending; j.Archive = j.Note = j.Detail = null; j.Packed = -1; j.Verified = false;
            queue.Invalidate();
            UpdateUi();
            estWake.Set();
        }

        // ------------------------------------------------------------ Actions

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
                string drive = Path.GetPathRoot(o.OutDir ?? j.Source);
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

            var o = new Options
            {
                Mx = LevelMx[segLevel.SelectedIndex],
                DictMb = dictChoices[segDict.SelectedIndex],
                LowPriority = togLowPrio.Checked
            };
            if (segOut.SelectedIndex == 1)
            {
                if (!Directory.Exists(outDir) && !BrowseOut()) return;
                o.OutDir = Path.GetFullPath(outDir);
            }

            foreach (Job j in jobs)
                if (j.State == JobState.Failed || j.State == JobState.Cancelled)
                {
                    j.State = JobState.Pending; j.Note = j.Detail = j.Archive = null; j.Packed = -1;
                }
            if (!EnoughSpace(o)) { queue.Invalidate(); UpdateUi(); return; }

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
                        ratio = Estimator.Ratio(sevenZip, files, (int)next[1], (int)next[2],
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
            Job job = jobs.Find(delegate(Job j) { return j.Size >= 0 && j.Packed < 0 && j.Est < 0 && !j.EstFailed && j.State != JobState.Working; });
            if (job == null) return null;
            job.Estimating = true;
            queue.Invalidate();
            return new object[] { job, LevelMx[segLevel.SelectedIndex], dictChoices[segDict.SelectedIndex], estGen };
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
                    try { packed = new FileInfo(job.Archive).Length; } catch { }
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
            try
            {
                string dir = o.OutDir ?? Path.GetDirectoryName(job.Source);
                string name = job.IsDir ? Path.GetFileName(job.Source) : Path.GetFileNameWithoutExtension(job.Source);
                Directory.CreateDirectory(dir);
                string archive = Path.Combine(dir, name + ".7z");
                for (int n = 2; File.Exists(archive); n++) archive = Path.Combine(dir, name + " (" + n + ").7z");
                job.Archive = archive;

                if (job.IsDir && archive.StartsWith(job.Source + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    note = "the output folder is inside the folder being compressed";
                    return;
                }

                // Every level and dictionary size is lossless LZMA2, and every archive is proven so before it is kept:
                // step 1 compresses, step 2 decompresses the archive and hashes each file, step 3 hashes the originals.
                const string common = " -bsp1 -bse2 -sccUTF-8";
                const string steps = " of 3";
                string err;
                int code = Run7z("a -t7z -mx=" + o.Mx + (o.DictMb > 0 ? " -md=" + o.DictMb + "m" : "") + " -bso0 -y" + common + " -- \"" + archive + "\" \"" + job.Source + "\"",
                                 job, "Step 1" + steps + "  \u00B7  Compressing", o, null, out err);
                if (cancel || code >= 2)
                {
                    TryDelete(archive);
                    if (cancel) { state = JobState.Cancelled; return; }
                    note = Explain(code, err); detail = err;
                    return;
                }
                if (code == 1)
                {
                    // Some files could not be read, so the archive would be incomplete. An incomplete copy is never kept.
                    TryDelete(archive);
                    note = "some files could not be read, so the archive was removed (double-click for details)";
                    detail = "Lossless check FAILED. 7-Zip could not read some files, so the archive would have been incomplete. " +
                             "It was removed and your original files were not touched.\r\n\r\n" +
                             "Close any program that may be using these files and try again.\r\n\r\n" + err;
                    return;
                }

                var packed = new Dictionary<string, string>();
                code = Run7z("t -scrcSHA256 -bso1" + common + " -- \"" + archive + "\"", job, "Step 2" + steps + "  \u00B7  Decompressing and hashing the archive", o, packed, out err);
                if (cancel) { TryDelete(archive); state = JobState.Cancelled; return; }
                if (code != 0 || !packed.ContainsKey("SHA256 for data"))
                {
                    TryDelete(archive);
                    note = "the archive did not pass 7-Zip's integrity test and was removed"; detail = err;
                    return;
                }

                var original = new Dictionary<string, string>();
                code = Run7z("h -scrcSHA256 -bso1" + common + " -- \"" + job.Source + "\"", job, "Step 3" + steps + "  \u00B7  Hashing the originals to compare", o, original, out err);
                if (cancel) { TryDelete(archive); state = JobState.Cancelled; return; }
                string difference = code != 0 ? "The original files could not all be read again.\r\n" + err : Compare(original, packed);
                if (difference != null)
                {
                    TryDelete(archive);
                    note = "the archive does not match the originals and was removed (double-click for details)";
                    detail = "Lossless check FAILED. The archive was removed and your original files were not touched.\r\n\r\n" + difference +
                             "\r\n\r\nThis usually means files changed while they were being compressed (is a program still using or saving them?).";
                    return;
                }

                proven = true;
                state = JobState.Done;
                string files;
                note = "Verified lossless  \u00B7  SHA-256 matches the originals" + (packed.TryGetValue("Files", out files) ? " (" + long.Parse(files).ToString("N0") + " files)" : "");
                detail = "VERIFIED LOSSLESS\r\n\r\n" +
                         "The archive was fully decompressed and every file hashed with SHA-256. The original files " +
                         "were then hashed the same way. Contents, file names, file and folder counts and total size are identical.\r\n\r\n" +
                         Describe(packed) + "\r\n\r\nArchive: " + archive;
            }
            catch (Exception ex)
            {
                note = ex.Message;
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
                string s = "Item " + started + " of " + total + "  \u00B7  " + Path.GetFileName(job.Source) + "  \u00B7  " + phase.Split('\u00B7')[0].Trim() + "  \u00B7  " + pct + "%  \u00B7  " + Clock(elapsed) + " elapsed";
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
                "theme=" + (Theme.Dark ? "dark" : "light"),
                "sevenzip=" + sevenZip
            };
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
