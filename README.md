# Rayfish-Agent / RayLink

RayLink 是一个基于 [Rayfish](https://github.com/rayfish/rayfish) 虚拟网络的跨网络桌面通信原型。Windows 安装包会同时安装 RayLink 桌面端和 Rayfish 服务；用户不需要单独配置 `ray.exe`。

> 当前版本是通信 MVP，不包含远程 Agent 指令执行。不要将它当作已经完成认证和授权的远程管理工具。

## 当前功能

- Avalonia 桌面界面（Windows 可用，应用项目也预留 macOS Runtime Identifier）
- 创建 Rayfish 网络、生成邀请、加入网络
- 自动查找并启动 Rayfish 服务
- 自动读取本机 Rayfish IPv6 地址
- 自动添加 Rayfish 入站 TCP 端口规则
- 通过 Rayfish IPv6 建立 TCP 双向文本通信
- JSON 行协议、20 秒应用心跳、60 秒接收超时和 TCP KeepAlive
- Windows x64 一体化安装器
- 安装、桌面/开始菜单快捷方式和卸载支持

## 仓库结构

```text
src/RayLink.App/                 RayLink Avalonia 桌面应用
installer/RayLink.Setup/         Windows 安装器
scripts/build-windows-installer.ps1
third-party/rayfish/             安装器内嵌的 Rayfish Windows MSI 与许可证
```

## 运行环境

开发环境需要：

- .NET 8 SDK
- Windows x64（构建一体化 Windows 安装器）

应用依赖的 Rayfish Windows 安装包已经放在：

```text
third-party/rayfish/ray-windows-x86_64.msi
```

其 SHA-256 为：

```text
279005CC1B8AC3D5254FC0F3ACD8CA5082EC06EFCF2D8CE7E44363F472B40C3A
```

对应开发时使用的 Rayfish 上游源码提交：

```text
971c95255e325e679ee248d5944d00c8417f5a77
```

## 开发运行

Windows PowerShell：

```powershell
.\scripts\run.ps1
```

或直接运行：

```powershell
dotnet restore .\src\RayLink.App\RayLink.App.csproj
dotnet run --project .\src\RayLink.App\RayLink.App.csproj
```

开发模式需要系统中已有可用的 Rayfish 安装。正式安装器会自动安装仓库内置的 Rayfish MSI。

## 构建 Windows 一体化安装包

```powershell
.\scripts\build-windows-installer.ps1
```

输出文件：

```text
artifacts/RayLink-Setup/RayLink.Setup.exe
```

该脚本会：

1. 将 RayLink 发布为 Windows x64 self-contained 单文件程序；
2. 将 RayLink 和 Rayfish MSI 嵌入安装器；
3. 生成一个可以直接分发的 `RayLink.Setup.exe`。

安装器需要管理员权限，用于安装 Rayfish Windows 服务、虚拟网络组件和执行 `ray up`。

## 使用流程

1. 在两台 Windows x64 电脑上安装 RayLink。
2. 第一台电脑创建网络并生成邀请。
3. 第二台电脑粘贴邀请并加入同一网络。
4. 第一台电脑点击“启动服务端模式”。
5. 第二台电脑填写第一台的 Rayfish IPv6 地址并连接。
6. 双方发送文本消息。

默认通信端口：`42821/TCP`。

## 当前限制与安全说明

- 当前只支持一个活动连接。
- 尚未实现设备列表和自动重连。
- 尚未实现应用层身份认证、权限控制、任务审计或 mTLS。
- 尚未实现远程 Agent 命令执行。
- 安装器和内嵌程序尚未进行商业代码签名，Windows SmartScreen 可能显示警告。
- Rayfish 本身处于实验阶段，不应直接用于关键生产环境。
- macOS 一体化 `.app` / `.dmg`、服务安装、签名和公证尚未完成。

在加入远程 Agent 执行前，应首先完成设备认证、明确授权、命令白名单、操作审计、任务取消和最小权限隔离。

## 第三方组件

Rayfish 来自其上游项目，使用 Mozilla Public License 2.0。详情见：

```text
THIRD_PARTY_NOTICES.md
third-party/rayfish/LICENSE
```

本仓库保留已有的根目录 `LICENSE`。
