use std::{
    path::PathBuf,
    str::FromStr,
    sync::Arc,
    time::Duration,
};

use chrono::Utc;
use data_encoding::HEXLOWER;
use iroh::{endpoint::presets, Endpoint, EndpointAddr, RelayMode, SecretKey};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader, BufWriter},
    sync::{mpsc, Mutex},
};
use tracing::warn;
use uuid::Uuid;

const ALPN: &[u8] = b"raylink/iroh/1";
const HEARTBEAT_INTERVAL: Duration = Duration::from_secs(20);
const MAX_MESSAGE_BYTES: usize = 1024 * 1024;

type Output = Arc<Mutex<BufWriter<tokio::io::Stdout>>>;
type SharedConnection = Arc<iroh::endpoint::Connection>;

#[derive(Debug, Clone, Serialize, Deserialize)]
struct WireMessage {
    #[serde(rename = "type")]
    message_type: String,
    sender: String,
    text: String,
    message_id: String,
    timestamp: String,
}

#[derive(Debug)]
enum Command {
    Start,
    Connect { endpoint_addr: EndpointAddr },
    Send { text: String },
    Disconnect,
    Shutdown,
}

#[derive(Debug)]
struct Args {
    identity_key: PathBuf,
    display_name: String,
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    tracing_subscriber::fmt()
        .with_env_filter("raylink_iroh_transport=info")
        .with_writer(std::io::stderr)
        .init();

    let args = Args::parse(std::env::args().skip(1));
    let output: Output = Arc::new(Mutex::new(BufWriter::new(tokio::io::stdout())));
    let secret_key = load_or_create_key(&args.identity_key)?;

    let endpoint = Endpoint::builder(presets::N0)
        .secret_key(secret_key)
        .alpns(vec![ALPN.to_vec()])
        .relay_mode(RelayMode::Default)
        .bind()
        .await?;
    let endpoint = Arc::new(endpoint);

    endpoint.online().await;
    emit(
        &output,
        json!({
            "type": "ready",
            "endpoint_id": endpoint.id().to_string(),
            "endpoint_addr": endpoint.addr(),
            "identity_key": args.identity_key,
        }),
    )
    .await?;

    let current_connection: Arc<Mutex<Option<SharedConnection>>> = Arc::new(Mutex::new(None));

    let (command_tx, mut command_rx) = mpsc::channel::<Command>(32);
    let endpoint_for_input = endpoint.clone();
    let output_for_input = output.clone();
    tokio::spawn(async move {
        if let Err(err) = read_commands(command_tx).await {
            let _ = emit(
                &output_for_input,
                json!({ "type": "error", "message": format!("控制通道已关闭：{err}") }),
            )
            .await;
        }
        endpoint_for_input.close().await;
    });

    let accept_task = tokio::spawn(accept_loop(
        endpoint.clone(),
        output.clone(),
        current_connection.clone(),
        args.display_name.clone(),
    ));
    let heartbeat_task = tokio::spawn(heartbeat_loop(
        current_connection.clone(),
        output.clone(),
        args.display_name.clone(),
    ));

    while let Some(command) = command_rx.recv().await {
        match command {
            Command::Start => {
                emit(
                    &output,
                    json!({ "type": "status", "message": "Iroh 服务已启动，正在等待远程连接。" }),
                )
                .await?;
            }
            Command::Connect { endpoint_addr } => {
                let endpoint = endpoint.clone();
                let output = output.clone();
                let current = current_connection.clone();
                let display_name = args.display_name.clone();
                tokio::spawn(async move {
                    match endpoint.connect(endpoint_addr, ALPN).await {
                        Ok(connection) => {
                            let connection = Arc::new(connection);
                            let remote_id = connection.remote_id();
                            replace_connection(&current, connection.clone()).await;
                            let _ = emit(
                                &output,
                                json!({
                                    "type": "connected",
                                    "remote_id": remote_id.to_string(),
                                    "message": "已通过 Iroh 建立连接。",
                                }),
                            )
                            .await;
                            spawn_receive_loop(connection, output, current, display_name).await;
                        }
                        Err(err) => {
                            let _ = emit(
                                &output,
                                json!({ "type": "error", "message": format!("Iroh 连接失败：{err}") }),
                            )
                            .await;
                        }
                    }
                });
            }
            Command::Send { text } => {
                let connection = current_connection.lock().await.clone();
                if let Some(connection) = connection {
                    let message = WireMessage {
                        message_type: "chat".to_string(),
                        sender: args.display_name.clone(),
                        text,
                        message_id: Uuid::new_v4().to_string(),
                        timestamp: Utc::now().to_rfc3339(),
                    };
                    if let Err(err) = send_message(&connection, &message).await {
                        emit(
                            &output,
                            json!({ "type": "error", "message": format!("发送失败：{err}") }),
                        )
                        .await?;
                    }
                } else {
                    emit(
                        &output,
                        json!({ "type": "error", "message": "当前没有可用的 Iroh 连接。" }),
                    )
                    .await?;
                }
            }
            Command::Disconnect => {
                if let Some(connection) = current_connection.lock().await.take() {
                    connection.close(0u32.into(), b"user disconnected");
                }
                emit(
                    &output,
                    json!({ "type": "disconnected", "message": "连接已断开。" }),
                )
                .await?;
            }
            Command::Shutdown => break,
        }
    }

    heartbeat_task.abort();
    accept_task.abort();
    endpoint.close().await;
    Ok(())
}

impl Args {
    fn parse<I: IntoIterator<Item = String>>(args: I) -> Self {
        let mut identity_key = default_identity_path();
        let mut display_name = "RayLink".to_string();
        let mut iter = args.into_iter();
        while let Some(arg) = iter.next() {
            match arg.as_str() {
                "--identity-key" => {
                    if let Some(value) = iter.next() {
                        identity_key = PathBuf::from(value);
                    }
                }
                "--display-name" => {
                    if let Some(value) = iter.next() {
                        display_name = value;
                    }
                }
                _ => {}
            }
        }
        Self {
            identity_key,
            display_name,
        }
    }
}

async fn read_commands(
    tx: mpsc::Sender<Command>,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let stdin = BufReader::new(tokio::io::stdin());
    let mut lines = stdin.lines();
    while let Some(line) = lines.next_line().await? {
        let value: Value = match serde_json::from_str(&line) {
            Ok(value) => value,
            Err(err) => {
                warn!("invalid command: {err}");
                continue;
            }
        };
        let command = match value.get("type").and_then(Value::as_str) {
            Some("start") => Command::Start,
            Some("connect") => {
                let raw = value
                    .get("endpoint_addr")
                    .cloned()
                    .ok_or("missing endpoint_addr")?;
                Command::Connect {
                    endpoint_addr: serde_json::from_value(raw)?,
                }
            }
            Some("send") => Command::Send {
                text: value
                    .get("text")
                    .and_then(Value::as_str)
                    .unwrap_or_default()
                    .to_string(),
            },
            Some("disconnect") => Command::Disconnect,
            Some("shutdown") => {
                tx.send(Command::Shutdown).await?;
                break;
            }
            _ => continue,
        };
        tx.send(command).await?;
    }
    Ok(())
}

async fn accept_loop(
    endpoint: Arc<Endpoint>,
    output: Output,
    current: Arc<Mutex<Option<SharedConnection>>>,
    display_name: String,
) {
    while let Some(incoming) = endpoint.accept().await {
        let output = output.clone();
        let current = current.clone();
        let display_name = display_name.clone();
        tokio::spawn(async move {
            let accepting = match incoming.accept() {
                Ok(value) => value,
                Err(err) => {
                    let _ = emit(
                        &output,
                        json!({ "type": "error", "message": format!("接受 Iroh 连接失败：{err}") }),
                    )
                    .await;
                    return;
                }
            };
            match accepting.await {
                Ok(connection) => {
                    let connection = Arc::new(connection);
                    let remote_id = connection.remote_id();
                    replace_connection(&current, connection.clone()).await;
                    let _ = emit(
                        &output,
                        json!({
                            "type": "connected",
                            "remote_id": remote_id.to_string(),
                            "message": "远程节点已通过 Iroh 连接。",
                        }),
                    )
                    .await;
                    spawn_receive_loop(connection, output, current, display_name).await;
                }
                Err(err) => {
                    let _ = emit(
                        &output,
                        json!({ "type": "error", "message": format!("Iroh 握手失败：{err}") }),
                    )
                    .await;
                }
            }
        });
    }
}

async fn spawn_receive_loop(
    connection: SharedConnection,
    output: Output,
    current: Arc<Mutex<Option<SharedConnection>>>,
    _display_name: String,
) {
    tokio::spawn(async move {
        loop {
            let (_send, mut recv) = match connection.accept_bi().await {
                Ok(streams) => streams,
                Err(err) => {
                    clear_if_current(&current, &connection).await;
                    let _ = emit(
                        &output,
                        json!({ "type": "disconnected", "message": format!("Iroh 连接已关闭：{err}") }),
                    )
                    .await;
                    break;
                }
            };
            let bytes = match recv.read_to_end(MAX_MESSAGE_BYTES).await {
                Ok(data) => data,
                Err(err) => {
                    let _ = emit(
                        &output,
                        json!({ "type": "error", "message": format!("读取 Iroh 消息失败：{err}") }),
                    )
                    .await;
                    continue;
                }
            };
            let line = String::from_utf8_lossy(&bytes);
            match serde_json::from_str::<WireMessage>(line.trim()) {
                Ok(message) if message.message_type != "heartbeat" => {
                    let _ = emit(
                        &output,
                        json!({
                            "type": "message",
                            "sender": message.sender,
                            "text": message.text,
                            "message_id": message.message_id,
                            "timestamp": message.timestamp,
                        }),
                    )
                    .await;
                }
                Ok(_) => {}
                Err(err) => {
                    let _ = emit(
                        &output,
                        json!({ "type": "error", "message": format!("忽略无法解析的消息：{err}") }),
                    )
                    .await;
                }
            }
        }
    });
}

async fn heartbeat_loop(
    current: Arc<Mutex<Option<SharedConnection>>>,
    output: Output,
    display_name: String,
) {
    let mut interval = tokio::time::interval(HEARTBEAT_INTERVAL);
    interval.tick().await;
    loop {
        interval.tick().await;
        if let Some(connection) = current.lock().await.clone() {
            let message = WireMessage {
                message_type: "heartbeat".to_string(),
                sender: display_name.clone(),
                text: String::new(),
                message_id: Uuid::new_v4().to_string(),
                timestamp: Utc::now().to_rfc3339(),
            };
            if let Err(err) = send_message(&connection, &message).await {
                let _ = emit(
                    &output,
                    json!({ "type": "status", "message": format!("Iroh 心跳发送失败：{err}") }),
                )
                .await;
            }
        }
    }
}

async fn send_message(
    connection: &iroh::endpoint::Connection,
    message: &WireMessage,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let (mut send, _recv) = connection.open_bi().await?;
    let bytes = serde_json::to_vec(message)?;
    send.write_all(&bytes).await?;
    send.finish()?;
    Ok(())
}

async fn replace_connection(
    current: &Arc<Mutex<Option<SharedConnection>>>,
    connection: SharedConnection,
) {
    if let Some(previous) = current.lock().await.replace(connection) {
        previous.close(0u32.into(), b"replaced");
    }
}

async fn clear_if_current(
    current: &Arc<Mutex<Option<SharedConnection>>>,
    connection: &SharedConnection,
) {
    let mut guard = current.lock().await;
    if guard.as_ref().is_some_and(|current| Arc::ptr_eq(current, connection)) {
        *guard = None;
    }
}

async fn emit(
    output: &Output,
    value: Value,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let mut output = output.lock().await;
    output
        .write_all(serde_json::to_string(&value)?.as_bytes())
        .await?;
    output.write_all(b"\n").await?;
    output.flush().await?;
    Ok(())
}

fn load_or_create_key(
    path: &PathBuf,
) -> Result<SecretKey, Box<dyn std::error::Error + Send + Sync>> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    if path.exists() {
        let raw = std::fs::read_to_string(path)?;
        return Ok(SecretKey::from_str(raw.trim())?);
    }
    let key = SecretKey::generate();
    std::fs::write(path, HEXLOWER.encode(&key.to_bytes()))?;
    Ok(key)
}

fn default_identity_path() -> PathBuf {
    if let Some(app_data) = std::env::var_os("APPDATA") {
        return PathBuf::from(app_data)
            .join("RayLink")
            .join("iroh-secret-key");
    }
    if let Some(home) = std::env::var_os("HOME") {
        return PathBuf::from(home)
            .join(".config")
            .join("raylink")
            .join("iroh-secret-key");
    }
    PathBuf::from("iroh-secret-key")
}
