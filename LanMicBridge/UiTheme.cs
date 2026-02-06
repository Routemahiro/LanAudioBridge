namespace LanMicBridge;

/// <summary>
/// UIテーマの一元管理クラス。
/// 状態色 / フォント / 余白定数 / モードボタンスタイルを提供する。
/// </summary>
internal static class UiTheme
{
    // ── アクセント色（モードボタン選択時） ──
    public static readonly Color Accent = ColorTranslator.FromHtml("#007ACC");
    public static readonly Color AccentHover = ColorTranslator.FromHtml("#1C97EA");
    public static readonly Color AccentPressed = ColorTranslator.FromHtml("#005A9E");

    // ── 状態色（接続インジケーター用） ──
    public static readonly Color StateOk = Color.LimeGreen;
    public static readonly Color StateWarning = Color.Gold;
    public static readonly Color StateError = Color.Red;
    public static readonly Color StateIdle = Color.Gray;

    // ── テキスト色（特殊用途） ──
    public static readonly Color WarningText = Color.DarkOrange;
    public static readonly Color AlertText = Color.DarkRed;

    // ── ボーダー ──────────────────────────
    public static readonly Color Border = SystemColors.ControlDark;

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

    /// <summary>
    /// モード切替ボタン（RadioButton Appearance.Button）をスタイル適用する。
    /// Checked 状態に応じてアクセント色/通常色を切り替える。
    /// </summary>
    public static void StyleModeButton(RadioButton rb)
    {
        rb.FlatStyle = FlatStyle.Flat;
        rb.FlatAppearance.BorderColor = Border;
        rb.FlatAppearance.BorderSize = 1;
        rb.Font = FontBody;

        if (rb.Checked)
        {
            rb.BackColor = Accent;
            rb.ForeColor = Color.White;
            rb.FlatAppearance.MouseOverBackColor = AccentHover;
        }
        else
        {
            rb.BackColor = SystemColors.Control;
            rb.ForeColor = SystemColors.ControlText;
            rb.FlatAppearance.MouseOverBackColor = SystemColors.ControlLight;
        }
    }
}
