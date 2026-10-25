using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal static class Camelot
{
    internal static readonly int[] Minor = { 8, 3, 10, 5, 0, 7, 2, 9, 4, 11, 6, 1 };
    internal static readonly int[] Major = { 11, 6, 1, 8, 3, 10, 5, 0, 7, 2, 9, 4 };
    private static readonly string[] MinorNames = { "A♭ minor", "E♭ minor", "B♭ minor", "F minor", "C minor", "G minor", "D minor", "A minor", "E minor", "B minor", "F♯ minor", "D♭ minor" };
    private static readonly string[] MajorNames = { "B major", "F♯ major", "D♭ major", "A♭ major", "E♭ major", "B♭ major", "F major", "C major", "G major", "D major", "A major", "E major" };
    internal static int Tonic(int section) { return section < 12 ? Minor[section] : Major[section - 12]; }
    internal static int Section(int tonic, bool minor) { return tonic < 0 ? -1 : Array.IndexOf(minor ? Minor : Major, tonic) + (minor ? 0 : 12); }
    internal static string Code(int section) { return section < 0 ? "Unknown" : (section % 12 + 1) + (section < 12 ? "A" : "B"); }
    internal static string Key(int section) { return section < 0 ? "Unknown" : section < 12 ? MinorNames[section] : MajorNames[section - 12]; }
    internal static int Shift(int source, int target) { int shift = (target - source + 12) % 12; return shift > 6 ? shift - 12 : shift; }
    internal static double Pitch(int semitones) { return Math.Pow(2, semitones / 12.0); }
}

internal sealed class CamelotWheel : Control
{
    internal int Current = -1, Original = -1, Other = -1;
    internal bool MinorMode;
    internal Action<int> Selected;
    internal static readonly Color CurrentFill = PlayerTheme.Purple, OriginalFill = Color.FromArgb(252, 211, 77), OtherFill = PlayerTheme.Green;
    private int hovered = -1;
    private readonly ToolTip tooltip = new ToolTip { Active = false };
    internal bool ShowHoverInfo { get { return tooltip.Active; } set { tooltip.Active = value; } }
    internal CamelotWheel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true; Cursor = Cursors.Hand; AccessibleName = "Camelot key wheel"; AccessibleDescription = "Inner ring: minor keys A. Outer ring: major keys B. Click a key in the selected song's mode. Arrow keys move around the wheel; Enter selects.";
        Size = new Size(570, 570); BackColor = PlayerTheme.Background;
    }
    private float Radius { get { return Math.Min(Width, Height) / 2f - 12; } }
    internal int SectionAt(Point point)
    {
        double dx = point.X - Width / 2.0, dy = point.Y - Height / 2.0, distance = Math.Sqrt(dx * dx + dy * dy), radius = Radius;
        if (distance < radius * .29 || distance > radius) return -1;
        double angle = (Math.Atan2(dy, dx) * 180 / Math.PI + 105 + 360) % 360;
        int number = ((int)(angle / 30) + 11) % 12;
        return number + (distance >= radius * .66 ? 12 : 0);
    }
    internal Point SectionPoint(int section)
    {
        double angle = (-90 + (section % 12 + 1) * 30) * Math.PI / 180, radius = Radius * (section < 12 ? .48 : .83);
        return new Point((int)(Width / 2.0 + Math.Cos(angle) * radius), (int)(Height / 2.0 + Math.Sin(angle) * radius));
    }
    internal int Highlights(int section) { return (section == Current ? 1 : 0) | (Original != Current && section == Original ? 2 : 0) | (section == Other ? 4 : 0); }
    private bool SelectableSection(int section) { return section >= 0 && (section < 12) == MinorMode && Current >= 0; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); int section = SectionAt(e.Location); if (section == hovered) return;
        hovered = section; Cursor = SelectableSection(section) ? Cursors.Hand : Cursors.Default;
        tooltip.SetToolTip(this, section < 0 ? "" : Camelot.Code(section) + " · " + Camelot.Key(section) + (SelectableSection(section) ? " · Click to transpose" : " · Transposition preserves major/minor mode")); Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e) { hovered = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); if (e.Button == MouseButtons.Left) Choose(SectionAt(e.Location)); base.OnMouseDown(e); }
    private void Choose(int section) { if (SelectableSection(section) && Selected != null) Selected(section); }
    protected override bool IsInputKey(Keys key) { return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Enter || base.IsInputKey(key); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Current >= 0 && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
        {
            int start = SelectableSection(hovered) ? hovered : Current;
            hovered = (start % 12 + (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up ? 11 : 1)) % 12 + (MinorMode ? 0 : 12); Invalidate(); e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter) { Choose(SelectableSection(hovered) ? hovered : Current); e.Handled = e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }
    private GraphicsPath Wedge(float inner, float outer, float start)
    {
        float x = Width / 2f, y = Height / 2f;
        GraphicsPath shape = new GraphicsPath();
        shape.AddArc(x - outer, y - outer, outer * 2, outer * 2, start, 30);
        shape.AddArc(x - inner, y - inner, inner * 2, inner * 2, start + 30, -30); shape.CloseFigure(); return shape;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias; float radius = Radius;
        for (int section = 0; section < 24; section++)
        {
            float inner = radius * (section < 12 ? .29f : .66f), outer = radius * (section < 12 ? .66f : 1), start = -105 + (section % 12 + 1) * 30;
            int flags = Highlights(section); List<Color> fills = new List<Color>();
            if ((flags & 1) != 0) fills.Add(CurrentFill); if ((flags & 2) != 0) fills.Add(OriginalFill); if ((flags & 4) != 0) fills.Add(OtherFill);
            if (fills.Count == 0) fills.Add(section < 12 ? Color.FromArgb(40, 34, 58) : Color.FromArgb(29, 39, 53));
            for (int band = 0; band < fills.Count; band++) using (GraphicsPath shape = Wedge(inner + (outer - inner) * band / fills.Count, inner + (outer - inner) * (band + 1) / fills.Count, start)) using (Brush fill = new SolidBrush(fills[band])) g.FillPath(fill, shape);
            using (GraphicsPath shape = Wedge(inner, outer, start)) using (Pen edge = new Pen(section == hovered && SelectableSection(section) ? Color.White : PlayerTheme.Border, section == hovered && SelectableSection(section) ? 2.5f : 1)) g.DrawPath(edge, shape);
            Point point = SectionPoint(section); int labelWidth = section < 12 ? 64 : 74; Rectangle label = new Rectangle(point.X - labelWidth / 2, point.Y - 21, labelWidth, 42);
            using (GraphicsPath box = PlayerTheme.Rounded(label, 6)) using (Brush fill = new SolidBrush(Color.FromArgb(225, 16, 16, 24))) g.FillPath(fill, box);
            using (Font code = new Font("Segoe UI", 9, FontStyle.Bold)) TextRenderer.DrawText(g, Camelot.Code(section), code, new Rectangle(label.X, label.Y + 1, label.Width, 19), PlayerTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            using (Font note = new Font("Segoe UI", 8)) TextRenderer.DrawText(g, Camelot.Key(section), note, new Rectangle(label.X, label.Y + 21, label.Width, 19), PlayerTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
        Rectangle center = new Rectangle(Width / 2 - 70, Height / 2 - 35, 140, 70);
        using (Font font = new Font("Segoe UI", 10, FontStyle.Bold)) TextRenderer.DrawText(g, "CAMELOT\nA · minor\nB · major", font, center, PlayerTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
    protected override void Dispose(bool disposing) { if (disposing) tooltip.Dispose(); base.Dispose(disposing); }
}
