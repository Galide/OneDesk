# OneDesk

用一套键盘鼠标同时操作 **Windows PC** 和 **Apple Silicon Mac**（或另一台 Windows PC），并在两台机器共用的显示器上自动切换输入源。

有键盘鼠标的那台机器作为 Host，另一台作为 Client（接收按键、鼠标和剪贴板）。按一次热键即可切换控制权；显示器通过 DDC/CI 自动切换，不需要额外的 KVM 硬件。

| 组合 | Host 安装 | Client 安装 |
| --- | --- | --- |
| Windows ↔ Mac | `OneDesk-Setup-*.exe` | `OneDesk-*.pkg` / zip |
| Windows ↔ Windows | `OneDesk-Setup-*.exe` | 同一个 `OneDesk-Setup-*.exe`，在设置里把角色改为“客户端” |

```
        ┌──────────────────────────────┐
        │           显示器              │
        │   DP 1  ←──── Windows         │
        │   HDMI 1 ←──── Mac            │
        └──────────────────────────────┘
                 ▲            ▲
                 │ DDC/CI     │ DDC/CI (m1ddc)
        ┌────────┴──────┐  ┌──┴─────────────┐
        │ Windows Host  │  │ macOS Client   │
        │ 键盘 / 鼠标    │  │ 注入键鼠事件    │
        │ 全局钩子       │  │ 辅助功能权限    │
        └───────┬───────┘  └───────┬────────┘
                └──── 局域网 TCP ────┘
                   AES-256-GCM
```

## 功能

- **一键切换**：默认 `Ctrl + Alt + Space`，可自定义。
- **键鼠转发**：Windows 捕获并屏蔽输入，Mac 通过 CoreGraphics、第二台 Windows 通过 `SendInput` 注入；支持组合键、鼠标按键、滚轮和横向滚轮。
- **剪贴板**：切换时双向同步纯文本；也可从托盘菜单手动发送。
- **显示器输入切换**：
  - Windows → Mac：Windows 侧通过 DDC/CI 切换到 Mac 所在的接口。
  - Mac → Windows：Mac 侧通过 `m1ddc` 切回 Windows 所在的接口。
- **断线回退**：连接中断时 Mac 自动释放所有按下的键并尝试切回 Windows；Windows 恢复本地鼠标位置。
- **加密传输**：AES-256-GCM，PBKDF2-SHA256（120,000 次迭代），每次连接使用随机盐和递增序号。
- **托盘常驻**：Windows 端无主窗口，配置、日志、开机启动都在托盘菜单里。
- **本地配置页**：Mac 端只监听 `127.0.0.1:47822`，用浏览器配置，不向局域网暴露管理接口。

## 环境要求

| 项目 | 要求 |
| --- | --- |
| Windows（Host 或 Windows 客户端） | Windows 10 1809 或更高版本，x64 |
| macOS | macOS 12 或更高版本，**Apple Silicon（arm64）** |
| 网络 | 两台机器在同一局域网；建议使用有线网络或稳定的 5 GHz Wi-Fi |
| 显示器 | 支持 DDC/CI，并在 OSD 菜单中启用 DDC/CI |
| .NET | Windows 安装包和 macOS 包均已自带运行时，无需单独安装 |

> macOS 包必须在 macOS 上构建（或使用仓库中的 GitHub Actions）。Windows 无法生成有效的 `.pkg`。本仓库的 `dist/` 中可包含已构建好的 Windows 安装包；macOS 包请用 CI 或 Mac 构建，见[构建](#构建)。

## 接线

1. **Mac 用雷电 / USB-C 转 DisplayPort 连接显示器，Windows 用 HDMI**（也可以反过来，接口值稍后配置即可）。
2. 键盘、鼠标接收器/线缆接在 **Windows** 上。
3. 两台机器接入同一局域网。
4. 在显示器 OSD 中打开 **DDC/CI**。部分显示器还需要把 DisplayPort 版本设为 1.2/1.4，或关闭“深度睡眠”。

为什么推荐这个方向：Apple Silicon 在 USB-C / DisplayPort Alt Mode 上的 DDC/CI 最可靠，而 M1 及入门级 M2 的**原生 HDMI** 口发不出 DDC 命令。把 Mac 放在 USB-C / DP 侧可以避开这个坑，详见 [m1ddc](#m1ddc)。

默认假设：

| 机器 | 接口 | VCP `0x60` 值 |
| --- | --- | --- |
| Mac | DisplayPort 1（雷电 / USB-C 转 DP） | `15` |
| Windows | HDMI 1 | `17` |

实际值以显示器为准。Windows 设置窗口里的“读取当前输入”可以验证；不同厂商的编号可能不同。

两端的值必须**互相对应**：

| | Windows 设置里的值 | Mac 配置页里的值 |
| --- | --- | --- |
| 本机接口 | `MyInput` = HDMI 口（如 `17`） | `MyInput` = DP 口（如 `15`） |
| 对端接口 | `PeerInput` = DP 口（如 `15`） | `PeerInput` = HDMI 口（如 `17`） |

也就是说，Windows 的 `PeerInput` 就是 Mac 的 `MyInput`，反之亦然。

## 安装 Windows

Windows 端只有**一个程序**：`OneDesk.exe`。它既可以当主机（接键盘鼠标），也可以当客户端（接收键鼠），在设置窗口顶部的“使用方式”里切换，无需分别安装两个版本。

运行：

```
dist\OneDesk-Setup-1.0.0.exe
```

安装程序会：

- 安装到 `C:\Program Files\OneDesk`；
- 添加入站防火墙规则（仅 **专用/域** 网络），允许对端连接 TCP `47821`（当客户端时这条规则不会被用到）；
- 可选创建桌面快捷方式；
- 安装完成后启动 OneDesk（托盘图标）。

首次启动会自动打开设置窗口。也可以右键托盘图标 → **设置…**。

需要填写/确认：

1. **使用方式**：有键盘鼠标的机器选“主机”，受控的机器选“客户端”。切换后保存，程序会立即停掉旧角色并启动新角色。
2. **配对密码**：默认随机生成 16 位。点击“复制”，稍后粘贴到另一端。密码中的空格和 `-` 会被忽略，大小写不敏感。
3. **端口**：默认 `47821`，两端必须一致。
4. **主机地址**（仅客户端）：主机的局域网 IPv4，主机设置页的“本机地址”里可以复制。
5. **显示器**：从下拉框选择支持 DDC/CI 的物理显示器。
6. **本机接口 / 对端接口**：选择或输入 VCP 值（见[接线](#接线)）。
7. **切换热键**（仅主机）：点击输入框后按下组合键；至少需要一个修饰键。
8. **选项**：
   - 切换时同步剪贴板；
   - 把 `Ctrl` 映射为 `Command`、`Win` 映射为 `Control`（仅主机 + Mac）；
   - 启动后自动连接（仅客户端）；
   - 登录 Windows 时自动启动。

点击“保存”。托盘菜单中的“开机启动”也可以随时切换。托盘右键的“角色”子菜单也能直接切换主机/客户端。

> 如果 Windows 网络被识别为“公用网络”，防火墙规则不会放行。请把该网络改为“专用”，或手动在 Windows 防火墙中允许 `OneDesk.exe`。

### 命令行诊断

```powershell
OneDesk.exe --list-monitors
OneDesk.exe --get-input
OneDesk.exe --set-input 17
```

- `--list-monitors` 列出物理显示器和当前输入值。
- `--get-input` 读取当前 VCP `0x60` 值（需要显示器支持读取）。
- `--set-input <值>` 直接切换输入源。

日志：`%APPDATA%\OneDesk\logs\onedesk.log`

## 安装 macOS（Client）

### 方式 A：pkg 安装包（推荐）

1. 在 Mac 上双击 `OneDesk-1.0.0.pkg`。
2. 如果提示“无法打开，因为 Apple 无法检查其是否包含恶意软件”，打开 **系统设置 → 隐私与安全性**，在底部点击 **仍要打开**；或者右键 pkg → **打开**。
3. 安装完成后，安装脚本会为当前登录用户安装 LaunchAgent 并启动后台客户端，然后打开配置页 `http://127.0.0.1:47822/`。
4. 首次运行会请求 **辅助功能** 权限。到 **系统设置 → 隐私与安全性 → 辅助功能**，勾选 **OneDesk**。如果没有出现，点击配置页里的“打开辅助功能设置”，或手动添加 `/Applications/OneDesk.app`。
5. 在配置页填写 Windows 的局域网 IP、端口、配对密码，以及两端的显示器接口值，点击“保存并应用”。

### 方式 B：zip 手动安装

1. 解压后把 `OneDesk.app` 拖到 **应用程序** 文件夹。
2. 右键 `OneDesk.app` → **打开**，以绕过 Gatekeeper。
3. 在终端安装登录代理：

   ```bash
   /Applications/OneDesk.app/Contents/MacOS/OneDesk --install-agent
   ```

4. 打开 `http://127.0.0.1:47822/` 完成配置。
5. 授予辅助功能权限。

> 不要把 app 留在“下载”文件夹里直接安装代理。Gatekeeper 的临时路径（App Translocation）会导致登录代理指向错误的位置。

### 终端向导（可选）

```bash
/Applications/OneDesk.app/Contents/MacOS/OneDesk --setup
```

向导会询问 Windows 地址、端口、密码、两端接口、是否同步剪贴板、是否交换 Ctrl/Cmd，并可安装登录代理。

### 其他命令

```bash
/Applications/OneDesk.app/Contents/MacOS/OneDesk --status
/Applications/OneDesk.app/Contents/MacOS/OneDesk --list-displays
/Applications/OneDesk.app/Contents/MacOS/OneDesk --test-ddc 17
/Applications/OneDesk.app/Contents/MacOS/OneDesk --uninstall-agent
```

配置：`~/Library/Application Support/OneDesk/config.json`

日志：`~/Library/Logs/OneDesk/client.log`

### m1ddc

Mac 端切回 Windows 依赖 [`m1ddc`](https://github.com/waydabber/m1ddc)。构建脚本会尝试把它放进 `OneDesk.app/Contents/Resources/m1ddc`；也可以手动安装：

```bash
brew install m1ddc
```

注意：

- `m1ddc` 只支持 Apple Silicon，不支持 Intel Mac。
- M1 及入门级 M2 的**原生 HDMI** 口发不出 DDC 命令，无法切换输入；请把 Mac 接在 USB-C / DisplayPort Alt Mode 上。M2 及更新机型的 HDMI 口通常可用，但仍以实测为准（取决于机型、线缆和转换器）。
- 用 USB-C 转 HDMI 转接头时，很多转接芯片不透传 DDC，需要带 VMM7100 之类支持 DDC 中继的芯片。
- `m1ddc` 只有 `set input`，**没有读取当前输入的命令**。因此 Mac 侧的接口值只能看显示器 OSD 里显示的活动输入名称，或用 `--test-ddc <值>` 逐个试——注意试错会让显示器切到无信号输入，需要用显示器上的按键切回来。
- 部分 LG 显示器需要启用“备用输入寻址”（配置文件中的 `MacAlternateInput`，目前可在配置 JSON 中设置）。

## 两台 Windows 互相控制

Windows 端的主机和客户端是**同一个安装包、同一个 `OneDesk.exe`**，区别只在设置里选的“使用方式”。有键盘鼠标的那台选“主机”，第二台选“客户端”。

1. 两台 Windows 都安装 `dist\OneDesk-Setup-1.0.0.exe`。
2. 在第二台（客户端）上打开设置，把**使用方式**切到“客户端”。
3. 填写主机信息：
   - **主机地址**：主机的局域网 IPv4（主机设置页的“本机地址”里点“复制”）；
   - **端口**：与主机一致，默认 `47821`；
   - **配对密码**：粘贴主机上“复制”得到的密码。
4. 显示器接口按实际接线填写，两台的 `MyInput` / `PeerInput` 必须**互换**：

   | | 主机（Windows A） | 客户端（Windows B） |
   | --- | --- | --- |
   | 本机接口 | 自己接的口，如 HDMI 1 = `17` | 自己接的口，如 DP 1 = `15` |
   | 对端接口 | 客户端接的口，如 `15` | 主机接的口，如 `17` |

5. 勾选“启动后自动连接”，点击“保存”。
6. 在主机上按热键切换；再按一次切回。主机托盘右键的“切换控制”也可以。

客户端与 macOS 客户端的工作方式相同：接管期间重放键鼠事件，释放时把显示器切回自己的接口。它只需要**出站**连接，因此不添加入站防火墙规则（安装包仍会添加一条，留着也无害）。

注意事项：

- **管理员权限（UIPI）**：以普通权限运行的客户端无法向以管理员身份运行的前台窗口（任务管理器、UAC 提示等）注入输入。需要在那些窗口里操作时，用 `OneDesk.exe` 的快捷方式 → **以管理员身份运行**。安装包本身不要求常驻管理员权限，开机启动也不会弹 UAC。
- 设置里选“客户端”的机器不要再接同一套键盘鼠标，否则会互相抢输入。
- 客户端的 `Ctrl ↔ Command` 交换、滚动反转不生效——这两个选项只对 macOS 有意义，客户端模式下也不会显示。
- 鼠标使用绝对坐标，只映射到客户端的**主显示器**。客户端接多显示器时，指针只会落在主屏。
- 显示器输入切换同样要求客户端的显示器接口支持 DDC/CI 写入；Windows 端没有 m1ddc 那类 HDMI 限制，但部分显示器的 HDMI 口仍然拒绝 DDC 写入。
- 从旧版本升级：如果之前装过单独的 `OneDeskClient-Setup-*.exe`，先在“应用”里卸载它，再安装统一的 `OneDesk-Setup-*.exe`。

## 配对与首次使用

1. Windows 托盘菜单 → **设置…**，复制配对密码，确认端口（默认 `47821`）。
2. Windows 设置窗口的“本机地址”一行会列出局域网 IPv4，点“复制”即可。
3. Mac 配置页填入同一个 IP、端口和密码，保存。
4. 配置页应显示“已连接”。如果没有，见[故障排查](#故障排查)。
5. 在 Windows 上按 `Ctrl + Alt + Space`：
   - Windows 向 Mac 发送接管请求；
   - 显示器切换到 Mac 的接口；
   - Mac 开始接收键鼠事件。
6. 再按一次热键，或从托盘菜单选择“切回 Windows”。

首次切换可能因显示器 DDC 响应慢而多等 0.5–1 秒。设置中的 `SettleMs` 默认 800 ms。

## 显示器 VCP 值

VCP 代码 `0x60`（96）表示输入源。常见值：

| 输入 | 值 |
| --- | --- |
| DisplayPort 1 | 15 |
| DisplayPort 2 | 16 |
| HDMI 1 | 17 |
| HDMI 2 | 18 |
| USB-C / DP Alt Mode | 27 |
| DVI | 3 |
| VGA | 1 |

不同显示器可能使用不同编号。以 Windows“读取当前输入”的结果为准；Mac 端的值无法自动读取（`m1ddc` 不支持读取输入源），需要从显示器 OSD 判断，或用 `--test-ddc <值>` 逐个尝试——试错时屏幕会切到无信号输入，需要用显示器按键切回。

## 切换流程与已知限制

- Windows 只能在**自己处于活动输入**时可靠地发送 DDC 命令，所以“切到 Mac”由 Windows 发起，“切回 Windows”由 Mac 发起。
- 如果 Mac 端崩溃或 m1ddc 不可用，显示器可能停在 Mac 输入。此时用显示器按键手动切回，或重启 Mac 端后重试。
- 只转发**键盘、鼠标和纯文本剪贴板**；不转发音频、U 盘、摄像头等 USB 设备。
- 不支持 Windows 安全桌面（`Ctrl+Alt+Del`、UAC 提示、锁屏）上的输入；这些界面由 Windows 自己处理。Windows 客户端还受 UIPI 限制，普通权限下无法向管理员权限的窗口注入输入。
- 同一时间只允许一个 Mac 客户端连接。
- 建议在可信局域网使用。协议是加密的，但没有做第三方安全审计；不要在公网直接暴露 `47821` 端口。
- 剪贴板为纯文本，按 UTF-8 限制在约 1.5 MB；图片/文件不会同步。
- 鼠标使用绝对坐标映射。Windows 与 Mac 的指针加速度不同，手感可能有差异。
- Mac 睡眠或锁屏后，连接会断开；唤醒后会自动重连。

## 故障排查

### Mac 显示“等待连接”/“无法连接”

1. 确认两端密码完全一致（空格和 `-` 会被忽略）。
2. 确认 Windows 的 IPv4 地址填写正确，端口一致。
3. 在 Windows 上确认网络类型是“专用”，并检查防火墙规则 `OneDesk` 是否存在：
   ```powershell
   netsh advfirewall firewall show rule name=OneDesk
   ```
4. 在 Mac 上测试端口：
   ```bash
   nc -vz <Windows-IP> 47821
   ```
5. 查看两端日志。Windows 日志在 `%APPDATA%\OneDesk\logs\onedesk.log`，Mac 日志在 `~/Library/Logs/OneDesk/client.log`。

### 键鼠没有反应

- Mac 配置页应显示“辅助功能：已授权”。
- 如果之前安装过旧版本，系统设置里可能残留多个 OneDesk 条目。删除旧条目，重新勾选当前的 `/Applications/OneDesk.app`。
- 某些安全软件会拦截全局钩子或模拟输入，需要把 OneDesk 加入白名单。
- 如果只有部分按键无效，查看 Mac 日志中的“不支持的按键 VK=...”。目前使用 US ANSI 键位映射，非 US 布局的特殊键可能缺失。

### DDC 读取/切换失败

- 确认显示器 OSD 中启用了 DDC/CI。
- 换用 DisplayPort 或 USB-C；部分显示器的 HDMI 口不支持 DDC 写入。
- **Mac 接在 HDMI 口时切回失败（尤其是 M1 / 入门级 M2）**：这些 HDMI 口不发 DDC 命令。把 Mac 改接到 USB-C / DisplayPort Alt Mode，让 Windows 用 HDMI。
- `--get-input` 失败但 `--set-input` 成功是正常的，有些显示器只支持写、不支持读。
- 如果 Windows 端切换失败，OneDesk 会自动退出远程模式，避免出现“显示器切走了但键鼠没过去”的状态。
- Mac 端 `m1ddc` 不存在时，配置页会显示 `m1ddc: false`。安装后重启 OneDesk 代理。

### macOS 提示“已损坏”或无法启动

未签名/未公证的包会触发 Gatekeeper：

```bash
xattr -dr com.apple.quarantine /Applications/OneDesk.app
open /Applications/OneDesk.app
```

如果仍然被阻止，到 **系统设置 → 隐私与安全性** 点击“仍要打开”。企业环境可能禁止运行未公证软件，需要使用自己的 Developer ID 重新签名并公证。

### 热键无效

- 热键至少需要一个修饰键（Ctrl / Alt / Shift / Win）。
- 某些游戏、远程桌面或键盘驱动会独占热键；换一个组合键试试。
- 如果热键与 Windows 系统快捷键冲突，Windows 可能优先处理。避免使用 `Win+L`、`Ctrl+Alt+Del` 等系统保留组合。

## 卸载

### Windows

设置 → 应用 → **OneDesk** → 卸载。安装程序会删除防火墙规则和开机启动项。用户配置和日志保留在 `%APPDATA%\OneDesk`，需要时手动删除。

### macOS

```bash
# 在仓库中运行
./installer/macos/uninstall.sh          # 保留配置和日志
./installer/macos/uninstall.sh --purge  # 一并删除
```

或手动：

```bash
launchctl bootout gui/$(id -u)/com.onedesk.client
rm -f ~/Library/LaunchAgents/com.onedesk.client.plist
sudo rm -rf /Applications/OneDesk.app
```

最后到 **系统设置 → 隐私与安全性 → 辅助功能** 删除残留的 OneDesk 条目。

## 构建

### Windows

需要 .NET SDK 8 和 [Inno Setup 6](https://jrsoftware.org/isdl.php)：

```powershell
dotnet build OneDesk.sln -c Release
dotnet run --project tools/OneDesk.SelfTest -c Release
powershell -ExecutionPolicy Bypass -File installer\windows\build.ps1
```

产物（自包含，约 58 MB）：

- `dist\OneDesk-Setup-1.0.0.exe`（主机 + 客户端同一个程序）

### macOS

需要 macOS、.NET SDK 8，以及可选的 `m1ddc`：

```bash
brew install m1ddc
chmod +x installer/macos/build.sh installer/macos/uninstall.sh installer/macos/scripts/*
./installer/macos/build.sh
```

产物：

- `dist/OneDesk-1.0.0.pkg`
- `dist/OneDesk-1.0.0-macOS-arm64.zip`

默认使用 ad-hoc 签名（`codesign -s -`），可以直接安装，但首次运行需要 Gatekeeper 确认。若要正式分发，设置环境变量：

```bash
export CODESIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
export PKG_SIGN_IDENTITY="Developer ID Installer: Your Name (TEAMID)"
./installer/macos/build.sh
```

公证（notarization）需要额外的 `notarytool` 步骤，本仓库未包含。

### GitHub Actions

`.github/workflows/build.yml` 会在以下环境构建安装包：

- `windows-latest`：构建、运行自测、用 Inno Setup 打包 Windows 安装程序；
- `macos-15`（Apple Silicon）：构建、运行自测、`brew install m1ddc`、生成 `.pkg` 和 `.zip`。

推送代码或手动触发 workflow 后，从 Actions 页面下载 `OneDesk-Windows-Setup` 和 `OneDesk-macOS-arm64` 两个 artifact。

### 测试

```powershell
dotnet run --project tools/OneDesk.SelfTest
```

覆盖配置规范化、键位映射、Windows 输入映射（绝对坐标、按键/滚轮标志位）、协议往返、加密握手、大剪贴板、错误密码拒绝等。macOS 上同样可以运行（不依赖 CoreGraphics，也不依赖 Windows 的 P/Invoke）。

## 项目结构

```
src/OneDesk.Core/           配置、日志、协议、加密、键位/坐标映射、本地 Web 服务
src/OneDesk.Platform.Win/   Windows 公共层：SendInput 注入、DDC/CI、剪贴板
src/OneDesk.Win/            Windows 托盘程序：主机（全局钩子）与客户端（连接/重放）二合一
src/OneDesk.Client.Mac/     macOS 守护进程、CoreGraphics 注入、m1ddc、LaunchAgent
tools/OneDesk.SelfTest/     无第三方依赖的自测
installer/windows/          Inno Setup 脚本与构建脚本
installer/macos/            Info.plist、pkg 脚本、构建/卸载脚本
.github/workflows/          Windows + macOS 自动构建
```

## 安全说明

- 传输：每条连接由客户端生成 16 字节随机盐，双方用 PBKDF2-SHA256（120,000 次）派生 256 位密钥；消息使用 AES-256-GCM 加密并带 8 字节序号，防止重放和篡改。
- 配对密码只用于派生密钥，不直接上线传输。密码中的空白和 `-` 会被移除，比较时不区分大小写。
- Windows 只接受一个客户端；握手超时 8 秒，心跳超时 8 秒。
- Mac 配置页只绑定 `127.0.0.1`，所有 API 需要页面内嵌的随机 token；没有跨域接口。
- 这仍然是一个局域网工具：拥有网络嗅探能力并不能直接解密流量，但弱密码可以被离线暴力破解。请使用安装程序生成的 16 位随机密码。

## 许可

本项目使用 MIT License，见 [LICENSE](LICENSE)。

macOS 包可选地捆绑 [`m1ddc`](https://github.com/waydabber/m1ddc)，其使用 MIT License，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
