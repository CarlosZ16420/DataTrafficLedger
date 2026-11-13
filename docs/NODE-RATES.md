# 节点目录与倍率

“节点与倍率”是独立页面，不受流量日期筛选影响。应用启动后自动从本机 Mihomo 读取 `/proxies` 和 `/providers/proxies`，之后约每分钟刷新；也可手动点击“读取节点”。只读当前已加载的配置和代理集合，不拉取额外订阅、不切换节点、不测速、不修改 Clash 设置。

目录包括从未产生流量的节点，排除 DIRECT、REJECT、PASS-RULE 等内置项、策略组及 Compatible provider 的合成副本。真正的 provider 保留来源；内联配置显示“当前配置 / 未指定来源”。读取失败保留目录，已经不再加载的节点保留为“当前未加载”。页面每页 30 项，支持名称／来源搜索，以及已加载、未知倍率和手动指定筛选。

## 倍率维护

优先级为：**手动指定 → 倍率文件 → 明确倍率元数据 → 名称标记 → 未知**。

- 支持 0–10000 倍，最多六位小数；0 表示免费节点。
- 可识别 `7.5x`、`x2`、`0.5倍`、`[倍率:3]` 等独立名称标记。不会把 IPv6、V1、端口或 200M 识别为倍率；冲突标记保留未知。
- 只有明确名为 `multiplier`、`trafficMultiplier` 或 `traffic-multiplier` 的数值元数据才参与识别。标准内核返回可能不保留机场自定义字段；读不到时使用文件或手动输入。
- 输入倍率并保存即锁定手动值，自动刷新和文件导入都不会覆盖。点击“恢复自动”回到文件或检测值；没有可用来源时显示未知，暂按 1 倍。
- 不同来源有同名节点且连接无法唯一对应时，按未知处理。第一版不做模糊改名匹配，不根据国家或地区推断倍率。

## 给用户或 AI 的倍率文件格式

先从页面“导出倍率”，让用户或 AI 填入 `multiplier`，保留 `id`、`name`、`provider`，再“导入倍率 → 预览匹配 → 确认导入”。这是一次性数据整理流程，应用不接管网站登录态。

也可以从零编写 UTF-8 JSON：

```json
{
  "version": 1,
  "source": "演示服务商倍率表",
  "nodes": [
    { "name": "演示节点 A", "multiplier": 3 },
    { "name": "演示节点 B", "provider": "演示订阅", "multiplier": 0.5 },
    { "name": "演示免费节点", "multiplier": 0 }
  ]
}
```

节点 ID 来源于“来源名称 + 节点名称”。有 `id` 时精确匹配标识，没有时按来源和名称匹配；只写名称时要求它唯一。导出的 `null` 表示未知，导入时不清除已有倍率。新建文件上限 1 MB、10000 项；名称和来源最多 300 字符，文件来源说明最多 120 字符。

预览区区分可更新、手动锁定、未找到、重名和无倍率项。默认不新增未找到的节点；勾选“同时添加未读取的节点”才会创建。目录或手动值在预览后发生变化时，要求重新预览。格式错误或重复匹配不会部分写入。导入不删除其他节点。

可给 AI 这样的任务：

> 根据我提供的服务商倍率表填写这份导出 JSON。保留节点的 id、name 和 provider，只填写能核实的 multiplier，缺失值保留 null。返回文件供我在流量账本预览导入。

网站名称与订阅名称可能带有不同的国旗或装饰，AI 应逐项核实对应关系并使用导出文件的精确名称；应用本身不自动去掉这些字符来猜测。

## 计量与旧版数据

“基础设置 → 自动计算节点倍率”默认开启。已归属代理流量按采样时倍率计算；直连、Clash 外和内核未归属差额不应用倍率。第一版上下行使用相同倍率，只计算连接的实际出口，不把策略组或代理链逐层相乘；不支持时段动态倍率和多节点分别计费。

原始字节始终保留，关闭设置只改变展示口径。新记录保存当时倍率，修改倍率不会改变旧账。旧版数据没有倍率快照时暂按 1 倍，并提示缺少倍率的原始字节量。估算值不能代替服务商账单。

节点目录独立保存在数据目录的 `nodes.json`；历史倍率快照属于 `traffic.db`。手动导入 1.6.1／1.6.2 数据时，先退出新旧实例并备份，再替换历史数据库，保留新版的 `nodes.json` 与 `settings.json`。不要用旧数据目录整体覆盖新版目录，也不要只复制仍在写入的数据库。具体备份原则见 [计量说明](MEASUREMENT.md)。

## 离线命令

命令只操作指定数据目录，需先退出使用该目录的实例；运行中请使用页面。命令出错时返回非零退出码，详情写入该目录 `error.log`。

```powershell
.\DataTrafficLedger.exe --headless --data "D:\Ledger\data" --refresh-nodes
.\DataTrafficLedger.exe --headless --data "D:\Ledger\data" --export-node-rates "D:\Ledger\node-rates.json"
.\DataTrafficLedger.exe --headless --data "D:\Ledger\data" --import-node-rates "D:\Ledger\node-rates.json"
```

额外传入 `--create-missing-nodes` 可添加文件中尚未读取到的节点。命令导入使用与页面相同的校验、匹配和手动值保护。个人倍率文件不随程序发行。

实现参考：[Mihomo API](https://wiki.metacubex.one/api/)、[provider API 序列化与 Compatible provider 源码](https://github.com/MetaCubeX/mihomo/blob/Meta/adapter/provider/provider.go)。
