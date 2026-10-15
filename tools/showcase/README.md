# README 展示图

`hamster_frames.py` 原样复制自 Hamster Archiver 的「README展示图工程/build.py」，
保留圆角、透明留白、柔和阴影和原始截图像素。`build.py` 将它适配到本项目。

截图来自编译后的原生程序嵌入页面和真实统计 API。测试宿主仅写入可复现的虚构用量，
不会连接 Mihomo，不读取用户账本。截图中的应用和网站用于展示，页面标有演示数据。

```powershell
.\build.ps1 -Test
# 在单独终端启动测试宿主，Ctrl+C 停止；也可向本地 /api/quit 发送认证请求。
.\dist\LedgerTests.exe --serve-fixture tests\live-demo
# 在另一个终端（Python 3，已安装 Pillow，Windows Chrome）：
python tests\browser_check.py tests\live-demo --showcase
python tools\showcase\build.py
```

输出位于 `assets/readme/`；原始截图和中间输出被 Git 忽略。
每次重建原生测试宿主后需要重启宿主，以保证截图使用最新资源。
