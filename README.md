# Quest3TriggerUI

为 **Virt-A-Mate 1.22.0.12** 在 **Quest 3（及 OpenVR/PCVR）** 下提供完整 VR 原生交互的 BepInEx 插件：VR 虚拟键盘 + 拼音输入法、径向快捷菜单、VR 预设/文件浏览器、原生对话框接管、包管理器封禁与卡顿缓解、物理降载档位与头发手动调试等。

## 功能一览

- **VR 虚拟键盘**：右手食指扳机+握把组合键呼出；世界空间 UI，可直接在 VR 里打字
- **离线中文拼音输入法**：rime-ice/雾凇拼音词典本地化——全拼、首字母简拼、模糊/前缀联想、词组补全、分次提交整句、**用户词频记忆**（`[IME] Learning` 开关）
- **径向快捷菜单**：右手食指长按呼出，物理降载/待机/重置等快捷动作
- **VR 预设浏览器**：接管 VaM 原生皮肤/外观/头发/服装/替换等全部文件对话框，var 包内容合并视图、标签页持久化、启动预热防首开卡顿
- **预设部件化**：服装/皮肤/头发/眼睛四区块独立存取——人物预设可当部件库直接"拆用"（只换发型/只换肤/只换装/只换眼），当前部件也可写回任意本地人物预设；含 VaM 原生没有的眼睛预设系统（irises/sclera/lacrimals + 原生缩略图）
- **VR 拍照机位**：多机位 VR 相机拍摄，缩略图、场景持久化、前后跳切
- **音频跟随**：VaM VR 音频自动跟随 Windows 默认输出设备切换，无需手动改路由
- **场景加载加速**：headless 菜单期预热（形变库/编目初始化 + 原子克隆池），加载窗口内选择器重建合并，大场景冷载 ~190s→~110-130s
- **包管理器封禁**：默认屏蔽原生包管理器入口（VaM 包扫描+缩略图解码是主要卡顿源），`[Browser] BlockPackageManager` 可关
- **物理降载档位 + 手动头发调试**：快捷降载，或逐片选中头发实时调 curl/density/模拟/碰撞
- **热重载架构**：HotLoader 常驻 + payload 字节加载，替换 payload 文件即热更新，无需重启 VaM

## 目录结构

```
src/          插件全部 C# 源码与发布/测试脚本（plugin_sources）
docs/         使用说明.md、安装说明.txt
tools/ime/    拼音词典转换脚本 build_dict.py、引擎独立测试 EngineTest.cs
tools/build/  编译响应文件样例、运行快照更新脚本
```

## 构建

需要本机 VaM 1.22.0.12 安装（引用 `VaM_Data\Managed` 下的 Assembly-CSharp / UnityEngine 等）与 BepInEx 5.x 引用集。参照 `tools/build/build.example.rsp` 把 `F:\vam1.22.0.12` 路径替换为本机路径，然后：

```
csc.exe -noconfig @build.rsp
```

产出 payload dll 用 `src/PUBLISH_HOT_UPDATE.ps1` 发布到
`BepInEx\plugins\Quest3TriggerUI\Quest3TriggerUI.payload.dll.disabled` 即热载生效。

> ⚠️ **热载约定**：每次构建的 `/out` 程序集名必须唯一（如 `Quest3TriggerUI_pHHMMSS.dll`）。同名程序集字节加载时，方法内对其他类型的引用会绑定到首个同名程序集，导致"改了代码运行无反应"。

## 输入法词典

`pinyin_*.txt`（约 50MB）由 rime-ice/雾凇拼音开源词典经 `tools/ime/build_dict.py` 转换生成，**不入库**——运行发布包中已含，或自行转换。

## 安装

见 `docs/安装说明.txt`。发布包（完整包 + 增量补丁）见 GitHub **Releases** 页附件。

## 依赖声明

- BepInEx 5.x、UnityEngine/VaM 托管程序集：仅编译期引用，**不包含在仓库/发布包中**
- 拼音词典数据源自 [rime-ice 雾凇拼音](https://github.com/iDvel/rime-ice)（开源）
- 本仓库不含任何 VaM 游戏文件与第三方插件二进制
