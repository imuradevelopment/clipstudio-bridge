# セットアップ手順（個別インストールと単体検証）

原則: **部品を混ぜない**。一つインストールしたら、その場で単体の動作を検証してから次へ進む。

## 共通の前提

- OS: Windows 11
- ドライバ類のインストールには管理者権限（UAC承認）を要する
- 取得元は全て公式のGitHubリリースとし、取得したバージョンを本書に記録する

## Phase 1: VirtualDisplayDriver（仮想モニタ）

| 項目 | 内容 |
|---|---|
| 取得元 | https://github.com/VirtualDrivers/Virtual-Display-Driver/releases |
| バージョン | v25.5.2（Virtual.Display.Driver-v25.05.03-setup-x64.exe） |
| SHA256 | ca10b85babecfb636c85b3f04d2306968d4f940dd3dd35767f866207bfba846e |
| 手順 | 1. setup exeを実行（UAC承認） 2. インストーラウィザードを完走 3. インストール先: C:\VirtualDisplayDriver\ |
| 単体検証 | ① 設定→ディスプレイに仮想モニタが現れる ② 仮想モニタの解像度を設定できる ③ フレームバッファをプログラムからキャプチャできる |
| 検証結果 | **合格（2026-09-21）**。①仮想モニタ出現: Display 2 (800x600, 拡張モードで物理モニタ右隣 1920,0 に配置) ③フレームバッファ読み取り成功（ComputerUseのディスプレイキャプチャで仮想デスクトップを取得可能）②解像度変更は未実施（現状800x600、vdd_settings.xmlの解像度リストから変更可能） |
| 検証の要点 | ドライバ自体は正常動作。モニタが出なかった原因は Windowsの表示トポロジが「PC画面のみ」だったこと。**拡張モードへの切替はプログラムから可能**: PowerShellで `SetDisplayConfig(0,0,0,0, SDC_APPLY|SDC_TOPOLOGY_EXTEND)`（要: 仮想モニタ有効状態を維持）。再現手順: インストール→有効化→SetDisplayConfigで拡張 |

### Phase 1 追加: VDDControl（管理アプリ）

| 項目 | 内容 |
|---|---|
| 取得元 | 同リリースの VDDControl-v25.05.03.exe |
| SHA256 | 3120cd1b9a27fe0c6e9a96e1abe3465a37726016ea4654692b91bb33ac07b1ee |
| 前提 | .NET Desktop Runtime 6.0 x64（https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/6.0.36/windowsdesktop-runtime-6.0.36-win-x64.exe / SHA256: 0d20debb26fc8b2bc84f25fbd9d4596a6364af8517ebf012e8b871127b798941） |

## Phase 2: OpenTabletDriver（OTD）

| 項目 | 内容 |
|---|---|
| 取得元 | https://github.com/OpenTabletDriver/OpenTabletDriver/releases |
| バージョン | v0.6.7（OpenTabletDriver-0.6.7_win-x64.zip） |
| SHA256 | 4ee9ae149404f0b39132624e488507639c8ce56d1ec77755c66ee7e58562110a |
| 手順 | 1. zipを展開 2. デーモンを起動 3. GUIを起動 |
| 単体検証 | ① デーモンが常駐する ② GUIが開く ③ タブレット未接続の状態でエラーなく起動する |

## Phase 3: vmulti（仮想ペンタブ装置）

| 項目 | 内容 |
|---|---|
| 取得元 | https://github.com/X9VoiD/vmulti-bin （OTD開発者による公式バイナリ配布） |
| バージョン | v1.0（VMulti.Driver.zip） |
| SHA256 | cc34f74a6bee7f3d1fdc3c10aae27118a359f56a51de2f5965b7d0d3e353d3a1 |
| 手順 | 1. ドライバを取得 2. インストール 3. テスト報告の書き込み |
| 単体検証 | ① デバイスマネージャに仮想HIDデジタイザが現れる ② テスト報告を書き込むとカーソル/ペンイベントが発生する |

## Phase 4（次フェーズ）: 組み合わせ検証

個別検証が全て通ってから着手する。組み合わせ方は設計書（design.md）の実験待ち事項を参照。
