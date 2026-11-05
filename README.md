# terminal-windows

[![CI](https://github.com/Nishi-Yura/terminal-windows/actions/workflows/ci.yml/badge.svg)](https://github.com/Nishi-Yura/terminal-windows/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Sponsor](https://img.shields.io/static/v1?label=Sponsor&message=%E2%9D%A4&logo=GitHub&color=%23fe8e86)](https://github.com/sponsors/Nishi-Yura)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-ffdd00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/nishiyura)

**AI エージェント専用の「もう 1 台の Windows PC」を、あなたの PC の中に。**

> *An AI-only Windows 11 VM you can watch in your terminal and let AI agents drive — your own mouse, keyboard and clipboard are never touched. Works on Windows 11 Home (VirtualBox).*

terminal-windows は、VirtualBox で **AI 専用の Windows 11** を自動で用意するツールです。

- 🤖 **AI はその VM の中だけで作業します。** ホスト PC のマウス・キーボード・クリップボードには一切触れないので、AI が作業している間もあなたは PC を普段どおり使えます。
- 🖥️ **VM の画面はターミナルの中に表示されます。** 見るだけでなく、クリックや入力もできます。
- 🧩 **AI のつなぎ方は 2 通りあります。** ホストの Claude Code などから MCP で操作する方法と、AI そのものを VM の中で動かす方法です。
- 🏠 **Windows 11 Home でも動きます。** Hyper-V は使わず、無料の VirtualBox を使います。

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
- [開発者向け](#開発者向け)
- [応援する](#応援する)

---

## 特徴

| | |
|---|---|
| **完全無人セットアップ** | ISO を渡すだけ。Windows のインストール、アカウント作成、自動ログオン、ヘルパーの導入、スナップショット作成まで自動です。 |
| **ターミナルで見て触れる** | Windows Terminal（Sixel）や WezTerm（kitty）に VM の画面を表示します。Win キーや Alt+Tab は **Ctrl+G** のパレットから送れます。 |
| **AI 向けの操作がそろっている** | スクリーンショット、クリック、ドラッグ、日本語入力、キー操作に加えて、**UI Automation のツリー**でボタンの位置を正確に取れます。 |
| **失敗しても戻せる** | スナップショットと巻き戻しができます。巻き戻す前の状態も自動で保存します。 |
| **作業を引き継げる** | 停止は「状態保存」が既定です。アプリを開いたまま止めて、続きから再開できます。作業ノートはホスト側に残ります。 |
| **ファイルは安全に受け渡し** | 共有フォルダは使わず、Inbox / Outbox を通してコピーします（SHA-256 で照合し、上書きはしません）。 |
| **VM の中で AI を動かせる** | `terminal-windows claude` で、VM の中の Claude Code をホストのターミナルから使えます。 |


---

## 今の状態

**0.1.0（開発中）**

- 自動テストは **135 件すべて合格**しています（GitHub Actions で毎回実行）。
- 実際の VirtualBox 7.2 で、次の段階まで確認済みです。
  - VM の作成、起動、スナップショット、クローン
  - Windows セットアップの起動、エディション選択、ファイルのコピー
- 次の項目は、まだ実機で確認中です。
  - 初回ログオン以降の流れ
  - `selftest`
  - VM の中の AI

詳しくは [docs/VALIDATION.md](docs/VALIDATION.md) にまとめています。ビルド済みの配布物（Release）はまだありません。今はソースからビルドしてください。

---

## 必要なもの

| 項目 | 要件 |
|---|---|
| ホスト OS | Windows 11 Home 以上 |
| 仮想化ソフト | [Oracle VirtualBox](https://www.virtualbox.org/) 7.1 以上。本体だけで動きます（Extension Pack は不要） |
| スペックの目安 | メモリ 16GB 以上、空きディスク 64GB 以上、BIOS で VT-x / AMD-V を有効にしておく |
| Windows 11 の ISO | [Microsoft 公式ページ](https://www.microsoft.com/ja-jp/software-download/windows11)から入手します（約 8.3GiB）。**ゲストの Windows のライセンスは利用者の責任です。** |
| ターミナル | Windows Terminal 1.22 以上か WezTerm。どちらもない場合は VirtualBox のウィンドウで表示します |
| ビルドする場合 | .NET SDK 8 以上 |

---

## クイックスタート

**1. ビルドしてインストールする**（管理者権限は不要です）

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

```bash
powershell -ExecutionPolicy Bypass -File install.ps1 -Skill
```

`%USERPROFILE%\.terminal-windows\bin` に入り、PATH に追加されます。新しいターミナルを開いてから続けてください。

**2. 環境を確認する**

```bash
terminal-windows doctor
```

**3. VM を作る**（1〜2 時間ほどかかります。**ご自身で開いたターミナルで**実行し、その間は PC をスリープさせないでください）

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

ビューアの中では **Ctrl+G** で Windows のショートカット一覧を開けます。**Ctrl+]** でビューアを閉じます（VM は動き続けます）。

> VM がなくても、ビューアの見え方は `terminal-windows view-demo` で試せます。

---

## AI のつなぎ方

### A. ホストの AI から操作する（MCP）

```bash
claude mcp add --scope user terminal-windows -- terminal-windows mcp default
```

Claude Code などの MCP クライアントから、次のツールで VM を操作できます。ほかのクライアント向けの設定は `terminal-windows mcp --config` で出力できます。

| ツール | 内容 |
|---|---|
| `screenshot` | 画面を取得する |
| `click` / `drag` / `scroll` | マウス操作 |
| `type` / `key` | 文字入力（日本語も可）、キー操作（`ctrl+s`、`win+r` など） |
| `ui_tree` | UI Automation の要素ツリー（ボタンの名前と位置） |
| `wait` | 一定時間、または画面が変わるまで待つ |
| `status` | VM の状態、ロック画面かどうか |
| `snapshot` / `restore` | スナップショット、巻き戻し |
| `export_file` / `list_inbox` | ファイルの受け渡し |
| `read_notes` / `append_notes` | 作業ノート |

### B. AI そのものを VM の中で動かす

```bash
terminal-windows agent-install default --yes
```

```bash
terminal-windows claude default
```

`agent-install` は、VM の中に Git、Claude Code、画面操作用の MCP、スキルを入れます。`claude` を実行すると、VM の中の Claude Code がホストのターミナルに表示されます（**Ctrl+]** で切り離し）。

- **サインインはご本人が行ってください。** 表示された URL をホストのブラウザで開き、出てきたコードを貼り付けます。
- サインイン情報は VM の中に保存され、VM の中の AI からも読める状態になります。
- VM からインターネットに出る必要があります（`--offline` の VM では使えません）。

**Claude 以外の AI も使えます。** `--agent` で入れる AI を選べます。

| AI | インストール | 起動 | 説明書の置き場所 |
|---|---|---|---|
| Claude Code（既定） | `agent-install NAME --yes` | `terminal-windows claude NAME` | `~\.claude\skills\terminal-windows-desktop\SKILL.md` |
| OpenAI Codex CLI | `agent-install NAME --agent codex --yes` | `terminal-windows codex NAME` | `~\.codex\AGENTS.md` |
| Google Gemini CLI | `agent-install NAME --agent gemini --yes` | `terminal-windows gemini NAME` | `~\.gemini\GEMINI.md` |

- どの AI にも、画面操作用の MCP サーバー `desktop` を登録します。
- Codex と Gemini は Node.js（npm）で入れます。
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
| `show` / `hide` NAME | VirtualBox のウィンドウを出す / 消す |
| `status NAME` / `ls` | 状態 / 一覧 |
| `snapshot NAME --name LABEL` | スナップショットを作る |
| `restore NAME LABEL` | 巻き戻す（直前の状態は `pre-restore-*` として自動保存） |
| `clone NAME NEW` | セットアップ済みの状態から複製する |
| `push` / `pull` NAME | ファイルを送る / 受け取る |
| `notes NAME` | 作業ノートを見る |
| `claude` / `term` / `exec` NAME | VM の中で Claude Code / 端末 / PowerShell を動かす |
| `tune NAME --yes` | VM の中を軽くする |
| `selftest NAME` | 実機での動作を確認する |
| `doctor` | 環境を診断する |

全部の一覧は `terminal-windows --help` で見られます。VM を消すコマンドは、誤って消すのを防ぐため用意していません。

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

WSL2、Docker Desktop、メモリ整合性などが有効な PC では、VirtualBox が Windows のハイパーバイザーを経由して動くため、**かなり遅くなります**（`doctor` が警告します）。

1. **ハイパーバイザーをオフにする（効果：大）**
   - 管理者のターミナルで `bcdedit /set hypervisorlaunchtype off` を実行し、再起動します。元に戻すときは `auto` を指定します。
   - オフの間は WSL2 と Docker が使えません。メモリ整合性もオフにする必要があり、ホストのセキュリティが下がります。ご自身で判断してください。
2. **VM の CPU を増やす:** 作成時に `--cpus 6` のように指定します。
3. **VM の中を軽くする:** `terminal-windows tune default --yes` を実行します（視覚効果、検索インデックス、SysMain などをオフ）。
4. **解像度を下げる:** `--width 1280 --height 800` のように指定します。

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
- **VM の中の設定（UAC の確認なし昇格、自動ログオン、ロック画面なし）は、使い捨て VM だから許容しているものです。** ホストには絶対に適用しないでください。ゲスト用ヘルパーは、VirtualBox 以外の環境では起動しません（誤実行を防ぐためのもので、セキュリティの保証ではありません）。
- **通信:** ホストの `127.0.0.1` だけで待ち受け、ランダムなトークンで認証します。トークンと VM のパスワードは、DPAPI で暗号化して保存します。
- **使い道の制限:** 堅牢なサンドボックスではないため、マルウェアの解析には使わないでください。CAPTCHA、ボット検知、アンチチート、各種サービスの規約を回避する機能はありません。

---

## 開発者向け

<details>
<summary>構成・データの保存場所・テスト</summary>

### 構成

```
src/TerminalWindows.Protocol   共通（通信のフレーム、RPC、トークン認証、キー解析、パス検証、PNG/LZ4、MCP の共通処理）
src/TerminalWindows.Cli        ホスト CLI（VirtualBox の操作、無人セットアップ、ビューア、MCP サーバー、VM の中の AI）
src/TerminalWindows.Guest      ゲスト用ヘルパー（画面取得 WGC→DXGI→GDI、SendInput、UI Automation、ConPTY、インストーラー、自己更新）
tests/TerminalWindows.Tests    自動テスト
skills/                        Claude Code 用スキル（ホスト用 / VM の中用）
docs/VALIDATION.md             検証記録
terminal-windows-SPEC.md       設計の元になった仕様書
```

### データの保存場所

`%USERPROFILE%\.terminal-windows\machines\NAME\`（`TERMINAL_WINDOWS_HOME` で変更できます）に、`machine.json`、VM のディスク、`outbox\` / `inbox\`、`notes.md`、`logs\` が入ります。

`%LOCALAPPDATA%` は使いません。Claude デスクトップアプリなどパッケージ化されたアプリから起動されると、Windows が AppData への書き込みを別の場所に振り替えてしまうためです。

### テスト

```bash
dotnet test tests/TerminalWindows.Tests
```

`TW_VBOX_TESTS=1` を付けると、本物の VirtualBox を使ったテストも実行します。`TW_TEST_ISO=<path>` を付けると、本物の ISO を読むテストも実行します。

### プラグイン

このリポジトリは、Claude Code のプラグインとしても読み込めます（`.claude-plugin/`、`.mcp.json`、`skills/`）。

</details>

---

## 応援する

terminal-windows が役に立ったら、応援してもらえるとうれしいです。開発の励みになります。

[![Sponsor](https://img.shields.io/static/v1?label=Sponsor&message=%E2%9D%A4&logo=GitHub&color=%23fe8e86)](https://github.com/sponsors/Nishi-Yura)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-ffdd00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/nishiyura)

## ライセンス

[MIT License](LICENSE) © 2026 Nishi-Yura
