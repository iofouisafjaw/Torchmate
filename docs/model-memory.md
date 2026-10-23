# 本机14B显存配置

本机可选配置位于 `.runtime/model-inference.json`。不随准备包分发，也不改变游戏中的18项Mod配置、模型文件或 `qwen3-4b` 兼容别名。

2026-10-20 当前选择：

```json
{
  "ctx": 8192,
  "cache_mb": 256,
  "batch_size": 512,
  "ubatch_size": 128,
  "kv_cache": "q8_0",
  "flash_attention": "on"
}
```

保留Qwen3-14B Q4_K_M及GPU权重卸载；上下文由12288降到8192，GPU KV由F16变为Q8，CUDA逻辑/物理批量由2048/512降到512/128。`cache_mb`限制的是主内存中的提示缓存，不能把它的降幅算作显存节省。

上下文容量减小，较长请求可能更早触及上限；KV量化可能改变少量输出数值，未重新声明原离线成功率。这次只复测连接和启动配置，没有进行游戏同时运行时的性能验收。

“启动模型.bat”和 `python scripts/play_local.py --service-only` 都会读取此文件。“开始游玩.bat”也通过同一启动器沿用设置，但会打开游戏。单纯修改文件不会改变已经运行的服务，需要重启已确认属于本项目的服务；启动器不会自动结束占用端口的其他程序。

命令行 `--ctx`、`--cache-mb`、`--batch-size`、`--ubatch-size`、`--kv-cache`、`--flash-attention` 可覆盖对应文件值。CPU路径保持F16 KV，不启用CUDA的Q8/Flash设置。

原配置和脚本已备份在 `.runtime/model-tuning/` 的时间目录中。若要恢复原启动内存参数，可将此配置文件改名后重启本项目服务；没有本地配置时仍使用原来的默认值。测量、生产客户端连接结果及当前配置见 `.runtime/model-tuning/report.json`。
