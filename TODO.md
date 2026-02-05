- [x] READMEにVB-CABLEのDL先を追記 (2026-02-05 完了)
- [x] .gitignoreの確認（ログ/設定の扱い） (2026-02-05 完了)
- [x] READMEの技術表記修正（WPF→WinForms） (2026-02-05 完了)

- [x] UIリファクタ Phase1: `Form1` をpartial分割（挙動変更なし） (2026-02-05 完了)
  - [x] `Form1.Ui.cs` へUI構築/表示更新を移動
  - [x] `Form1.Settings.cs` へ設定永続化/設定ウィンドウ周りを移動
  - [x] `Form1.Receiver.cs` へ受信系ロジックを移動
  - [x] `Form1.Sender.cs` へ送信系ロジックを移動
  - [x] `Form1.Audio.cs` へ音声処理ヘルパを移動（関数移動のみ）
  - [x] `dotnet build` でビルド確認
  - [x] Phase1完了の声かけ（動作確認依頼）
  - [x] ユーザー動作確認（起動/受信/送信/設定）

- [x] UIリファクタ Phase2: UIとロジック分離（Engine化） (2026-02-05 完了)
  - [x] Engine境界/API設計（イベント通知、UIスレッド境界） (2026-02-05 完了)
  - [x] `ReceiverEngine` 作成（受信/ジッタ/再生） (2026-02-05 完了)
  - [x] `SenderEngine` 作成（キャプチャ/エンコード/送信） (2026-02-05 完了)
  - [x] `Form1` をEngine利用に置換（UIは表示と操作だけ） (2026-02-05 完了)
  - [x] `dotnet build` + スモーク確認 (2026-02-05 完了)

- [x] 音量/ノイズ改善: 送信ゲイン二重適用の解消 + Opus無音時も常時送信（PLCノイズ抑制） (2026-02-05 完了)
  - [x] `SenderEngine` の送信ゲインを1回適用に統一 (2026-02-05 完了)
  - [x] Opusモードは無音でも毎フレーム送信（KeepAlive依存を減らす） (2026-02-05 完了)
  - [x] `dotnet build` で確認 (2026-02-05 完了)

- [x] 配布/共有: `dotnet publish` 出力をリポジトリ内に固定 (2026-02-05 完了)
  - [x] `publish.ps1` を追加（出力先: `.\publish\...`）
  - [x] READMEに「LAN共有で配布/実行」手順を追記
  - [x] `dotnet publish` 実行確認
