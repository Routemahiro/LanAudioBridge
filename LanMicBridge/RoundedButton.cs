using System.Drawing.Drawing2D;

namespace LanMicBridge;

/// <summary>
/// 角丸ボタン。OnPaint で角丸の背景・ボーダー・テキストを描画する。
/// </summary>
internal class RoundedButton : Button
{
    public int BorderRadius { get; set; } = 8;

    private bool _hover;
    private bool _pressed;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 親の背景でクリア（角の外側を透明にする）
        using (var clearBrush = new SolidBrush(Parent?.BackColor ?? SystemColors.Control))
        {
            g.FillRectangle(clearBrush, ClientRectangle);
        }

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = CreateRoundedPath(rect, BorderRadius);

        // 背景色（プレス → ホバー → 通常）
        var bg = _pressed
            ? ControlPaint.Dark(BackColor, 0.05f)
            : _hover
                ? ControlPaint.Light(BackColor, 0.3f)
                : BackColor;

        using (var bgBrush = new SolidBrush(bg))
        {
            g.FillPath(bgBrush, path);
        }

        // ボーダー
        using (var pen = new Pen(UiTheme.Border, 1))
        {
            g.DrawPath(pen, path);
        }

        // テキスト
        var textColor = Enabled ? ForeColor : SystemColors.GrayText;
        TextRenderer.DrawText(g, Text, Font, rect, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
