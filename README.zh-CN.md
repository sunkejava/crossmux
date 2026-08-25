# CrossMux

[English](./README.md) | **简体中文**

**CrossMux** 是 [CrossPoint Reader](https://github.com/crosspoint-reader/crosspoint-reader) 的社区 fork：在原有电子书阅读体验之上，新增了一个 Apps 应用中心（小游戏 / 小工具）、更丰富的待机表盘，以及一套完整的简体中文固件。

**版本：** CrossMux 1.5.9（基于 CrossPoint Reader 1.5.0，并同步上游 `develop` 至 `eef20504`）

**运行设备：** 基于 ESP32-C3 的 Xteink [X4](https://www.xteink.com/products/xteink-x4) 与 [X3](https://www.xteink.com/products/xteink-x3)。

> 本文档面向中文用户，重点说明 CrossMux 的中文相关功能与简体中文固件的编译方式。完整的英文说明请见 [README.md](./README.md)。

![CrossMux 运行在 Xteink 设备上](./docs/images/cover.jpg)

---

## CrossMux 相比上游新增了什么

- **Apps 应用中心**（首页的 `Apps` 菜单）：2048、扫雷、数独、五子棋、中国象棋、电子木鱼（支持按键与触屏敲击，累计值永久保留并惰性写盘）、「Ugly Avatar」头像生成器，以及 **AirPage**——扫码上传自定义内容，再通过手动刷新或仅前台运行的 MQTT 实时推送获取并全屏显示 BMP 或 JPEG 图片。AirPage 每次均从二维码页进入，并在刷新或实时模式需要联网时才连接 Wi‑Fi；连接或重连本身不会下载。底部映射按键可进入设置、浏览最近 20 张投送图片或刷新，侧键同步对应图片/刷新操作。图片复用 EPUB 的等比居中与 4 级灰度链路，可手动设为自定义休眠画面，也可在 AirPage 设置中开启“每次投送后自动设置”。刷新失败保留原图，实时连接连续失败约两分钟后暂停并恢复系统自动休眠。应用超过一屏时以页点分页。
- **微信读书**：扫码登录、浏览个人书架、下载图书、以 EPUB 离线阅读并同步阅读进度。阅读器只保留一个「同步进度」入口：识别出的标准微信书籍优先同步微信读书，其他 EPUB 仍同步 KOReader。新缓存的微信书籍还会在下载正文前尽力预取云端进度，首次打开时可从云端位置继续阅读。
- **阅读分析**：阅读统计、按月阅读热力图、阅读档案与成就，数据以 JSON 存于 SD 卡。
- **待机表盘**：手绘风格的「潦草时钟」与中式老黄历表盘，并提供可选的 4 级灰度增强与反色显示模式。
- **简体中文固件**（`gh_release_cn`）：中文 UI + i18n、内嵌 CJK 字体、面向中文的 EPUB 排版（断词与禁则等）。详见下方 [编译简体中文固件](#编译简体中文固件)。
- **桌面模拟器**：可在电脑上开发与预览 UI。
- **文件同步（1.5.8）**：从自建 CrossMux 同步服务拉取电子书与图片，利用 SHA-256 跳过未变化文件，并以安全替换方式避免中断下载破坏已有文件。
- **智能待机页（1.5.9）**：订阅设备专属 MQTT 更新，把服务端渲染的天气、股票、歌词、图片和自定义数据看板实时显示为待机表盘。

> **微信读书安全提示**：微信读书使用可能随时变化的非公开 Web 协议。真机通过
> wolfSSL 加密传输，但调用 `setInsecure()`，不会验证服务器 CA 与主机身份，存在
> 中间人攻击风险；请只在可信网络中使用。原生模拟器仍通过 libcurl 的主机信任库
> 验证证书。

上游 CrossPoint 的全部能力（EPUB 2/3 渲染、多格式支持、无线传书、OPDS、OTA 等）在 CrossMux 中同样可用，详见 [English README](./README.md#what-can-crosspoint-do)。

---

## CrossMux 1.5.8：文件同步

CrossMux 1.5.8 新增从自建服务到阅读器的单向内容同步。服务端可通过网页或管理 API 上传电子书和图片；设备会把电子书下载到 SD 卡 `/Books`，把图片下载到 `/Images`。

支持上传的格式：

- 电子书：`.epub`、`.txt`、`.xtc`
- 图片：`.png`、`.bmp`、`.jpg`、`.jpeg`（能否显示取决于打开文件的具体功能；原生文件浏览器支持 PNG 与 BMP）

### 1. 启动同步服务

使用 Docker 最便捷。对外提供服务前，请修改 `sync-service/docker-compose.yml` 中的 `Sync__ApiKey`，并将 `Sync__PublicBaseUrl` 设置为阅读器能够访问的地址。

```bash
cd sync-service
docker compose up -d --build
```

浏览器打开 `http://服务器IP:8080/` 即可上传文件，也可以调用管理 API：

```bash
curl -X POST http://服务器IP:8080/api/admin/files \
  -H "X-Api-Key: 你的_API_KEY" \
  -F "file=@/path/to/book.epub"
```

不使用 Docker 时，可通过 .NET 直接运行：

```bash
cd sync-service
Sync__ApiKey=change-me \
Sync__PublicBaseUrl=http://192.168.1.10:8080 \
dotnet run --project CrossMux.SyncService/CrossMux.SyncService.csproj
```

### 2. 配置阅读器并同步

1. 将服务端与阅读器连接到同一个可信网络。真机必须填写电脑的局域网地址，不能使用 `localhost` 或 `127.0.0.1`。
2. 打开阅读器内置的 Web 设置页面，填写 **文件同步服务器地址（File Sync Server URL）**，例如 `http://192.168.1.10:8080`；不需要附加 API 路径。
3. 在阅读器上进入 **设置 > 系统 > 文件同步**，启动同步。
4. 保持设备唤醒，直至显示同步结果。首次运行会下载匹配文件；后续同步会根据 SHA-256 跳过内容未变化的文件。

下载过程使用临时文件与备份文件，因此传输中断不会覆盖有效的本地文件。服务端清单采用分页返回，以控制设备端内存占用。

### 安全提示与故障排查

- 上传与删除管理 API 必须提供 `X-Api-Key`；清单和下载接口为只读接口，按设计不要求管理密钥。建议仅在可信局域网使用；如需从互联网访问，请在服务前增加 HTTPS 与访问控制。
- 如果清单中的下载地址指向错误主机，请修正 `Sync__PublicBaseUrl` 并重启服务。
- 请确认阅读器能够访问 `http://服务器IP:8080/api/v1/sync/manifest?page=1&pageSize=10`，且 SD 卡有足够空间。
- 兼容 OPDS 的客户端可通过 `http://服务器IP:8080/opds` 浏览电子书。

完整配置说明见 [同步服务部署与 API 使用文档](./sync-service/README.md) 和 [设备同步协议](./docs/sync-service.md)。

---

## 编译简体中文固件

CrossMux 提供一个专用的简体中文构建环境 `gh_release_cn`，产出**仅中文**的固件：简体中文 UI + i18n、内嵌 CJK 点阵字体、面向中文的 EPUB 排版，以及中国象棋 / 微信读书等应用。全新设备首次开机即直接进入中文界面。

### 前置条件

- [pioarduino](https://github.com/pioarduino/pioarduino)，或 VS Code + pioarduino 插件
- Python 3.8+
- `clang-format` 21（仅提交代码时需要）
- 支持数据传输的 USB-C 数据线

### 获取源码

```bash
git clone --recursive https://github.com/0x1abin/crossmux.git
cd crossmux

# 如果克隆时漏掉了 --recursive：
git submodule update --init --recursive
```

### 构建与烧录

仓库已内置 CJK 字体头文件，常规构建无需任何额外的字体处理步骤：

```bash
# 仅构建中文固件
pio run -e gh_release_cn

# 构建并烧录到已连接的设备
pio run -e gh_release_cn -t upload
```

构建产物位于 `.pio/build/gh_release_cn/firmware.bin`，也可以通过网页烧录器上传（见下方 [烧录固件](#烧录固件)）。

### 重新生成 CJK 字体（可选）

只有在**修改字符集**或**更新内嵌字体**时，才需要重新生成 CJK 点阵字体头文件。完整的字体工具链、字符集策略与 Flash 空间预算说明见 [docs/engineering/chinese-build.md](./docs/engineering/chinese-build.md)。

---

## 中文阅读注意事项

简体中文固件在有限的 Flash 空间内做了取舍，使用时请注意：

- **内嵌字体覆盖现代汉语 3500 常用字**（《现代汉语常用字表》）。生僻字、古字、繁体字、部分人名地名用字可能在阅读器中显示为 □。
- **不支持繁体中文**：本构建为简体专用（`ZH_CN`），繁体字形不在任何字库中。
- **大号字下中文正文可能留白**：16pt / 18pt（阅读器的 LARGE / EXTRA_LARGE）仅内嵌 UI 所需的小字集，这两档字号是为英文 EPUB 调优的。读中文请切到 MEDIUM 档。
- **无 CJK 粗体 / 斜体字形**：粗体 / 斜体会回退为常规字重。若需要更多字重，可在 SD 卡上加载自定义字体。
- 需要扩充字符覆盖范围时，参见 [docs/engineering/chinese-build.md](./docs/engineering/chinese-build.md) 中的「Expanding character coverage」。

---

## 烧录固件

### 网页烧录器（推荐）

1. 用 USB-C 连接设备到电脑，并唤醒 / 解锁设备。
2. 打开 https://crosspointreader.com/#flash-tools ，选择设备型号（X3 或 X4），点击「Custom .bin」上传你构建出的 `firmware.bin`。

### 命令行

1. 安装 [`esptool`](https://github.com/espressif/esptool)：

   ```bash
   pip install esptool
   ```

2. 用 USB-C 连接设备，找到串口（Linux 连接后运行 `dmesg`；macOS 可用 `log stream --predicate 'subsystem == "com.apple.iokit"' --info`）。
3. 烧录：

   ```bash
   esptool.py --chip esp32c3 --port /dev/ttyACM0 --baud 921600 write_flash 0x10000 /path/to/firmware.bin
   ```

   请把 `/dev/ttyACM0` 换成你的实际串口。

### USB 锁定设备说明

部分通过第三方渠道（如 AliExpress）购买的 Xteink 设备出厂即锁定了 USB 刷写。若你的设备被锁定，需要先使用官方的 **Xteink Unlocker** 工具（https://crosspointreader.com/#unlock-tool ）解锁后才能刷写。**直接从 xteink.com 购买的设备不需要此工具。**

> ⚠️ 解锁工具仅官方支持 CrossPoint 与 CrossInk 固件。在已锁定的设备上刷入不受支持的固件，可能导致设备永久变砖或无可恢复路径，操作前务必阅读 [英文 README 的完整警告](./README.md#usb-locked-devices-xteink-unlocker)。

---

## 自定义 SD 卡字体

无需重新刷写固件，即可把你自己的 TTF/OTF 转换成可从 SD 卡加载的 `.cpfont` 字体：

1. 打开 https://crosspointreader.com/fonts ，找到「SD-card font builder」表单。
2. 上传最多四种字形（常规、粗体、斜体、粗斜体），设置字族名、字号与 Unicode 范围。
3. 下载生成的 `.cpfont` 文件，复制到 SD 卡的 `/fonts/你的字体/`（或 `/.fonts/你的字体/` 以隐藏目录）。
4. 在设备的字体设置中选择该字体。

> 提示：通过这种方式加载的字体可以补充内置 3500 字之外的字形（包括粗体 / 斜体），适合需要更大字符覆盖或更多字重的中文阅读场景。

---

## 文档

- [用户指南（英文）](./USER_GUIDE.md)
- [文件同步服务](./sync-service/README.md)
- [文件同步协议](./docs/sync-service.md)
- [简体中文固件构建深度文档](./docs/engineering/chinese-build.md)
- [Web 服务使用说明](./docs/webserver.md) · [Web 接口](./docs/webserver-endpoints.md)
- [项目范围 SCOPE](./SCOPE.md) · [贡献文档](./docs/contributing/README.md)

---

## 开发快速开始

```bash
# 构建并烧录（默认英文构建）
pio run --target upload

# 提交 PR 前的检查
./bin/clang-format-fix
pio check -e default
pio run -e default
```

安装 SDL2 与 `curl`（Linux 还需 OpenSSL 开发头文件）后，可把 EPUB 放入
`fs_/books/`，再运行 CrossMux 使用的桌面模拟器：

```bash
# X4
pio run -e simulator -t run_simulator

# X3
pio run -e simulator_x3 -t run_simulator

# eego A4
pio run -e simulator_eego_a4 -t run_simulator

# Murphy M4
pio run -e simulator_murphy_m4 -t run_simulator
```

模拟器由固定版本的
[CrossMux simulator fork](https://github.com/0x1abin/crosspoint-simulator/tree/6058c3da013fbe1579d41c7c5cc77cd466d37f12)
作为原生 PlatformIO 依赖提供。

调试日志（先 `python3 -m pip install pyserial colorama matplotlib`）：

```bash
# Linux
python3 scripts/debugging_monitor.py
# macOS（替换为你的串口）
python3 scripts/debugging_monitor.py /dev/cu.usbmodem2101
```

更多开发细节见 [English README](./README.md#development-quick-start) 与 [CLAUDE.md](./CLAUDE.md) 中链接的工程文档。

---

CrossMux / CrossPoint Reader **与 Xteink 及任何设备厂商均无隶属关系**。

特别感谢 [diy-esp32-epub-reader](https://github.com/atomic14/diy-esp32-epub-reader) 项目的启发，以及上游 [CrossPoint Reader](https://github.com/crosspoint-reader/crosspoint-reader) 社区。
