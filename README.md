# terminal-windows

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Sponsor](https://img.shields.io/static/v1?label=Sponsor&message=%E2%9D%A4&logo=GitHub&color=%23fe8e86)](https://github.com/sponsors/Nishi-Yura)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-ffdd00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/nishiyura)

**AI エージェント専用の「もう 1 台の Windows PC」を、あなたの PC の中に。**

> *An AI-only Windows 11 VM you can watch in your terminal and let AI agents drive — your own mouse, keyboard and clipboard are never touched. Works on Windows 11 Home (VMware Workstation or VirtualBox).*

terminal-windows は、VMware Workstation（無料）か VirtualBox で **AI 専用の Windows 11** を自動で用意するツールです。

- 🤖 **AI はその VM の中だけで作業します。** ホスト PC のマウス・キーボード・クリップボードには一切触れないので、AI が作業している間もあなたは PC を普段どおり使えます。
- 🖥️ **VM の画面はターミナルの中に表示されます。** 見るだけでなく、クリックや入力もできます。
- 🧩 **AI のつなぎ方は 2 通りあります。** ホストの AI ツール（MCP に対応したもの。Codex CLI、Gemini CLI、Claude、Cursor、VS Code など）から MCP で操作する方法と、AI そのものを VM の中で動かす方法です。
- 🏠 **Windows 11 Home でも動きます。** Hyper-V は使わず、無料の VMware Workstation か VirtualBox を使います。

---

## 目次

- [特徴](#特徴)
- [今の状態](#今の状態)
- [必要なもの](#必要なもの)
- [クイックスタート](#クイックスタート)
- [AI のつなぎ方](#ai-のつなぎ方)
- [よく使うコマンド](#よく使うコマンド)
- [ファイルの受け渡し](#ファイルの受け渡し)
- [速くしたいとき](#速くしたいとき)
- [注意してほしいこと](#注意してほしいこと)
- [セキュリティと隔離](#セキュリティと隔離)
- [そのほか](#そのほか)
- [応援する](#応援する)

---

## 特徴

| | |
|---|---|
| **完全無人セットアップ** | ISO を渡すだけ。Windows のインストール、アカウント作成、自動ログオン、ヘルパーの導入、スナップショット作成まで自動です。 |
| **ターミナルで見て触れる** | Windows Terminal（Sixel）や WezTerm（kitty）の中に VM の画面を表示します。色はフレームごとに画面の色から作るので、ウィンドウの色や文字のにじみも元のままです。Win キーや Alt+Tab は **Ctrl+G** のパレットから送れます。 |
| **AI 向けの操作がそろっている** | スクリーンショット、クリック、ドラッグ、日本語入力、キー操作に加えて、**UI Automation のツリー**でボタンの位置を正確に取れます。 |
| **失敗しても戻せる** | スナップショットと巻き戻しができます。巻き戻す前の状態も自動で保存します。 |
| **作業を引き継げる** | 停止は「状態保存」が既定です。アプリを開いたまま止めて、続きから再開できます。作業ノートはホスト側に残ります。 |
| **ファイルは安全に受け渡し** | 共有フォルダは使わず、Inbox / Outbox を通してコピーします（SHA-256 で照合し、上書きはしません）。 |
| **VM の中で AI を動かせる** | Claude Code / Codex CLI / Gemini CLI のうち選んだものを VM の中に入れ、ホストのターミナルから使えます（`terminal-windows claude` / `codex` / `gemini`）。 |

---

## 今の状態

**0.1.0（開発中）**

- Windows 11 Home（VMware Workstation 17.6）で、ISO からの完全自動セットアップ（AI の導入まで含めて約 15 分）、画面表示と操作、MCP、スナップショット、VM の中の AI（Claude Code / Codex CLI / Gemini CLI）を確認しています。VirtualBox は、Windows のハイパーバイザーを切った状態で確認しています。
- ビルド済みの配布物（Release）はありません。[1 行インストール](#いちばん簡単な方法-powershell-に-1-行)が、ソースから自動でビルドします。

---

## 必要なもの

| 項目 | 要件 |
|---|---|
| ホスト OS | Windows 11 Home 以上 |
| 仮想化ソフト | [VMware Workstation Pro](https://www.vmware.com/products/desktop-hypervisor/workstation-and-fusion)（個人利用は無料）か [Oracle VirtualBox](https://www.virtualbox.org/) 7.1 以上（Extension Pack は不要）。セットアップの案内がどちらにするか聞きます |
| スペックの目安 | メモリ 16GB 以上、空きディスク 64GB 以上、BIOS で VT-x / AMD-V を有効にしておく |
| Windows 11 の ISO | [Microsoft 公式ページ](https://www.microsoft.com/ja-jp/software-download/windows11)から入手します（約 8.3GiB）。**ゲストの Windows のライセンスは利用者の責任です。** |
| ターミナル | Windows Terminal 1.22 以上か WezTerm（どちらもないときは色付きの文字で表示します） |
| ビルドする場合 | .NET SDK 8 以上（1 行インストールでは自動で入ります） |

---

## クイックスタート

### いちばん簡単な方法: PowerShell に 1 行

PowerShell を開いて、次の 1 行を貼り付けます（管理者権限は不要です）。

```powershell
irm https://raw.githubusercontent.com/Nishi-Yura/terminal-windows/main/get.ps1 | iex
```

ビルドに必要な .NET SDK がなければ Microsoft の公式スクリプトで `%USERPROFILE%\.terminal-windows\sdk` に入れ、このリポジトリのソースを取ってきてビルドし、インストールして、下のセットアップを始めます。同じ 1 行をもう一度実行すると最新版に更新されます。

### exe をダブルクリック

`terminal-windows.exe` は 1 つの exe で完結しています（VM 内のヘルパーも中に入っています）。ダブルクリックすると、セットアップの案内が始まります。質問に答えていくだけで、最後まで進みます。

1. 仮想化ソフトを決めます。VMware Workstation が入っていればそれを使います。
   - Windows のハイパーバイザー（メモリ整合性・VBS・WSL2 など）が動いている PC では、VirtualBox はとても遅く、固まることがあります。その場合は「ハイパーバイザーをオフにして VirtualBox を使う」か「VMware Workstation Pro を入れる」かを選びます。新しい PC は、たいていハイパーバイザーがオンです。
   - VirtualBox は winget で入れます（管理者の確認画面が出ます）。ハイパーバイザーをオフにするときは再起動が必要で、サインインすると続きから自動で再開します。元に戻すときは `terminal-windows host-mode secure --yes` を実行します。
   - VMware は Broadcom のサイトからダウンロードします（アカウント登録が必要）。ダウンロードが終わると、インストーラーを自動で開きます。
3. ダウンロードフォルダーかデスクトップにある Windows 11 の ISO を使います。見つからなければ Microsoft の公式ページを開き、ダウンロードが終わると自動で次に進みます。
4. VM の中で使う AI（Claude Code / Codex CLI / Gemini CLI、または入れない）を選びます。VM の作成（VMware で約 15 分、VirtualBox で約 40 分）、AI の導入、軽量化、HiDPI 表示、スナップショット作成まで自動で進みます。
5. 最後に AI が開くので、サインインします。次からは exe を開くだけで、VM の中の AI につながります。

何をするかだけを確認したいときは、`terminal-windows setup --dry-run` を実行します。何も入れず、何も変えずに、確認結果だけを表示します。

### ふだんの使い方

`terminal-windows.exe` を開くと（ターミナルで `terminal-windows` と打っても同じ）、**番号で選ぶメニューが出ます**。Enter だけで 1 番（AI の窓。AI を入れていなければ VM の画面）が開きます。最初からメニューを出さずに AI の窓や VM の画面を出したいときは、メニューの「開いたときに最初に出すもの」で変えられます。

- 画面のいちばん上の行に、状態（マシン名、キー操作、fps など）が出ます。
- **Ctrl+]** で画面を閉じます（VM は動き続けます）。メニューから開いたときは、番号のメニューに戻ります。
- **F12** を押すと、上の行にメニューが出ます。

  `1:閉じる  2:スナップショット  3:VM を止める  4:Windows キー  5:VM の解像度をこの枠に合わせる  6:元の解像度`

  スナップショットは、画面を見たまま裏で撮れます。
- **Ctrl+G** を押すと、Windows のショートカット一覧（Win キー、Alt+Tab など）が出ます。

```
== terminal-windows: default (vmware, 起動中) ==
  1) codex を開く（VM のデスクトップの窓で。画面ごと見えます）   ← 選んだ AI の名前が出ます
  2) 画面を見る・操作する
  3) VM の中のターミナル（PowerShell、文字だけ）
  4) AI の成果物（Outbox）を この PC に取り込む
  5) この PC の AI ツール（MCP に対応したもの）から VM を使えるようにする
  6) スナップショット（撮る・戻す・消す）
  7) VM を止める（状態を保存。次回そのまま再開）
  8) 開いたときに最初に出すもの: このメニュー（変更）
  q) 終わる（VM はそのまま）
```

- `terminal-windows codex --window`（`claude` / `gemini` も同じ）で、AI の窓を直接開くこともできます。
- マシンが `default` だけのときは、コマンドのマシン名を省略できます（`terminal-windows view`、`terminal-windows push file.txt` など）。
- 画面はターミナルの中に表示します。画像表示（Sixel / kitty）に対応していないターミナルでは、色付きの文字で表示します（解像度は低めです）。
- **画質について:** 新しい VM は **HiDPI（2 倍）表示**です。画面の作り（1280x720）はそのままで、実際のピクセルは 2 倍（2560x1440、拡大率 200%）なので、ビューアで縮めても文字がくっきりします。`terminal-windows display NAME --hidpi` / `--standard` でいつでも切り替えられます（再起動は不要。開いていたアプリは開き直すとくっきりします）。
  - Windows Terminal は Sixel の画像を「1 文字 = 横 10 × 縦 20 ピクセル」として扱うので、表示の大きさは窓の大きさ（文字数）で決まります。小さい窓では上の行に「縮小 79%」のように出ます。**窓を最大化するか、Ctrl+- で文字を小さくすると**大きく映ります。
  - 標準表示のときは、F12 のメニューの 5 で VM の解像度をこの枠に合わせることもできます（VMware の画面ドライバーにある大きさの中で、今よりくっきりするときだけ変えます。6 で元に戻ります）。
- ターミナルの外に窓を開きたいときは `terminal-windows view --window` で専用の窓（元の解像度、マウスと日本語入力もそのまま）が開きます。

### コマンドで進める方法

**1. ビルドしてインストールする**（管理者権限は不要です）

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

```bash
powershell -ExecutionPolicy Bypass -File install.ps1 -Skill
```

`%USERPROFILE%\.terminal-windows\bin` に入り、PATH に追加されます。新しいターミナルを開いてから続けてください。

Visual Studio の C++ ツール（「C++ によるデスクトップ開発」）が入っている PC では、`build.ps1` が自動でネイティブコード（Native AOT）としてビルドし、起動が数倍速くなります（入っていなければ通常のビルドになります。`-NoAot` で通常のビルドを選べます）。

**2. 環境を確認する**

```bash
terminal-windows doctor
```

**3. VM を作る**（15〜40 分ほどかかります。**ご自身で開いたターミナルで**実行し、その間は PC をスリープさせないでください）

```bash
terminal-windows up --iso C:\Users\you\Downloads\Windows11_Client_x64_ja-jp.iso
```

Windows のインストールから初期設定、ヘルパーの導入、スナップショット `clean-setup` の作成まで、すべて自動で進みます。一般向けの ISO からは、既定で **Pro** を入れます（`--edition` で変更できます）。

**4. 動作を確認する**

```bash
terminal-windows selftest default
```

**5. 画面を見る**

```bash
terminal-windows view default
```

ビューアの中では **Ctrl+G** で Windows のショートカット一覧、**F12** でメニューを開けます。**Ctrl+]** でビューアを閉じます（VM は動き続けます）。

> VM がなくても、ビューアの見え方は `terminal-windows view-demo` で試せます。

---

## AI のつなぎ方

### A. ホストの AI から操作する（MCP）

```bash
terminal-windows connect
```

`connect` は、この PC に入っている AI ツールを探して、それぞれに MCP サーバーと使い方の説明を登録します（メニューからも選べます。`--dry-run` を付けると、何を変えるかだけ表示します）。

| ID | ツール | MCP の登録先 | 使い方の説明 |
|---|---|---|---|
| `antigravity` | Antigravity（Google） | `~\.gemini\antigravity\mcp_config.json` | MCP サーバーの説明文 |
| `claude-code` | Claude Code | `claude mcp add --scope user` | `~\.claude\skills\terminal-windows\SKILL.md` |
| `claude-desktop` | Claude Desktop | `%LOCALAPPDATA%\Claude-Data\claude_*\claude_desktop_config.json`（古い版は `%APPDATA%\Claude\`） | MCP サーバーの説明文 |
| `cline` | Cline（VS Code 拡張） | `…\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json` | MCP サーバーの説明文 |
| `codex` | Codex CLI（OpenAI / ChatGPT） | `~\.codex\config.toml` | `~\.codex\AGENTS.md` の専用セクション |
| `cursor` | Cursor | `~\.cursor\mcp.json` | MCP サーバーの説明文 |
| `gemini` | Gemini CLI（Google） | `~\.gemini\settings.json` | `~\.gemini\GEMINI.md` の専用セクション |
| `lmstudio` | LM Studio（手元のモデル。無料） | `~\.lmstudio\mcp.json` | MCP サーバーの説明文 |
| `vscode` | VS Code（Copilot のエージェント） | `%APPDATA%\Code\User\mcp.json` | MCP サーバーの説明文 |
| `windsurf` | Windsurf | `~\.codeium\windsurf\mcp_config.json` | MCP サーバーの説明文 |

ChatGPT のアプリ（Web / デスクトップ）は、手元の PC で動く MCP サーバーにはつなげません（リモートのサーバーだけに対応）。ChatGPT のアカウントで使う場合は Codex CLI を使ってください。

- 設定ファイルのほかの内容はそのまま残します。最初に書き換える前に、元のファイルを `*.bak-terminal-windows` として保存します。
- コメント入りの JSON（VS Code など）は書き換えず、貼り付ける設定を表示します。
- exe の場所を絶対パスで登録するので、PATH の変更がまだ反映されていないアプリでも動きます。
- 上の表にないツールには、`terminal-windows mcp --config` で出る設定を貼ってください（よくある形式をまとめて表示します）。

**登録の確認と変更**

```bash
terminal-windows connect --list                 # 対応ツールの一覧と、入っているか・登録済みか
terminal-windows connect --tool codex,cursor    # まだ入れていないツールにも先に登録しておく
terminal-windows connect --remove --tool cursor # 登録を外す（ほかの設定はそのまま）
```

**MCP がない AI でも使えます**

コマンドを実行できる AI なら、次の 1 行を伝えるだけで VM を使えます。

> Windows の VM を使う前に `terminal-windows skills get` を実行して、書かれている手順に従って

`skills get` は使い方の説明を表示します。AI はそれを読んで、`terminal-windows action` で VM を操作します。

```bash
terminal-windows action default open notepad
terminal-windows action default set-text --window メモ帳 --type Document --value "こんにちは"
terminal-windows action default click-element --window メモ帳 --name ファイル
terminal-windows action default read-text --window メモ帳
terminal-windows action default run "Get-ChildItem $env:USERPROFILE\Desktop"
```

どのコマンドも結果を JSON で返し、失敗すると 0 以外で終わります。VM が止まっていれば自動で起動します。

**無料で確認したこと**（チャットは送らず、MCP の起動と接続だけを確認）

| ツール | 結果 |
|---|---|
| Claude Code | 接続して実際に操作 |
| Codex CLI | `codex mcp list` で enabled（`config.toml` を正しく読める） |
| Gemini CLI | `gemini mcp list` で Connected（作業フォルダーを信頼済みにする必要あり） |
| Claude Desktop | 設定ファイルどおりにサーバーを起動（アプリが MCP サーバーを立ち上げたことを確認） |
| Cursor ほか | 設定ファイルどおりにサーバーを起動し、27 個のツールと VM の状態を取得 |

MCP に対応した AI ツールから、次のツールで VM を操作できます。

| ツール | 内容 |
|---|---|
| `screenshot` | 画面を取得する |
| `click` / `drag` / `scroll` | マウス操作 |
| `type` / `key` | 文字入力（日本語も可）、キー操作（`ctrl+s`、`win+r` など） |
| `ui_tree` | UI Automation の要素ツリー（ボタンの名前と位置） |
| `click_element` | ボタン・メニュー・タブ・リンクなどを**名前で**押す（座標の推測がいらない） |
| `set_text` | 入力欄に文字を入れる（パスワード欄は拒否） |
| `wait_for` | ダイアログやボタンが出る（消える）まで待つ |
| `read_text` | 窓の文字を読む（スクリーンショットなしで、結果やエラー文を正確に） |
| `find_element` | 名前・種類・AutomationId で部品を探す |
| `list_windows` / `focus_window` / `close_window` | 窓の一覧、前面に出す、名前を指定して閉じる（AI 自身の窓は閉じない） |
| `open` | アプリ・ファイル・フォルダ・URL を開く |
| `clipboard_get` / `clipboard_set` | VM のクリップボード（ホストのものには触れません） |
| `run_powershell` | VM の中で PowerShell を実行して結果を受け取る |
| `wait` | 一定時間、または画面が変わるまで待つ |
| `status` | VM の状態、ロック画面かどうか |
| `snapshot` / `restore` | スナップショット、巻き戻し |
| `export_file` / `list_inbox` | ファイルの受け渡し |
| `read_notes` / `append_notes` | 作業ノート |

### B. AI そのものを VM の中で動かす

```bash
terminal-windows agent-install default --agent codex --yes
```

```bash
terminal-windows codex default
```

`agent-install` は、VM の中に Git、選んだ AI、画面操作用の MCP、説明書を入れます（`--agent claude|codex|gemini` は必須です）。AI の名前のコマンドを実行すると、VM の中の AI がホストのターミナルに表示されます（**Ctrl+]** で切り離し）。

- **サインインはご本人が行ってください。** 表示された URL をホストのブラウザで開き、出てきたコードを貼り付けます。
- サインイン情報は VM の中に保存され、VM の中の AI からも読める状態になります。
- VM からインターネットに出る必要があります（`--offline` の VM では使えません）。

入れる AI は `--agent` で選びます。

| AI | インストール | 起動 | 説明書の置き場所 |
|---|---|---|---|
| Anthropic Claude Code | `agent-install NAME --agent claude --yes` | `terminal-windows claude NAME` | `~\.claude\skills\terminal-windows-desktop\SKILL.md` と `~\.claude\CLAUDE.md` |
| OpenAI Codex CLI | `agent-install NAME --agent codex --yes` | `terminal-windows codex NAME` | `~\.codex\AGENTS.md` |
| Google Gemini CLI | `agent-install NAME --agent gemini --yes` | `terminal-windows gemini NAME` | `~\.gemini\GEMINI.md` |

- どの AI にも、画面操作用の MCP サーバー `desktop` を登録します。
- Codex と Gemini は Node.js（npm）で入れます。
- Gemini CLI は、作業フォルダーを「信頼する」と答えるまで画面操作ツールを使いません。最初の起動で聞かれたら信頼を選んでください（選ぶのはご本人です）。
- 説明書がすでにある場合は、あなたの書いた内容を残したまま、terminal-windows の部分だけを追加・更新します。
- 各サービスの料金と利用条件は、それぞれのアカウントの契約に従います。

それ以外の AI CLI も、`terminal-windows term NAME -- コマンド` で VM の中で動かせます。

---

## よく使うコマンド

| コマンド | 内容 |
|---|---|
| `up [NAME] --iso PATH` | VM がなければ作り、起動して、ビューアを開く |
| `start` / `stop` NAME | 起動 / 停止。停止は既定で状態保存（`--shutdown` で OS を終了、`--force` で強制的に電源を切る） |
| `view NAME` | ターミナルで画面を見る |
| `show` / `hide` NAME | 仮想化ソフト（VMware / VirtualBox）のウィンドウを出す / 消す |
| `status NAME` / `ls` | 状態 / 一覧 |
| `snapshot NAME --name LABEL` | スナップショットを作る |
| `restore NAME LABEL` | 巻き戻す（直前の状態は `pre-restore-*` として自動保存） |
| `clone NAME NEW` | セットアップ済みの状態から複製する |
| `push` / `pull` NAME | ファイルを送る / 受け取る |
| `notes NAME` | 作業ノートを見る |
| `claude` / `codex` / `gemini` / `term` / `exec` NAME | VM の中で AI / 端末 / PowerShell を動かす |
| `tune NAME --yes` | VM の中を軽くする |
| `display NAME --hidpi` / `--standard` | HiDPI（2 倍）表示 / 標準表示 |
| `delete NAME --yes` | VM を消す（ディスクとスナップショットも消えます） |
| `selftest NAME` | 実機での動作を確認する |
| `doctor` | 環境を診断する |

全部の一覧は `terminal-windows --help` で見られます。

---

## ファイルの受け渡し

```bash
terminal-windows push default C:\work\data.xlsx
```

```bash
terminal-windows pull default
```

- `push` で送ったものは、VM のデスクトップの **Inbox** に置かれます。
- `pull` は、VM のデスクトップの **Outbox** を、ホストの `%USERPROFILE%\.terminal-windows\machines\default\outbox\日時\` にコピーします。
- `pull --sync-outbox` を付けると、Outbox に置かれたファイルを自動でコピーし続けます。

---

## 速くしたいとき

WSL2、Docker Desktop、メモリ整合性などが有効な PC では、VirtualBox が Windows のハイパーバイザーを経由して動くため、**かなり遅くなります**（`doctor` が警告します）。実機では、Windows 11 のセットアップが再起動のあとで毎回固まりました。

**おすすめ: VMware Workstation Pro を使う。** 個人利用は無料で、Broadcom のアカウントを登録すればダウンロードできます。ハイパーバイザーがオンのままでも、セットアップは約 12 分で終わり、自己テストもすべて通りました。VMware が入っていて、ハイパーバイザーがオンの PC では、新しい VM は自動で VMware を使います（`--backend vmware|virtualbox` で選べます）。設定を何も変えずに使えるので、WSL2 や Docker もそのまま使えます。

VMware を使わない場合は、次の方法があります。

1. **ハイパーバイザーをオフにする:** `terminal-windows host-mode` で、なぜオンになっているかを確認できます。管理者のターミナルで `terminal-windows host-mode fast --yes` を実行して再起動するとオフになります（元に戻すときは `host-mode secure --yes`）。オフの間は WSL2、Docker、Windows サンドボックスが使えず、ホストのセキュリティも下がるので、ご自身で判断してください。
2. **VM の省電力スロットリングは自動で外します:** Windows 11 は、窓を出していない処理の CPU 速度を省電力のために絞り、VM が固まる原因になります。terminal-windows は、VM のプロセスだけこの制限を外します（Windows の設定は変えません）。CPU は 4 コアが標準で、`--cpus N` で変えられます。
3. **VM の中を軽くする:** `terminal-windows tune default --yes` を実行します（視覚効果、検索インデックス、SysMain、Edge の常駐、ストアの自動更新、テレメトリのタスクをオフ。休止ファイルと予約済み記憶域をなくし、古い更新ファイルを消して TRIM。Windows Update は続けますが、勝手に再起動しません）。セットアップの案内では自動で実行します。
4. **使っていない VM を休ませる:** MCP サーバーが自分で起動した VM は、ツールが 15 分呼ばれないと状態を保存して止まり、PC のメモリを返します（VM の中で AI などが動いていて CPU を使っているときは止めません）。次の操作で自動的に再開します。時間は環境変数 `TERMINAL_WINDOWS_IDLE_MINUTES` で変えられます（`0` で無効）。自分で起動した VM は止めません。
5. **解像度を下げる:** `--width 1280 --height 800` のように指定します。

---

## 注意してほしいこと

- **時間のかかるコマンド（`up` / `create`）は、ご自身のターミナルで実行してください。** ほかのアプリの中から実行すると、そのアプリの終了と一緒に止まることがあります。
- **セットアップ中は PC をスリープさせないでください。** Windows のコピー中に VM が止まると、作り直しになります。作成コマンドだけが止まった場合は、`terminal-windows start NAME` で続きを引き継げます。
- **PowerShell でフルパスを使って実行するときは、先頭に `&` が必要です**（例: `& "C:\…\terminal-windows.exe" doctor`）。
- 評価版の ISO は約 90 日で期限が切れます。

---

## セキュリティと隔離

- **ホストとは切り離されています。** ホストのフォルダ、クリップボード、ドラッグ＆ドロップ、オーディオ、USB は VM に接続しません。共有フォルダは `--share PATH` を指定したときだけ使え、既定は読み取り専用です。
- **VM からはインターネットに出られます。** NAT 経由です。ホストの localhost のサービスには届きませんが、LAN には届く可能性があります。
- **`--offline` は隔離の保証ではありません。** VM の中のファイアウォールで遮断するだけです。VM の中の AI は管理者権限を持っているので、自分で解除できます。
- **VM の中の設定（UAC の確認なし昇格、自動ログオン、ロック画面なし）は、使い捨て VM だから許容しているものです。** ホストには絶対に適用しないでください。ゲスト用ヘルパーは、VirtualBox / VMware の VM の中でしか起動しません（誤実行を防ぐためのもので、セキュリティの保証ではありません）。
- **通信:** ホストの `127.0.0.1` だけで待ち受け、ランダムなトークンで認証します。トークンと VM のパスワードは、DPAPI で暗号化して保存します。
- **使い道の制限:** 堅牢なサンドボックスではないため、マルウェアの解析には使わないでください。CAPTCHA、ボット検知、アンチチート、各種サービスの規約を回避する機能はありません。

---

## そのほか

### データの保存場所

`%USERPROFILE%\.terminal-windows\machines\NAME\`（`TERMINAL_WINDOWS_HOME` で変更できます）に、`machine.json`、VM のディスク、`outbox\` / `inbox\`、`notes.md`、`logs\` が入ります。

### Claude Code のプラグイン

ほかの AI ツールは `connect` で登録します。Claude Code を使う場合は、このリポジトリをプラグインとして入れることもできます。先に `terminal-windows.exe` を 1 回開いて、インストールを済ませておいてください。

```
/plugin marketplace add Nishi-Yura/terminal-windows
/plugin install terminal-windows@terminal-windows
```

---

## 応援する

terminal-windows が役に立ったら、応援してもらえるとうれしいです。開発の励みになります。

[![Sponsor](https://img.shields.io/static/v1?label=Sponsor&message=%E2%9D%A4&logo=GitHub&color=%23fe8e86)](https://github.com/sponsors/Nishi-Yura)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-ffdd00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/nishiyura)

## ライセンス

[MIT License](LICENSE) © 2026 Nishi-Yura
