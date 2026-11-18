# 邮件提醒助手中转服务

运行环境为 Node.js 22 或更新版本，使用 Node 自带模块。服务器只监听本机，HTTPS 入口由现有 FEIAN 反向代理提供。

```text
HOST=127.0.0.1
PORT=45822
BASE_PATH=/mail-reminder
PUBLIC_URL=https://blog.feian.online/mail-reminder/
MAIL_DATA_DIR=/var/lib/mail-reminder
RELEASES_DIR=/srv/apps/mail-reminder/downloads
```

进入本目录执行 `node server.mjs`。执行 `node --test tests/*.test.mjs` 运行 HTTP 回归。提供的 `mail-reminder.service` 使用专用系统用户 `mail-reminder`，发布目录为 `/srv/apps/mail-reminder/current`，数据目录需要由该用户写入。

以下所有路径都相对于 `BASE_PATH`。请求与响应均采用 UTF-8。POST 请求使用 `Content-Type: application/json`。错误响应为 `{"error":"机器可读代码","message":"中文说明"}`，不会回显凭据。设备接口通过 `Authorization: Bearer <token>` 认证。

原生客户端使用可识别的产品 User-Agent，例如 `MailReminderAssistant/1.3.0`。实际公网验收中，Python 默认 User-Agent 被既有 Cloudflare 入口返回 1010，改用诚实产品名称后请求通过。本次部署没有修改 Cloudflare 的安全规则。

## 桌面注册和网段

`POST /api/desktop/register` 接受 `{roomKey,subnet,nodeId,name,roomName}`，返回 `{token,roomId,subnet,cursor,publicUrl}`。

`roomKey` 是现有团队配置中 32 字节密钥的标准 Base64。`subnet` 是规范私有 IPv4 CIDR，例如 `192.168.20.0/24`。相同团队密钥与相同网段会加入同一房间。房间标识采用下列连续字节的 SHA-256 小写十六进制表示。

```text
UTF8("mail-reminder-v1\0") + roomKey原始字节 + UTF8("\0" + subnet)
```

同一房间和 `nodeId` 重新注册时，会轮换桌面 token 并关闭旧连接。桌面会话五分钟没有请求后失效。服务重启后桌面重新注册。新注册返回当前游标。

`POST /api/desktop/packet` 接受 `{packet:"完整WireEnvelope JSON字符串"}`，通过签名和时间校验后放入本房间序列。返回 `{ok:true}`。

`GET /api/desktop/poll?cursor=N` 最多等待 25 秒，返回 `{cursor,packets:["完整WireEnvelope JSON字符串", ...]}`。`packets` 在 0、1、多个报文时始终为数组。收到结果后先处理报文，再保存响应游标。断线重试可以继续原游标。服务重启导致游标大于当前序列时，返回重置后的当前游标。

单次响应最多包含 128 个报文。积压超过该数量时，响应游标只推进到本批最后一条，继续收取即可处理剩余报文。

`POST /api/desktop/pair-code` 接受 `{}`，返回 `{code,expiresIn:300,subnet}`。绑定码是八位数字，一次性使用，五分钟有效。同一桌面新建绑定码会撤销它之前的绑定码。

## 安卓绑定和收发

`POST /api/mobile/pair` 接受 `{code,name,previousToken?}`，返回 `{token,roomId,subnet,nodeId,name,cursor,publicUrl}`。`nodeId` 采用 `mobile-` 加 32 位随机十六进制。换绑时传入原 token，可在新绑定落盘后撤销原绑定及其待收连接。

`GET /api/mobile/poll?cursor=N` 与桌面返回同形态。手机连接过滤 `hello`，在线心跳会更新游标而不会单独唤醒手机长轮询。服务器每四秒为存在待收连接或最近四十秒发起过收取的手机生成 `hello`，供桌面维护在线状态。

`POST /api/mobile/alert` 接受 `{isTest:false}` 或 `{}`，返回 `{alertId}`。正式提醒文案由客户端显示为“收到一封加急邮件，请立刻处理”。同一手机五秒内只能发出一次提醒。

`POST /api/mobile/ack` 接受 `{alertId,seen:false}`，表示软件已收到。用户主动点击“知道了”时发送 `{alertId,seen:true}`。自动关闭弹窗只影响显示，不产生知晓回执。

`POST /api/mobile/name` 接受 `{name}`，返回 `{ok:true,name}`，立即生成带新名称的心跳。名称允许中文，长度上限为 64 个 UTF-16 单元，控制字符会被拒绝。

`POST /api/mobile/unbind` 接受 `{}`，撤销自身绑定。后续请求及已等待的收取连接返回 401。移动 token 的 SHA-256 散列和绑定数据原子落盘，原 token 只保存在设备。服务重启保留手机绑定。

## 报文

```json
{"Payload":"原始JSON字符串","Signature":"Base64 HMAC-SHA256"}
```

签名采用房间团队密钥和原始 `Payload` 的 UTF-8 字节。验证时保持字符串原样。`Payload` 使用桌面现有版本 1 格式。

```json
{
  "Version": 1,
  "Type": "alert",
  "SenderId": "mobile-32位十六进制",
  "Name": "中文手机名称",
  "Id": "32位十六进制提醒标识",
  "TargetId": null,
  "Time": 1794533880000,
  "AlertTime": 1794533880000,
  "IsTest": false
}
```

类型为 `hello`、`alert`、`ack`、`seen`。`hello` 的提醒标识可为空。回执的 `TargetId` 为原发送方，`AlertTime` 与原提醒相同。时间为 Unix 毫秒。报文发送时间允许约两分钟偏差，提醒原始时间也要在该范围内。回执可引用更早的提醒。

原始报文通过 SHA-256 去重，允许多个桌面桥接同一条 LAN 消息。房间缓冲最多 2048 条，并只返回两分钟以内的报文。回执路由记录最多 2048 条、保留约三十分钟。历史报文缓冲为内存数据。收取可能包含发送方自身报文，客户端按 `SenderId` 忽略自身。

## 更新和下载

`GET /api/updates/windows`、`GET /api/updates/android` 返回以下字段。缺少对应安装包时返回 503 和 `release_unavailable`。

```json
{
  "version": "1.3.0",
  "versionCode": 130,
  "url": "https://blog.feian.online/mail-reminder/download/安装包名称",
  "sha256": "安装包SHA256十六进制",
  "size": 12345,
  "notes": "更新说明"
}
```

服务只从 `RELEASES_DIR` 读取 `邮件提醒助手-1.3.0.zip` 与 `mail-reminder-android-1.3.0.apk`。下载路径为 `/download/` 加文件名，支持 GET 和 HEAD。Windows 文件名在 URL 中采用 UTF-8 百分号编码。客户端在打开安装程序或解压更新前核对 SHA-256。

公开 Windows 在线更新包仅包含 EXE、运行配置和说明，更新器保留本机团队配置。初始完整分发包中的生产 `team.json` 含房间主密钥，只通过用户授权的团队内渠道分发。部署者不可把带有生产 `team.json` 的完整私有分发包上传到公开 `RELEASES_DIR`。

兼容参数 `APK_FILE` 可单独指定安卓文件。配置 `RELEASES_DIR` 后优先采用该目录。

## 运行边界

`GET /api/health` 返回服务名称、版本和 `ok` 状态。服务不记录请求正文、token、绑定码或团队密钥。数据目录为 0700，`state.json` 为 0600。部署时从现有 HTTPS 反向代理转入 `127.0.0.1:45822`，保留路径前缀。

服务具备注册、绑定猜测、绑定码创建和消息频率限制，JSON 正文上限 16 KiB，单报文上限 4096 字节。浏览器来源受 `PUBLIC_URL` 的同源校验约束，原生客户端可以省略 Origin。
