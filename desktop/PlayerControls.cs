using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Label's built-in ellipsis tooltip must follow the same preference as hover help.
internal sealed class PlayerLabel : Label
{
    internal ToolTip HoverInfo;
    protected override void OnMouseEnter(EventArgs e)
    {
        if (!AutoEllipsis || (HoverInfo != null && HoverInfo.Active)) base.OnMouseEnter(e);
    }
}

internal static class LibraryScrollBars
{
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowScrollBar(IntPtr window, int bar, bool show);
    internal static void Hide(Control control)
    {
        if (control.IsHandleCreated && (GetWindowLong(control.Handle, -16) & 0x00300000) != 0) ShowScrollBar(control.Handle, 3, false);
    }
}

// One pixel-based motion model for the browser, sidebar and session queue.
internal sealed class SmoothScroll : IDisposable
{
    private readonly Timer timer = new Timer { Interval = 15 };
    private readonly Func<double> maximum;
    private readonly Action<int> apply;
    private long lastFrame;
    private int appliedOffset = Int32.MinValue;
    internal double Position { get; private set; }
    internal double Target { get; private set; }
    internal bool Moving { get { return timer.Enabled; } }
    internal SmoothScroll(Func<double> limit, Action<int> update)
    {
        maximum = limit; apply = update;
        timer.Tick += delegate
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = Math.Min(.05, (now - lastFrame) / (double)Stopwatch.Frequency); lastFrame = now;
            Advance(elapsed);
        };
    }
    private double Clamp(double value) { return Math.Max(0, Math.Min(Math.Max(0, maximum()), value)); }
    private void ApplyPosition(bool force)
    {
        int offset = (int)Math.Round(Position);
        if (force || offset != appliedOffset) { appliedOffset = offset; apply(offset); }
    }
    internal static double WheelDistance(int delta, int viewportHeight)
    {
        int lines = SystemInformation.MouseWheelScrollLines;
        return -delta / (double)SystemInformation.MouseWheelScrollDelta * (lines < 0 ? Math.Max(32, viewportHeight * .9) : lines * 32);
    }
    internal void Wheel(int delta, int viewportHeight)
    {
        double distance = WheelDistance(delta, viewportHeight);
        if (distance == 0) return;
        // A reversed wheel starts from what is on screen, without a lingering coast.
        if (distance * (Target - Position) < 0) Target = Position;
        MoveTo(Target + distance);
    }
    internal void MoveTo(double target)
    {
        Target = Clamp(target);
        if (Math.Abs(Target - Position) < .25) { JumpTo(Target); return; }
        if (!timer.Enabled) { lastFrame = Stopwatch.GetTimestamp(); timer.Start(); }
    }
    internal void JumpTo(double position) { timer.Stop(); Position = Target = Clamp(position); ApplyPosition(true); }
    internal void ClampRange()
    {
        Position = Clamp(Position); Target = Clamp(Target); ApplyPosition(true);
        if (Position == Target) timer.Stop();
    }
    internal void Shift(double distance)
    {
        Position = Clamp(Position + distance); Target = Clamp(Target + distance); ApplyPosition(true);
    }
    internal void Advance(double seconds)
    {
        Target = Clamp(Target);
        Position = Clamp(Position + (Target - Position) * (1 - Math.Exp(-Math.Max(0, seconds) / .055)));
        if (Math.Abs(Target - Position) < .25) { Position = Target; timer.Stop(); }
        ApplyPosition(false);
    }
    internal void Stop() { JumpTo(Position); }
    public void Dispose() { timer.Dispose(); }
}

// Native list boxes only expose whole-row scrolling. Paint visible rows at a
// pixel offset so their text, artwork, selection and hit testing move together.
internal class SmoothList : Control
{
    internal sealed class ItemCollection : Collection<object>
    {
        private readonly SmoothList owner;
        internal ItemCollection(SmoothList list) { owner = list; }
        protected override void InsertItem(int index, object item) { base.InsertItem(index, item); owner.ItemsChanged(); }
        protected override void SetItem(int index, object item) { base.SetItem(index, item); owner.ItemsChanged(); }
        protected override void RemoveItem(int index)
        {
            base.RemoveItem(index);
            if (owner.selectedIndex >= index) owner.SetSelection(owner.selectedIndex == index ? -1 : owner.selectedIndex - 1, false);
            owner.ItemsChanged();
        }
        protected override void ClearItems() { base.ClearItems(); owner.SetSelection(-1, false); owner.ItemsChanged(); }
    }
    private readonly SmoothScroll scroll;
    private int selectedIndex = -1, itemHeight = 24, updateDepth;
    private int pixelOffset;
    internal readonly ItemCollection Items;
    internal event DrawItemEventHandler DrawItem;
    internal event EventHandler SelectedIndexChanged;
    internal int ItemHeight { get { return itemHeight; } set { itemHeight = Math.Max(1, value); ItemsChanged(); } }
    internal int SelectedIndex { get { return selectedIndex; } set { SetSelection(value, true); } }
    internal object SelectedItem { get { return selectedIndex < 0 ? null : Items[selectedIndex]; } }
    internal int TopIndex { get { return pixelOffset / ItemHeight; } set { scroll.JumpTo(Math.Max(0, value) * (double)ItemHeight); } }
    internal double ScrollOffset { get { return scroll.Position; } }
    internal double ScrollTarget { get { return scroll.Target; } }
    protected virtual double MaximumScroll { get { return Math.Max(0, Items.Count * (double)ItemHeight - ClientSize.Height); } }
    internal SmoothList()
    {
        Items = new ItemCollection(this);
        scroll = new SmoothScroll(() => MaximumScroll, offset => { pixelOffset = offset; if (updateDepth == 0) Invalidate(); });
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        TabStop = true; AccessibleRole = AccessibleRole.List;
    }
    private void ItemsChanged() { if (scroll != null && updateDepth == 0) scroll.ClampRange(); }
    internal void BeginUpdate() { updateDepth++; }
    internal void EndUpdate() { if (updateDepth > 0) updateDepth--; if (updateDepth == 0) { scroll.ClampRange(); Invalidate(); } }
    internal void ShiftScroll(double pixels) { scroll.Shift(pixels); }
    internal void ScrollBy(double pixels) { scroll.MoveTo(scroll.Target + pixels); }
    internal void EnsureSelectionVisible() { RevealSelection(true); }
    internal Rectangle GetItemRectangle(int index) { return new Rectangle(0, index * ItemHeight - pixelOffset, ClientSize.Width, ItemHeight); }
    internal int IndexFromPoint(Point point)
    {
        if (!ClientRectangle.Contains(point)) return -1;
        int index = (point.Y + pixelOffset) / ItemHeight; return index < Items.Count ? index : -1;
    }
    private void SetSelection(int index, bool reveal)
    {
        if (index < -1 || index >= Items.Count) throw new ArgumentOutOfRangeException("index");
        if (selectedIndex == index) return;
        selectedIndex = index;
        if (reveal && updateDepth == 0) RevealSelection(false);
        Invalidate(); AccessibilityNotifyClients(AccessibleEvents.Selection, index);
        if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
    }
    private void RevealSelection(bool animated)
    {
        if (selectedIndex < 0) return;
        double top = selectedIndex * (double)ItemHeight, target = scroll.Position;
        if (top < target) target = top;
        else if (top + ItemHeight > target + ClientSize.Height) target = top + ItemHeight - ClientSize.Height;
        else return;
        if (animated) scroll.MoveTo(target); else scroll.JumpTo(target);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (updateDepth != 0) return;
        int first = Math.Max(0, (pixelOffset + e.ClipRectangle.Top) / ItemHeight);
        for (int index = first; index < Items.Count; index++)
        {
            Rectangle bounds = GetItemRectangle(index); if (bounds.Top >= e.ClipRectangle.Bottom) break;
            DrawItemState state = index == selectedIndex ? DrawItemState.Selected : DrawItemState.None;
            if (Focused && index == selectedIndex) state |= DrawItemState.Focus;
            OnDrawItem(new DrawItemEventArgs(e.Graphics, Font, bounds, index, state, ForeColor, BackColor));
        }
    }
    protected virtual void OnDrawItem(DrawItemEventArgs e) { if (DrawItem != null) DrawItem(this, e); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        scroll.Wheel(e.Delta, ClientSize.Height);
        HandledMouseEventArgs handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true;
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        scroll.Stop(); Focus();
        int index = IndexFromPoint(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0) SetSelection(index, false);
        base.OnMouseDown(e);
    }
    protected override bool IsInputKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        return key == Keys.Enter || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || base.IsInputKey(keyData);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && !e.Alt && !e.Control && Items.Count > 0)
        {
            int index = selectedIndex, page = Math.Max(1, ClientSize.Height / ItemHeight - 1);
            if (e.KeyCode == Keys.Up) index = Math.Max(0, index - 1);
            else if (e.KeyCode == Keys.Down) index = Math.Min(Items.Count - 1, index + 1);
            else if (e.KeyCode == Keys.Home) index = 0;
            else if (e.KeyCode == Keys.End) index = Items.Count - 1;
            else if (e.KeyCode == Keys.PageUp) index = Math.Max(0, index - page);
            else if (e.KeyCode == Keys.PageDown) index = Math.Min(Items.Count - 1, index + page);
            else { base.OnKeyDown(e); return; }
            SetSelection(index, false); RevealSelection(true); e.Handled = e.SuppressKeyPress = true;
        }
        base.OnKeyDown(e);
    }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (scroll != null) scroll.ClampRange(); }
    protected override void OnVisibleChanged(EventArgs e) { if (!Visible && scroll != null) scroll.Stop(); base.OnVisibleChanged(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) scroll.Dispose(); base.Dispose(disposing); }
    protected override AccessibleObject CreateAccessibilityInstance() { return new ListAccessibility(this); }
    private sealed class ListAccessibility : ControlAccessibleObject
    {
        private readonly SmoothList list;
        internal ListAccessibility(SmoothList owner) : base(owner) { list = owner; }
        public override int GetChildCount() { return list.Items.Count; }
        public override AccessibleObject GetChild(int index) { return index < 0 || index >= list.Items.Count ? null : new ItemAccessibility(list, index); }
        public override AccessibleObject GetSelected() { return GetChild(list.SelectedIndex); }
        public override AccessibleObject GetFocused() { return list.Focused ? GetSelected() : null; }
        public override AccessibleObject HitTest(int x, int y) { return GetChild(list.IndexFromPoint(list.PointToClient(new Point(x, y)))); }
    }
    private sealed class ItemAccessibility : AccessibleObject
    {
        private readonly SmoothList list; private readonly int index;
        internal ItemAccessibility(SmoothList owner, int item) { list = owner; index = item; }
        public override string Name
        {
            get
            {
                if (index >= list.Items.Count) return "";
                SongInfo song = list.Items[index] as SongInfo; QueueRow row = list.Items[index] as QueueRow;
                return song != null ? song.Title + " by " + song.Artist : row != null ? row.Title + " · " + row.Status : Convert.ToString(list.Items[index]);
            }
        }
        public override AccessibleRole Role { get { return AccessibleRole.ListItem; } }
        public override AccessibleObject Parent { get { return list.AccessibilityObject; } }
        public override Rectangle Bounds { get { return list.RectangleToScreen(Rectangle.Intersect(list.ClientRectangle, list.GetItemRectangle(index))); } }
        public override AccessibleStates State { get { return AccessibleStates.Selectable | AccessibleStates.Focusable | (index == list.SelectedIndex ? AccessibleStates.Selected : 0) | (index == list.SelectedIndex && list.Focused ? AccessibleStates.Focused : 0) | (!list.Visible || !list.ClientRectangle.IntersectsWith(list.GetItemRectangle(index)) ? AccessibleStates.Offscreen : 0); } }
        public override string DefaultAction { get { return "Select"; } }
        public override void Select(AccessibleSelection flags) { if (index < list.Items.Count) { list.Focus(); list.SelectedIndex = index; } }
        public override void DoDefaultAction() { Select(AccessibleSelection.TakeSelection); }
    }
}

// AutoScroll still lays out cards and list rows; wheel movement is animated in pixels.
internal sealed class LibraryGridPanel : FlowLayoutPanel
{
    private bool hidingScrollBars;
    private bool scrollHandleReady;
    private int contentHeight;
    private readonly SmoothScroll scroll;
    internal double ScrollOffset { get { return scroll.Position; } }
    internal double ScrollTarget { get { return scroll.Target; } }
    internal LibraryGridPanel()
    {
        DoubleBuffered = true;
        scroll = new SmoothScroll(() => MaximumScroll, offset =>
        {
            base.AutoScrollPosition = new Point(0, offset);
            // A handle can exist before the panel is shown; the framework's
            // property setter defers movement in that state.
            if (!Created) SetDisplayRectLocation(0, -offset);
        });
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        contentHeight = 0;
        int offset = base.AutoScrollPosition.Y;
        foreach (Control child in Controls) contentHeight = Math.Max(contentHeight, child.Bottom - offset + child.Margin.Bottom + Padding.Bottom);
    }
    private double MaximumScroll { get { return Math.Max(0, contentHeight - ClientSize.Height); } }
    public new Point AutoScrollPosition { get { return base.AutoScrollPosition; } set { scroll.JumpTo(value.Y); } }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!scroll.Moving) scroll.JumpTo(-base.AutoScrollPosition.Y);
        scroll.Wheel(e.Delta, ClientSize.Height);
        HandledMouseEventArgs handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true;
    }
    protected override Point ScrollToControl(Control activeControl)
    {
        Point target = base.ScrollToControl(activeControl);
        if (scroll == null) return target;
        // Tabbing to a card should reveal it through the same motion as the wheel.
        scroll.MoveTo(-target.Y);
        return base.AutoScrollPosition;
    }
    protected override void OnVisibleChanged(EventArgs e) { if (!Visible && scroll != null) scroll.Stop(); base.OnVisibleChanged(e); }
    protected override void Dispose(bool disposing) { if (disposing) scroll.Dispose(); base.Dispose(disposing); }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e); scrollHandleReady = true; LibraryScrollBars.Hide(this);
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        scrollHandleReady = false; base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Scrollbar styles can change during layout. Other messages, especially
        // paint and mouse input, do not need a native scrollbar query.
        if (m.Msg != 0x007D || !scrollHandleReady || hidingScrollBars) return;
        try { hidingScrollBars = true; LibraryScrollBars.Hide(this); }
        finally { hidingScrollBars = false; }
    }
}

internal static class PlayerTheme
{
    internal static readonly Color Background = Color.FromArgb(11, 11, 16), Sidebar = Color.FromArgb(9, 9, 14), Glass = Color.FromArgb(23, 23, 31), Border = Color.FromArgb(43, 43, 53), Text = Color.FromArgb(231, 231, 239), Muted = Color.FromArgb(140, 140, 153), Purple = Color.FromArgb(167, 139, 250), Green = Color.FromArgb(52, 211, 153);
    internal static readonly Color[] Stems = { Purple, Color.FromArgb(248, 113, 113), Color.FromArgb(96, 165, 250), Green };
    internal static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        GraphicsPath path = new GraphicsPath();
        if (d <= 0) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
    }
    internal static Color Blend(Color a, Color b, float amount) { return Color.FromArgb((int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount)); }
    internal static void Icon(Graphics g, string icon, RectangleF r, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (Pen pen = new Pen(color, 1.7f))
        using (Brush brush = new SolidBrush(color))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            if (icon == "play") g.FillPolygon(brush, new[] { new PointF(x + w * .30f, y + h * .17f), new PointF(x + w * .30f, y + h * .83f), new PointF(x + w * .82f, y + h * .50f) });
            else if (icon == "pause") { g.FillRectangle(brush, x + w * .26f, y + h * .2f, w * .17f, h * .6f); g.FillRectangle(brush, x + w * .59f, y + h * .2f, w * .17f, h * .6f); }
            else if (icon == "previous" || icon == "next")
            {
                bool next = icon == "next";
                g.FillPolygon(brush, new[] { new PointF(x + w * (next ? .24f : .76f), y + h * .22f), new PointF(x + w * (next ? .24f : .76f), y + h * .78f), new PointF(x + w * (next ? .70f : .30f), y + h * .5f) });
                g.DrawLine(pen, x + w * (next ? .78f : .22f), y + h * .22f, x + w * (next ? .78f : .22f), y + h * .78f);
            }
            else if (icon == "download") { g.DrawLine(pen, x + w / 2, y + h * .12f, x + w / 2, y + h * .65f); g.DrawLines(pen, new[] { new PointF(x + w * .29f, y + h * .44f), new PointF(x + w / 2, y + h * .65f), new PointF(x + w * .71f, y + h * .44f) }); g.DrawLines(pen, new[] { new PointF(x + w * .20f, y + h * .73f), new PointF(x + w * .20f, y + h * .86f), new PointF(x + w * .8f, y + h * .86f), new PointF(x + w * .8f, y + h * .73f) }); }
            else if (icon == "vocals")
            {
                using (GraphicsPath mic = Rounded(new RectangleF(x + w * .37f, y + h * .09f, w * .26f, h * .51f), w * .13f)) g.DrawPath(pen, mic);
                g.DrawArc(pen, x + w * .22f, y + h * .27f, w * .56f, h * .46f, 0, 180); g.DrawLine(pen, x + w * .5f, y + h * .73f, x + w * .5f, y + h * .9f); g.DrawLine(pen, x + w * .34f, y + h * .9f, x + w * .66f, y + h * .9f);
            }
            else if (icon == "drums")
            {
                g.DrawEllipse(pen, x + w * .17f, y + h * .15f, w * .66f, h * .24f); g.DrawArc(pen, x + w * .17f, y + h * .4f, w * .66f, h * .24f, 0, 180); g.DrawLine(pen, x + w * .17f, y + h * .27f, x + w * .17f, y + h * .52f); g.DrawLine(pen, x + w * .83f, y + h * .27f, x + w * .83f, y + h * .52f); g.DrawLine(pen, x + w * .24f, y + h * .6f, x + w * .17f, y + h * .86f); g.DrawLine(pen, x + w * .76f, y + h * .6f, x + w * .83f, y + h * .86f);
            }
            else if (icon == "bass")
            {
                g.DrawEllipse(pen, x + w * .13f, y + h * .54f, w * .35f, h * .32f); g.DrawLine(pen, x + w * .37f, y + h * .59f, x + w * .76f, y + h * .20f); g.DrawLine(pen, x + w * .43f, y + h * .65f, x + w * .83f, y + h * .26f); g.DrawLines(pen, new[] { new PointF(x + w * .7f, y + h * .12f), new PointF(x + w * .87f, y + h * .29f), new PointF(x + w * .93f, y + h * .20f) });
            }
            else if (icon == "grid")
            {
                for (int row = 0; row < 2; row++) for (int column = 0; column < 2; column++)
                    g.DrawRectangle(pen, x + w * (.12f + column * .44f), y + h * (.12f + row * .44f), w * .28f, h * .28f);
            }
            else if (icon == "list")
            {
                for (int row = 0; row < 3; row++)
                {
                    float lineY = y + h * (.22f + row * .28f);
                    g.FillEllipse(brush, x + w * .1f, lineY - 1, 2, 2); g.DrawLine(pen, x + w * .34f, lineY, x + w * .88f, lineY);
                }
            }
            else if (icon == "plus") { g.DrawLine(pen, x + w * .5f, y + h * .2f, x + w * .5f, y + h * .8f); g.DrawLine(pen, x + w * .2f, y + h * .5f, x + w * .8f, y + h * .5f); }
            else if (icon == "external") { g.DrawLines(pen, new[] { new PointF(x + w * .54f, y + h * .17f), new PointF(x + w * .83f, y + h * .17f), new PointF(x + w * .83f, y + h * .46f) }); g.DrawLine(pen, x + w * .83f, y + h * .17f, x + w * .4f, y + h * .60f); g.DrawLines(pen, new[] { new PointF(x + w * .36f, y + h * .24f), new PointF(x + w * .18f, y + h * .24f), new PointF(x + w * .18f, y + h * .82f), new PointF(x + w * .76f, y + h * .82f), new PointF(x + w * .76f, y + h * .64f) }); }
            else if (icon == "volume") { g.DrawPolygon(pen, new[] { new PointF(x + w * .08f, y + h * .38f), new PointF(x + w * .27f, y + h * .38f), new PointF(x + w * .48f, y + h * .19f), new PointF(x + w * .48f, y + h * .81f), new PointF(x + w * .27f, y + h * .62f), new PointF(x + w * .08f, y + h * .62f) }); g.DrawArc(pen, x + w * .38f, y + h * .14f, w * .51f, h * .72f, -55, 110); }
            else if (icon == "search") { g.DrawEllipse(pen, x + w * .14f, y + h * .12f, w * .5f, h * .5f); g.DrawLine(pen, x + w * .56f, y + h * .56f, x + w * .85f, y + h * .85f); }
            else // Wave / logo mark.
                for (int i = 0; i < 5; i++) { float height = i == 2 ? .84f : i % 2 == 0 ? .3f : .57f; g.DrawLine(pen, x + w * (.14f + i * .18f), y + h * (1 - height) / 2, x + w * (.14f + i * .18f), y + h * (1 + height) / 2); }
        }
    }
}

internal sealed class BackdropPanel : Panel
{
    internal BackdropPanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); BackColor = PlayerTheme.Background; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (LinearGradientBrush fill = new LinearGradientBrush(ClientRectangle, Color.FromArgb(20, 16, 33), PlayerTheme.Background, 125)) e.Graphics.FillRectangle(fill, ClientRectangle);
    }
}

internal class GlassPanel : Panel
{
    internal Color Fill = PlayerTheme.Glass;
    internal Color Outline = PlayerTheme.Border;
    internal int Radius = 16;
    internal GlassPanel() { SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true); BackColor = Color.Transparent; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = PlayerTheme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), Radius))
        using (Brush fill = new SolidBrush(Fill))
        using (Pen edge = new Pen(Outline)) { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(edge, path); }
        base.OnPaint(e);
    }
}

// Retain native selection and keyboard behavior while drawing the library theme.
internal sealed class GlassComboBox : ComboBox
{
    internal GlassComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 24; FlatStyle = FlatStyle.Flat;
        BackColor = PlayerTheme.Glass; ForeColor = PlayerTheme.Text;
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (Brush fill = new SolidBrush(selected ? Color.FromArgb(45, 38, 64) : PlayerTheme.Glass)) e.Graphics.FillRectangle(fill, e.Bounds);
        Rectangle text = e.Bounds; text.Inflate(-10, 0);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, text, Enabled ? PlayerTheme.Text : PlayerTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F && m.Msg != 0x0317 && m.Msg != 0x0318) return;
        using (Graphics g = m.Msg == 0x000F ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(m.WParam))
        {
            using (Brush background = new SolidBrush(PlayerTheme.Glass)) g.FillRectangle(background, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath shape = PlayerTheme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 9))
            using (Brush fill = new SolidBrush(Color.FromArgb(34, 34, 44)))
            using (Pen edge = new Pen(Enabled && (Focused || DroppedDown) ? PlayerTheme.Purple : PlayerTheme.Border))
            { g.FillPath(fill, shape); g.DrawPath(edge, shape); }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(10, 0, Width - 36, Height), Enabled ? ForeColor : PlayerTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            using (Pen arrow = new Pen(Enabled ? PlayerTheme.Muted : Color.FromArgb(90, 90, 106), 1.5f))
                g.DrawLines(arrow, new[] { new PointF(Width - 21, Height / 2f - 2), new PointF(Width - 17, Height / 2f + 2), new PointF(Width - 13, Height / 2f - 2) });
        }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

internal sealed class GlassNumericUpDown : NumericUpDown
{
    private readonly NumberStepper stepper;
    internal GlassNumericUpDown()
    {
        AutoSize = false; BorderStyle = BorderStyle.None; BackColor = PlayerTheme.Glass; ForeColor = PlayerTheme.Text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        stepper = new NumberStepper(this); Controls.Add(stepper);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (stepper == null) return;
        foreach (Control child in Controls)
        {
            if (child == stepper) continue;
            TextBox text = child as TextBox;
            if (text == null) child.Visible = false;
            else
            {
                text.BorderStyle = BorderStyle.None; text.BackColor = BackColor;
                text.SetBounds(8, Math.Max(1, (Height - text.PreferredHeight) / 2), Math.Max(1, Width - 34), text.PreferredHeight);
            }
        }
        stepper.Bounds = new Rectangle(Width - 24, 4, 18, Height - 8);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        using (Brush background = new SolidBrush(PlayerTheme.Glass)) e.Graphics.FillRectangle(background, ClientRectangle);
    }
    protected override void OnEnter(EventArgs e) { base.OnEnter(e); Invalidate(); }
    protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(true); }
    private sealed class NumberStepper : Control
    {
        private readonly GlassNumericUpDown number;
        private readonly Timer repeat = new Timer();
        private bool increasing;
        internal NumberStepper(GlassNumericUpDown owner)
        {
            number = owner; TabStop = false; Cursor = Cursors.Hand; BackColor = PlayerTheme.Glass;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            repeat.Tick += delegate { repeat.Interval = 80; Step(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen arrow = new Pen(Enabled ? PlayerTheme.Muted : Color.FromArgb(70, 70, 84), 1.3f))
            {
                float x = Width / 2f, top = Height / 4f, bottom = Height * .75f;
                e.Graphics.DrawLines(arrow, new[] { new PointF(x - 3, top + 1), new PointF(x, top - 2), new PointF(x + 3, top + 1) });
                e.Graphics.DrawLines(arrow, new[] { new PointF(x - 3, bottom - 1), new PointF(x, bottom + 2), new PointF(x + 3, bottom - 1) });
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            number.Focus(); increasing = e.Y < Height / 2; Capture = true; Step(); repeat.Interval = 400; repeat.Start();
        }
        private void Step() { if (Enabled) { if (increasing) number.UpButton(); else number.DownButton(); } }
        protected override void OnMouseUp(MouseEventArgs e) { repeat.Stop(); Capture = false; base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { repeat.Stop(); base.OnMouseCaptureChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) repeat.Stop(); base.OnEnabledChanged(e); Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing) repeat.Dispose(); base.Dispose(disposing); }
    }
}

internal sealed class GlassButton : Control
{
    internal bool Selected;
    internal bool Toggle;
    internal Color SelectedFill = PlayerTheme.Text;
    internal Color Swatch = Color.Empty;
    private Color loadingColor = PlayerTheme.Purple;
    internal Color LoadingColor
    {
        get { return loadingColor; }
        set { if (loadingColor == value) return; loadingColor = value; if (loadingProgress >= 0) Invalidate(); }
    }
    private double loadingProgress = -1;
    internal double LoadingProgress
    {
        get { return loadingProgress; }
        set { if (loadingProgress == value) return; loadingProgress = value; Invalidate(); }
    }
    internal string Glyph;
    internal string Subtitle;
    internal bool DisplayOnly;
    internal int Radius = 10;
    internal bool Plain;
    internal bool TextOnly;
    internal bool Borderless;
    private bool hover;
    private Color FillColor
    {
        get
        {
            Color fill = Selected ? SelectedFill : hover ? Color.FromArgb(45, 44, 57) : Color.FromArgb(34, 34, 44);
            if (!Swatch.IsEmpty) return Enabled && hover ? PlayerTheme.Blend(Swatch, Color.White, .12f) : Swatch;
            return Enabled ? fill : PlayerTheme.Blend(PlayerTheme.Glass, fill, .4f);
        }
    }
    internal GlassButton()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable, true);
        BackColor = Color.Transparent; ForeColor = PlayerTheme.Text; Font = new Font("Segoe UI", 9.5f);
        TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.PushButton;
        MouseEnter += delegate { hover = true; Invalidate(); }; MouseLeave += delegate { hover = false; Invalidate(); };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (TextOnly)
        {
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            return;
        }
        if (Borderless)
        {
            PaintContent(e.Graphics, !Enabled ? PlayerTheme.Muted : Selected || Focused && ShowFocusCues ? PlayerTheme.Purple : hover ? PlayerTheme.Text : ForeColor);
            return;
        }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = FillColor;
        using (GraphicsPath shape = PlayerTheme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), Radius))
        {
            if (loadingProgress >= 0)
            {
                var saved = e.Graphics.Save();
                e.Graphics.SetClip(shape);
                using (Brush brush = new SolidBrush(LoadingColor)) e.Graphics.FillRectangle(brush, 0, 0, (float)(Width * loadingProgress), Height);
                e.Graphics.Restore(saved);
                using (Pen pen = new Pen(LoadingColor, 1.5f)) e.Graphics.DrawPath(pen, shape);
            }
            else
            {
                using (Brush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, shape);
                using (Pen pen = new Pen(Focused && ShowFocusCues ? PlayerTheme.Purple : Selected || Plain ? fill : PlayerTheme.Border)) e.Graphics.DrawPath(pen, shape);
            }
        }
        Color text = !Swatch.IsEmpty ? PlayerSongColors.ButtonText(fill) : !Enabled ? Color.FromArgb(90, 90, 106) : Selected ? PlayerTheme.Background : ForeColor;
        PaintContent(e.Graphics, loadingProgress >= 0 ? LoadingColor : text);
        if (loadingProgress > 0)
        {
            var saved = e.Graphics.Save();
            e.Graphics.SetClip(new RectangleF(0, 0, (float)(Width * loadingProgress), Height));
            PaintContent(e.Graphics, PlayerSongColors.ButtonText(LoadingColor));
            e.Graphics.Restore(saved);
        }
    }
    private void PaintContent(Graphics graphics, Color text)
    {
        Rectangle textRect = ClientRectangle;
        if (!String.IsNullOrEmpty(Glyph))
        {
            int size = Text.Length == 0 ? Math.Min(22, Height - 12) : 16;
            int x = Text.Length == 0 ? (Width - size) / 2 : 13;
            PlayerTheme.Icon(graphics, Glyph, new RectangleF(x, (Height - size) / 2f, size, size), text);
            if (Text.Length > 0) { textRect.X = 34; textRect.Width -= 42; }
        }
        if (!String.IsNullOrEmpty(Subtitle))
        {
            textRect.Inflate(-5, 0);
            TextRenderer.DrawText(graphics, Text, Font, new Rectangle(textRect.X, 3, textRect.Width, Height / 2), text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
            using (Font small = new Font("Segoe UI", 7.5f)) TextRenderer.DrawText(graphics, Subtitle, small, new Rectangle(textRect.X, Height / 2, textRect.Width, Height / 2 - 3), text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
        }
        else if (Text.Length > 0) TextRenderer.DrawText(graphics, Text, Font, textRect, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
    }
    protected override void OnClick(EventArgs e) { if (!DisplayOnly) base.OnClick(e); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Focus(); }
    internal void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }
    protected override bool IsInputKey(Keys keyData) { return (keyData & Keys.KeyCode) == Keys.Enter || base.IsInputKey(keyData); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = e.SuppressKeyPress = true; } }
    protected override AccessibleObject CreateAccessibilityInstance() { return new ButtonAccessibility(this); }
    private sealed class ButtonAccessibility : ControlAccessibleObject
    {
        private readonly GlassButton button;
        internal ButtonAccessibility(GlassButton control) : base(control) { button = control; }
        public override string DefaultAction { get { return button.DisplayOnly ? "" : "Press"; } }
        public override string Value { get { return button.LoadingProgress < 0 ? base.Value : Math.Floor(button.LoadingProgress * 100) + "% loaded"; } }
        public override void DoDefaultAction() { button.PerformClick(); }
        public override AccessibleStates State { get { return base.State | (button.Toggle && button.Selected ? AccessibleStates.Checked : AccessibleStates.None); } }
    }
}

internal sealed class IconBadge : Control
{
    internal string Glyph;
    internal Color Accent = PlayerTheme.Purple;
    internal IconBadge() { SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true); BackColor = Color.Transparent; TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath shape = PlayerTheme.Rounded(new RectangleF(0, 0, Width, Height), 9))
        using (Brush brush = new SolidBrush(PlayerTheme.Blend(PlayerTheme.Glass, Accent, .16f))) e.Graphics.FillPath(brush, shape);
        PlayerTheme.Icon(e.Graphics, Glyph, new RectangleF(7, 7, Width - 14, Height - 14), Accent);
    }
}

internal sealed class MixSlider : Control
{
    private int value;
    private bool dragging;
    internal int Maximum = 100;
    internal Color Accent = PlayerTheme.Text;
    internal Color AccentEnd = Color.Empty;
    internal bool Gradient;
    internal bool GradientFollowsFill;
    internal event Action Changed, Committed;
    internal int Value { get { return value; } set { int clamped = Math.Max(0, Math.Min(Maximum, value)); if (this.value == clamped) return; this.value = clamped; Invalidate(); if (Changed != null) Changed(); } }
    internal bool Dragging { get { return dragging; } }
    internal MixSlider()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable, true); BackColor = Color.Transparent; Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.Slider;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float left = 7, length = Math.Max(1, Width - 14), x = left + length * value / Maximum, y = Height / 2f;
        using (Pen track = new Pen(Color.FromArgb(54, 54, 65), 4)) { track.StartCap = track.EndCap = LineCap.Round; e.Graphics.DrawLine(track, left, y, left + length, y); }
        if (x > left)
        using (LinearGradientBrush brush = new LinearGradientBrush(new PointF(left, 0), new PointF(GradientFollowsFill ? Math.Max(left + 1, x) : left + length, 0), Accent, Gradient ? AccentEnd.IsEmpty ? PlayerTheme.Green : AccentEnd : Accent))
        using (Pen active = new Pen(brush, 4)) { active.StartCap = active.EndCap = LineCap.Round; e.Graphics.DrawLine(active, left, y, x, y); }
        using (Brush halo = new SolidBrush(Color.FromArgb(55, 255, 255, 255))) e.Graphics.FillEllipse(halo, x - 8, y - 8, 16, 16);
        using (Brush thumb = new SolidBrush(Enabled ? PlayerTheme.Text : PlayerTheme.Muted)) e.Graphics.FillEllipse(thumb, x - 5, y - 5, 10, 10);
        if (Focused && ShowFocusCues) using (Pen focus = new Pen(PlayerTheme.Purple)) e.Graphics.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
    }
    private void At(int x) { Value = (int)Math.Round(Math.Max(0, Math.Min(1, (x - 7.0) / Math.Max(1, Width - 14))) * Maximum); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; Focus(); dragging = true; Capture = true; At(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) At(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (!dragging) return; At(e.X); dragging = false; Capture = false; if (Committed != null) Committed(); }
    protected override bool IsInputKey(Keys keyData) { Keys key = keyData & Keys.KeyCode; return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); int step = Math.Max(1, Maximum / 100);
        if (e.KeyCode == Keys.Home) Value = 0; else if (e.KeyCode == Keys.End) Value = Maximum; else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value -= step; else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value += step; else return;
        e.Handled = e.SuppressKeyPress = true; if (Committed != null) Committed();
    }
    protected override AccessibleObject CreateAccessibilityInstance() { return new SliderAccessibility(this); }
    private sealed class SliderAccessibility : ControlAccessibleObject
    {
        private readonly MixSlider slider;
        internal SliderAccessibility(MixSlider control) : base(control) { slider = control; }
        public override string Value { get { return Math.Round(slider.Value * 100.0 / slider.Maximum) + "%"; } set { double percentage; if (Double.TryParse(value.TrimEnd('%'), out percentage)) { slider.Value = (int)(percentage / 100 * slider.Maximum); if (slider.Committed != null) slider.Committed(); } } }
    }
}

internal sealed class AutoWaveformMarkers
{
    internal float[] OutgoingPeaks, IncomingPeaks;
    internal double OutgoingDuration, IncomingDuration, Cut, Start, OutgoingMinimum, IncomingMinimum;
    internal double OutgoingProgress = -1, IncomingProgress = -1;
    internal double ViewStart, ViewSeconds;
    internal Color OutgoingColor, IncomingColor;
    internal string CutText, StartText;
}

internal sealed class WaveformView : Control
{
    internal AutoWaveformMarkers AutoMarkers;
    internal event Action<bool, double> TransitionPointRequested;
    internal event Action<int, double> ZoomRequested;
    internal float[] Peaks;
    internal Color Accent;
    internal Color ProgressAccent = Color.Empty, ProgressEnd = Color.Empty;
    internal bool Audible = true;
    internal double Duration, SourceDuration;
    private double SourceLength { get { return SourceDuration > 0 ? SourceDuration : Duration; } }
    internal double Bpm;
    private double cursorProgress = -1;
    internal double CursorProgress { get { return cursorProgress; } set { if (cursorProgress == value) return; cursorProgress = value; Invalidate(); } }
    internal Func<bool> BeginGesture;
    internal event Action<double, bool> ShiftChanged;
    internal event Action<double> LoopRequested, LoopEndChanged;
    internal event Action GestureEnded;
    private readonly Timer holdTimer = new Timer { Interval = 1500 };
    private StemTiming timing = new StemTiming();
    private bool rightHeld, shifting, resizing;
    private Point pressPoint;
    private double pressShift, pressEnd;
    private bool timingEnabled = true;
    internal bool TimingEnabled { get { return timingEnabled; } set { timingEnabled = value; if (!value) ResetGesture(); Invalidate(); } }
    internal bool Gesturing { get { return rightHeld || resizing; } }
    private double progress;
    internal event Action<double> Seek;
    internal double Progress { get { return progress; } set { double next = Math.Max(0, Math.Min(1, value)); if (progress == next) return; progress = next; Invalidate(); } }
    internal WaveformView()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable, true); BackColor = Color.Transparent; TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.Slider;
        holdTimer.Tick += delegate { ActivateHeldLoop(); };
    }
    internal void ShowTiming(StemTiming state)
    {
        if (timing.Shift == state.Shift && timing.LoopStart == state.LoopStart && timing.LoopEnd == state.LoopEnd && timing.LoopAnchor == state.LoopAnchor && timing.LoopShift == state.LoopShift) return;
        timing = state.Copy(); Invalidate();
    }
    internal void ResetGesture()
    {
        holdTimer.Stop(); rightHeld = shifting = resizing = false; if (Capture) Capture = false; Cursor = Cursors.Hand;
    }
    private double TimeAt(int x) { return x / (double)Math.Max(1, Width - 1) * Duration; }
    private float XAt(double seconds) { return (float)(seconds / Math.Max(.001, Duration) * (Width - 1)); }
    private float LoopEndX { get { return Math.Max(4, Math.Min(Math.Max(4, Width - 5), XAt(timing.LoopEnd + timing.Shift))); } }
    private bool OnLoopEnd(int x) { return TimingEnabled && timing.Looping && Math.Abs(x - LoopEndX) <= 9; }
    private void ActivateHeldLoop()
    {
        holdTimer.Stop(); if (!rightHeld || shifting || !Enabled || !TimingEnabled) return;
        if (LoopRequested != null) LoopRequested(Math.Max(0, Math.Min(SourceLength, TimeAt(pressPoint.X) - timing.Shift)));
        if (timing.Looping) { resizing = true; pressEnd = timing.LoopEnd; Cursor = Cursors.SizeWE; }
    }
    private void FinishGesture()
    {
        if (!Gesturing) return;
        holdTimer.Stop(); bool snap = shifting; rightHeld = shifting = resizing = false;
        if (Capture) Capture = false; Cursor = Cursors.Hand;
        if (snap && ShiftChanged != null)
        {
            double limit = Math.Floor(Duration * Bpm / 60) * 60 / Bpm, leadLimit = Math.Floor(SourceLength * Bpm / 60) * 60 / Bpm;
            ShiftChanged(Math.Max(-leadLimit, Math.Min(limit, StemTiming.SnapShift(timing.Shift, Bpm))), true);
        }
        if (GestureEnded != null) GestureEnded();
    }
    private void PaintAutoMarkers(Graphics graphics)
    {
        AutoWaveformMarkers data = AutoMarkers;
        PaintAutoRow(graphics, new Rectangle(0, 0, Width, Height / 2), data.OutgoingPeaks, data.OutgoingDuration, data.Cut, data.OutgoingMinimum, data.OutgoingProgress, data.OutgoingColor, "OUT " + data.CutText);
        PaintAutoRow(graphics, new Rectangle(0, Height / 2, Width, Height - Height / 2), data.IncomingPeaks, data.IncomingDuration, data.Start, data.IncomingMinimum, data.IncomingProgress, data.IncomingColor, "IN " + data.StartText);
        using (Pen divider = new Pen(PlayerTheme.Border)) graphics.DrawLine(divider, 0, Height / 2, Width, Height / 2);
        if (Focused && ShowFocusCues) using (Pen focus = new Pen(PlayerTheme.Purple)) graphics.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
    }
    private void PaintAutoRow(Graphics graphics, Rectangle row, float[] peaks, double length, double point, double minimum, double cursorAt, Color color, string label)
    {
        if (peaks == null || length <= 0) return;
        double start = AutoMarkers.ViewStart, span = AutoMarkers.ViewSeconds > 0 ? AutoMarkers.ViewSeconds : length;
        int top = row.Top + 13, bottom = Math.Max(top + 2, row.Bottom - 2);
        float boundary = (float)Math.Max(0, Math.Min(Width - 1, (minimum - start) / span * (Width - 1)));
        using (Brush shade = new SolidBrush(Color.FromArgb(35, PlayerTheme.Muted))) graphics.FillRectangle(shade, 0, top, boundary, bottom - top);
        using (Pen waveform = new Pen(PlayerTheme.Blend(PlayerTheme.Glass, color, Audible ? .7f : .3f), 1.3f))
        {
            for (int x = 1; x < Width; x += 2)
            {
                double at = start + x / (double)Math.Max(1, Width - 1) * span;
                int first = Math.Max(0, (int)Math.Floor(at / length * peaks.Length)), last = Math.Min(peaks.Length, (int)Math.Ceiling((at + 2 * span / Math.Max(1, Width - 1)) / length * peaks.Length)); float peak = .025f;
                if (first >= peaks.Length) continue;
                for (int bin = first; bin < last; bin++) peak = Math.Max(peak, peaks[bin]);
                float height = peak * (bottom - top), center = (top + bottom) / 2f;
                graphics.DrawLine(waveform, x, center - height / 2, x, center + height / 2);
            }
        }
        if (point >= start && point <= start + span)
        {
            float marker = (float)Math.Max(1, Math.Min(Width - 2, (point - start) / span * (Width - 1)));
            using (Pen shadow = new Pen(PlayerTheme.Background, 5)) graphics.DrawLine(shadow, marker, top, marker, bottom);
            using (Pen line = new Pen(PlayerTheme.Text, 2)) graphics.DrawLine(line, marker, top, marker, bottom);
            using (Brush flag = new SolidBrush(color)) graphics.FillPolygon(flag, new[] { new PointF(marker - 3, top), new PointF(marker + 3, top), new PointF(marker, top + 4) });
        }
        double cursorSeconds = cursorAt * length;
        if (cursorAt >= 0 && cursorSeconds >= start && cursorSeconds <= start + span) using (Pen cursor = new Pen(PlayerTheme.Text, 1)) graphics.DrawLine(cursor, (float)((cursorSeconds - start) / span * (Width - 1)), top, (float)((cursorSeconds - start) / span * (Width - 1)), bottom);
        using (Font small = new Font("Segoe UI", 7.5f)) TextRenderer.DrawText(graphics, label, small, new Rectangle(0, row.Top, Width, 13), color, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        if (row.Top == 0) using (Font small = new Font("Segoe UI", 7.5f)) TextRenderer.DrawText(graphics, (length / span).ToString("0.#") + "× · " + start.ToString("0.0") + "–" + (start + span).ToString("0.0") + "s", small, new Rectangle(80, row.Top, Math.Max(1, Width - 80), 13), PlayerTheme.Muted, TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
    internal double AutoTimeAt(int x)
    {
        if (AutoMarkers == null) return TimeAt(x);
        double span = AutoMarkers.ViewSeconds > 0 ? AutoMarkers.ViewSeconds : AutoMarkers.OutgoingDuration;
        return AutoMarkers.ViewStart + Math.Max(0, Math.Min(1, x / (double)Math.Max(1, Width - 1))) * span;
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Enabled && AutoMarkers != null && ZoomRequested != null)
        {
            ZoomRequested(e.Delta, Math.Max(0, Math.Min(1, e.X / (double)Math.Max(1, Width - 1))));
            HandledMouseEventArgs handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true;
            return;
        }
        base.OnMouseWheel(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (AutoMarkers != null) { PaintAutoMarkers(e.Graphics); return; }
        if (Peaks == null)
        {
            TextRenderer.DrawText(e.Graphics, Enabled ? "Drawing waveform…" : "Stems not available yet", Font, ClientRectangle, PlayerTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter); return;
        }
        Color primary = ProgressAccent.IsEmpty ? Accent : ProgressAccent, secondary = ProgressEnd.IsEmpty ? primary : ProgressEnd;
        Color played = Audible ? primary : PlayerTheme.Blend(PlayerTheme.Glass, primary, .4f), ending = Audible ? secondary : PlayerTheme.Blend(PlayerTheme.Glass, secondary, .4f), future = PlayerTheme.Blend(PlayerTheme.Glass, Accent, Audible ? .55f : .22f);
        double cursorAt = CursorProgress < 0 ? progress : CursorProgress;
        using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF((float)Math.Max(1, cursorAt * Width), 0), played, ending))
        using (Pen active = new Pen(fill, 1.5f))
        using (Pen inactive = new Pen(future, 1.5f))
        {
            for (int x = 1; x < Width; x += 2)
            {
                double source = (TimeAt(x) - timing.Shift) / Math.Max(.001, SourceLength);
                int start = (int)Math.Floor(source * Peaks.Length), end = Math.Min(Peaks.Length, (int)Math.Ceiling((source + 2.0 * Duration / Math.Max(1, Width) / Math.Max(.001, SourceLength)) * Peaks.Length)); float peak = .025f;
                if (start < 0 || start >= Peaks.Length) continue;
                for (int bin = start; bin < end; bin++) peak = Math.Max(peak, Peaks[bin]);
                float h = peak * Math.Max(1, Height - 8); e.Graphics.DrawLine(x <= cursorAt * Width ? active : inactive, x, (Height - h) / 2, x, (Height + h) / 2);
            }
        }
        if (timing.Looping)
        {
            float left = XAt(timing.LoopStart + timing.Shift), right = XAt(timing.LoopEnd + timing.Shift);
            using (Brush shade = new SolidBrush(Color.FromArgb(48, Accent))) e.Graphics.FillRectangle(shade, left, 1, Math.Max(3, right - left), Height - 2);
            using (Pen edge = new Pen(Accent, 2)) { e.Graphics.DrawLine(edge, left, 2, left, Height - 2); e.Graphics.DrawLine(edge, right, 2, right, Height - 2); }
            using (Brush handle = new SolidBrush(Accent)) e.Graphics.FillRectangle(handle, LoopEndX - 4, Height / 2 - 8, 8, 16);
        }
        using (Pen cursor = new Pen(Color.FromArgb(216, 216, 225), 1.5f)) e.Graphics.DrawLine(cursor, (float)(cursorAt * (Width - 1)), 3, (float)(cursorAt * (Width - 1)), Height - 3);
        if (Focused && ShowFocusCues) using (Pen focus = new Pen(PlayerTheme.Purple)) e.Graphics.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (!Enabled || Peaks == null || Duration <= 0 || Gesturing) return; Focus();
        if (e.Button == MouseButtons.Right && AutoMarkers != null)
        {
            if (TransitionPointRequested != null)
            {
                bool incoming = e.Y >= Height / 2;
                TransitionPointRequested(incoming, AutoTimeAt(e.X));
            }
            return;
        }
        if (e.Button == MouseButtons.Left && AutoMarkers != null) return;
        bool end = (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) && OnLoopEnd(e.X);
        if (e.Button == MouseButtons.Right || end)
        {
            if (!TimingEnabled || BeginGesture != null && !BeginGesture() || Bpm <= 0) return;
            pressPoint = e.Location; pressShift = timing.Shift; pressEnd = timing.LoopEnd;
            rightHeld = e.Button == MouseButtons.Right; resizing = end; shifting = false; Capture = true;
            if (end) Cursor = Cursors.SizeWE; else holdTimer.Start();
        }
        else if (e.Button == MouseButtons.Left && Seek != null) Seek(Math.Max(0, Math.Min(Duration, TimeAt(e.X))));
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Gesturing)
        {
            double delta = TimeAt(e.X - pressPoint.X);
            if (resizing) { if (LoopEndChanged != null) LoopEndChanged(pressEnd + delta); }
            else if (shifting || Math.Abs(e.X - pressPoint.X) >= Math.Max(4, SystemInformation.DragSize.Width / 2) || Math.Abs(e.Y - pressPoint.Y) >= Math.Max(4, SystemInformation.DragSize.Height / 2))
            {
                holdTimer.Stop(); shifting = true; Cursor = Cursors.SizeWE;
                if (ShiftChanged != null) ShiftChanged(Math.Max(-SourceLength, Math.Min(Duration, pressShift + delta)), false);
            }
        }
        else Cursor = OnLoopEnd(e.X) ? Cursors.SizeWE : Cursors.Hand;
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right || resizing && e.Button == MouseButtons.Left)
        {
            if (shifting || resizing) OnMouseMove(e);
            FinishGesture();
        }
    }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) FinishGesture(); }
    protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) ResetGesture(); base.OnEnabledChanged(e); }
    protected override void Dispose(bool disposing) { if (disposing) { ResetGesture(); holdTimer.Dispose(); } base.Dispose(disposing); }
    protected override bool IsInputKey(Keys keyData) { Keys key = keyData & Keys.KeyCode; return key == Keys.Left || key == Keys.Right || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (Duration <= 0 || Seek == null) return;
        double at = progress * Duration;
        if (e.KeyCode == Keys.Home) at = 0; else if (e.KeyCode == Keys.End) at = Duration; else if (e.KeyCode == Keys.Left) at -= 5; else if (e.KeyCode == Keys.Right) at += 5; else return;
        Seek(Math.Max(0, Math.Min(Duration, at))); e.Handled = e.SuppressKeyPress = true;
    }
    protected override AccessibleObject CreateAccessibilityInstance() { return new WaveformAccessibility(this); }
    private sealed class WaveformAccessibility : ControlAccessibleObject
    {
        private readonly WaveformView waveform;
        internal WaveformAccessibility(WaveformView control) : base(control) { waveform = control; }
        public override string Value
        {
            get { return Math.Round(waveform.Progress * 100) + "%"; }
            set { double percentage; if (waveform.Enabled && waveform.Seek != null && Double.TryParse(value.TrimEnd('%'), out percentage)) waveform.Seek(Math.Max(0, Math.Min(1, percentage / 100)) * waveform.Duration); }
        }
    }
}
