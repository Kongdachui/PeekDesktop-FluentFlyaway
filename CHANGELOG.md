# Changelog

## v0.1.0-fluent — FluentFlyaway 初始版本

基于 [shanselman/PeekDesktop](https://github.com/shanselman/PeekDesktop) 的 fork，专注于改善 Fly Away 动画体验。

### ✨ 新特性

- **流畅动画**：动画参数从 12 步/160ms 调整为 30 步/200ms，配合 `PumpingSleep` 消息泵送机制，动画期间 UI 保持响应
- **点击打断**：飞走动画进行中再次点击桌面，窗口立即反向飞回；飞回过程中点击则再次飞走。多次连续点击均有响应
- **鼠标不锁定**：动画期间鼠标可正常移动和点击，不再被 `Thread.Sleep` 阻塞消息循环
- **最大化窗口动画**：最大化窗口参与飞走/飞回动画流程，un-maximize 的尺寸变化在单帧内完成（~6ms），视觉上无感知

### 🔧 修复

- **最大化窗口视觉 glitch**：修复了拖到屏幕顶部最大化的窗口在飞走时"先缩小再飞"的问题
- **Z 序错乱**：修复了多个最大化窗口同时飞走/恢复时，顶层窗口顺序错乱的问题
- **响应延迟**：焦点/点击宽限期从 200ms/300ms 缩短至 50ms，操作更跟手

### 📦 体积优化

- 启用 `PublishTrimmed`（IL Trimming），自包含单文件从 ~71MB 降至 ~13MB
- 保留 Native AOT 配置，CI 构建可进一步压缩至 ~2MB

### ⚙️ 其他

- 禁用了启动时自动更新检查
- 仓库地址更新为 `Kongdachui/PeekDesktop-FluentFlyaway`
- 新增 `PumpingSleep` 方法：通过 `PeekMessage`/`TranslateMessage`/`DispatchMessage` 在动画等待期间泵送 Windows 消息
- 新增 `WindowTracker.CancelAnimation` 标志，支持动画中途取消
- 新增 `DesktopPeek._reversalRequested` 状态，支持动画方向反转
- 新增 `NativeMethods.MSG` 结构体及消息函数 P/Invoke 声明
