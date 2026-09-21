# セットアップ手順（個別インストールと単体検証）

原則: **部品を混ぜない**。一つインストールしたら、その場で単体の動作を検証してから次へ進む。
本書は再現手順書であり、実施結果もここに記録していく。

## 共通の前提

- OS: Windows 11
- ドライバ類のインストールには管理者権限（UAC承認）を要する
- 取得元は全て公式のGitHubリリースとし、取得したバージョンを本書に記録する
- 検証は次の部品に進む前に行い、結果を本書に残す

## Phase 1: VirtualDisplayDriver（仮想モニタ／ディスプレイちゃん）

| 項目 | 内容 |
|---|---|
| 取得元 | https://github.com/VirtualDrivers/Virtual-Display-Driver/releases （リポジトリ名は Virtual-Display-Driver） |
| 手順 | 1. リリースからインストーラ（またはzip）を取得 2. インストール実行 3. 要再起動の確認 |
| 単体検証 | ① 設定→ディスプレイに仮想モニタが現れる ② 仮想モニタの解像度を設定できる ③ フレームバッファをプログラムからキャプチャできる |
| バージョン | **v25.5.2**（セットアップexe: Virtual.Display.Driver-v25.05.03-setup-x64.exe、署名ドライバv24.12.24） |
| 取得記録 | 2026-09-21取得、5,516,873バイト、SHA256: ca10b85babecfb636c85b3f04d2306968d4f940dd3dd35767f866207bfba846e |
| 結果 | **ドライバ導入完了（2026-09-21）**。デバイス登録確認済み: ROOT\DISPLAY\0000 / Status OK / Provider MikeTheTech / ドライバ日付2024-12-19。だが**仮想モニタは未出現**（ディスプレイ1枚のまま） |
| トラブル記録 | ①elevatedインストーラはUIPIによりエージェントから操作不可 → ウィザードは人間がクリックして完走。②モニタ出現のためVDDControlでDisplay Count=1設定→Restart Driver(s)を実行したところ、**ドライバ再起動でディスプレイ系がハング**（OSは生存、画面のみ凍結。隠れたUACが一つ待機中だった）→ **ユーザーが強制再起動を選択（2026-09-21）** |
| 再起動後の再開手順 | 1. ディスプレイ枚数を確認（起動時にドライバがロードされ仮想モニタが自動出現する可能性） 2. 出てなければ VDDControl-v25.05.03.exe を起動（.NET Runtime 6.0.36 導入済みなので起動するはず）→ Virtual Display Driverメニュー → Display Count → 1 → Restart Driver(s)（UAC承認） 3. 枚数確認 → 2枚になれば Phase 1 検証合格 |
| 再起動後の経過 | 有効化維持確認済み（Status OK）。だがモニタ未出現。VDDControlを起動しコマンド調査: ①ヘルプコマンド一覧取得成功（SETCOUNT/RELOAD_DRIVER/RESTART_DRIVER等）②LOGGING true / DEBUGLOGGING true 有効化済み ③RELOAD_DRIVER → ドライバ応答なし ④SETCOUNT 1 → XML更新成功だが「**[RESPONSE] No response received from driver.**」＝**ドライバがゾンビ状態（コマンドに無応答）**。結論: **完全シャットダウン→コールドブートを実施する（2026-09-21）**。コールドブート後も「No response/モニタ未出現」ならドライバ互換性問題として計画B（MolotovCherry/virtual-display-rs 等）へ |

## Phase 2: OpenTabletDriver（OTD／ペン側ドライバ）

| 項目 | 内容 |
|---|---|
| 取得元 | https://github.com/OpenTabletDriver/OpenTabletDriver/releases |
| 手順 | 1. Windows向けビルドを取得・展開 2. デーモンを起動 3. GUIを起動 |
| 単体検証 | ① デーモンが常駐する ② GUIが開く ③ タブレット未接続の状態でエラーなく起動する |
| バージョン | **v0.6.7**（OpenTabletDriver-0.6.7_win-x64.zip） |
| 取得記録 | 2026-09-21取得、9,086,725バイト、SHA256: 4ee9ae149404f0b39132624e488507639c8ce56d1ec77755c66ee7e58562110a |
| 結果 | 未実施（ダウンロード済み） |

## Phase 3: vmulti（仮想ペンタブ装置）

| 項目 | 内容 |
|---|---|
| 取得元 | https://github.com/X9VoiD/vmulti-bin （OTD開発者による公式バイナリ配布。ソースはdjpnewton/vmultiフォーク他） |
| 手順 | 1. ドライバを取得 2. インストール 3. テスト報告の書き込み |
| 単体検証 | ① デバイスマネージャに仮想HIDデジタイザが現れる ② テスト報告を書き込むとカーソル/ペンイベントが発生する |
| バージョン | **v1.0**（VMulti.Driver.zip、2020-10-15リリース） |
| 取得記録 | 2026-09-21取得、1,989,586バイト、SHA256: cc34f74a6bee7f3d1fdc3c10aae27118a359f56a51de2f5965b7d0d3e353d3a1 |
| 結果 | 未実施（ダウンロード済み） |

## Phase 4（次フェーズ）: 組み合わせ検証

個別検証が全て通ってから着手する。組み合わせ方（入力側ニセ装置の用意、OTDの画面マッピング設定、出力側VMultiとの分離）は設計書（design.md）の実験待ち事項 #6 を参照。

## 記録のルール

- 各Phaseの結果は、実施後に本書へ記録する（日付・バージョン・確認方法・判定）
- 手順の変更が必要になった場合は、次へ進む前に本書を更新する
