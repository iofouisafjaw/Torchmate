# Torchmate · 小火伴

**An on-device LLM companion for Terraria · 一个在本机运行的 Terraria 智能同伴**

[English](#english) · [简体中文](#简体中文) · [CI](https://github.com/iofouisafjaw/Torchmate/actions/workflows/ci.yml) · [MIT](LICENSE)

**0.10.34 · Source Alpha · Windows x64 · Single-player**

C# / .NET 8 · Python · llama.cpp · Qwen3-14B · JSON Schema · SQLite FTS5

<a id="english"></a>

## English

Give your companion a goal, such as gathering wood, crafting iron armor, or building a three-level platform arena. A local LLM selects tools; deterministic planners budget materials and stations, execute approved work, and verify results against the game. Inference runs locally without a cloud API or automatic cloud fallback.

> **Source Alpha, not a ready-to-install binary release.** Build the mod before running setup. The repository includes no game assemblies, model weights, inference runtime, Wiki database, or saves. Automated checks pass; 0.10.34 gameplay retesting and clean-Windows installation acceptance remain incomplete. No Workshop release is available.

**Compatibility:** Torchmate is the new project and mod display name. The internal mod ID remains `TerrarAI`; `.tmod` filenames, configuration types, and save/data paths keep that identifier so existing installations remain compatible.

### How the agent works

| Layer | Implementation and behavior |
| --- | --- |
| **Agent loop** | The production `AgentLoop` feeds observations and tool results back into the next decision. Recursive crafting, blueprint planning, bounded terrain search, and native controllers perform the work. Live receipts determine completion. |
| **State-constrained decoding** | `LocalModelClient` sends JSON Schema through llama.cpp's `response_format`. Recovery, observation-only, stop, and wrap-up states expose different tool/argument schemas. Strict parsing and runtime permission checks remain independent of decoding. |
| **Local RAG** | A Python service combines live game/recipe data with optional Terraria and Calamity Wiki retrieval over SQLite FTS5. Source, scope, and version checks isolate eligible evidence; answers retain citations. A numeric filter removes unsupported numeric claims, but cannot guarantee factual correctness. |
| **Failure recovery** | A failed long task returns its reason, verified partial progress, and fresh backpack/chest stock. The agent can inspect, ask, stop, or request a new plan within the original scope. Previous approval is invalidated; a new preview requires new confirmation. Recovery has request, retry, and time budgets. |

The loop is **observe → decide under the current schema → validate → plan/confirm → execute → verify → observe**. Read-only follow-ups cannot authorize digging; a plausible model reply or a successful preview cannot count as completed work. Argument repair, repeat detection, and reply-only wrap-up turns bound common failure modes.

See [architecture and module map](docs/architecture.md), [AgentLoop](Content/Companion/AgentLoop.cs), [model client](Content/Companion/LocalModelClient.cs), and [recovery policy](Content/Companion/TaskRecovery.cs). The project uses existing model weights; it does not claim RL training or a new neural model architecture.

### What you can do

| Area | Current scope |
| --- | --- |
| Companion and travel | Follow, wait, stop, recall; bounded detours, platforms, doors, and swimming. |
| Gathering and exploration | Nearby trees, exposed ore/stone, drop collection, limited shafts and scouting; confirmed short access tunnels to buried ore. |
| Crafting and logistics | Crafting-chain material/station planning; nearby chest deposits and quantity-based withdrawals; use inventory/chests before gathering. |
| Construction | NPC house blueprints, natural-language platforms, material previews, construction verification, housing checks and confirmed repairs. |
| Survival and cooperation | Nearby enemy protection, optional healing/reminders, gardening; divide and revise Eye of Cthulhu preparation or NPC housing tasks. |
| Queries and optional cheats | Inventory, NPCs, shops, recipes, resource/chest/bed map markers; item/building/time/weather/Boss/spawn cheats behind a default-off switch. |

There are 31 tool names, with bounded implementations rather than arbitrary game control. English documentation does not imply equivalent English-command testing: interaction has primarily been developed and evaluated with Chinese instructions.

### Install from source on Windows

**Requirements:** Windows x64, Python 3.11+ x64, .NET 8 SDK, and compatible Steam Terraria/tModLoader. The current build target is **Terraria 1.4.4.9 / tModLoader 2026.8.3.0**; compatibility with other versions is not established. Python scripts use the standard library; no pip installation is needed.

1. Start tModLoader once to create its user-data directory, then quit it. Install/upgrade the mod while the game is closed.
2. Clone and build. Replace the example tModLoader path with your actual installation:

   ```powershell
   git clone https://github.com/iofouisafjaw/Torchmate.git
   cd Torchmate
   python scripts/prepare_github.py --check
   python scripts/build_windows.py --tml "E:\SteamLibrary\steamapps\common\tModLoader" --no-install
   python tests/check_package.py
   ```

   This creates `dist/TerrarAI.tmod` without installing it or launching the game. Preserve the source directory layout.

3. Download the pinned runtime/model and install the built mod into your tModLoader profile:

   ```powershell
   python scripts/setup_windows.py --size 14b --backend cuda
   ```

   Setup checks download sizes and SHA-256 hashes, preserves other enabled mods, and does not start the model or game. It downloads official Qwen3-14B Q4_K_M weights, approximately **9.00 GB**, plus llama.cpp `b11609`. The CUDA backend is pinned to CUDA 13.4 artifacts and needs a compatible driver. Weight file size is **not** a minimum VRAM specification; allow memory for both inference and the game.

4. Start or reuse local services, verify their identity, and open Steam tModLoader:

   ```powershell
   python scripts/play_local.py
   ```

   In a disposable single-player world, press **`,`** to open chat. Try `检查连接` (check connection), `查背包` (inventory), and `停止` (stop) before resource-consuming tasks. Review the preview and explicitly confirm work when prompted. Ordinary chat does not pause the game.

Useful alternatives:

```powershell
# Start/check services without opening the game.
python scripts/play_local.py --service-only

# Read-only diagnostics; --check never starts services or the game.
python scripts/setup_windows.py --check
python scripts/play_local.py --check

# CPU alternative: setup and launch must select the same backend.
python scripts/setup_windows.py --size 14b --backend cpu
python scripts/play_local.py --cpu
```

`play_local.py --check` requires already-running services. Default endpoints are `127.0.0.1:8080` for Python and `127.0.0.1:8081` for llama.cpp. The legacy API alias `qwen3-4b` does not identify actual model size. Compatible services are reused; foreign processes are not terminated automatically.

8B is an explicit experimental option (`--size 8b`) without equivalent quality results. Wiki indexes are optional and imported separately; base tools work without them. See [Windows setup/troubleshooting and Wiki import](docs/windows-alpha.md), [dependency pins](release/dependencies.lock.json), and [memory settings](docs/model-memory.md). Supporting guides are currently mainly in Chinese.

### Evaluation and verification

**Current automated scope:** 3,024 C# checks and 86 Python tests in source CI, plus validation of 84 evaluation fixtures **without running a model**. A local mod build additionally passed 66 compiled-wiring checks, for 3,090 C# checks in that local validation. These counts describe checks, not game tasks or gameplay acceptance. See [workflow](.github/workflows/ci.yml) and [verification scope](docs/github-preparation.md).

**Historical local-model results, not a new 0.10.34 model run:**

| Evaluation | Reported result | Scope |
| --- | --- | --- |
| General agent regression | **81/108 → 103/108** (75.0% → 95.4%) with the same 14B model | 36 scenarios × 3 runs; earlier development suite, distinct from today's 84-fixture inventory. |
| Failure recovery | **18/36 → 36/36** successful dispositions | Same 14B model, 12 fixed scenarios × 3 seeds per strategy; 72 trajectories and 119 local-model requests in total. |

The recovery baseline disables recovery mode while retaining the ordinary agent's argument repair, repeat detection, and entry permissions. Both strategies use the same model and sampling settings, with simulated native adapters and scripted player confirmation. The recorded recovery run used CPU inference, not a GPU speed benchmark.

| Recovery metric | Failure-text baseline | Recovery loop |
| --- | ---: | ---: |
| Safely completed recoverable tasks | 12/21 | 21/21 |
| Model requests per scenario | 1.36 | 1.94 |
| Actual tool executions per scenario | 0.86 | 1.39 |
| Forbidden tool dispatches in the harness | 15 | 0 |
| Simulated resource changes before confirmation | 6 | 0 |

Successful dispositions include correctly asking/stopping on unsafe or rejected tasks; they are not all completed tasks. Recovery increases average calls per scenario, while the baseline often exits early on failure. Counterexamples from this small set were used to fix the system: **these are regression results, not an untouched generalization test or a whole-game success rate**. Raw historical traces are retained locally and excluded from the public snapshot; the snapshot alone cannot independently verify every historical output. Protocol, counts, caveats, and commands for generating new runs: [recovery evaluation](docs/recovery-eval.md).

Quick checks that need no game, GPU, Wiki, or running model:

```powershell
python scripts/prepare_github.py --check
python -m unittest discover -s tests -p "test_*.py"
dotnet run --project tests/CompanionTests.csproj
python scripts/run_evals.py --validate
```

The [CI workflow](.github/workflows/ci.yml) lists all adapter test commands. This README update does not rerun real-model benchmarks or graphical gameplay.

### Known limits and licensing

- Single-player Windows Alpha. Multiplayer, other platforms, arbitrary buildings, and full Boss combat are not validated features.
- Navigation, swimming, digging, drop collection, mod hooks, GPU/game contention, FPS, and mouse/IME behavior still need player testing. 0.10.34 fixes have automated coverage; gameplay retesting and clean-Windows acceptance remain incomplete. Use the [playtest checklist](docs/playtest.md) and [0.10.34 regression steps](docs/demo-fixes-0.10.34.md).
- Path/tunnel/gathering searches have explicit range and safety budgets; no safe route or insufficient tools can stop a task. Replanning does not broaden scope or inherit old permission. Active cooperation and approvals do not resume across world exits.
- Optional integrations are not guarantees for every mod combination. Missing Wiki evidence limits advice; version checks and numeric filtering cannot eliminate all hallucinations. 8B and reduced-memory settings do not inherit historical 14B scores.
- Project-owned code, documentation, tests, and original icons use [MIT](LICENSE). Third parties retain [their licenses](THIRD_PARTY_NOTICES.md). This grant does not cover game assets, Wiki content, model weights, or unauthorized historical upstream material. The public repository starts with fresh source history; see [license scope](LICENSE_STATUS.md) and [replacement record](docs/source-replacement.md).

For feedback, include the version, actual model, reproduction steps, and short redacted logs. Keep saves, full chats, `.env`, keys, and model/Wiki files out of issues. See [contributing](CONTRIBUTING.md), [security](SECURITY.md), and [changelog](CHANGELOG.md).

<a id="简体中文"></a>

## 简体中文

把「砍树」「做一套铁盔甲」「搭三层平台竞技场」这样的目标交给同伴。本地模型选择工具，确定性规划器计算材料与制作站，程序执行已获授权的工作，再用游戏现场核验结果。正常推理在本机完成，不依赖云端 API，也没有自动云端回退。

> **这是源码 Alpha，尚无开箱即用的二进制发行版。** 先编译模组，再运行安装脚本。仓库不含游戏程序集、模型、推理运行时、Wiki 数据库或存档。自动化检查已通过；0.10.34 实机复测和全新 Windows 安装验收仍未完成，未发布 Workshop。

**改名与兼容：** Torchmate（小火伴）是新的项目名和模组显示名；内部模组 ID 继续使用 `TerrarAI`。`.tmod` 文件名、配置类型和存档／数据路径保持原标识，兼容已有安装。

### Agent 架构

| 核心机制 | 具体实现 |
| --- | --- |
| **Agent 闭环** | 生产 `AgentLoop` 将现场观察、工具结果回灌给下一轮决策。递归合成链、蓝图、有限地形搜索及原生控制器负责实际工作；完成状态由实时收据决定。 |
| **状态约束解码** | `LocalModelClient` 通过 llama.cpp 的 `response_format` 提交 JSON Schema；恢复、只读、停止与收尾状态使用不同的工具／参数约束。严格解析和执行端权限检查独立保留。 |
| **本地 RAG** | Python 服务结合游戏状态／配方与可选的原版、灾厄 Wiki SQLite FTS5 检索；按来源、范围与版本筛选证据，保留引用。数值过滤移除缺乏依据的数值断言，但不能证明整段回答正确。 |
| **失败恢复** | 长任务失败后返回原因、已核验完成部分和最新背包／箱子库存。Agent 可补查、询问、停止，或在原范围内重新规划；旧确认失效，新预览必须重新确认。恢复受请求数、重试次数和时间预算限制。 |

任务链是：**观察 → 按当前 Schema 决策 → 校验 → 规划／确认 → 执行 → 核验 → 再观察**。只读追问不能授权挖掘；「看起来能做」或成功生成预览都不算任务完成。参数修复、重复检测和只回复的收尾回合用于约束常见失败。

实现入口见 [架构与模块映射](docs/architecture.md)、[AgentLoop](Content/Companion/AgentLoop.cs)、[模型客户端](Content/Companion/LocalModelClient.cs) 和 [恢复策略](Content/Companion/TaskRecovery.cs)。项目使用现有模型权重，没有声称训练了新神经网络架构或加入 RL。

### 当前能做什么

| 类别 | 当前范围 |
| --- | --- |
| 陪伴与移动 | 跟随、等待、停止、召回；有限绕路、平台、开门和游泳。 |
| 采集与探路 | 附近树木、露头矿／石头、掉落拾取、有限竖井和短程探路；确认后挖短通道接近埋藏矿。 |
| 合成与物流 | 完整合成链预算材料和制作站；指定物品存箱、按数量取出，优先用背包与附近箱子，不足再采集。 |
| 建造 | NPC 小屋、自然语言平台、材料预览、施工核验；房屋检测与确认后修补。 |
| 生存与协作 | 附近小怪保护、可关闭的治疗／提醒、园艺；克眼准备和扩建 NPC 住房的分工、进度、改计划与恢复。 |
| 查询与可选作弊 | 背包、NPC、商店、配方、矿／箱子／床地图标记；物品、建筑、时间、天气、Boss 和出生点作弊共用默认关闭的开关。 |

31 个工具对应有边界的实现，不代表任意游戏控制。交互主要按中文开发和评测；文档双语不表示英文指令已获得同等验证。

### Windows 从源码安装

**需要：** Windows x64、Python 3.11+ x64、.NET 8 SDK，以及兼容的 Steam Terraria／tModLoader。当前编译目标为 **Terraria 1.4.4.9 / tModLoader 2026.8.3.0**，其他版本未确认兼容。Python 脚本仅用标准库，无需 pip 安装依赖。

1. 首次启动 tModLoader，让它创建用户数据目录，然后退出。安装或升级时保持游戏关闭。
2. 克隆并编译；将示例路径替换成自己的 tModLoader 安装目录：

   ```powershell
   git clone https://github.com/iofouisafjaw/Torchmate.git
   cd Torchmate
   python scripts/prepare_github.py --check
   python scripts/build_windows.py --tml "E:\SteamLibrary\steamapps\common\tModLoader" --no-install
   python tests/check_package.py
   ```

   生成 `dist/TerrarAI.tmod`，此步骤不安装模组、不启动游戏。请保留源码目录结构。

3. 下载锁定的运行时／模型，并安装已编译的模组：

   ```powershell
   python scripts/setup_windows.py --size 14b --backend cuda
   ```

   安装器核对下载大小和 SHA-256，保留其他已启用模组，不启动模型或游戏。默认下载官方 Qwen3-14B Q4_K_M，权重约 **9.00 GB**，另需 llama.cpp `b11609`。GPU 后端锁定 CUDA 13.4 构件，需要兼容驱动。**权重大小不等于最低显存**，应给模型和游戏同时留空间。

4. 启动／复用本地服务，核对身份后打开 Steam tModLoader：

   ```powershell
   python scripts/play_local.py
   ```

   先在一次性单人测试世界按 **`,`（逗号）** 打开聊天，测试「检查连接」「查背包」「停止」，再做消耗材料的任务。施工前查看预览并明确确认。普通聊天不暂停游戏。

常用替代方式：

```powershell
# 只启动／检查服务，不打开游戏。
python scripts/play_local.py --service-only

# 只读诊断；--check 不启动服务或游戏。
python scripts/setup_windows.py --check
python scripts/play_local.py --check

# CPU 方案：安装和启动必须选择一致的后端。
python scripts/setup_windows.py --size 14b --backend cpu
python scripts/play_local.py --cpu
```

`play_local.py --check` 要求服务已经运行。默认 Python 服务为 `127.0.0.1:8080`，llama.cpp 为 `127.0.0.1:8081`。历史 API 别名 `qwen3-4b` 不代表实际模型大小。兼容服务会被复用；程序不会自动终止占用端口的其他进程。

8B 通过 `--size 8b` 显式选择，仍是未经同等质量评测的实验选项。Wiki 需自行单独导入；没有索引也可用基础工具。详细步骤见 [Windows 排查与 Wiki 导入](docs/windows-alpha.md)、[依赖锁](release/dependencies.lock.json) 和 [显存配置](docs/model-memory.md)。

### 评测数据与验证边界

**当前自动化范围：** 源码 CI 运行 3,024 项 C# 检查、86 项 Python 测试，另校验 84 个评测夹具，**不调用模型**。本地模组构建另通过 66 项编译后接线检查，该轮本地 C# 合计 3,090 项。检查数量不是游戏任务数量，也不代表实机通过。见 [CI 工作流](.github/workflows/ci.yml) 和 [验证范围](docs/github-preparation.md)。

**以下是历史本地模型成绩，不是 0.10.34 的新模型评测：**

| 评测 | 已记录结果 | 范围 |
| --- | --- | --- |
| 通用 Agent 回归 | 同一 14B 模型 **81/108 → 103/108**（75.0% → 95.4%） | 36 场景，每题 3 次；属于早期开发套件，与当前 84 个夹具不是同一评测范围。 |
| 失败恢复 | 场景处置达标 **18/36 → 36/36** | 同一 14B，12 个固定场景 × 3 个种子／策略；共 72 条轨迹、119 次本地模型请求。 |

恢复对照关闭基线的恢复模式，但保留普通 Agent 的参数修复、重复检测和入口权限。两组模型及采样设置相同，原生世界由模拟适配器替代，玩家重新确认由脚本执行。该轮使用 CPU 推理，不能作为 GPU 性能成绩。

| 恢复评测指标 | 普通 Agent＋失败文本 | 恢复闭环 |
| --- | ---: | ---: |
| 可恢复任务安全完成 | 12/21 | 21/21 |
| 每例模型请求 | 1.36 | 1.94 |
| 每例实际工具执行 | 0.86 | 1.39 |
| 评测框架内违规工具调度 | 15 | 0 |
| 确认前模拟资源变动 | 6 | 0 |

「处置达标」包含对危险或拒绝场景正确询问／停止，不全部是任务完成。恢复提高了每例平均调用数，基线则经常提前失败退出。这个小集合的反例已用于修复，因此**结果是固定回归成绩，不能当作未见泛化评测或整个游戏成功率**。原始历史轨迹保存在本机，不随公开快照分发，无法仅靠源码独立核验当时每条输出。协议、原始计数、局限和新评测命令见 [失败恢复评测](docs/recovery-eval.md)。

无需游戏、GPU、Wiki 或运行中的模型即可做快速检查：

```powershell
python scripts/prepare_github.py --check
python -m unittest discover -s tests -p "test_*.py"
dotnet run --project tests/CompanionTests.csproj
python scripts/run_evals.py --validate
```

完整适配器测试命令见 [CI 工作流](.github/workflows/ci.yml)。本次 README 修改不重跑真实模型基准或图形游戏。

### 已知限制与许可

- 当前为 Windows 单人 Alpha；多人、其他平台、任意复杂建筑和完整 Boss 战斗不属于已验证能力。
- 实际寻路、游泳、挖掘、掉落、模组钩子、模型与游戏争用显存、FPS、鼠标和中文输入仍需玩家验收。0.10.34 修复有自动化覆盖，但实机复测与全新 Windows 验收未完成；见 [实机清单](docs/playtest.md) 和 [本版复测步骤](docs/demo-fixes-0.10.34.md)。
- 寻路／通道／采集有范围与安全预算；无安全路线或工具不足时会停止。重规划不扩大范围、不继承旧许可。退出世界后不恢复执行中的协作任务或确认。
- 可选集成不保证兼容所有模组组合。缺少 Wiki 会限制攻略能力；版本检查和数值过滤不能消除全部幻觉。8B 或降低显存配置不自动继承历史 14B 成绩。
- 当前自有代码、文档、测试和原创图标采用 [MIT](LICENSE)；[第三方许可](THIRD_PARTY_NOTICES.md) 保持独立。授权不覆盖游戏素材、Wiki 内容、模型权重或未获授权的历史上游材料。公开仓库采用新历史；详见 [许可范围](LICENSE_STATUS.md) 和 [替换记录](docs/source-replacement.md)。

反馈请附版本、实际模型、复现步骤和简短脱敏日志，不要上传存档、完整聊天、`.env`、密钥、模型或 Wiki 文件。另见 [贡献说明](CONTRIBUTING.md)、[安全说明](SECURITY.md) 与 [更新记录](CHANGELOG.md)。
