# Windows Alpha 操作与排查

## 安装与运行

这是Python脚本式准备包，不是无依赖EXE。需要Windows x64、Python 3.11+ x64和Steam tModLoader。所有脚本只使用Python标准库，不需要pip。请完整解压到可写目录，退出游戏后安装或升级。

```powershell
python scripts/setup_windows.py --size 14b --backend cuda
python scripts/play_local.py
```

对应根目录的「安装到Windows.bat」「开始游玩.bat」。批处理会先确认Python版本／位数，再尝试`py -3`，失败时说明缺少Python；不会自动安装Python。`setup_windows.py`不打开Steam，也不启动模型；`play_local.py`启动服务并在验证后打开Steam。想只启动服务不打开游戏：

```powershell
python scripts/play_local.py --service-only
```

模型和runtime均从`release/dependencies.lock.json`的固定官方URL下载，核对大小和SHA-256。不静默升级llama.cpp，也不改用第三方量化模型。失败的`.part`不改名为最终文件；支持续传，完整坏文件需移走后重下。CUDA 13.4需要兼容驱动，尚无本Alpha最低配置实测承诺。CPU安装改用`--backend cpu`；8B通过`--size 8b`显式选择，属于未经同等评测的实验选项。

预留模型和运行时空间，运行时升级会保留`.runtime/llama-win.previous-*`旧目录，以便手动恢复。完成升级且不再需要时，可自行删除这些备份。程序不会终止任何占用旧runtime文件的进程；文件被占用时安装停止，原目录保留。

## 文件位置与设置

| 内容 | 位置 |
|---|---|
| 模型 | 本解压目录`.models/` |
| 固定版llama.cpp | `.runtime/llama-win/` |
| 模型／后端选择 | `.runtime/alpha-settings.json` |
| 下载缓存 | `.runtime/download/` |
| 启动日志 | `.runtime/playtest/qwen.log` |
| Mod | 当前Windows实际Documents下`My Games/Terraria/tModLoader/Mods/TerrarAI.tmod` |
| 游戏中的模型连接配置 | tModLoader ModConfigs下`TerrarAI_TerrarAI_Config.json` |

Documents可能被OneDrive或系统设置重定向，脚本使用Windows Known Folder查询。显式指定其他tModLoader用户数据目录：

```powershell
python scripts/setup_windows.py --profile "D:\My Profile\tModLoader"
python scripts/play_local.py --profile "D:\My Profile\tModLoader" --service-only
```

目录应已存在。自定义profile时只启动服务，之后手动打开使用该profile的tModLoader实例；启动器不假定Steam默认实例会使用自定义目录。安装保留其他已启用模组，仅追加TerrarAI；升级前保存不同的旧`.tmod.bak`，不修改已有连接配置、世界、玩家存档。非法`enabled.json`会使安装停止，不覆盖它。

默认brain监听`127.0.0.1:8080`，llama-server监听`127.0.0.1:8081`。启动器读取已有Mod配置的本地端口；不是预期loopback地址时拒绝启动，不自动改配置。历史别名`qwen3-4b`仅用于协议兼容，实际模型可能是14B或8B；不要根据别名判断参数量。启动器从服务`/props`核对真实模型文件，版本／模型不匹配时保留原服务并提示手动处理。

## 只读检查

```powershell
python scripts/setup_windows.py --check
python scripts/play_local.py --check
```

前者检查已有文件及完整性（9 GB模型哈希可能需要一些时间），后者要求服务已经就绪并检查安装、接口、brain代码和模型身份。两者都不下载、不安装、不启动／终止服务，也不打开游戏。未就绪时非零退出；这不是自动修复按钮。根目录「检查安装.bat」运行前者。

## 可选Wiki

无Wiki索引是受支持的基础安装状态，界面应显示资料不足。若已自行取得原版Wiki兼容离线快照，可使用：

```powershell
python scripts/import_wiki.py "D:\Wiki\snapshot.db" --output .runtime/wiki-v010-index.db --provenance "D:\Wiki\provenance.json"
```

先核对快照版本1.4.4.9以及来源、快照日期、rights字段；不要把不明版本的数据库改名冒充兼容。导入器文档和`--help`列出要求。灾厄可选联网导入：

```powershell
python scripts/import_calamity_wiki.py --source wikigg
```

当前Wiki页面可能包含更新的模组资料，不能把Terraria版本匹配视为所有灾厄机制均匹配。数据库和页面缓存保留在本机，不附带到Alpha准备包；导入后重启自己启动的本地服务。

## 常见问题

- **Python找不到／版本太旧**：安装3.11+ x64，重新打开终端；确认`python`或`py -3`可运行。脚本没有打包Python。
- **缺少模型**：重跑安装，或`python scripts/get_model.py --size 14b`；下载器不会把分片或损坏文件当成完整模型。
- **缺少DLL／CUDA启动失败**：看日志；确认兼容驱动，或用CPU runtime重新安装。CPU很慢是可能的，不承诺与GPU相同体验。
- **端口被占用／brain版本不匹配**：检查自己已打开的终端或启动器。脚本不会根据PID文件杀进程，也不会在`--check`时重启服务；先手动关闭已确认属于自己的旧服务。
- **服务模型或后端不匹配**：更改8B／14B或CPU／CUDA设置不会自动重启已运行的服务；手动退出自己的服务，再启动。`--cpu`影响新启动的服务，不会修改已在运行的服务。
- **没有Wiki数据库**：基础工具仍可用；攻略会缺少Wiki证据。不要将模型生成的资料当成已检索事实。
- **安装时文件占用**：关闭tModLoader；原安装保留。不要在游戏加载着旧模组时覆盖文件。
- **运行很卡**：记录实际模型、上下文、显存、推理时间和游戏帧率；可尝试8B或CPU诊断，但这不构成质量／性能保证。

许可和真实安装／游戏验收完成前，只在本机使用PRIVATE-PREP准备包。初次实机请用测试世界并遵循`docs/playtest.md`。
