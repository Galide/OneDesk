using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace OneDesk.App;

/// <summary>Colours and fonts for the settings window, modelled on macOS System Settings.</summary>
internal static class Theme
{
    public static readonly Color Window = Color.FromArgb(245, 245, 247);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(227, 227, 232);
    public static readonly Color Text = Color.FromArgb(29, 29, 31);
    public static readonly Color Secondary = Color.FromArgb(110, 110, 115);
    public static readonly Color Tertiary = Color.FromArgb(161, 161, 166);
    public static readonly Color Accent = Color.FromArgb(0, 122, 255);
    public static readonly Color Green = Color.FromArgb(52, 199, 89);
    public static readonly Color Orange = Color.FromArgb(255, 159, 10);
    public static readonly Color Red = Color.FromArgb(255, 59, 48);
    public static readonly Color Track = Color.FromArgb(233, 233, 237);
    public static readonly Color FieldBorder = Color.FromArgb(214, 214, 219);
    public static readonly Color Hover = Color.FromArgb(242, 242, 247);

    public const string Family = "Microsoft YaHei UI";

    public static readonly Font Body = new(Family, 9.25f);
    public static readonly Font BodyMedium = new(Family, 9.25f, FontStyle.Bold);
    public static readonly Font Small = new(Family, 8.5f);
    public static readonly Font SmallMedium = new(Family, 8.5f, FontStyle.Bold);
    public static readonly Font Title = new(Family, 17f, FontStyle.Bold);
    public static readonly Font Section = new(Family, 8.75f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 10f);
    public static readonly Font Button = new(Family, 9.25f);
    public static readonly Font ButtonMedium = new(Family, 9.25f, FontStyle.Bold);
}

internal static class Gfx
{
    public static GraphicsPath Round(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1f, radius * 2f);
        if (d > rect.Width) d = rect.Width;
        if (d > rect.Height) d = rect.Height;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Text(Graphics g, string text, Font font, Rectangle bounds, Color color,
        TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding)
    {
        TextRenderer.DrawText(g, text, font, bounds, color, flags);
    }

    public static Color Mix(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }
}

/// <summary>Custom-painted control base: double buffered and flicker free.</summary>
internal abstract class Surface : Control
{
    protected Surface(Color background)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = background;
    }
}

/// <summary>Custom-painted panel base (cards and rows).</summary>
internal abstract class SurfacePanel : Panel
{
    protected SurfacePanel(Color background)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = background;
        DoubleBuffered = true;
    }
}

/// <summary>White rounded container.</summary>
internal sealed class Card : SurfacePanel
{
    public Card() : base(Color.Transparent)
    {
        Padding = new Padding(18, 10, 18, 10);
    }

    public Color Fill { get; set; } = Theme.Card;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = Gfx.Round(rect, 12f);
        using var brush = new SolidBrush(Fill);
        g.FillPath(brush, path);
        using var pen = new Pen(Theme.Border);
        g.DrawPath(pen, path);
    }
}

/// <summary>Small grey heading that sits above a card, like "通用" in System Settings.</summary>
internal sealed class GroupLabel : Label
{
    public GroupLabel(string text)
    {
        Text = text;
        AutoSize = true;
        Font = Theme.Section;
        ForeColor = Theme.Secondary;
        Margin = new Padding(6, 0, 0, 6);
    }
}

/// <summary>Base row: paints an inset separator and tells the card whether it is last.</summary>
internal abstract class SettingRow : SurfacePanel
{
    private bool _showSeparator;

    protected SettingRow() : base(Theme.Card)
    {
    }

    public bool ShowSeparator
    {
        get => _showSeparator;
        set
        {
            if (_showSeparator == value) return;
            _showSeparator = value;
            Invalidate();
        }
    }

    private bool _rowVisible = true;

    /// <summary>Visibility that also works before the window is shown (Control.Visible is effective).</summary>
    public bool RowVisible
    {
        get => _rowVisible;
        set
        {
            if (_rowVisible == value) return;
            _rowVisible = value;
            Visible = value;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!ShowSeparator) return;
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>A row with a title, optional subtitle and a control on the right.</summary>
internal sealed class SwitchRow : SettingRow
{
    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly bool _hasSubtitle;

    public SwitchRow(string title, string? subtitle, ToggleSwitch toggle)
        : base()
    {
        _hasSubtitle = subtitle is not null;
        _title = new Label
        {
            Text = title,
            AutoSize = true,
            Font = Theme.Body,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent
        };
        Controls.Add(_title);

        _subtitle = new Label
        {
            Text = subtitle ?? "",
            AutoSize = false,
            Font = Theme.Small,
            ForeColor = Theme.Secondary,
            BackColor = Color.Transparent,
            Visible = subtitle is not null
        };
        Controls.Add(_subtitle);

        Toggle = toggle;
        Controls.Add(toggle);
        Height = _hasSubtitle ? 62 : 52;
    }

    public ToggleSwitch Toggle { get; }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var toggleX = Width - Toggle.Width;
        Toggle.Location = new Point(toggleX, (Height - Toggle.Height) / 2);

        var textWidth = Math.Max(40, toggleX - 16);
        if (_hasSubtitle)
        {
            var block = _title.PreferredHeight + 3 + 18;
            var top = Math.Max(0, (Height - block) / 2);
            _title.SetBounds(0, top, textWidth, _title.PreferredHeight);
            _subtitle.SetBounds(0, _title.Bottom + 3, textWidth, 18);
        }
        else
        {
            _title.SetBounds(0, (Height - _title.PreferredHeight) / 2, textWidth, _title.PreferredHeight);
        }
    }
}

/// <summary>A label on the left, one field (plus optional trailing buttons) on the right.</summary>
internal sealed class FieldRow : SettingRow
{
    private readonly Label _label;
    private readonly Control _field;
    private readonly Control[] _trailing;
    private readonly HashSet<Control> _hiddenTrailing = new();
    private Label? _hint;
    private const int LabelWidth = 104;

    public FieldRow(string label, Control field, params Control[] trailing)
        : base()
    {
        _label = new Label
        {
            Text = label,
            AutoSize = false,
            Font = Theme.Body,
            ForeColor = Theme.Secondary,
            BackColor = Color.Transparent
        };
        Controls.Add(_label);

        _field = field;
        Controls.Add(field);

        _trailing = trailing;
        foreach (var control in trailing) Controls.Add(control);
        Height = 52;
    }

    /// <summary>Optional hint shown under the field; setting it makes the row taller.</summary>
    public Label? Hint
    {
        set
        {
            if (value is null) return;
            value.Font = Theme.Small;
            value.ForeColor = Theme.Secondary;
            value.AutoSize = false;
            value.BackColor = Color.Transparent;
            _hint = value;
            Controls.Add(value);
            Height = 70;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutContent();
    }

    /// <summary>Hides or shows one of the trailing buttons and re-runs the layout.</summary>
    public void SetTrailingVisible(Control control, bool visible)
    {
        if (visible) _hiddenTrailing.Remove(control);
        else _hiddenTrailing.Add(control);
        control.Visible = visible;
        LayoutContent();
    }

    private void LayoutContent()
    {
        var fieldTop = _hint is null ? (Height - 34) / 2 : 8;
        _label.SetBounds(0, fieldTop + 7, LabelWidth - 12, 20);

        var right = Width;
        for (var i = _trailing.Length - 1; i >= 0; i--)
        {
            var control = _trailing[i];
            if (_hiddenTrailing.Contains(control)) continue;
            control.SetBounds(right - control.Width, fieldTop + (34 - control.Height) / 2, control.Width, control.Height);
            right -= control.Width + 8;
        }

        var fieldWidth = Math.Max(80, right - LabelWidth);
        _field.SetBounds(LabelWidth, fieldTop, fieldWidth, 34);
        _hint?.SetBounds(LabelWidth, fieldTop + 38, Width - LabelWidth, 20);
    }
}

/// <summary>A row holding buttons on the left and optional status text underneath.</summary>
internal sealed class ActionRow : SettingRow
{
    private readonly Control[] _buttons;

    public ActionRow(params Control[] buttons) : base()
    {
        _buttons = buttons;
        foreach (var button in buttons) Controls.Add(button);
        Height = 52;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var x = 0;
        foreach (var button in _buttons)
        {
            button.SetBounds(x, (Height - button.Height) / 2, button.Width, button.Height);
            x += button.Width + 10;
        }
    }
}

/// <summary>Multi-line secondary text inside a card.</summary>
internal sealed class TextRow : SettingRow
{
    private readonly Label _text;

    public TextRow(string text, int height = 40) : base()
    {
        _text = new Label
        {
            Text = text,
            AutoSize = false,
            Font = Theme.Small,
            ForeColor = Theme.Secondary,
            BackColor = Color.Transparent
        };
        Controls.Add(_text);
        Height = height + 8;
    }

    public new string Text
    {
        get => _text.Text;
        set => _text.Text = value;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _text.SetBounds(0, 7, Width, Height - 14);
    }
}

internal sealed class ToggleSwitch : Surface
{
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 15 };
    private bool _checked;
    private float _position;
    private float _target;

    public ToggleSwitch() : base(Theme.Card)
    {
        Size = new Size(46, 28);
        Cursor = Cursors.Hand;
        _animation.Tick += (_, _) =>
        {
            _position += (_target - _position) * 0.35f;
            if (Math.Abs(_target - _position) < 0.01f)
            {
                _position = _target;
                _animation.Stop();
            }

            Invalidate();
        };
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            _target = value ? 1f : 0f;
            _animation.Start();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    public event EventHandler? CheckedChanged;

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var track = new RectangleF(0.5f, 2.5f, Width - 1f, Height - 5f);
        using var trackPath = Gfx.Round(track, track.Height / 2f);
        using var trackBrush = new SolidBrush(Gfx.Mix(Theme.Track, Theme.Green, _position));
        g.FillPath(trackBrush, trackPath);

        var knobSize = Height - 9f;
        var travel = Width - knobSize - 6f;
        var x = 3f + travel * _position;
        var knob = new RectangleF(x, 4.5f, knobSize, knobSize);

        using var shadow = new SolidBrush(Color.FromArgb(28, 0, 0, 0));
        g.FillEllipse(shadow, knob.X, knob.Y + 1f, knob.Width, knob.Height);
        using var knobBrush = new SolidBrush(Color.White);
        g.FillEllipse(knobBrush, knob);
    }
}

internal sealed class SegmentedControl : Surface
{
    private int _selectedIndex;
    private int _hoverIndex = -1;

    public SegmentedControl() : base(Theme.Card)
    {
        Height = 38;
        Cursor = Cursors.Hand;
        Font = Theme.BodyMedium;
    }

    public string[] Items { get; set; } = Array.Empty<string>();

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, Items.Length - 1));
            if (_selectedIndex == clamped) return;
            _selectedIndex = clamped;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SelectedIndexChanged;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = IndexAt(e.X);
        if (index == _hoverIndex) return;
        _hoverIndex = index;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        SelectedIndex = IndexAt(e.X);
    }

    private int IndexAt(int x)
    {
        if (Items.Length == 0) return -1;
        var index = x * Items.Length / Math.Max(1, Width);
        return Math.Clamp(index, 0, Items.Length - 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var path = Gfx.Round(new RectangleF(0, 0, Width, Height), 9f))
        using (var brush = new SolidBrush(Theme.Track))
        {
            g.FillPath(brush, path);
        }

        if (Items.Length == 0) return;

        var segmentWidth = (float)Width / Items.Length;
        var selected = new RectangleF(segmentWidth * _selectedIndex + 2f, 2f, segmentWidth - 4f, Height - 4f);
        using (var path = Gfx.Round(selected, 7.5f))
        using (var brush = new SolidBrush(Color.White))
        {
            g.FillPath(brush, path);
        }

        using var border = new Pen(Color.FromArgb(218, 218, 223));
        using (var path = Gfx.Round(selected, 7.5f))
        {
            g.DrawPath(border, path);
        }

        for (var i = 0; i < Items.Length; i++)
        {
            var bounds = new Rectangle((int)(segmentWidth * i), 0, (int)segmentWidth, Height);
            Gfx.Text(g, Items[i], i == _selectedIndex ? Theme.ButtonMedium : Theme.Body, bounds,
                i == _selectedIndex ? Theme.Text : Theme.Secondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

internal sealed class ActionButton : Surface
{
    public enum ButtonKind
    {
        Primary,
        Secondary,
        Ghost
    }

    private bool _hover;
    private bool _pressed;

    public ActionButton(string text, ButtonKind kind = ButtonKind.Secondary) : base(Theme.Card)
    {
        Text = text;
        Kind = kind;
        Height = 34;
        Font = kind == ButtonKind.Primary ? Theme.ButtonMedium : Theme.Button;
        Cursor = Cursors.Hand;
        AutoSize = false;
    }

    public ButtonKind Kind { get; set; }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color fill;
        Color border;
        Color text;
        switch (Kind)
        {
            case ButtonKind.Primary:
                fill = Enabled
                    ? (_pressed ? Color.FromArgb(0, 100, 210) : _hover ? Color.FromArgb(20, 135, 255) : Theme.Accent)
                    : Color.FromArgb(190, 190, 195);
                border = Color.Transparent;
                text = Color.White;
                break;

            case ButtonKind.Ghost:
                fill = _hover && Enabled ? Theme.Hover : Color.Transparent;
                border = Color.Transparent;
                text = Enabled ? Theme.Accent : Theme.Tertiary;
                break;

            default:
                fill = Enabled ? (_pressed ? Color.FromArgb(232, 232, 237) : _hover ? Theme.Hover : Color.White)
                    : Color.FromArgb(248, 248, 250);
                border = Theme.FieldBorder;
                text = Enabled ? Theme.Text : Theme.Tertiary;
                break;
        }

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = Gfx.Round(rect, 8f))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            if (border != Color.Transparent)
            {
                using var pen = new Pen(border);
                g.DrawPath(pen, path);
            }
        }

        Gfx.Text(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.EndEllipsis);
    }
}

internal sealed class TextField : SurfacePanel
{
    private readonly TextBox _box = new()
    {
        BorderStyle = BorderStyle.None,
        Font = Theme.Body,
        BackColor = Color.White,
        ForeColor = Theme.Text
    };

    private bool _focused;

    public TextField() : base(Color.White)
    {
        Height = 34;
        Padding = new Padding(10, 0, 10, 0);
        Controls.Add(_box);
        _box.GotFocus += (_, _) =>
        {
            _focused = true;
            Invalidate();
        };
        _box.LostFocus += (_, _) =>
        {
            _focused = false;
            Invalidate();
        };
        Click += (_, _) => _box.Focus();
        foreach (Control child in Controls) child.Click += (_, _) => _box.Focus();
    }

    public TextBox Inner => _box;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => _box.Text;
        set => _box.Text = value ?? "";
    }

    public string Placeholder
    {
        get => _box.PlaceholderText;
        set => _box.PlaceholderText = value;
    }

    public bool ReadOnly
    {
        get => _box.ReadOnly;
        set
        {
            _box.ReadOnly = value;
            _box.BackColor = value ? Color.FromArgb(250, 250, 252) : Color.White;
            BackColor = _box.BackColor;
            Invalidate();
        }
    }

    public bool Monospace
    {
        get => _box.Font == Theme.Mono;
        set => _box.Font = value ? Theme.Mono : Theme.Body;
    }

    public new event KeyEventHandler? KeyDown
    {
        add => _box.KeyDown += value;
        remove => _box.KeyDown -= value;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var height = _box.PreferredHeight;
        _box.SetBounds(Padding.Left, (Height - height) / 2, Math.Max(10, Width - Padding.Horizontal), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = Gfx.Round(rect, 8f);
        using var brush = new SolidBrush(BackColor);
        g.FillPath(brush, path);
        using var pen = new Pen(_focused ? Theme.Accent : Theme.FieldBorder, _focused ? 1.6f : 1f);
        g.DrawPath(pen, path);
    }
}

internal sealed class SelectField : Surface
{
    public sealed record Item(string Text, object? Tag);

    private int _selectedIndex = -1;
    private bool _hover;
    private bool _open;

    public SelectField() : base(Theme.Card)
    {
        Height = 34;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
    }

    public List<Item> Items { get; } = new();

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = Items.Count == 0 ? -1 : Math.Clamp(value, -1, Items.Count - 1);
            _selectedIndex = clamped;
            Invalidate();
        }
    }

    public Item? Selected => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;

    public event EventHandler? SelectionChanged;

    public void SetItems(IEnumerable<Item> items, int selectedIndex)
    {
        Items.Clear();
        Items.AddRange(items);
        _selectedIndex = Items.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, Items.Count - 1);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Items.Count == 0) return;

        var menu = new ContextMenuStrip
        {
            Font = Theme.Body,
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Padding(5),
            BackColor = Color.White,
            Renderer = new MenuRenderer(),
            DropShadowEnabled = true
        };

        var width = Math.Max(Width - 10, 120);
        for (var i = 0; i < Items.Count; i++)
        {
            var selected = i == _selectedIndex;
            var item = new ToolStripMenuItem((selected ? "✓  " : "    ") + Items[i].Text)
            {
                AutoSize = false,
                Height = 30,
                Width = width,
                Padding = new Padding(6, 0, 6, 0),
                ForeColor = Theme.Text
            };
            var index = i;
            item.Click += (_, _) =>
            {
                _selectedIndex = index;
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
            menu.Items.Add(item);
        }

        _open = true;
        Invalidate();
        menu.Closed += (_, _) =>
        {
            _open = false;
            Invalidate();
        };
        menu.Show(this, new Point(0, Height + 3));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = Gfx.Round(rect, 8f);
        using var brush = new SolidBrush(Enabled ? Color.White : Color.FromArgb(248, 248, 250));
        g.FillPath(brush, path);
        using var pen = new Pen(_open ? Theme.Accent : _hover && Enabled ? Color.FromArgb(190, 190, 196) : Theme.FieldBorder);
        g.DrawPath(pen, path);

        var text = Selected?.Text ?? "未选择";
        var color = Selected is null ? Theme.Tertiary : Enabled ? Theme.Text : Theme.Tertiary;
        Gfx.Text(g, text, Font, new Rectangle(10, 0, Width - 38, Height), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.EndEllipsis);

        // Chevron.
        var cx = Width - 18;
        var cy = Height / 2 - 1;
        using var chevron = new Pen(Theme.Secondary, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(chevron, new[]
        {
            new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2)
        });
    }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new Colors())
        {
            RoundedEdges = false;
        }

        private sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Color.White;
            public override Color MenuItemSelected => Theme.Hover;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuBorder => Theme.Border;
            public override Color ImageMarginGradientBegin => Color.White;
            public override Color ImageMarginGradientMiddle => Color.White;
            public override Color ImageMarginGradientEnd => Color.White;
            public override Color SeparatorDark => Theme.Border;
            public override Color SeparatorLight => Theme.Border;
        }
    }
}

internal sealed class StatusPill : Surface
{
    private string _text = "";
    private Color _accent = Theme.Tertiary;

    public StatusPill() : base(Theme.Card)
    {
        Height = 26;
        Font = Theme.SmallMedium;
    }

    public void SetStatus(string text, Color accent)
    {
        _text = text;
        _accent = accent;
        var width = TextRenderer.MeasureText(text, Font, new Size(int.MaxValue, 26), TextFormatFlags.NoPadding).Width;
        Width = width + 36;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var path = Gfx.Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), (Height - 1f) / 2f))
        using (var brush = new SolidBrush(Color.FromArgb(28, _accent)))
        {
            g.FillPath(brush, path);
        }

        using (var dot = new SolidBrush(_accent))
        {
            g.FillEllipse(dot, 11f, Height / 2f - 3.5f, 7f, 7f);
        }

        Gfx.Text(g, _text, Font, new Rectangle(24, 0, Width - 30, Height), _accent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>Rounded gradient mark used in the header.</summary>
internal sealed class LogoBadge : Surface
{
    public LogoBadge() : base(Theme.Card)
    {
        Size = new Size(46, 46);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = Gfx.Round(rect, 11f);
        using (var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(Width, Height),
                   Color.FromArgb(64, 156, 255), Color.FromArgb(0, 106, 235)))
        {
            g.FillPath(brush, path);
        }

        using var white = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var screen = new RectangleF(Width * 0.22f, Height * 0.27f, Width * 0.56f, Height * 0.34f);
        using (var screenPath = Gfx.Round(screen, 3.5f))
        {
            g.DrawPath(white, screenPath);
        }

        var cx = Width / 2f;
        g.DrawLine(white, cx, Height * 0.61f, cx, Height * 0.71f);
        g.DrawLine(white, Width * 0.33f, Height * 0.73f, Width * 0.67f, Height * 0.73f);

        using var dot = new SolidBrush(Color.White);
        g.FillEllipse(dot, Width * 0.30f, Height * 0.35f, 3.4f, 3.4f);
    }
}

/// <summary>A small apple-styled message / prompt window.</summary>
internal sealed class UiDialog : Form
{
    private readonly TextField? _input;

    private UiDialog(string title, string message, string okText, string? cancelText, string? initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Window;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        const int inner = 376;
        var measured = TextRenderer.MeasureText(message, Theme.Body, new Size(inner, 0), TextFormatFlags.WordBreak);
        var height = 18 + 26 + 8 + measured.Height + 4 + 20;
        if (initial is not null) height += 46;
        height += 34 + 18;

        ClientSize = new Size(452, height + 32);

        var card = new Card
        {
            Location = new Point(16, 16),
            Size = new Size(420, height),
            BackColor = Color.Transparent,
            Padding = new Padding(0)
        };
        Controls.Add(card);

        card.Controls.Add(new Label
        {
            Text = title,
            Font = new Font(Theme.Family, 11.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = false,
            Location = new Point(22, 16),
            Size = new Size(inner, 26)
        });

        card.Controls.Add(new Label
        {
            Text = message,
            Font = Theme.Body,
            ForeColor = Theme.Secondary,
            BackColor = Color.Transparent,
            AutoSize = false,
            Location = new Point(22, 46),
            Size = new Size(inner, measured.Height + 4)
        });

        var y = 46 + measured.Height + 18;
        if (initial is not null)
        {
            _input = new TextField { Location = new Point(22, y), Size = new Size(inner, 34) };
            _input.Text = initial;
            card.Controls.Add(_input);
            y += 46;
        }

        var ok = new ActionButton(okText, ActionButton.ButtonKind.Primary)
        {
            Size = new Size(88, 34)
        };
        ok.Click += (_, _) => DialogResult = DialogResult.OK;

        var x = 22 + inner - ok.Width;
        ok.Location = new Point(x, y);
        card.Controls.Add(ok);

        if (cancelText is not null)
        {
            var cancel = new ActionButton(cancelText)
            {
                Size = new Size(80, 34)
            };
            cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(x - 10 - cancel.Width, y);
            card.Controls.Add(cancel);
            CancelButton = null;
        }

        AcceptButton = null;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.KeyCode == Keys.Enter)
            {
                DialogResult = DialogResult.OK;
                e.SuppressKeyPress = true;
            }
        };

        Shown += (_, _) => _input?.Inner.Focus();
    }

    public static bool Confirm(IWin32Window? owner, string title, string message, string okText = "确定")
    {
        using var dialog = new UiDialog(title, message, okText, "取消", null);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    public static string? Prompt(IWin32Window? owner, string title, string message, string initial)
    {
        using var dialog = new UiDialog(title, message, "确定", "取消", initial);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._input?.Text.Trim() : null;
    }
}
