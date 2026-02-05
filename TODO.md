- [x] READMEにVB-CABLEのDL先を追記 (2026-02-05 完了)
- [x] .gitignoreの確認（ログ/設定の扱い） (2026-02-05 完了)

- [x] UIリファクタ Phase1: `Form1` をpartial分割（挙動変更なし） (2026-02-05 完了)
  - [x] `Form1.Ui.cs` へUI構築/表示更新を移動
  - [x] `Form1.Settings.cs` へ設定永続化/設定ウィンドウ周りを移動
  - [x] `Form1.Receiver.cs` へ受信系ロジックを移動
  - [x] `Form1.Sender.cs` へ送信系ロジックを移動
  - [x] `Form1.Audio.cs` へ音声処理ヘルパを移動（関数移動のみ）
  - [x] `dotnet build` でビルド確認
  - [x] Phase1完了の声かけ（動作確認依頼）
  - [x] ユーザー動作確認（起動/受信/送信/設定）

- [ ] UIリファクタ Phase2: UIとロジック分離（Engine化）
  - [ ] Engine境界/API設計（イベント通知、UIスレッド境界）
  - [ ] `ReceiverEngine` 作成（受信/ジッタ/再生）
  - [ ] `SenderEngine` 作成（キャプチャ/エンコード/送信）
  - [ ] `Form1` をEngine利用に置換（UIは表示と操作だけ）
  - [ ] `dotnet build` + スモーク確認

- [x] 配布/共有: `dotnet publish` 出力をリポジトリ内に固定 (2026-02-05 完了)
  - [x] `publish.ps1` を追加（出力先: `.\publish\...`）
  - [x] READMEに「LAN共有で配布/実行」手順を追記
  - [x] `dotnet publish` 実行確認
