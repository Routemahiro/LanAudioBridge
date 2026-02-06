namespace LanMicBridge;

/// <summary>
/// ダークテーマの一元管理クラス。
/// カラーパレット / フォント / 余白定数 / 再帰適用メソッドを提供する。
/// </summary>
internal static class UiTheme
{
    // ── 背景色 ────────────────────────────
    public static readonly Color BgPrimary = ColorTranslator.FromHtml("#1E1E1E");
    public static readonly Color BgSecondary = ColorTranslator.FromHtml("#252526");
    public static readonly Color BgTertiary = ColorTranslator.FromHtml("#2D2D30");
    public static readonly Color BgHeader = ColorTranslator.FromHtml("#333333");

    // ── 前景色 ────────────────────────────
    public static readonly Color FgPrimary = ColorTranslator.FromHtml("#CCCCCC");
    public static readonly Color FgSecondary = ColorTranslator.FromHtml("#969696");
    public static readonly Color FgBright = ColorTranslator.FromHtml("#FFFFFF");

    // ── アクセント色 ──────────────────────
    public static readonly Color Accent = ColorTranslator.FromHtml("#007ACC");
    public static readonly Color AccentHover = ColorTranslator.FromHtml("#1C97EA");
    public static readonly Color AccentPressed = ColorTranslator.FromHtml("#005A9E");

    // ── 状態色 ────────────────────────────
    public static readonly Color StateOk = ColorTranslator.FromHtml("#4EC9B0");
    public static readonly Color StateWarning = ColorTranslator.FromHtml("#DCDCAA");
    public static readonly Color StateError = ColorTranslator.FromHtml("#F44747");
    public static readonly Color StateIdle = ColorTranslator.FromHtml("#808080");

    // ── ボーダー ──────────────────────────
    public static readonly Color Border = ColorTranslator.FromHtml("#3E3E42");
    public static readonly Color BorderFocus = Accent;

    // ── ボタン ────────────────────────────
    public static readonly Color BtnBg = ColorTranslator.FromHtml("#3E3E42");
    public static readonly Color BtnHover = ColorTranslator.FromHtml("#505050");

    // ── フォント ──────────────────────────
    public static readonly Font FontHeading = new("Segoe UI", 11f, FontStyle.Bold);
    public static readonly Font FontBody = new("Segoe UI", 9.5f);
    public static readonly Font FontCaption = new("Segoe UI", 8.5f);
    public static readonly Font FontIndicator = new("Segoe UI", 10f, FontStyle.Bold);

    // ── 余白（8px 基準グリッド） ──────────
    public const int SpaceXs = 4;
    public const int SpaceSm = 8;
    public const int SpaceMd = 16;
    public const int SpaceLg = 24;
    public const int SpaceXl = 32;

    // ───────────────────────────────────────
    //  公開メソッド
    // ───────────────────────────────────────

    /// <summary>
    /// フォームとその全子コントロールにダークテーマを再帰適用する。
    /// </summary>
    public static void Apply(Form form)
    {
        form.BackColor = BgPrimary;
        form.ForeColor = FgPrimary;
        form.Font = FontBody;
        ApplyRecursive(form);
    }

    /// <summary>
    /// StatusStrip とその項目にテーマを適用する。
    /// ToolStripItem は Control を継承していないため別途処理。
    /// </summary>
    public static void ApplyToStatusStrip(StatusStrip strip)
    {
        strip.BackColor = BgSecondary;
        strip.ForeColor = FgPrimary;
        strip.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
        foreach (ToolStripItem item in strip.Items)
        {
            item.ForeColor = FgPrimary;
            item.BackColor = BgSecondary;
            if (item is ToolStripButton btn)
            {
                btn.ForeColor = Accent;
            }
        }
    }

    /// <summary>
    /// モード切替ボタン（RadioButton Appearance.Button）をテーマ適用する。
    /// Checked 状態に応じてアクセント色/通常色を切り替える。
    /// </summary>
    public static void StyleModeButton(RadioButton rb)
    {
        rb.FlatStyle = FlatStyle.Flat;
        rb.FlatAppearance.BorderColor = Border;
        rb.FlatAppearance.BorderSize = 1;
        rb.FlatAppearance.MouseOverBackColor = BtnHover;
        rb.Font = FontBody;

        if (rb.Checked)
        {
            rb.BackColor = Accent;
            rb.ForeColor = FgBright;
        }
        else
        {
            rb.BackColor = BtnBg;
            rb.ForeColor = FgPrimary;
        }
    }

    /// <summary>
    /// TabControl のタブヘッダーをオーナー描画でダーク化する。
    /// </summary>
    public static void ApplyToTabControl(TabControl tc)
    {
        tc.DrawMode = TabDrawMode.OwnerDrawFixed;
        tc.SizeMode = TabSizeMode.Fixed;
        tc.ItemSize = new Size(120, 32);
        tc.DrawItem += TabControl_DrawItem;
        tc.BackColor = BgSecondary;

        foreach (TabPage tp in tc.TabPages)
        {
            tp.BackColor = BgSecondary;
            tp.ForeColor = FgPrimary;
        }
    }

    // ───────────────────────────────────────
    //  Private
    // ───────────────────────────────────────

    private static void ApplyRecursive(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            StyleControl(c);
            if (c.HasChildren)
            {
                ApplyRecursive(c);
            }
        }
    }

    private static void StyleControl(Control c)
    {
        switch (c)
        {
            case RadioButton rb when rb.Appearance == Appearance.Button:
                // モード切替ボタンは StyleModeButton() で別途処理するのでスキップ
                break;

            case Button btn:
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderColor = Border;
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.MouseOverBackColor = BtnHover;
                btn.FlatAppearance.MouseDownBackColor = AccentPressed;
                btn.BackColor = BtnBg;
                btn.ForeColor = FgPrimary;
                break;

            case TextBox tb:
                tb.BackColor = BgTertiary;
                tb.ForeColor = FgPrimary;
                tb.BorderStyle = BorderStyle.FixedSingle;
                break;

            case ComboBox cb:
                cb.BackColor = BgTertiary;
                cb.ForeColor = FgPrimary;
                cb.FlatStyle = FlatStyle.Flat;
                break;

            case CheckBox chk:
                chk.ForeColor = FgPrimary;
                chk.BackColor = Color.Transparent;
                break;

            case GroupBox gb:
                gb.ForeColor = FgSecondary;
                gb.BackColor = BgSecondary;
                break;

            case LinkLabel ll:
                ll.LinkColor = Accent;
                ll.ActiveLinkColor = AccentHover;
                ll.VisitedLinkColor = Accent;
                ll.BackColor = Color.Transparent;
                break;

            case Label lbl:
                lbl.ForeColor = FgPrimary;
                // ボーダー付きラベルは入力フィールド風にする
                lbl.BackColor = lbl.BorderStyle != BorderStyle.None ? BgTertiary : Color.Transparent;
                break;

            case TrackBar tb:
                tb.BackColor = BgPrimary;
                break;

            case TabControl:
                // ApplyToTabControl() で別途処理
                break;

            case TabPage tp:
                tp.BackColor = BgSecondary;
                tp.ForeColor = FgPrimary;
                break;

            case StatusStrip:
                // ApplyToStatusStrip() で別途処理
                break;

            case TableLayoutPanel:
            case Panel:
                c.BackColor = BgPrimary;
                c.ForeColor = FgPrimary;
                break;

            default:
                c.BackColor = BgPrimary;
                c.ForeColor = FgPrimary;
                break;
        }
    }

    private static void TabControl_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tc)
        {
            return;
        }

        var tabPage = tc.TabPages[e.Index];
        var isSelected = tc.SelectedIndex == e.Index;

        var bgColor = isSelected ? BgSecondary : BgTertiary;
        var fgColor = isSelected ? FgBright : FgSecondary;

        using var bgBrush = new SolidBrush(bgColor);
        using var fgBrush = new SolidBrush(fgColor);

        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        e.Graphics.DrawString(tabPage.Text, FontBody, fgBrush, e.Bounds, sf);
    }

    /// <summary>ToolStripProfessionalRenderer 用のダークカラーテーブル</summary>
    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => BgSecondary;
        public override Color ToolStripGradientEnd => BgSecondary;
        public override Color ToolStripGradientMiddle => BgSecondary;
        public override Color StatusStripGradientBegin => BgSecondary;
        public override Color StatusStripGradientEnd => BgSecondary;
        public override Color MenuItemSelected => BtnHover;
        public override Color MenuItemBorder => Border;
    }
}
