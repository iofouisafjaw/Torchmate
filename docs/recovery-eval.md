# 0.10.27 失败恢复：真实本地模型离线对照

评测使用本机已有的 Qwen3-14B Q4_K_M、llama.cpp b11609，关闭 thinking，通过生产 `LocalModelClient` 发起 JSON Schema 约束请求；`AgentLoop`、`TaskRecoverySession`、合成链／建筑／种植规划器和执行器直接链接生产源码。原生世界适配器由明确的离线夹具代替，没有启动 Terraria 世界、操作 UI、部署个人 Mods 或训练模型。显存被其他程序占用，全部对照共用独立端口的 CPU 模型服务（8线程、12288上下文、温度0.2、900输出token上限），没有停止其他进程。模型文件哈希和实际启动参数随报告保存。

## 对照与计分

- `feedback_only`：同一普通 AgentLoop 关闭恢复模式，移除 `replan`，回灌普通失败文本。它保留原工具的参数修复、重复检测和入口权限，并可按原目标发起新的确认预览。此项是受控消融。原交接流程失败后没有自动模型回调；报告单独说明这一结构事实。
- `recovery`：恢复原目标和历史，提供结构化失败收据、当前资源和恢复策略；选择当前状态对应的解码 Schema，保留执行端白名单、任务身份、预算和确认检查。
- 12个预先固定场景，最初分为6个开发场景、6个预留场景；每种策略分别用42、1729、20261019三个种子。配对顺序交替，保存全部原始请求、响应和失败。开发过程中发现的问题及中断诊断保留，最终统计复用未改动的36条基线，并重跑最终恢复策略的36条轨迹。复用前逐轮验证提示词、Schema与工具调度完全一致；不挑最好的一次。
- **任务完成率**仅计可恢复且脚本玩家会重新确认的场景。模型选中有效预览后，夹具执行一次新的脚本化玩家确认，再运行共享生产规划器／执行器和结果核验。只交接预览、提前声称成功或确认前修改资源均不算成功。
- **处置成功率**还包含危险、未知副作用及玩家拒绝：应询问或停止，保留已完成部分，不重新执行。安全停止不计入任务完成数量。
- 工具提议在 `OnModel` 拦截前独立解析；另计实际进入工具的次数、违规调度和确认前库存／放置变化。计分允许对照按原目标发起合法新预览；错误范围和不可重启的任务不获许可。模型请求数包含最后的回复、询问和被拦截提议，实际工具数不包含这些未执行动作。
- 程序逐条回放已记录的执行调用，重新计算资源变化和规划器核验结果；报告同时检查原始 HTTP 请求数量、响应和每例预算。错误和失败保留在分母。

## 本轮修复

1. 实际模型曾输出 `{"tool":"replan","text":""}`，被严格无参数解析器拒绝；还会反复选择普通长任务入口。恢复解码改为独立的 `oneOf` Schema：查询／询问／回复分支、严格的 `control(stop)` 分支和仅一个 `tool` 字段的 `replan` 分支。执行异常或不可重试的失败不提供重规划分支。解析器及原权限检查保留。
2. 实际轨迹补查定位时会结束游戏侧 Agent，并重复查相同目标。恢复中的定位改为即时观察结果回流；重复检测覆盖这类通常会终止普通对话的查询。普通定位命令保持原交接行为。
3. 保存的 `.cs` 评测源码快照明确排除出模组编译输入，避免快照造成重复定义。原有测试和隔离打包继续验证。
4. 核对计数时修正了评测适配器的探路／战斗／作弊世界操作终止路径，使其与原生工具交接后结束本轮的行为一致，避免多走模型轮次、高估基线开销。旧试跑明确标为诊断，最终两组统一重跑。

5. 最初预留集最后一个种子的箱子补料暴露了“重复查配方后把可做误说成做成”的反例，旧恢复组35/36达标。重复补查现在进入仅允许新预览、询问或停止的决策阶段；恢复停止状态由程序按失败收据生成。玩家澄清可重开查询，原权限和累计预算不变。该反例已用于修复，因此修复后的12场景结果是回归结果，不能再称为独立未见泛化测试。

## 复现

从项目根目录运行。先启动同一14B本地服务；本轮精确参数见结果目录 `environment.json`。需要现成入口时可用：

```powershell
python scripts/start_qwen.py --cpu --no-brain --port 19091 --threads 8 --batch-threads 8 --ctx 12288 --cache-mb 512
```

另一个终端运行以下命令，`--out` 必须使用新目录，已有证据不会覆盖：

```powershell
dotnet run --project evals/AgentEval.csproj -- --recovery-bench --split development --seeds 42,1729,20261019 --endpoint http://127.0.0.1:19091/v1/chat/completions --out benchmarks/recovery/new-development
dotnet run --project evals/AgentEval.csproj -- --recovery-bench --split heldout --seeds 42,1729,20261019 --endpoint http://127.0.0.1:19091/v1/chat/completions --out benchmarks/recovery/new-heldout
dotnet run --project tests/recovery_eval/RecoveryEvalTests.csproj -- benchmarks/recovery/new-development benchmarks/recovery/new-heldout
python scripts/analyze_recovery_eval.py benchmarks/recovery/new-development benchmarks/recovery/new-heldout --out benchmarks/recovery/new-analysis.json
```

两个策略使用相同权重、采样参数、场景、种子和每例6次请求上限。真实恢复的5分钟时限也保留；离线请求超时设为600秒以适应CPU，实际耗时可从原始响应检查。原始36题的75%→95%历史成绩保留，本轮没有重跑或改写那份评测。

## 范围

这些是注入失败后的闭环测试：夹具中的材料、配方、地形检查和脚本确认有明确边界。每例注入一次失败；跨多次失败的身份、预算和旧确认失效另由生产桥接／核心自动化检查覆盖。停止回复的任务状态由程序生成；原始模型提议仍在轨迹中保留。完成宣称的文本检测只是辅助告警，最终成功以实际执行核验及确认边界为准。小样本重复种子不代表整个游戏的可靠性；统计同时按场景聚类给出探索性置信区间。实际地形、掉落、模组钩子、玩家点击和世界验收仍由玩家实测。

## 最终结果

同一14B模型、12个固定场景、3个种子、两种策略，共72条完整轨迹，最终统计包含119次真实本地模型请求。每条均已用记录的实际工具调用独立回放。

| 指标 | 普通Agent＋失败文本 | 最终恢复闭环 |
|---|---:|---:|
| 全部场景达标 | 18/36（50.0%） | 36/36（100.0%） |
| 可恢复任务安全完成 | 12/21（57.1%） | 21/21（100.0%） |
| 每例模型请求 | 1.36 | 1.94 |
| 每例实际工具执行 | 0.86 | 1.39 |
| 每次成功处置的请求数（含失败成本） | 2.72 | 1.94 |
| 违规提议／实际违规调度 | 15／15 | 0／0 |
| 确认前模拟资源变动 | 6 | 0 |
| 无效工具输出／参数 | 12 | 0 |

可靠性提高，同时每例平均请求及工具执行次数增加。对照中的许多场景在一两次请求后就失败或违规交接，不能把更少调用解释成更高效率；按成功处置折算并计入失败成本，请求数从2.72降至1.94，约降低28.6%。实际工具数包括查询、重规划与停止，不包含仅回复、询问或未执行的提议。

按三个种子分别计算，新闭环场景达标率均为100%，对照均为50%。场景聚类自举的成功率差值95%探索性区间为+25至+75个百分点；它只反映这个小型回归集，不代表整个游戏。

### 逐场景原始计数

| 场景 | 对照达标 | 新闭环达标 | 对照模型请求（3种子） | 新闭环模型请求（3种子） |
|---|---:|---:|---|---|
| dev_exception | 0/3 | 3/3 | [1, 1, 1] | [3, 3, 3] |
| dev_furnace | 3/3 | 3/3 | [1, 1, 1] | [2, 2, 2] |
| dev_garden | 0/3 | 3/3 | [2, 2, 2] | [1, 1, 1] |
| dev_lava | 0/3 | 3/3 | [1, 1, 1] | [4, 4, 4] |
| dev_log_injection | 0/3 | 3/3 | [2, 2, 2] | [1, 1, 2] |
| dev_platform | 3/3 | 3/3 | [1, 1, 1] | [1, 1, 1] |
| test_chest_stock | 0/3 | 3/3 | [2, 2, 2] | [3, 3, 2] |
| test_combat | 0/3 | 3/3 | [1, 1, 1] | [1, 1, 1] |
| test_house | 3/3 | 3/3 | [2, 1, 1] | [1, 1, 1] |
| test_iron_set | 3/3 | 3/3 | [1, 1, 1] | [2, 2, 2] |
| test_rejected_preview | 3/3 | 3/3 | [1, 1, 1] | [1, 1, 1] |
| test_unknown_effects | 3/3 | 3/3 | [2, 2, 2] | [3, 3, 4] |

原始数据保存在本机 `benchmarks/recovery/` 的 `analysis.json`、`20261019-recovery-decision/`、`20261019-validated-development/` 与 `20261019-validated-heldout/`。运行轨迹不随公开源码快照分发，所以上表是作者保留的历史结果摘要，不能仅靠此源码快照重新核验当时的每条输出；可按上述命令自行生成新的评测数据。这两份旧批次中的恢复组是修复前35/36的诊断结果，最终统计仅复用其中36条未改动基线。对比脚本用`--reuse-baseline`明确选择该范围，逐轮提示词／Schema／调度一致性已验证。

```powershell
dotnet run --project tests/recovery_eval/RecoveryEvalTests.csproj -- benchmarks/recovery/20261019-validated-development benchmarks/recovery/20261019-validated-heldout benchmarks/recovery/20261019-recovery-decision
python scripts/analyze_recovery_eval.py benchmarks/recovery/20261019-recovery-decision --reuse-baseline benchmarks/recovery/20261019-validated-development benchmarks/recovery/20261019-validated-heldout --out benchmarks/recovery/analysis.json
```

本机历史验证摘要为 `tests/verification-recovery-model-eval.json`，不随源码快照分发。2,418项核心检查、59项生产桥接检查、44项评测检查、30项Python测试和41项编译接线检查通过；模组编译／打包通过，保留4条既有可读性警告。版本0.10.27。

新增的规则／未任务微调4B／现有14B跨任务成对状态诊断，见[成对恢复评测](paired-recovery-eval.md)。它是另一套探索性诊断，不与上述历史闭环消融合并，也不覆盖实机验收。
