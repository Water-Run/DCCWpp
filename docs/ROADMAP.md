# DCCW++ 开发计划

## 定位与独立性

DCCW++ 是独立的 Windows 桌面应用。基于公开 Windows 接口实现显示器发现、ICC 配置关联和用户登录启动，不依赖原版 dccw 程序、RunOnce 或 Clash#。这说明独立实现具有技术路径；不代表已经实现系统级校准，也不代表所有显示硬件支持相同能力。

长期标准：在交互、可靠性、无障碍、资源使用和安装更新方面，达到适合作为 Windows 11 原生颜色校准工具的水平。不会通过修改系统文件替换 dccw。

## 确定的产品要求

- 名称为 **DCCW++**；Windows 11，WinUI 3；Microsoft Store；GPL。
- 用户登录后启动，恢复用户明确选定的显示器预设；处理唤醒、显示器重连和拓扑变化。
- 国际化参考 Clash#，从简体中文、繁体中文、英语开始，资源结构支持继续增加语言。
- 本地预设和他人预设的导入导出；共享预设不携带本机显示器绑定或可执行行为。
- CLI 可由 Agent 调用，稳定 JSON、退出码、预演、状态查询与可恢复应用事务。
- 校准用摄影作品由项目所有者提供。基础测试图使用可控的数学图案。
- 图标保留原版“显示器＋彩色立方体＋校准卡片”的识别关系，采用 Windows 11 的圆角、柔和层次和材质。

## 交互改进

1. **先选屏，再调整。** 明确显示器名称、位置、当前模式及生效范围；多屏不会误改。
2. **步骤可自由返回。** 准备、明暗、Gamma、灰阶平衡、验证、保存；保持已输入内容，不用从头重走向导。
3. **基础与精细调整共用一套数据。** 有视觉提示，也有数值输入、键盘微调、单项重置。
4. **连续预览与原始对比。** 按住查看原效果；中性校准画布与彩色操作区分开，避免周边颜色干扰判断。
5. **有时间限制的试用。** 未确认、进程退出、显示器消失时恢复原状态；可用键盘紧急恢复。
6. **结果明确。** 区分草稿、预览中、已保存、已绑定、已验证生效、恢复失败，不以“函数成功”替代效果确认。
7. **完整原生体验。** 参考 WinUI Gallery 的 NavigationView、NumberBox、InfoBar、ContentDialog；支持系统主题、高对比度、键盘、Narrator、缩放及窄窗口。

## 里程碑

### M0 — 基础框架（本轮）

独立工程、三语言、预设读写、只读显示器查询、CLI、WinUI 窗口、SVG/PNG/ICO、MSIX 构建。校准接口明确返回未实现状态。

### M1 — 校准可行性验证

- 用 QueryDisplayConfig 获取物理目标、适配器、路径、HDR/Advanced Color 状态，验证克隆模式和同型号多屏。
- 查询并保存当前显示器的 ICC 关联和校准状态，先完成可验证的恢复能力。
- 验证 ICC/WCS 路径及校准曲线加载方式，记录 Windows、驱动、显示器组合的实际行为。
- Gamma 数值的语义、现有 profile 的保留/组合、vcgt 支持、色彩管理应用与非色彩管理应用的区别必须在此阶段确定。
- 不凭肉眼结果虚构实测色域、白点或专业测量精度；共享数值不能保证不同屏幕显示一致。

验收：单台 SDR 显示器上能可靠预览、确认和恢复；崩溃后恢复通过验证；HDR 环境不能误走 SDR 路径。

### M2 — 完整校准流程

测试图、用户摄影素材、每项重置、数值微调、对比、事务确认、当前显示器的预设绑定。引入独立恢复进程或等效可靠机制，解决主进程崩溃后的回退。

验收：整套流程可用键盘完成；缩放 100/150/200%、浅色/深色/高对比度、两种以上显示器组合通过人工验收。

### M3 — 持久应用

MSIX StartupTask 在登录后恢复；监听显示器/电源/会话事件，合并重复事件并有限重试。状态持久化、诊断日志、用户暂停恢复。尊重用户在系统中禁用启动的决定；不无休止覆盖夜间模式、显卡控制面板或其他校准软件的改变。

验收：重启、睡眠唤醒、拔插、换端口、休眠、远程会话、驱动重置、升级、卸载都能解释并验证最终状态。应用不会承诺控制所有外部进程。

### M4 — Agent 与预设生态

在现有 CLI 上加入事务式 `preview` / `confirm` / `revert`、绑定、诊断和幂等请求 ID。超时/崩溃恢复同 GUI；无交互时不能永久停留在危险的临时效果。协议字段不随 UI 语言改变。预设升级、重复导入、描述/作者、兼容提示和迁移测试。

### M5 — Microsoft Store 发布

专属 Partner Center 身份、签名/提交包、WACK、标准用户安装升级卸载、x64/ARM64 真机验证、截图、隐私政策、GPL 对应源码归档、依赖许可清单和明确的能力描述。正式版本前不把草稿参数编辑宣传为已完成的颜色校准。

## 技术依据

- [微软 SetDeviceGammaRamp 文档](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp)：存在覆盖、静默失败和 HDR 限制；建议 ICC profile 路径。
- [ColorProfileSetDisplayDefaultAssociation](https://learn.microsoft.com/en-us/windows/win32/api/icm/nf-icm-colorprofilesetdisplaydefaultassociation)：公开的显示器 profile 默认关联接口。
- [StartupTask](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask)：MSIX 桌面应用登录启动与用户禁用状态。
- [WinUI Gallery](https://github.com/microsoft/WinUI-Gallery)：采用原生控件与交互习惯；避免为了外观引用实验性 SDK。
- [MSIX 单项目打包](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/single-project-msix)。

调查日期：2026-10-23。当前脚手架固定 Windows App SDK 1.8.260317003 和 .NET 10，升级依赖应随构建、打包、启动验证一起进行。
