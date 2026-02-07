## セッション引き継ぎ資料（2026-02-07 / PART1）

次チャットではまずこのファイル（`SESSION_HANDOVER_20260207_PART1.md`）と `TODO.md` を見れば再開できます。

---

## 今回実施した内容（詳細）

### 1. 同時起動防止（Mutex + 既存ウィンドウ前面化）

- `Program.cs` に名前付き Mutex (`Global\LanMicBridge_SingleInstance`) を追加
- 2回目の起動時は `SetForegroundWindow` / `ShowWindow(SW_RESTORE)` で既存ウィンドウを前面に
- プロセスベースで検出（`Process.GetProcessesByName`）するため、ウィンドウタイトルに依存しない

### 2. Phase3: タスクトレイ常駐 + 自動接続（案B実装）

#### タスクトレイ
- `NotifyIcon` でトレイアイコン常時表示
- 右クリックコンテキストメニュー: 「ウィンドウを表示」「設定」「───」「終了」
- 最小化 → `Hide()` + `ShowInTaskbar = false` でトレイ格納
- ダブルクリック → `RestoreFromTray()` でウィンドウ復帰
- ツールチップに接続状態（「受信中」「待機中」等）を表示（`UpdateConnectionIndicator` 内で同期）

#### 自動接続
- `AppSettings.AutoConnect` (bool?) プロパティ追加
- 設定ウィンドウに「動作」タブを新設（SettingsForm を4タブ構成に変更）
- `Form1_Load` 内で、`AutoConnect=true` かつ送信モードの場合に `StartSender()` を BeginInvoke
- 受信モードは `UpdateModeUi(true)` 内で既に自動開始されるため追加不要

#### EventWaitHandle（トレイ格納中の2重起動復帰）
- `Program.cs` に `EventWaitHandle` (`Global\LanMicBridge_ShowWindow`) を追加
- 2回目起動 → `EventWaitHandle.Set()` → 1回目インスタンスが `RestoreFromTray()` を呼ぶ
- `Form1_Load` 内で `Task.Run` による WaitOne ループで監視（500ms間隔）
- `FormClosing` 時に `CancellationTokenSource.Cancel()` で安全に終了
- フォールバック: EventWaitHandle が開けない場合は従来の `SetForegroundWindow` を使用

### 3. Windows自動起動 + 最小化起動

#### AutoStartMinimized
- `AppSettings.AutoStartMinimized` (bool?) 追加
- `Form1_Load` 内で `BeginInvoke` → `WindowState = Minimized` → `MinimizeToTray()`

#### RunAtWindowsStartup
- `AppSettings.RunAtWindowsStartup` (bool?) 追加
- チェックON → スタートアップフォルダ (`Environment.SpecialFolder.Startup`) に `.lnk` ショートカット作成
- チェックOFF → ショートカット削除
- ショートカット作成は `WScript.Shell` COM 経由（`Type.GetTypeFromProgID` + `dynamic`）
- 設定読み込み時はファイル存在で判定（`IsWindowsStartupEnabled()`、設定値より実態を優先）
- `CheckedChanged` イベントで即座に登録/解除（`_loadingSettings` ガード付き）

### 4. README 全面更新

- ビルドコマンドのパス修正（`LanAudioBridge` → `LanMicBridge`）
- 特長セクション: トレイ常駐、自動接続、同時起動防止、ダッシュボードUI を追加
- 使い方セクション: モード切替UI・タスクトレイ・全自動運用の説明を追加
- 設定セクション: 「動作」タブ追加、受信AGCを「OFF推奨」に修正
- FAQ: 2回起動/自動起動/受信AGC推奨のQ&A追加
- トラブルシューティング: 音量0/音が出ない の項目追加
- 開発者向け: プロジェクト構成テーブルを最新14ファイルに更新

---

## Gitコミット履歴

直近（新しい順）：

- `081f163` `docs: update README for tray, auto-connect, auto-startup, and current UI`
- `002df4f` `docs: mark auto-startup feature complete`
- `234468f` `feat: auto-start minimized to tray and Windows startup shortcut management`
- `d2381df` `docs: Phase3 user verified, add auto-startup task`
- `3afd3ee` `docs: mark Phase3 tray + auto-connect complete`
- `1eebfe8` `feat: system tray, auto-connect, and show-window event for tray restore`
- `4879c4a` `docs: Phase3 tray + auto-connect task start`
- `03b5280` `docs: mark single-instance mutex task complete`
- `42ec6ae` `feat: single-instance guard with mutex and foreground activation`
- `5ccf8ce` `docs: add mutex single-instance task to TODO`

---

## TODO進捗状況

- **完了**
  - 同時起動防止（Mutex + 既存ウィンドウ前面化 + EventWaitHandle）
  - Phase3: タスクトレイ常駐 + 自動接続
  - Windows自動起動 + 最小化起動
  - README全面更新

- **未着手**
  - **★ Phase4: エラー復旧導線の強化（次にやるべき）**

---

## 次回対応すべきこと（具体的に）

### ★ Phase4: エラー復旧導線の強化

目的: トレイ常駐中でもエラーに気づけるように。「何が起きた→どうすればいい」をセットで伝える。

#### 実装方針案（3パターン）

**案A（控えめ）: エラーUI改善のみ**
- メイン画面のインジケーター色変化は既に実装済み（`UpdateConnectionIndicator`）
- エラーメッセージを「原因 + 対処法」セットに整理
- `ShowAlert` の表示を改善（ステータスバーに対処法も表示）
- **メリット**: 工数極小、既存コードの改善のみ
- **デメリット**: トレイ格納中はエラーに気づけない
- **工数**: 小

**案B（バランス）: 案A + トレイ通知 ★おすすめ**
- 案Aの全内容 +
- `NotifyIcon.ShowBalloonTip()` でバルーン通知
  - 接続切断時: 「接続が切断されました — 再接続を試みています」
  - エラー時: 「エラー: {具体的な内容} — {対処法}」
  - 再接続成功時: 「再接続しました」
- Engine のイベントから適切なタイミングで通知を発行
- **メリット**: トレイ格納中でもエラーに気づける、ユーザー体験が大幅向上
- **デメリット**: 通知頻度の調整が必要（連続エラーで通知が出すぎないように）
- **工数**: 中

**案C（積極的）: 案B + 自動再接続の可視化**
- 案Bの全内容 +
- ステータスバーにリトライ回数/状態を表示（「再接続中 (3/10)」等）
- 再接続成功/失敗のログ表示
- 一定回数（例: 10回）失敗したら「手動確認が必要」のバルーン通知 + インジケーター変更
- **メリット**: 自動再接続の状況が完全に把握できる
- **デメリット**: リトライロジックの修正が Engine 側にも必要な可能性
- **工数**: 中〜大

#### 実装箇所

| 対象 | ファイル | 変更内容 |
|---|---|---|
| バルーン通知 | `Form1.cs` or `Form1.Ui.cs` | `_notifyIcon.ShowBalloonTip()` を呼ぶヘルパー |
| エラー通知トリガー | `Form1.Receiver.cs` | `StatusChanged` / `Error` イベントハンドラ内で通知 |
| エラー通知トリガー | `Form1.Sender.cs` | `SetSenderStatus` / `Error` イベントハンドラ内で通知 |
| エラーメッセージ整理 | `Form1.cs` `ShowAlert` | 対処法をセットにしたメッセージ体系 |
| 通知抑制 | 共通 | 同一メッセージの連続通知を抑制するロジック（既存の `_lastAlert` / `_lastAlertTime` と同様） |

---

## 現状コードの該当箇所（引用付き）

### 1) Program.cs（同時起動防止 + EventWaitHandle）

```csharp
static void Main()
{
    using var mutex = new Mutex(true, MutexName, out bool createdNew);
    if (!createdNew)
    {
        SignalExistingInstance(); // EventWaitHandle.Set() → フォールバック SetForegroundWindow
        return;
    }
    using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
    ApplicationConfiguration.Initialize();
    Application.Run(new Form1(showEvent));
}
```

### 2) Form1.cs（トレイ管理 + 自動接続 + 自動起動）

```csharp
// フィールド
private NotifyIcon _notifyIcon = null!;
private CheckBox _chkAutoConnect = null!;
private CheckBox _chkAutoStartMinimized = null!;
private CheckBox _chkRunAtWindowsStartup = null!;
private GroupBox _groupBehavior = null!;
private readonly EventWaitHandle? _showWindowEvent;
private CancellationTokenSource? _showWindowCts;

// Form1_Load 内の自動接続
if (_appSettings.AutoConnect == true && _radioSender.Checked)
{
    if (IPAddress.TryParse(_txtIp.Text.Trim(), out _))
    {
        BeginInvoke(() => StartSender());
    }
}

// Form1_Load 内の最小化起動
if (_appSettings.AutoStartMinimized == true)
{
    BeginInvoke(() => { WindowState = FormWindowState.Minimized; MinimizeToTray(); });
}

// トレイ復帰/格納
public void RestoreFromTray() { Show(); WindowState = Normal; ShowInTaskbar = true; Activate(); }
private void MinimizeToTray() { Hide(); ShowInTaskbar = false; }
```

### 3) Form1.Ui.cs（NotifyIcon + スタートアップ管理）

```csharp
// トレイアイコン生成（BuildUi内）
_notifyIcon = new NotifyIcon
{
    Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
    Text = "LanMicBridge",
    Visible = true
};
// コンテキストメニュー: 表示/設定/終了
// DoubleClick → RestoreFromTray()
// Resize → Minimized → MinimizeToTray()

// ツールチップ更新（UpdateConnectionIndicator内）
_notifyIcon.Text = $"LanMicBridge - {status}";

// スタートアップ管理
static string GetStartupShortcutPath() => Path.Combine(Startup, "LanMicBridge.lnk");
static bool IsWindowsStartupEnabled() => File.Exists(GetStartupShortcutPath());
static void SetWindowsStartup(bool enable) { /* .lnk 作成/削除 via WScript.Shell COM */ }
```

### 4) 既存のエラー通知の仕組み（Phase4で拡張する部分）

```csharp
// Form1.cs — ShowAlert: ステータスバーにアラート表示（2秒間の重複抑制）
private void ShowAlert(string message)
{
    var now = DateTime.UtcNow;
    if (message == _lastAlert && (now - _lastAlertTime).TotalSeconds < 2) return;
    _lastAlert = message;
    _lastAlertTime = now;
    // → _alertLabel.Text に表示
}

// Form1.Receiver.cs — ReceiverEngine のイベント接続
_receiverEngine.StatusChanged += SetStatus;       // ← 接続状態変更
_receiverEngine.Error += ShowAlert;                // ← エラー通知

// Form1.Sender.cs — SenderEngine のイベント接続
_senderEngine.StatusChanged += SetSenderStatus;    // ← 接続状態変更
_senderEngine.Error += ShowAlert;                  // ← エラー通知

// Form1.cs — SetStatus: 接続状態に応じてインジケーター色を変更
if (text == "接続中")        → StateOk (LimeGreen)
else if (text == "再接続中") → StateWarning (Gold)
else if (text.Contains("エラー")) → StateError (Red)
else                         → StateIdle (Gray)
```

### 5) AppSettings.cs（設定プロパティ一覧 — 動作系抜粋）

```csharp
public bool? AutoConnect { get; set; }
public bool? AutoStartMinimized { get; set; }
public bool? RunAtWindowsStartup { get; set; }
```

### 6) SettingsForm.cs（4タブ構成）

```csharp
private readonly TabPage _receiverTab  = new() { Text = "受信" };
private readonly TabPage _senderTab    = new() { Text = "送信" };
private readonly TabPage _infoTab      = new() { Text = "情報" };
private readonly TabPage _behaviorTab  = new() { Text = "動作" };

public SettingsForm(Control receiverContent, Control senderContent,
                    Control infoContent, Control behaviorContent)
```

---

## コード構造（最新）

| ファイル | 役割 |
|---|---|
| `Program.cs` | エントリポイント（Mutex + EventWaitHandle + SignalExistingInstance） |
| `Form1.cs` | メインフォーム（フィールド + Load/Closing + トレイ管理 + 自動接続/起動） |
| `Form1.Ui.cs` | UI構築・表示更新・NotifyIcon・スタートアップ管理 |
| `Form1.Settings.cs` | 設定永続化・設定ウィンドウ管理（EnsureSettingsForm で4タブ構成） |
| `Form1.Receiver.cs` | 受信UIロジック（ReceiverEngine 呼び出し） |
| `Form1.Sender.cs` | 送信UIロジック（SenderEngine 呼び出し + インジケーター連動） |
| `Engine/ReceiverEngine.cs` | 受信コアロジック |
| `Engine/SenderEngine.cs` | 送信コアロジック |
| `Engine/EngineTypes.cs` | Engine共通型（AudioLevel, ReceiverStats, JitterMode 等） |
| `Audio/AudioProcessor.cs` | 音声処理（AGC/ゲート/クリップ） |
| `AppSettings.cs` | 設定モデル（JSON永続化、AutoConnect/AutoStartMinimized/RunAtWindowsStartup 含む） |
| `SettingsForm.cs` | 設定ウィンドウ（タブ4つ: 受信/送信/情報/動作） |
| `UiTheme.cs` | UIテーマ一元管理（色/フォント/余白/ヘルパー） |
| `RoundedButton.cs` | 角丸ボタン カスタムコントロール |

---

## 確認事項リスト

- Phase4: 案A/B/Cのどれにするか（おすすめは案B）
- バルーン通知の頻度制御: 何秒間隔で抑制するか（現状のShowAlertは2秒抑制）
- Engine側のリトライ回数の可視化が必要か（案Cの場合のみ）
- TrackBarのカスタム描画: 見送りで合意済み
