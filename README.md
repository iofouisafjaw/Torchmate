# TerrarAI — Local Terraria Companion

An on-device, tool-using LLM companion for Terraria: grounded observations, constrained decisions, verified actions.

**0.10.34 · MIT 源码 Alpha · Windows 单人游戏**

通过本地 Qwen3 和 llama.cpp，把中文目标交给 Terraria 同伴。模型选择工具，程序负责执行、权限检查和结果核验；正常游戏推理不需要云端 API。

**C# / .NET 8 · Python · llama.cpp · Qwen3-14B · JSON Schema · SQLite FTS**

- **Agent 闭环**：工具结果回流同一 AgentLoop，程序以真实库存和现场收据核验长任务。
- **状态约束解码与恢复**：恢复／只读／停止状态缩小 Schema；执行端仍独立检查权限，重规划需新确认。
- **资料问答**：游戏配方与版本匹配的 Wiki 检索，保留引用；不把模型回答当成施工完成。

[架构与验证边界](docs/architecture.md) · [源码上传准备](docs/github-preparation.md) · [贡献说明](CONTRIBUTING.md)

> **当前公开的是源码 Alpha，Windows 二进制版仍待玩家验收。** 当前自有代码、文档、测试和原创图标采用 [MIT 许可证](LICENSE)，第三方组件保留各自许可。0.10.30 已移除或重写继承实现并替换两个图标；旧版本和原 Git 历史不因此获得许可，公开仓库从干净快照建立新历史。来源与范围见 [替换说明](docs/source-replacement.md)、[许可状态](LICENSE_STATUS.md) 和 [第三方声明](THIRD_PARTY_NOTICES.md)。自动化检查通过不代表实机通过；未发布 Windows 二进制 Release 或 Workshop。

## 当前能做什么

| 功能 | 范围 |
|---|---|
| 陪伴与移动 | 跟随、等待、停止、召回；有限地形绕路、平台、开门和游泳 |
| 采集与探路 | 附近树木、暴露矿石／石头、掉落拾取；有限向下挖掘与短程探路，报告资源和危险 |
| 合成与箱子 | 指定物品存取；完整合成链预算材料及制作站，优先主背包和附近可用箱子 |
| 建造 | NPC 木屋、自然语言平台蓝图、材料预览、施工与核验；房屋检测和确认后修补 |
| 生存 | 附近小怪保护／反击；可关闭的治疗和提醒，治疗遵守原生药水冷却 |
| 查询 | 背包、配方、附近环境、NPC、商店；矿／箱子／床地图标记；可选本地 Wiki RAG |
| 协作 | 克眼场地／药水、1–3间NPC住房备料／施工的分工、进度、交接和失败恢复 |
| 可选作弊 | 物品／建筑、时间、天气、Boss与世界出生点，共用默认关闭的作弊开关 |

31个工具不等于31项任意任务。范围、材料、制作站、地形和权限条件仍受执行模块限制。不是完整Boss战斗AI，也不支持多人或任意复杂建筑。魔法存储、Census、更好的体验和商店集成是可选软依赖，不是基础安装要求。

0.10.34 修正演示发现的只读追问误执行、房屋修补模板冲突、通道草皮变化和种植贴图刷新误停、克眼备料循环配方，以及召唤条件问答混淆。只做自动化验证，实机复测待完成；见 [修复记录与复测步骤](docs/demo-fixes-0.10.34.md)。

## Windows 快速开始

源码快照不含 `.tmod`、游戏程序集、模型或推理运行时。首次从源码使用时，先完成下方“从源码构建”；已有本地 Windows 准备包时才直接按本节安装。

当前为需要 **Python 3.11+ x64** 的脚本式 Alpha。准备包不含 Python、模型、llama.cpp、Wiki数据库或游戏文件；玩家不需要安装 .NET SDK。没有独立安装器，也尚未完成全新Windows和图形游戏验收。

1. 安装 Steam Terraria 与 tModLoader，先启动一次 tModLoader 到主菜单，再退出。当前编译目标是 **Terraria 1.4.4.9 / tModLoader 2026.8.3.0**，其他版本未在本轮声明兼容。
2. 将私用准备包完整解压到可写目录，不要直接在ZIP里运行；安装 [Python 3.11+ x64](https://www.python.org/downloads/windows/)。可在终端运行 `python --version` 或 `py -3 --version` 检查。
3. 双击 **安装到Windows.bat**。默认分别下载锁定的 llama.cpp CUDA运行时和官方 **Qwen3-14B Q4_K_M**，核对大小及SHA-256，将 `.tmod` 安装并启用到当前用户的 tModLoader profile。整个14B模型约 **9.00 GB**，请预留下载、运行时、模型和游戏所需空间。中断模型下载可重跑续传。
4. 双击 **开始游玩.bat**。启动／复用本地服务，确认brain版本和实际GGUF身份，再打开Steam tModLoader。进入单人世界后按 **`,`（逗号）** 打开聊天；快捷键可在游戏控件设置修改。

GPU后端锁定到 llama.cpp `b11609` 的CUDA 13.4包；驱动兼容性、游戏并行FPS和最低显存需要实测。9 GB权重大小不代表9 GB显存即可运行。没有NVIDIA显卡或需CPU排查时，在目录中运行：

```powershell
python scripts/setup_windows.py --backend cpu
python scripts/play_local.py --cpu
```

8B可显式选择，尚无本Alpha的同等任务成功率验证：

```powershell
python scripts/setup_windows.py --size 8b --backend cuda
```

脚本运行方式、安装位置、升级与故障排查见 [Windows说明](docs/windows-alpha.md)。安装前预览许可和下载来源见 [依赖锁](release/dependencies.lock.json)。安装需联网下载，下载后游戏推理在本机进行；无自动联网问答或云端回退。

## 第一次尝试

先在一次性测试世界中检查连接与停止，再尝试资源任务：

```text
检查连接
查背包
帮我砍5棵树
停止
最近的铁矿在哪
做一套铁盔甲
```

需要材料消耗或施工的长任务先查看预览，再点击实际确认。回复“能做”或查到配方不代表任务已完成；以程序收据和实际背包／现场为准。

协作示例（每行分别发送）：

```text
一起准备打克眼
药水我来
竞技场你来
继续协作
协作进度
```

或说「协作扩建2间NPC住房」「备料你来」「住房我来」「继续协作」。中途可「暂停协作」「住房你来」交接。玩家完成项用「核验协作」读取真实库存或原位置现场。完整句式和边界见 [协作说明](docs/collaboration.md)。

## 界面、确认与存档

- 一个聊天窗口，Enter发送，Esc关闭；中文输入法候选确认不应误发送。**普通聊天不暂停游戏**；世界选址／预览的暂停由对应工坊管理。玩家负责的施工选址完成后恢复游戏，同伴施工确认被禁用。
- 任务条显示状态和同意／拒绝／停止；关闭窗口后可查看顶部任务提示。可以直接说「等待」「跟随」「召回」「停止」，无需等模型响应。
- 失败回执包含原因、已确认进度和最新资源。自动恢复有累计预算，重规划保留原范围并生成新预览；旧确认不能继承。未知副作用需要人工检查。
- 协作运行计划、未完成请求和旧权限不跨退出恢复；已建内容由游戏正常保存。偏好和历史记录按世界区分，详见游戏ModData目录。
- 作弊默认关闭，离开世界后关闭；模型不能自行开启。商店消费仍需玩家明确授权。

## 可选 Wiki

基础安装 **不含任何Wiki数据库**。无索引时仍可使用工具和实时游戏状态；Wiki支撑的攻略能力不可等同于已安装完整资料库。原版Wiki从玩家自行取得的兼容快照导入；灾厄导入器单独联网下载。来源、版本和各自许可仍保存在索引中，回答附引用，不将其当作代码许可内容分发。步骤见 [Windows说明](docs/windows-alpha.md)。

## 开发与自动化

源码需要 .NET 8 SDK、Python 3.11+及匹配的tModLoader安装；准备包玩家不需要SDK。

### 从源码构建

在完整源码目录运行；将路径替换为自己的 Steam tModLoader 安装目录：

```powershell
python scripts/prepare_github.py --check
python scripts/build_windows.py --tml "E:\SteamLibrary\steamapps\common\tModLoader" --no-install
python tests/check_package.py
```

构建得到 `dist/TerrarAI.tmod`。构建和检查不会启动游戏或安装到个人 Mods。
决定本机试用后，再运行 `安装到Windows.bat` 下载锁定的依赖并安装；默认需要联网下载模型，
其后的推理在本机运行。导出的源码可直接做哈希和来源验证，不依赖原仓库历史。

### 自动化验证

```powershell
python scripts/check_provenance.py
python scripts/prepare_github.py --check
dotnet run --project tests/CompanionTests.csproj
dotnet run --project tests/native_replacement/NativeReplacementTests.csproj
dotnet run --project tests/recovery_adapter/RecoveryAdapterTests.csproj
dotnet run --project tests/collaboration_adapter/CollaborationAdapterTests.csproj
python -m unittest discover -s tests -p "test_*.py"
python scripts/build_windows.py --tml .runtime/tModLoader --no-install
python tests/check_package.py
python scripts/release_alpha.py --check
```

最后一条在授权或实机验收未完成时退出2，这是预期的发布阻塞。仅本机准备可用 `--prepare-only --kind windows` 或 `--kind source`；不会创建GitHub Release、推送Git或上传Steam。

GitHub CI 使用 .NET 8 与 Python 3.11，在没有游戏、模型和 Wiki 的环境中运行核心和模拟适配器检查。
0.10.34 功能修复曾通过3,090项C#检查（含本机编译后接线）及75项Python测试；
准备脚本另有独立回归，最新数量以实际日志为准。这些自动化结果不表示实机验收通过。

原有14B失败恢复离线对照见 [评测说明](docs/recovery-eval.md)。这是固定夹具上的回归，不是全部真实游戏任务成功率；本次替换与安装流程测试不启动真实模型或游戏。功能与安装实机步骤见 [玩家验收](docs/playtest.md) 和 [Alpha发布检查](docs/release-readiness.md)。

## 已知限制与反馈

- Windows x64单人Alpha准备；其他平台旧脚本保留，本轮未验证。
- 空间保护区、智能箱子分类、跨退出恢复协作任务、完整Boss协作均未承诺实现。
- 推理延迟、游戏帧率、复杂寻路和模组组合需实测；模型不可用时基本控制仍可用。
- 不要把现有仓库直接推送为公开版：旧代码／图标和继承的`.env`仍存在于历史中；当前实现替换不会清理或许可历史。源码准备ZIP不是原Git历史，按白名单排除了本地数据。
- 报告问题时提供版本、Windows／tModLoader版本、实际模型文件名、复现指令和脱敏日志片段；不要上传存档、完整聊天或`.env`。

历史变更见 [CHANGELOG](CHANGELOG.md)。技术方向见 [DESIGN](DESIGN.md)，其中未来目标不代表当前能力。

### 埋藏矿的确认式通道（0.10.33）

说“规划通道挖铁矿”或“挖过去采矿”：先显示目标矿、阶梯路线和最大移除数，必须同意后才施工。
每次只处理预览中的一个矿格；横向最多28格、深度最多12格，最多移除128块。
通道三格宽、四格高，逐格复查液体、落沙、电路、设施、镐力及支撑；检查不到安全方案就拒绝。
说停止或等待会中止，已完成部分保留。普通“挖1处矿”仍只找露头矿，并提示此入口。
详见[通道设计与验收](docs/ore-tunnel.md)。
