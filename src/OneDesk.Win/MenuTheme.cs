using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace OneDesk.App;

/// <summary>
/// A rounded, light drop-down menu that matches the settings window instead of the
/// default square/blue WinForms menu.
/// </summary>
public sealed class RoundedMenu : ContextMenuStrip
{
    private const int CornerRadius = 10;

    public RoundedMenu(bool withCheckMarks = false)
    {
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        RenderMode = ToolStripRenderMode.Professional;
        Renderer = new AppleMenuRenderer();
        DropShadowEnabled = true;
        Padding = new Padding(5, 6, 5, 6);

        if (withCheckMarks)
        {
            ShowCheckMargin = true;
            ShowImageMargin = true;
        }
        else
        {
            ShowImageMargin = false;
        }
    }

    protected override void OnItemAdded(ToolStripItemEventArgs e)
    {
        base.OnItemAdded(e);
        if (e.Item is ToolStripMenuItem item)
        {
            item.Padding = new Padding(2, 4, 8, 4);
            item.TextAlign = ContentAlignment.MiddleLeft;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RoundCorners();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        RoundCorners();
    }

    private void RoundCorners()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = Gfx.Round(new RectangleF(0, 0, Width, Height), CornerRadius);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }
}

/// <summary>Flat, apple-ish colours for the tray menu (accent hover, no gradients).</summary>
internal sealed class AppleColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Card;
    public override Color MenuStripGradientBegin => Theme.Card;
    public override Color MenuStripGradientEnd => Theme.Card;
    public override Color ImageMarginGradientBegin => Theme.Card;
    public override Color ImageMarginGradientMiddle => Theme.Card;
    public override Color ImageMarginGradientEnd => Theme.Card;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => Theme.Accent;
    public override Color MenuItemSelectedGradientBegin => Theme.Accent;
    public override Color MenuItemSelectedGradientEnd => Theme.Accent;
    public override Color MenuItemPressedGradientBegin => Theme.Accent;
    public override Color MenuItemPressedGradientEnd => Theme.Accent;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;
    public override Color ButtonSelectedHighlight => Theme.Accent;
    public override Color ToolStripBorder => Color.Transparent;
}

internal sealed class AppleMenuRenderer : ToolStripProfessionalRenderer
{
    public AppleMenuRenderer() : base(new AppleColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var strip = e.ToolStrip;
        var rect = new RectangleF(0.5f, 0.5f, strip.Width - 1f, strip.Height - 1f);
        using var path = Gfx.Round(rect, 10f);
        using var brush = new SolidBrush(Theme.Card);
        g.FillPath(brush, path);
        using var pen = new Pen(Theme.Border);
        g.DrawPath(pen, path);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // The background paints its own rounded border; the default square frame is not wanted.
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        var strip = e.ToolStrip;
        if (!item.Selected || !item.Enabled || strip is null) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(4.5f, 1.5f, strip.Width - 9f, item.Height - 3f);
        using var path = Gfx.Round(rect, 6f);
        using var brush = new SolidBrush(Theme.Accent);
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled
            ? Theme.Tertiary
            : e.Item.Selected
                ? Color.White
                : Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var g = e.Graphics;
        var strip = e.ToolStrip;
        if (g is null || strip is null) return;
        var y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Border);
        g.DrawLine(pen, 12, y, strip.Width - 12, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        if (e.Item is null) return;
        e.ArrowColor = e.Item.Selected ? Color.White : Theme.Tertiary;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        if (g is null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var box = e.ImageRectangle;
        var x = box.X + 7f;
        var y = box.Y + box.Height / 2f + 1f;
        using var pen = new Pen(e.Item.Selected ? Color.White : Theme.Accent, 1.9f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        g.DrawLines(pen, new[]
        {
            new PointF(x, y - 1f),
            new PointF(x + 3.6f, y + 3f),
            new PointF(x + 9.6f, y - 4.5f)
        });
    }
}
