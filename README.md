# AgentLink

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
- 已实现按 Iroh EndpointId 的显式节点信任、按 Agent 的显式共享及消息权限；尚未实现命令白名单、审计、自动重连和远程 Agent 执行。
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

注册后可使用 `agentlink_receive` 读取桌面端或远程 Agent 发来的消息，并使用 `agentlink_reply` 回复。`agentlink_list_agents` 查询本机 Agent 和获授权的远程 Agent；`agentlink_get_agent` 查询其能力和可达状态；`agentlink_set_sharing` 控制当前 Agent 是否共享；`agentlink_send` 按准确的 `agent_id` 和 `conversation_id` 投递远程消息。AgentLink 桌面端右键 Agent 卡片可进入该实例的独立聊天框或设置信息；消息和注册状态保存在当前用户应用数据目录的 `RayLink\\mcp` 下。MCP 会话会按客户端声明的名称记录 Provider，不限于 Codex；设置页可一键写入 Claude Code 的 `%USERPROFILE%\\.claude.json` 和 Cursor 的 `%USERPROFILE%\\.cursor\\mcp.json`，其他客户端可手动使用相同的 stdio 配置。

## 下一步开发目标：跨电脑 MCP Agent 发现、共享与通信

> 状态：第一阶段实现中。当前已实现本机 MCP Agent 目录、稳定身份、显式共享、节点信任确认、授权目录快照同步及指定 Agent 的可靠消息队列；尚未完成两台物理电脑上的联调，也未实现自动执行。

### 1. 当前能力与缺口

当前已具备本地 MCP Agent 注册、发现、收件、回复和 Iroh 文本通信。第一阶段已经增加：

- 按“节点 ID + 本地实例 ID”生成稳定的 Agent 身份。
- 经桌面端确认信任后同步显式共享的远程 Agent 目录。
- 面向指定远程 Agent 的消息路由、去重和送达回执。

远程消息到达后自动唤醒或调度 AI 执行任务仍未实现。

连接同一个 MCP 服务不等于所有 Agent 自动互相认识；AgentLink 需要提供业务层目录与发现工具。两台桌面端能够发送文本，也不等于两端 Agent 已能互相调用。

### 2. 推荐架构

采用“本机注册、双向共享目录、按 Agent 身份路由消息”的对等架构。

```text
你的电脑 A                                  同事电脑 B
Codex A1 / Claude A2                        Codex B1 / Claude B2
          │ 本地 MCP                                 │ 本地 MCP
          ▼                                          ▼
     AgentLink A          ←── Iroh 连接 ──→       AgentLink B
     · 本机 Agent 注册表                         · 本机 Agent 注册表
     · 远程 Agent 目录                           · 远程 Agent 目录
     · 共享权限与消息路由                        · 共享权限与消息路由
```

职责划分：

| 层级 | 职责 |
| --- | --- |
| 本地 MCP | Agent 注册、发现、发送、领取及回复消息 |
| AgentLink | 身份归属、共享授权、目录同步、消息路由与回执 |
| Iroh | 跨网络连接与加密传输 |

Agent 只向所在电脑注册。远程电脑保存的是该 Agent 的可访问目录记录，不创建第二套独立身份。注册、注销和资料变更以所属电脑为准。

双方不需要部署独立业务服务器，也不需要每个 Agent 单独配置对方的公网地址。不过“不自建服务器”不等于不依赖公网基础设施：无法直连时仍可能需要 Iroh Relay。

### 3. Agent 发现工具

以下为拟增加或扩展的 AgentLink 业务工具，不是 MCP 标准自带的 Agent 目录功能。

#### `agentlink_list_agents`

查询当前调用者获准访问的本机和远程 Agent，可按职位、能力、在线状态筛选。只返回授权范围内的信息，不默认公开工作目录、聊天记录或所有本机实例。

示例返回：

```json
{
  "agents": [
    {
      "agent_id": "node-b/agent-b1",
      "name": "后端开发助手",
      "provider": "Codex",
      "location": "remote",
      "device_name": "同事的电脑",
      "role": "后端工程师",
      "capabilities": ["代码分析", "编写测试"],
      "status": "available",
      "permissions": ["message"]
    }
  ]
}
```

#### `agentlink_get_agent`

查询指定 Agent 的详细职位、能力说明、可达状态和允许的操作。“擅长编写代码”只是能力描述，不意味着获准远程修改文件或执行命令。

#### `agentlink_send`

使用目标 `agent_id` 发送消息，由 AgentLink 解析所属电脑并转发；调用者不需要知道 IP、端口或 Iroh 地址。

```json
{
  "to_agent_id": "node-b/agent-b1",
  "conversation_id": "conversation-001",
  "text": "请帮我检查这段接口设计。"
}
```

MCP 使用说明应引导 Agent：需要协作时先查询可用 Agent，再按实际 ID 选择目标，不猜测身份。第一版主动查询即可；以后可在收件时附带上线、离线和资料更新事件，但不能把事件通知等同于自动运行 AI。

### 4. 双向共享与远程注册流程

用户侧建议将此功能命名为“共享 Agent”，底层采用授权目录同步，而不是要求远程 Agent 直接向另一台电脑重复注册。

1. 同事的 B1 调用本机 `agentlink_register`，在 AgentLink B 注册。
2. A、B 建立 Iroh 连接，首次连接确认对端身份并记录信任关系。
3. 同事选择共享给 A 的 Agent，以及允许查看资料、发送消息等权限。
4. B 把获准共享的目录记录同步到 A，A 标记其来源为电脑 B、类型为远程 Agent。
5. A 的角色列表展示 B1，A 的 Agent 也可通过发现工具查询 B1。
6. 你共享 A1 后，同事以相同方式发现和联系 A1；不需要另建服务端。

默认不共享，连接建立不应自动授予访问全部 Agent 的权限。查看资料、发送消息与提交执行任务分别授权。

不优先采用“每个 Agent 直接向所有远程电脑注册”，原因是会引入额外远程入口和凭据管理，增加多份注册状态、身份冲突及离线清理的复杂度。

### 5. 消息路由与回执

```text
A1 调用 agentlink_send（目标 B1）
  → AgentLink A 检查权限并解析目标节点
  → Iroh 转发
  → AgentLink B 再次检查权限并持久化到 B1 收件箱
  → B1 调用 agentlink_receive 领取
  → B1 调用 agentlink_reply 回复
  → 沿反方向路由回 A1 的会话
```

现有收件及回复工具需要扩展为能保留收发身份、会话及回复关联的形式，不能直接把当前单机收件箱视为跨节点路由实现。

建议消息字段：

| 字段 | 用途 |
| --- | --- |
| `message_id` | 全局唯一消息编号，用于重试去重 |
| `conversation_id` | 区分聊天会话 |
| `from_agent_id` / `to_agent_id` | 收发实例 |
| `reply_to` | 关联原消息 |
| `expires_at` | 防止过期请求在重连后意外执行 |

发送者身份由本地 MCP 会话及已验证的远程节点身份确定，不能相信正文中任意自报的身份。远程消息作为不可信用户输入处理，不能提升为系统指令。

回执区分：

- **已送达**：目标 AgentLink 已保存消息。
- **已领取**：目标 Agent 已取走消息。
- **已回复**：目标 Agent 已返回回复。

网络发送成功不等于任务执行完成。第一版建议目标离线时明确失败，不默认无限排队；对确认丢失的重试使用原消息 ID 去重，不能保证任意任务恰好执行一次。

### 6. 唯一身份、目录一致性与在线状态

| 问题 | 设计规则 |
| --- | --- |
| 双方都有名为 Codex 的实例 | 使用“节点 ID + 本地稳定实例 ID”，显示名称不作为主键 |
| 刷新或重连产生重复项 | 按唯一身份更新或插入，不盲目追加 |
| 远端冒充第三台电脑的 Agent | 只接受对端声明其自身所属 Agent；第一版不做第三方转发 |
| 首次连接或重连目录不一致 | 同步授权范围的完整快照，日常采用带版本的增量更新；版本缺口重新取快照 |
| 注销或取消共享 | 发布删除或撤销事件；目标节点每次收件仍重新检查权限 |
| 电脑断开但缓存 Agent 显示在线 | 立即将该节点的远程 Agent 标记为不可达，缓存不能代表在线 |
| MCP 服务还活着但 AI 未处理任务 | 将 MCP 会话存活、Agent 可领取任务及忙碌状态分开表达 |

在线状态至少区分电脑连接状态、MCP 会话状态、Agent 就绪状态和忙碌状态。不能只凭本地 MCP 服务进程的心跳判断 AI 正在工作。

### 7. 通信与自动执行分阶段实现

#### 第一阶段：Agent 通信

实现发现、共享、按目标发送、领取与回复，不承诺已有 AI 会话在消息到达后自动醒来。

#### 后续阶段：受控自动执行

若要实现“发送指令后同事的 Agent 自动工作”，另设计 Agent 执行适配层：

- 监听已获授权的执行任务，通过对应 Agent 支持的集成方式调度。
- 指定工作目录、允许的操作和审批规则。
- 支持任务 ID、执行状态、超时、取消及结果回传。
- 限制自动互聊轮数及资源消耗，防止循环调用。
- 不默认接管同事所有正在运行的会话；优先使用明确授权、由 AgentLink 管理的实例。
- 对已有 Codex、Claude Code 会话能否调度，分别验证，不预先承诺兼容所有会话。

### 8. 下一版开发范围与验收

实施顺序：

- [x] 增加本地 Agent 发现工具与统一身份定义。
- [x] 增加节点信任确认、按对端共享 Agent 和消息访问权限。
- [x] 通过 Iroh 双向同步授权目录，区分本机和远程 Agent。
- [x] 增加指定 Agent 的消息路由、收件、回复与送达回执。
- [x] 完成断线标记、重连快照同步、目录与消息去重。
- [ ] 完成两台电脑上的双向联调和权限测试。

验收要求：

1. 两台电脑各注册至少两个实际实例，彼此只能发现获准共享的 Agent。
2. A1 能给 B1 发消息并收到回复；B1 也能主动联系 A1，其他实例不串消息。
3. 连续刷新、重复同步与重连不会增加重复项，资料按所属节点更新。
4. 取消共享后对端无法继续发送；断线后不会错误显示远端可达。
5. 重试同一消息不会重复入队；已送达、已领取、已回复不混淆。
6. 无注册实例时不出现占位 Agent；MCP 心跳不被误报成任务执行状态。

第一版暂不包含多级转发、自动组队、任意远程命令执行和跨电脑文件同步。继续使用现有对等连接，不在本阶段扩展为多电脑全互联网络。

**目标效果：双方 Agent 只连接各自本机 MCP，两台 AgentLink 连接并授权共享后，就能互相发现并联系对方共享的 Agent。先使通信链路可靠，再增加自动执行。**
