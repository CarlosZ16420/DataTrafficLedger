# 构建、测试与发布

运行：Windows 10/11 x64、.NET Framework 4.8。编译还需 .NET SDK 中的 Roslyn。
不需要 NuGet、Node、Docker、外部 SQLite 或网页打包器。C# 托盘和 HTTP 服务将 `web/` 作为资源嵌入 EXE。

```powershell
.\build.ps1 -Test
.\package.ps1 -SkipBuild
```

测试包含增量、重置、短连接缺口、HTTP Unicode 分块、SQLite 事务／保留策略、IPv4／IPv6／UDP、
回环排除、图标缓存、会话／Host／防伪校验、配置迁移、主题持久化和端口冲突回退。
`package.ps1` 默认先构建并测试，`-SkipBuild` 只供已验证的构建使用；生成 ZIP、SHA-256 和严格五文件清单。
`releases/`、`dist/`、`app/`、`data/` 和测试运行数据不进入源码仓库。

浏览器验收需要 Python 3 与 Chrome；对虚构数据进行验证：

```powershell
.\dist\LedgerTests.exe --serve-fixture tests\live-demo
# 另一个终端运行：
python tests\browser_check.py tests\live-demo --showcase
```

宿主不启动采集器；真实统计 API 从独立 SQLite 中读取虚构用量。浏览器使用隔离配置，
检查应用路径标签、原始域名、图标、排序、键盘读图、设置、CSV、主题、窄屏和外部请求。
还会验证日历全天／时分筛选、两次和连续点击选区、反向／同一分钟／无数据时段、取消和重置，
并将浏览器时区设为洛杉矶，验证筛选仍采用北京时间。GitHub Windows workflow 同样运行浏览器和便携搬迁检查。
展示图工程参见 [tools/showcase](../tools/showcase/README.md)，其 Pillow 依赖仅用于文档制图。
可用 `tests/runtime_report.py` 在维护机检查已安装实例；其结果只写入被忽略的本地文件。

## 发布顺序

1. 通过原生与浏览器检查，检查便携包及截图不含真实用户资料。
2. 将源码与公开文档提交并推送到私有开发仓库。
3. 从该私有提交导出公开文件快照，推送到独立公有仓库。不复制私有仓库的 Git 历史。
4. 在公有仓库发布版本与便携包，附同名 `.sha256`。

公开快照采用允许文件列表；用户数据库、运行凭据、日志、个人浏览器资料、旧版本和本地路径记录不会同步。
GitHub workflow 支持手动验证和标签发布。维护时更新 `src/Server.cs` 的版本、README、CHANGELOG 与包版本。

`tools/prepare-public.ps1 -SourceRef origin/main` 从已提交版本的允许列表导出独立快照到被忽略的 `releases/`，
并拒绝运行数据库、凭据和日志类文件。导出前先完成私有仓库同步，公开端对快照单独提交。
`tests/portable_check.ps1` 验证中文／空格路径解压、启动、退出和目录搬迁；
`tests/startup_check.py data` 会切换已安装应用的自启动，并在结束时恢复原偏好。

旧 `install.ps1` 可用于维护现有 `app/` 安装布局；新用户直接使用便携 ZIP。
若移动已授权 Windows 补充采集的程序，需重新在设置中授权，以更新其专用任务路径。
