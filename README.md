# terminal-windows

AI エージェント専用の「もう 1 台の Windows PC」を VirtualBox の VM として用意し、

1. その画面をターミナル内に表示して人間が見たり触ったりでき、
2. AI エージェント（Claude Code など）が MCP 経由でその VM 内の Windows を操作できる

ようにするツールです。AI の操作は **VM の中だけ** で行われ、ホスト PC のマウス・キーボード・クリップボードには一切触れません。AI が作業している間も、ホスト PC は普段どおり使えます。


> **状態: 0.1.0（未検証）**
> コードと自動テスト（104 件）はそろっていますが、**実際の VirtualBox VM での動作確認はまだしていません**。
> 詳しくは [docs/VALIDATION.md](docs/VALIDATION.md) を見てください。

## 必要なもの

| 項目 | 要件 |
|---|---|
| ホスト OS | Windows 11 Home 以上 |
| VirtualBox | Oracle VirtualBox 7.1 以上（本体のみ。Extension Pack は不要で、使いません） |
| スペック目安 | メモリ 16GB 以上、空きディスク 64GB 以上、BIOS で VT-x / AMD-V を有効化 |
| ゲスト OS | Windows 11 の ISO（ご自身で用意してください。Microsoft 公式の評価版 Enterprise ISO も使えます） |
| ターミナル | Windows Terminal 1.22 以上（Sixel）か WezTerm（kitty graphics）。どちらも使えない場合は VirtualBox のウィンドウで表示します |

**ライセンス:** ゲストの Windows のライセンスは利用者の責任です。評価版 ISO は約 90 日で期限が切れます。期限が来たら、新しく作り直してください（`clean-setup` スナップショットから `clone` しても、評価期間はリセットされません）。

### WSL2 / Docker Desktop と一緒に使う場合

WSL2、Docker Desktop、「仮想マシン プラットフォーム」、メモリ整合性などが有効だと、VirtualBox は Windows Hypervisor Platform を経由して動きます。**動作はしますが遅くなります。** `doctor` はこの状態を検出して警告を出すだけで、設定は変更しません。

## ビルド

.NET SDK 8 以上が必要です。

```bash
powershell -ExecutionPolicy Bypass -File build.ps1 -Test
```

ビルドすると、`dist\terminal-windows.exe`（ホスト CLI）と `dist\guest\TerminalWindowsGuest.exe`（ゲスト用ヘルパー）ができます。2 つはこのフォルダ構成のまま置いておいてください。

ユーザー単位でインストールする場合（管理者権限は不要です）:

```bash
powershell -ExecutionPolicy Bypass -File install.ps1 -Skill -Mcp
```

`%LOCALAPPDATA%\Programs\terminal-windows` にコピーし、ユーザーの PATH に追加します。PATH に入れずにフルパスで実行する場合、PowerShell では先頭に `&` が必要です（例: `& "C:\…\dist\terminal-windows.exe" doctor`）。`-Skill` を付けると Claude Code のスキルを入れ、`-Mcp` を付けると `claude mcp add` で登録します。

## ISO の用意

Microsoft の公式ページ（https://www.microsoft.com/ja-jp/software-download/windows11）で、「x64 デバイス用 Windows 11 ディスク イメージ (ISO)」を日本語で選んでダウンロードしてください（約 8.3GiB）。中に入っているエディションは次のコマンドで確認できます。

```bash
terminal-windows iso-info C:\Users\you\Downloads\Windows11_Client_x64_ja-jp.iso
```

一般向けの ISO には Home / Education / Pro などが入っています。`create` / `up` は既定で **Pro** を選びます。変えたい場合は `--edition Home` のように指定してください。エディションを選ばないと、無人インストールがエディション選択の画面で止まります。

## 使い方

```bash
terminal-windows doctor
```

```bash
terminal-windows up --iso C:\ISO\Win11_Japanese_x64.iso
```

`up` を 1 回実行するだけで、次の処理がすべて自動で進みます。

1. VM を作成する（EFI、セキュアブート、TPM 2.0、NAT）
2. `autounattend.xml` で Windows を無人インストールする（ローカルアカウント `agent`、パスワードはランダム生成、OOBE はスキップ、自動ログオン）
3. 初回ログオン時に、ゲスト用ヘルパーと Guest Additions をインストールし、ゲスト内の設定を行う
4. 再起動後、ヘルパーとの通信を確認し、解像度を設定し、インストール用メディアを取り外す
5. スナップショット `clean-setup` を作成し、ビューアを開く

所要時間は 20〜60 分ほどです（WSL2 などが有効で WHP 経由になる環境では、さらにかかります）。途中経過は次の 2 つの方法で見られます。

- `terminal-windows show default` で VirtualBox のウィンドウを開く
- 2 分ごとに保存される `machines\default\logs\setup-screen.png` を見る

セットアップが終わったら、実機チェックを自動で実行できます。

```bash
terminal-windows selftest default
```

`selftest` は、VM の中で次の項目を順に試し、結果を `logs\selftest-*.md` に書き出します。

- スクリーンショット
- ポインター座標の正確さ
- Win+R
- 日本語入力
- UI Automation
- メモ帳の起動、クリック、終了
- スクロールとドラッグ
- ファイルの受け渡し

`--with-restore` を付けると、スナップショットと巻き戻しも試します。

**すでにある VM を作り直すことはありません。** 2 回目以降の `up` は、起動して接続するだけです。手順だけ確認したいときは `--plan` を付けてください（何も変更しません）。

### ビューアを VM なしで試す

```bash
terminal-windows view-demo
```

試験用の画面を送るデモ用ゲストに、ビューアをつないで開きます。画面には座標グリッド、アニメーション、受け取った入力の記録が出ます。入力は記録するだけで、ホストにも VM にも送りません。表示方式（Sixel / kitty）と、クリック・キー入力の確認に使ってください。

### よく使うコマンド

```bash
terminal-windows view default
```

```bash
terminal-windows stop default
```

```bash
terminal-windows start default
```

- `stop` は既定で状態保存（savestate）します。開いているアプリはそのまま残り、`start` で続きから再開できます。OS をシャットダウンしたい場合は `--shutdown`、電源を強制的に切りたい場合は `--force` を付けます。
- ビューアでは **Ctrl+G** で Windows のショートカット一覧（Win キー、Win+R、Alt+Tab など）を開き、**Ctrl+]** で終了します。ビューアを閉じても VM は動き続けます。
- ビューアのマウス操作（押す・動かす・離す）は、その場でゲストに送られます。ダブルクリックやトリプルクリックは Windows 自身が判定し、ドラッグ中の動きもそのまま見えます。ゲストのヘルパーが古い場合は、ボタンを離したときにクリックかドラッグとしてまとめて送る方式に自動で切り替わります（`terminal-windows update-helper NAME` で更新できます）。
- 用途ごとに VM を分けられます（例: `up unity`、`up web`）。セットアップ済みの状態から複製したい場合は `clone default web2 --from-snapshot clean-setup` を使います（リンククローン）。

全コマンドの一覧は `terminal-windows --help` で確認できます。

### Claude Code から使う

```bash
claude mcp add terminal-windows -- terminal-windows mcp default
```

```bash
terminal-windows skills install
```

MCP ツールは次のとおりです。

| ツール | 内容 |
|---|---|
| `screenshot` | 画面を取得する |
| `click` / `drag` / `scroll` | マウスで操作する |
| `type` / `key` | 文字やキーを入力する |
| `ui_tree` | UI Automation の要素ツリーを取得する |
| `wait` | 一定時間、または画面が変わるまで待つ |
| `status` | VM とヘルパーの状態を確認する |
| `snapshot` / `restore` | スナップショットを作成する / 巻き戻す |
| `export_file` / `list_inbox` | ファイルを受け渡す |
| `read_notes` / `append_notes` | 作業ノートを読み書きする |

補足:

- MCP のスクリーンショットは、モデルの画像サイズ上限に収まるよう縮小されます（既定の 1600×900 なら約 1430×804）。座標はすべて、この縮小後の画像の座標で指定します。
- このリポジトリは Claude Code プラグインとしても読み込めます（`.claude-plugin/`、`.mcp.json`、`skills/terminal-windows/SKILL.md`）。
- 他の MCP クライアント向けの設定は `terminal-windows mcp --config` で出力できます。

### ファイルの受け渡し

共有フォルダは使わず、制御チャネル経由でファイルをコピーします。

```bash
terminal-windows push default C:\work\data.xlsx
```

```bash
terminal-windows pull default
```

- `push` で送ったファイルは、ゲストの `Desktop\Inbox` に置かれます。
- `pull` は、ゲストの `Desktop\Outbox` を `%USERPROFILE%\.terminal-windows\machines\default\outbox\YYYYMMDD-HHMMSS\` にコピーします。
- `pull default --sync-outbox` を使うと、ゲストの Outbox に書き込みが終わったファイルを自動でホストへコピーし続けます。
- 名前に `..` や絶対パスを含むファイルは拒否します。シンボリックリンクとジャンクションはたどりません。転送後は SHA-256 で照合し、既存のファイルは上書きしません。ホストは受け取ったファイルを実行したり展開したりしません。

## セキュリティと隔離について（必ず読んでください）

- **ホストとは切り離されています。** ホストのフォルダ、クリップボード、ドラッグ＆ドロップ、オーディオ、USB は VM に接続しません。共有フォルダは `--share PATH` を指定したときだけ使え、既定は読み取り専用です。
- **ネットワークには出られます。** VirtualBox の NAT 経由で、ゲストからインターネットへ出られます。`--nat-localhostreachable1 off` を設定しているため、ホストの localhost のサービスには届きませんが、ホストの LAN 側のアドレスなどには届く可能性があります。
- **`--offline` は隔離の保証ではありません。** ゲストのファイアウォールで外向きの通信を遮断するだけの設定です。ゲスト内の AI は管理者権限を持っているので、ファイアウォールを自分で解除できます。「うっかり外部に出ない」ための設定と考えてください。
- **ゲスト内の設定は、AI 専用の使い捨て環境だから許容しているものです。** UAC の確認なし昇格・セキュアデスクトップの無効化、自動ログオン、ロック画面の無効化は、ゲスト VM の中でだけ行います。**ホストや普段使いの PC には絶対に適用しないでください。** ゲスト用ヘルパーは、SMBIOS が VirtualBox でない環境では起動を拒否します。ただし、これは誤実行を防ぐためのもので、セキュリティを保証するものではありません。
- **通信は制限しています。** ホスト側のポートは `127.0.0.1` にだけ割り当てます。接続時にはランダムなトークンで認証し、不一致なら即座に切断します。メッセージのサイズ上限とタイムアウトも設けています。
- **秘密情報の扱い:** 接続トークンと自動ログオン用パスワードは、DPAPI（現在のユーザーだけが復号できる）で暗号化して `machine.json` に保存し、そのファイルには現在のユーザーだけが読める ACL を設定しています。自動ログオン用パスワードは仕組み上ゲスト内にも残るため、ランダムに生成した値だけを使います（普段使いのパスワードは使いません）。
- **使い道の制限:** 堅牢なサンドボックスではないため、マルウェアの解析には使わないでください。また、CAPTCHA、ボット検知、アンチチート、各種サービスの利用規約を回避する機能はありません。

## データの保存場所

`%USERPROFILE%\.terminal-windows\machines\NAME\`（環境変数 `TERMINAL_WINDOWS_HOME` で変更できます）に、次のものが入っています。

- `machine.json`
- `vm\`（VDI ディスクと VM の定義）
- `outbox\` / `inbox\`
- `notes.md`（作業ノート）
- `logs\`（セットアップ中の画面 `setup-screen.png`、`selftest-*.md` など）

**仕様からの変更:** 仕様書では `%LOCALAPPDATA%\terminal-windows` でしたが、変更しました。Claude デスクトップアプリなど MSIX パッケージのアプリから起動されると、Windows が AppData への書き込みをアプリ専用の場所へ黙って振り替えます。そのため VirtualBox がセットアップ用 ISO を見つけられず、作業ノートも二重化してしまいます（実機で発生を確認済み）。`TERMINAL_WINDOWS_HOME` を AppData の下に向けた場合は、`doctor` がエラーとして知らせます。

VM を削除するコマンドは、誤って消してしまうのを防ぐため、まだ用意していません。

## 構成

```
src/TerminalWindows.Protocol   共通: 長さプレフィックス付き JSON とバイナリのフレーム、RPC、トークン認証、キー解析、パス検証、PNG/LZ4
src/TerminalWindows.Cli        ホスト CLI: IVmBackend（VirtualBoxBackend）、無人セットアップ、ビューア、MCP サーバー
src/TerminalWindows.Guest      ゲスト用ヘルパー: WGC → DXGI → GDI の順で画面を取得、SendInput、UI Automation、インストーラー、自己更新
tests/TerminalWindows.Tests    自動テスト
skills/terminal-windows        Claude Code スキル
```

将来 Hyper-V のバックエンド（Pro 以上）を追加する場合は、`IVmBackend` を実装し、`GetGuestEndpoint` を差し替える想定です。
