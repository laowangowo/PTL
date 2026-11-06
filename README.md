# Pain Thunder Launcher（PTL）

Pain Thunder Launcher（简称 PTL）是基于 PCL 界面框架制作的战争雷霆启动器，提供游戏启动、内置更新、玩家资料、战绩查询、瞄具与声音包管理，以及工具下载。

## 项目结构

```text
PainThunderLauncher/
├─ PTL/                         # 启动器主项目
│  ├─ Controls/                 # PTL 自定义 WPF 控件
│  ├─ Images/                   # 主题、玩家头像和应用图标
│  ├─ Modules/                  # 启动、更新、下载和业务模块
│  │  ├─ Base/                  # 动画、网络、日志等基础模块
│  │  └─ ModWarThunder*.vb      # 战争雷霆启动、更新与资源逻辑
│  ├─ Pages/
│  │  ├─ PageDownload/          # 瞄具、涂装、音效和工具下载页
│  │  ├─ PageLaunch/            # 主页、玩家、战绩和组件管理页
│  │  ├─ PageSetup/             # 个性化、启动器和关于设置页
│  │  ├─ PageSpeedLeft.xaml     # 下载任务列表
│  │  └─ PageSpeedRight.xaml    # 下载任务详情
│  ├─ Resources/                # 运行时资源、第三方 DLL 和字体
│  ├─ My Project/               # 程序集、资源和应用配置
│  ├─ Application.xaml          # WPF 应用入口
│  ├─ FormMain.xaml             # 主窗口与页面导航
│  └─ PTL.vbproj                # 启动器项目文件
├─ MeloongCore/                 # UI、文件和注册表基础组件（Git 子模块）
│  ├─ Shared/                   # .NET Standard 公共基础库
│  └─ Wpf/                      # WPF 专用基础库
├─ WarThunderFsbExtractor/      # 战雷音乐 FSB 提取工具
├─ WTLiveAPI/                   # WT Live 与战雷接口文档
├─ global.json                  # .NET SDK 版本约束
├─ PTL.sln                      # Visual Studio 解决方案
└─ README.md                    # 项目入口说明
```

战雷更新相关源码：`PainThunderLauncher/PTL/Modules/ModWarThunderSelfUpdate.vb`

## 许可声明
- 本项目虽参考了较大量的内容，但制作的软件与 PCL 的实质功能不存在任何重叠，故属于轻度使用。
- 本项目采用 **Non-Commercial, No-Paid-Content License with Attribution Requirement**。

允许免费分发与非商业使用；禁止一切商业使用；禁止将本项目用于任何包含付费内容、付费门槛或收费安排的使用场景；修改或参考本项目应保留适当署名。详见 [LICENSE](./LICENSE)。