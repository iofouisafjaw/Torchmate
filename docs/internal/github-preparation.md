# GitHub 源码上传准备

所有者已于2026-10-23选择 [MIT许可证](../../LICENSE)，版权署名为 `2026 TerrarAI contributors`，并授权向 `iofouisafjaw/Torchmate`（原名 `iofouisafjaw/TerrarAI`） 公开上传干净源码。Windows 二进制版仍待验收；旧 Git 历史和私用准备包不在上传范围。
当前源码与依赖来源记录见 [授权状态](../../LICENSE_STATUS.md) 和 [第三方声明](../../THIRD_PARTY_NOTICES.md)。

## 已整理的内容

- 当前 C# 模组、Python 服务、固定评测夹具和自动化测试。
- README、架构、开发／反馈说明、来源记录、安装和复测文档。
- Windows GitHub Actions：只读权限、固定 Action 提交、不依赖个人模型或游戏目录。
- 源码导出白名单、凭据模式扫描、文档链接和解压后完整性检查。
- 普通忽略规则排除旧版本快照、原始轨迹、录屏、运行数据、模型、Wiki 和存档。

## 生成可检查的快照

```powershell
python scripts/prepare_github.py --check
python scripts/prepare_github.py --prepare
```

`--check` 成功表示技术检查通过，不表示授权／发布批准。报告仍明确列出发布阻塞。
`--prepare` 复用 Alpha 打包器：生成带 SHA-256 清单的 `PRIVATE-PREP` 源码 ZIP，
并解压到新的 `dist/github-preparation/TerrarAI-<版本>-source-<时间>/`。
快照不含原 `.git`，不更改原仓库的远端；解压后会再检查哈希、来源和文档链接。
目录旁的 `.preparation.json` 是本地准备报告。
同目录还保存带时间戳的独立 ZIP 和 SHA-256 文件；后续准备不会覆盖该副本。

所有者批准公开源码后，使用单独的源码发布关卡：

```powershell
python scripts/release_alpha.py --check --kind source
python scripts/release_alpha.py --kind source
```

它检查 MIT／第三方许可、来源哈希和明确的无历史源码授权，不把实机未验收标成通过。
公开仓库只提交白名单内源码；不提交 `PRIVATE-PREP` 标记或原 `.git`。

## 本地验证结果（0.10.34，MIT 准备版）

在新导出的文件上建立一次仅供验证的本地 Git 历史，再重新克隆，确认
`core.autocrlf=true` 不会改变来源记录要求的文件字节。该临时验证仓库不作为公开历史上传。
对新检出的副本执行：

| 项目 | 结果 |
| --- | --- |
| 核心与模拟适配器 | 3,024 项检查通过；这也是 CI 的 C# 范围 |
| Python | 86 项测试通过，包含许可完整性、第三方许可打包和源码／二进制发布关卡隔离 |
| 模型评测夹具 | 84 例校验通过，模型调用0次 |
| 带显式外部 tModLoader 路径的源码构建 | 通过，0错误；4个既有分析器警告及既有图像加载警告 |
| 编译后生产接线与包校验 | 66 项检查和包校验通过；C# 总计3,090项 |
| 来源、文档链接、导出排除与静态凭据模式扫描 | 通过 |

这是本地验证结果，不是已经在 GitHub 上运行的 CI，也不是游戏验收。
无游戏依赖的检查未启动模型或图形游戏；实际模组编译使用已有的 tModLoader 构建器。

原工作目录的 `origin` 是继承上游，旧 Git 历史含 `.env` 和已替换的实现／资源。
不要在原目录执行推送。忽略规则不会清理已有 Git 历史。
脚本不读取、导出或打印继承 `.env` 的内容。

## 公开源码的操作边界

1. 项目许可证已确定为 MIT；当前自有代码、文档、测试和原创图标适用根目录 LICENSE。
   ImproveGame、llama.cpp、Qwen 等保留原有许可；游戏资源和 Wiki 内容不属于本项目 MIT 授权范围。
   历史上游内容的授权问题不会因添加 LICENSE 自动消失。
2. 核对准备报告，按已授权的源码范围重新导出并复核；带 `NOT_FOR_DISTRIBUTION.txt` 的旧准备包不能直接公开。
3. 在审核后的新源码目录初始化新的 Git 历史，创建所有者自己的仓库，核对可见性和新的远端。
   本次已获所有者上传授权；首个提交不会带入原仓库历史。
4. 上传后观察真正的 GitHub Actions 结果。当前只运行了本地对应检查，不能预先宣称远端 CI 通过。

Windows 二进制 Alpha、全新系统安装与游戏验收的发布条件仍按
[发布检查](release-readiness.md) 单独保留。取消实机测试不等于该项已经通过。
源码上传不启动游戏、不跑真实模型评测、不录制 Demo，也不发布二进制 Release 或 Workshop。
