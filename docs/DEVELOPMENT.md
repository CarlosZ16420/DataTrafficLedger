# 构建、测试与发布

运行：Windows 10/11 x64、.NET Framework 4.8。编译还需 .NET SDK 中的 Roslyn。
不需要 NuGet、Node、Docker、外部 SQLite 或网页打包器。C# 托盘和 HTTP 服务将 `web/` 作为资源嵌入 EXE。

```powershell
.\build.ps1 -Test
.\package.ps1 -SkipBuild
```

测试包含增量、重置、短连接缺口、HTTP Unicode 分块、SQLite 事务／保留策略、IPv4／IPv6／UDP、
回环排除、图标缓存、会话／Host／防伪校验、配置迁移、主题持久化和端口冲突回退。
`package.ps1` 默认先构建并测试，`-SkipBuild` 只供已验证的构建使用；生成 ZIP、SHA-256 和严格七文件清单（包含卸载入口）。
`releases/`、`dist/`、`app/`、`data/` 和测试运行数据不进入源码仓库。

英文产品名为 **Data Traffic Ledger**，程序为 `DataTrafficLedger.exe`；公有仓库为 `CarlosZ16420/DataTrafficLedger`，私有开发仓库为 `CarlosZ16420/data-traffic-ledger`。
为兼容旧版账本和 Windows 集成，非便携数据目录、互斥量、ETW 会话、自启动与采集任务及旧注册表项继续使用 `LocalTrafficLedger` 标识。
更换 EXE 文件名后需在设置中修复自启动路径，并重新授权已启用的 Windows 补充采集；卸载程序同时识别当前目录中的新旧 EXE。

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

## 全量汇总与分页的性能验证

运行 `build.ps1 -Test -OutputDirectory dist/paging-verified` 可在旧程序运行时构建独立副本。
原生测试验证超过 5,000 组的完整汇总、三种视图的分支、跨页一致性、排序、快照刷新／淘汰、读取取消和采集写入并行。

```powershell
.\dist\paging-verified\LedgerTests.exe --benchmark
.\dist\paging-verified\LedgerTests.exe --serve-paging-fixture tests\live-paging-large
# 另一个终端：
python tests\browser_check.py tests\live-paging-large --paging
```

基准使用虚构数据，每组 5 个分钟桶、100 个应用、10 个节点。2026-10-26 在本机单轮测得：

| 明细组合 / 分钟记录 | 原实现：统计并序列化 | 新实现：完整统计并序列化 | 首次网站页 | 后续网站页 |
| --- | ---: | ---: | ---: | ---: |
| 10,000 / 50,000 | 583 ms | 156 ms | 28 ms | <1 ms |
| 50,000 / 250,000 | 6,695 ms | 1,249 ms | 296 ms | <1 ms |
| 100,000 / 500,000 | 14,960 ms | 2,720 ms | 600 ms | 1 ms |

这些是服务端时间，首次页面时间需加上完整统计时间；不含浏览器布局和本机传输。原实现限制数值明细为 5,000 组，但仍传送全部路径元数据；10 万组时响应约 14.58 MB。新实现完整摘要与首个 30 项网站页合计 11,578 字节，趋势点数量和节点名称较多时响应会增大。后续页复用完整汇总与排序索引，不重新扫描分钟历史。

另以 100,010 组、501,200 条记录运行完整浏览器验收，汇总为 2,927 ms。浏览器 CPU 降速 4 倍时，页面初始化完成后的统计、分页和展开任务最长 163 ms；冷启动脚本任务最长 722 ms，单独记录，不计入数据操作指标。此测试覆盖各根页面的总量核对、分支翻页、延迟响应的加载条、过期响应保护、失败重试、8 分支／270 行上限与窄屏布局。耗时受硬件、缓存、筛选、历史长度影响，并非性能保证。

查询用独立只读连接，分钟事实先聚合再关联维度；中间结果保存在 SQLite 临时文件中。
每份快照最多缓存 48 个分支、25 万条汇总行，单个超大分支可独占缓存；不同排序共享汇总，按需建立排序索引。
SQLite 执行期间周期性检查请求取消并让出线程，不固定等待数秒。慢查询显示不定进度条，不显示虚构百分比。

## 发布顺序

1. 通过原生与浏览器检查，检查便携包及截图不含真实用户资料。
2. 将源码与公开文档提交并推送到私有开发仓库。
3. 从该私有提交导出公开文件快照，推送到独立公有仓库。不复制私有仓库的 Git 历史。
4. 在公有仓库发布版本与便携包，附同名 `.sha256`。

公开快照采用允许文件列表；用户数据库、运行凭据、日志、个人浏览器资料、旧版本和本地路径记录不会同步。
发行 ZIP 和包内文件名均为英文 ASCII，离线说明使用英文 README.txt；打包与便携测试同时校验这一要求。
GitHub workflow 支持手动验证和标签发布。维护时更新 `src/Server.cs` 的版本、README、CHANGELOG 与包版本。

`tools/prepare-public.ps1 -SourceRef origin/main` 从已提交版本的允许列表导出独立快照到被忽略的 `releases/`，
并拒绝运行数据库、凭据和日志类文件。导出前先完成私有仓库同步，公开端对快照单独提交。
`tests/portable_check.ps1` 验证中文／空格路径解压、启动、退出和目录搬迁；
`tests/startup_check.py data` 会切换已安装应用的自启动，并在结束时恢复原偏好。

`tests/uninstall_check.ps1` 从发行 ZIP 解压独立副本，以随机名称注册当前用户的普通权限测试任务；
通过 Windows PowerShell 5.1 运行正式卸载脚本，验证关闭运行实例、清理任务、保留或删除数据及程序自删除。
还会模拟权限错误和删除后任务仍存在的情况，验证程序和数据不会被提前删除，并检查外部路径／目录联接保护。
现有应用任务的定义和启用状态在测试前后必须一致；测试不创建高权限任务，也不实际卸载维护机的应用。
管理员授权交互仍需人工验收；脚本只允许同一 Windows 账户提权，拒绝静默模式自动弹出授权。

旧 `install.ps1` 可用于维护现有 `app/` 安装布局；新用户直接使用便携 ZIP。
若移动已授权 Windows 补充采集的程序，需重新在设置中授权，以更新其专用任务路径。
