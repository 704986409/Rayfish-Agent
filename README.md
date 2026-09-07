# Rayfish-Agent / AgentLink

AgentLink 是一个基于 Iroh 的跨网络桌面通信原型。当前版本已经移除 Rayfish 虚拟网卡、虚拟 IPv6、Rayfish CLI 和 MSI，改为将 Iroh 原生 Rust 通信组件嵌入桌面端。

> 当前版本是通信 MVP：支持两台电脑之间通过 Iroh 双向发送/接收文本，不包含远程 Agent 指令执行、设备管理和权限系统。

## 当前功能

- Avalonia 桌面界面（Windows；项目保留 macOS Runtime Identifier）
- 内置 Iroh Endpoint 身份，并持久化本机 SecretKey
- 生成并展示本机 EndpointId / EndpointAddr
- 通过完整 EndpointAddr JSON 连接远程节点
- Iroh QUIC 双向流文本通信
- Iroh 默认 Relay + NAT 穿透，不要求双方处于同一局域网
- 20 秒应用层心跳；连接由 QUIC 负责可靠、有序传输和保活
- Windows x64 一体化安装器，将 Iroh bridge 与 AgentLink 一并部署

## 架构

    AgentLink.exe
        ↓ stdin/stdout newline-delimited JSON
    AgentLink.Transport.exe（Rust bridge）
        ↓ Iroh 1.1.0
    QUIC / UDP / NAT traversal / Relay
        ↓
    remote Iroh Endpoint

Iroh 不是虚拟网络服务，不会创建 Rayfish 那样的虚拟网卡或 IPv6 地址；每台设备使用自己的 EndpointId 公钥身份。

## 仓库结构

- src/AgentLink.App/：AgentLink Avalonia 桌面应用
- native/iroh-transport/：Iroh Rust 原生桥接程序
- installer/AgentLink.Setup/：Windows 安装器
- scripts/build-windows-installer.ps1：构建 Windows 安装包
- third-party/iroh/：Iroh 许可证说明

## 开发环境

- .NET 8 SDK
- Rust 1.91 或更高版本（Iroh 1.1.0 当前要求）
- Windows x64；macOS 原生构建需要对应 Rust target

启动桌面应用：

    .\scripts\run.ps1

Rust bridge 开发构建：

    cargo build --manifest-path native\iroh-transport\Cargo.toml

## 构建 Windows 一体化安装包

    .\scripts\build-windows-installer.ps1

输出：artifacts/AgentLink-Setup/AgentLink.Setup.exe

脚本会：

1. 使用固定的 Iroh 上游提交构建 Rust bridge；
2. 发布 AgentLink self-contained 单文件程序；
3. 将 bridge 嵌入安装器，并生成可直接分发的安装包。

安装器不再调用 msiexec，不再安装 Rayfish 服务，也不再创建虚拟网卡。

## 使用流程

1. 两台电脑安装同一个 AgentLink 版本。
2. 两边打开 AgentLink，点击“启动 Iroh 服务”。
3. 将本机 EndpointAddr JSON 复制给对方。
4. 对方粘贴到“远程 EndpointAddr JSON”并点击“连接”。
5. 双方发送和接收文本消息。

## 安全与限制

- 当前只支持一个活动连接。
- EndpointAddr 包含可分享的节点 ID、Relay 和直接地址；SecretKey 只保存在本机 AppData，不会展示在 UI。
- 尚未实现设备认证白名单、授权、命令白名单、审计、自动重连和远程 Agent 执行。
- Rust 组件尚未在本机编译验证：构建机必须安装 Rust 1.91+。
- 安装器尚未进行商业代码签名，Windows SmartScreen 可能提示风险。

## 第三方组件

Iroh 使用 MIT OR Apache-2.0，详见 THIRD_PARTY_NOTICES.md 和 third-party/iroh/。

## MCP Agent 注册（本地）

AgentLink 内置 MCP stdio 服务。启动 `AgentLink.exe --mcp` 后，将该命令配置到 Codex、Claude Code 或其他支持 MCP 的客户端；客户端连接后必须调用 `agentlink_register` 注册实际 Agent 实例，角色设定页的“刷新”只显示仍在心跳租约内的注册实例，不再扫描本机进程。

示例 MCP 配置（将路径替换为实际安装路径）：

```json
{
  "mcpServers": {
    "agentlink": {
      "command": "C:\\Program Files\\AgentLink\\AgentLink.exe",
      "args": ["--mcp"]
    }
  }
}
```

注册后可使用 `agentlink_receive` 读取桌面端发来的消息，并使用 `agentlink_reply` 回复。AgentLink 桌面端右键 Agent 卡片可进入该实例的独立聊天框或设置信息；消息和注册状态保存在当前用户应用数据目录的 `RayLink\\mcp` 下。
