import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const MAX_BODY_BYTES = 16 * 1024;
const MAX_PACKET_BYTES = 4096;
const PACKET_LIFETIME_MS = 120_000;
const PAIR_LIFETIME_MS = 300_000;
const ALERT_LIFETIME_MS = 30 * 60_000;
const SESSION_LIFETIME_MS = 5 * 60_000;
const publicDirectory = fileURLToPath(new URL('./public/', import.meta.url));

class RequestError extends Error {
  constructor(status, code, message) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

function reject(status, code, message) {
  throw new RequestError(status, code, message);
}

function validName(value) {
  return typeof value === 'string' && value.trim().length > 0 && value.length <= 64 && !/[\u0000-\u001f\u007f-\u009f]/u.test(value);
}

function readName(value, fallback) {
  const name = value === undefined ? fallback : value;
  if (!validName(name)) reject(400, 'invalid_name', '名称需为 1 至 64 个有效字符。');
  return name.trim();
}

function validIdentifier(value) {
  return typeof value === 'string' && /^[a-zA-Z0-9_-]{1,64}$/u.test(value);
}

function canonicalSubnet(value) {
  if (typeof value !== 'string') reject(400, 'invalid_subnet', '请输入有效的私有 IPv4 网段。');
  const match = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\/(\d{1,2})$/u.exec(value);
  if (!match) reject(400, 'invalid_subnet', '请输入有效的私有 IPv4 网段。');
  const octets = match.slice(1, 5).map(Number);
  const prefix = Number(match[5]);
  if (octets.some((octet, index) => octet > 255 || String(octet) !== match[index + 1]) || prefix < 8 || prefix > 30)
    reject(400, 'invalid_subnet', '请输入规范的私有 IPv4 网段。');
  const address = (((octets[0] * 0x1000000) + (octets[1] << 16) + (octets[2] << 8) + octets[3]) >>> 0);
  const mask = (0xffffffff << (32 - prefix)) >>> 0;
  if (((address & mask) >>> 0) !== address) reject(400, 'invalid_subnet', '网段地址包含主机位。');
  const end = (address | (~mask >>> 0)) >>> 0;
  const ranges = [[0x0a000000, 0x0affffff], [0xac100000, 0xac1fffff], [0xc0a80000, 0xc0a8ffff]];
  if (!ranges.some(([start, stop]) => address >= start && end <= stop))
    reject(400, 'invalid_subnet', '仅支持私有 IPv4 网段。');
  return `${octets.join('.')}/${prefix}`;
}

function readRoomKey(value) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9+/]{43}=$/u.test(value))
    reject(400, 'invalid_room_key', '团队配置无效。');
  const key = Buffer.from(value, 'base64');
  if (key.length !== 32 || key.toString('base64') !== value) reject(400, 'invalid_room_key', '团队配置无效。');
  return key;
}

export function deriveRoomId(key, subnet) {
  return crypto.createHash('sha256').update('mail-reminder-v1\0').update(key).update(`\0${subnet}`).digest('hex');
}

function tokenHash(token) {
  return crypto.createHash('sha256').update(token).digest('hex');
}

function freshToken() { return crypto.randomBytes(32).toString('base64url'); }

function newRoom(record) {
  return {
    ...record,
    key: Buffer.from(record.roomKey, 'base64'),
    sequence: 0,
    frames: [],
    packetHashes: new Map(),
    alerts: new Map(),
    waiters: new Set(),
  };
}

function packetEnvelope(packet, key) {
  const payload = JSON.stringify(packet);
  return JSON.stringify({ Payload: payload, Signature: crypto.createHmac('sha256', key).update(payload, 'utf8').digest('base64') });
}

export class RelayHub {
  constructor(options = {}) {
    this.now = options.now ?? Date.now;
    this.dataDirectory = options.dataDirectory ?? path.join(process.cwd(), '.data');
    this.statePath = path.join(this.dataDirectory, 'state.json');
    this.publicUrl = new URL(options.publicUrl ?? 'https://blog.feian.online/mail-reminder/').href;
    this.pollTimeoutMs = options.pollTimeoutMs ?? 25_000;
    this.maxFrames = options.maxFrames ?? 2048;
    this.maxPollPackets = options.maxPollPackets ?? 128;
    this.rooms = new Map();
    this.desktops = new Map();
    this.mobileByHash = new Map();
    this.mobileActivity = new Map();
    this.pairCodes = new Map();
    this.limits = new Map();
    fs.mkdirSync(this.dataDirectory, { recursive: true, mode: 0o700 });
    if (process.platform !== 'win32') fs.chmodSync(this.dataDirectory, 0o700);
    this.state = { version: 1, rooms: [], mobiles: [] };
    if (fs.existsSync(this.statePath)) {
      const saved = JSON.parse(fs.readFileSync(this.statePath, 'utf8'));
      if (saved.version !== 1 || !Array.isArray(saved.rooms) || !Array.isArray(saved.mobiles)) throw new Error('Invalid relay state file.');
      for (const record of saved.rooms) {
        const key = readRoomKey(record.roomKey);
        const subnet = canonicalSubnet(record.subnet);
        if (record.id !== deriveRoomId(key, subnet) || !validName(record.roomName) || this.rooms.has(record.id))
          throw new Error('Invalid persisted room record.');
        this.rooms.set(record.id, newRoom(record));
      }
      for (const record of saved.mobiles) {
        if (!/^[a-f0-9]{64}$/u.test(record.tokenHash) || !validIdentifier(record.nodeId) || !validName(record.name) || !this.rooms.has(record.roomId) || this.mobileByHash.has(record.tokenHash))
          throw new Error('Invalid persisted mobile record.');
        this.mobileByHash.set(record.tokenHash, record);
      }
      this.state = saved;
      if (process.platform !== 'win32') fs.chmodSync(this.statePath, 0o600);
    }
    this.heartbeatTimer = setInterval(() => this.maintenance(), options.heartbeatMs ?? 4000);
    this.heartbeatTimer.unref();
  }

  persist(nextState) {
    const tempPath = `${this.statePath}.${process.pid}.${crypto.randomBytes(4).toString('hex')}.tmp`;
    let descriptor;
    try {
      descriptor = fs.openSync(tempPath, 'wx', 0o600);
      fs.writeFileSync(descriptor, `${JSON.stringify(nextState)}\n`, 'utf8');
      fs.fsyncSync(descriptor);
      fs.closeSync(descriptor);
      descriptor = undefined;
      fs.renameSync(tempPath, this.statePath);
      this.state = nextState;
    } finally {
      if (descriptor !== undefined) fs.closeSync(descriptor);
      if (fs.existsSync(tempPath)) fs.unlinkSync(tempPath);
    }
  }

  rateLimit(key, maximum, periodMs) {
    const now = this.now();
    let entry = this.limits.get(key);
    if (!entry || entry.until <= now) { entry = { count: 0, until: now + periodMs }; this.limits.set(key, entry); }
    entry.count += 1;
    if (entry.count > maximum) reject(429, 'rate_limited', '操作过于频繁，请稍后再试。');
  }

  registerDesktop(body, ip) {
    this.rateLimit(`register:${ip}`, 60, 60_000);
    const key = readRoomKey(body.roomKey);
    const subnet = canonicalSubnet(body.subnet);
    if (!validIdentifier(body.nodeId)) reject(400, 'invalid_node_id', '设备标识无效。');
    const name = readName(body.name);
    const roomName = readName(body.roomName, '办公室');
    const id = deriveRoomId(key, subnet);
    let room = this.rooms.get(id);
    if (!room) {
      if (this.rooms.size >= 128) reject(503, 'capacity', '服务容量已满。');
      const record = { id, roomKey: body.roomKey, subnet, roomName, createdAt: this.now() };
      this.persist({ ...this.state, rooms: [...this.state.rooms, record] });
      room = newRoom(record);
      this.rooms.set(id, room);
    }
    const sameNode = [...this.desktops.values()].find((entry) => entry.roomId === id && entry.nodeId === body.nodeId);
    if (!sameNode && [...this.desktops.values()].filter((entry) => entry.roomId === id).length >= 128)
      reject(503, 'capacity', '此网段设备数量已达上限。');
    const token = freshToken();
    const hash = tokenHash(token);
    if (sameNode) {
      this.desktops.delete(sameNode.tokenHash);
      this.closeSessionWaiters(sameNode);
    }
    this.desktops.set(hash, { kind: 'desktop', roomId: id, nodeId: body.nodeId, name, tokenHash: hash, lastUsed: this.now() });
    return { token, roomId: id, subnet, cursor: room.sequence, publicUrl: this.publicUrl };
  }

  authenticate(header, kind) {
    const match = /^Bearer ([A-Za-z0-9_-]{43})$/u.exec(header ?? '');
    if (!match) reject(401, 'unauthorized', '请重新连接或绑定。');
    const hash = tokenHash(match[1]);
    let session;
    if (kind === 'desktop') {
      session = this.desktops.get(hash);
      if (session && this.now() - session.lastUsed > SESSION_LIFETIME_MS) {
        this.desktops.delete(hash);
        session = undefined;
      }
      if (session) session.lastUsed = this.now();
    } else {
      const saved = this.mobileByHash.get(hash);
      if (saved) session = { ...saved, kind: 'mobile' };
    }
    if (!session) reject(401, 'unauthorized', '请重新连接或绑定。');
    return session;
  }

  createPairCode(session) {
    this.rateLimit(`code:${session.tokenHash}`, 10, 60_000);
    const now = this.now();
    for (const [code, pair] of this.pairCodes) if (pair.until <= now) this.pairCodes.delete(code);
    for (const [code, pair] of this.pairCodes) if (pair.owner === session.tokenHash) this.pairCodes.delete(code);
    if (this.pairCodes.size >= 1024) reject(503, 'capacity', '绑定请求数量过多，请稍后再试。');
    let code;
    do { code = String(crypto.randomInt(0, 100_000_000)).padStart(8, '0'); } while (this.pairCodes.has(code));
    this.pairCodes.set(code, { roomId: session.roomId, until: now + PAIR_LIFETIME_MS, owner: session.tokenHash });
    return { code, expiresIn: 300, subnet: this.rooms.get(session.roomId).subnet };
  }

  pairMobile(body, ip) {
    this.rateLimit(`pair-requests:${ip}`, 60, 60_000);
    const failures = this.limits.get(`pair-failed:${ip}`);
    if (failures && failures.until > this.now() && failures.count >= 5)
      reject(429, 'rate_limited', '绑定码尝试过多，请稍后再试。');
    const name = readName(body.name, '我的手机');
    if (typeof body.code !== 'string' || !/^\d{8}$/u.test(body.code)) {
      this.rateLimit(`pair-failed:${ip}`, 5, 60_000);
      reject(400, 'invalid_pair_code', '绑定码无效或已过期。');
    }
    const pair = this.pairCodes.get(body.code);
    if (!pair || pair.until <= this.now()) {
      this.pairCodes.delete(body.code);
      this.rateLimit(`pair-failed:${ip}`, 5, 60_000);
      reject(400, 'invalid_pair_code', '绑定码无效或已过期。');
    }
    const room = this.rooms.get(pair.roomId);
    const previous = typeof body.previousToken === 'string' && /^[A-Za-z0-9_-]{43}$/u.test(body.previousToken)
      ? this.mobileByHash.get(tokenHash(body.previousToken)) : undefined;
    const remaining = this.state.mobiles.filter((entry) => entry !== previous);
    if (remaining.length >= 512 || remaining.filter((entry) => entry.roomId === room.id).length >= 64)
      reject(503, 'capacity', '此网段手机数量已达上限。');
    const token = freshToken();
    const record = { roomId: room.id, nodeId: `mobile-${crypto.randomBytes(16).toString('hex')}`, name, tokenHash: tokenHash(token), createdAt: this.now() };
    this.persist({ ...this.state, mobiles: [...remaining, record] });
    this.pairCodes.delete(body.code);
    if (previous) this.removeMobileRuntime(previous);
    this.mobileByHash.set(record.tokenHash, record);
    this.publishMobileHello(record);
    return { token, roomId: room.id, subnet: room.subnet, nodeId: record.nodeId, name, cursor: room.sequence, publicUrl: this.publicUrl };
  }

  removeMobileRuntime(record) {
    this.mobileByHash.delete(record.tokenHash);
    this.mobileActivity.delete(record.tokenHash);
    this.closeSessionWaiters(record);
  }

  unbindMobile(session) {
    this.persist({ ...this.state, mobiles: this.state.mobiles.filter((entry) => entry.tokenHash !== session.tokenHash) });
    this.removeMobileRuntime(session);
    return { ok: true };
  }

  renameMobile(session, body) {
    const name = readName(body.name);
    const record = this.mobileByHash.get(session.tokenHash);
    const updated = { ...record, name };
    this.persist({ ...this.state, mobiles: this.state.mobiles.map((entry) => entry.tokenHash === session.tokenHash ? updated : entry) });
    this.mobileByHash.set(session.tokenHash, updated);
    this.publishMobileHello(updated);
    return { ok: true, name };
  }

  decodePacket(raw, room) {
    if (typeof raw !== 'string' || Buffer.byteLength(raw, 'utf8') > MAX_PACKET_BYTES)
      reject(400, 'invalid_packet', '消息无效。');
    let envelope;
    let packet;
    try {
      envelope = JSON.parse(raw);
      if (!envelope || typeof envelope.Payload !== 'string' || envelope.Payload.length > 3000 || typeof envelope.Signature !== 'string' || !/^[A-Za-z0-9+/]{43}=$/u.test(envelope.Signature))
        throw new Error('invalid');
      const supplied = Buffer.from(envelope.Signature, 'base64');
      const expected = crypto.createHmac('sha256', room.key).update(envelope.Payload, 'utf8').digest();
      if (supplied.length !== 32 || !crypto.timingSafeEqual(supplied, expected)) throw new Error('invalid');
      packet = JSON.parse(envelope.Payload);
    } catch { reject(400, 'invalid_packet', '消息校验失败。'); }
    const now = this.now();
    if (!packet || packet.Version !== 1 || !validIdentifier(packet.SenderId) || !validName(packet.Name) ||
      !Number.isSafeInteger(packet.Time) || Math.abs(packet.Time - now) > PACKET_LIFETIME_MS || typeof packet.IsTest !== 'boolean')
      reject(400, 'invalid_packet', '消息字段无效或已过期。');
    if (!['hello', 'alert', 'ack', 'seen'].includes(packet.Type)) reject(400, 'invalid_packet', '消息类型无效。');
    if (packet.Type !== 'hello') {
      if (typeof packet.Id !== 'string' || !/^[a-fA-F0-9]{32}$/u.test(packet.Id) || !Number.isSafeInteger(packet.AlertTime))
        reject(400, 'invalid_packet', '消息标识无效。');
      if (packet.Type === 'alert' && Math.abs(packet.AlertTime - now) > PACKET_LIFETIME_MS)
        reject(400, 'invalid_packet', '提醒已过期。');
      if (packet.Type !== 'alert' && !validIdentifier(packet.TargetId)) reject(400, 'invalid_packet', '回执目标无效。');
    }
    return packet;
  }

  publishDesktopPacket(session, body) {
    this.rateLimit(`packet:${session.tokenHash}`, 100, 1000);
    const room = this.rooms.get(session.roomId);
    const packet = this.decodePacket(body.packet, room);
    this.publish(room, body.packet, packet);
    return { ok: true };
  }

  publish(room, raw, packet) {
    const hash = crypto.createHash('sha256').update(raw, 'utf8').digest('hex');
    if (room.packetHashes.has(hash)) return false;
    const now = this.now();
    room.sequence += 1;
    room.frames.push({ cursor: room.sequence, raw, type: packet.Type, receivedAt: now });
    room.packetHashes.set(hash, now);
    if (packet.Type === 'alert') room.alerts.set(packet.Id, { senderId: packet.SenderId, alertTime: packet.AlertTime, isTest: packet.IsTest, receivedAt: now });
    while (room.frames.length > this.maxFrames) room.frames.shift();
    while (room.alerts.size > 2048) room.alerts.delete(room.alerts.keys().next().value);
    while (room.packetHashes.size > this.maxFrames * 4) room.packetHashes.delete(room.packetHashes.keys().next().value);
    for (const waiter of [...room.waiters]) {
      const payload = this.framesSince(room, waiter.cursor, waiter.session.kind);
      if (payload.packets.length > 0) waiter.finish(200, payload);
    }
    return true;
  }

  mobilePacket(record, type, alertId = null, targetId = null, alertTime = this.now(), isTest = false) {
    return {
      Version: 1, Type: type, SenderId: record.nodeId, Name: record.name,
      Id: alertId, TargetId: targetId, Time: this.now(), AlertTime: alertTime, IsTest: isTest,
    };
  }

  publishMobileHello(record) {
    const room = this.rooms.get(record.roomId);
    const packet = this.mobilePacket(record, 'hello');
    this.publish(room, packetEnvelope(packet, room.key), packet);
  }

  mobileAlert(session, body) {
    this.rateLimit(`alert:${session.tokenHash}`, 1, 5000);
    if (body.isTest !== undefined && typeof body.isTest !== 'boolean') reject(400, 'invalid_test_flag', '测试标记无效。');
    const record = this.mobileByHash.get(session.tokenHash);
    const room = this.rooms.get(session.roomId);
    const id = crypto.randomBytes(16).toString('hex');
    const packet = this.mobilePacket(record, 'alert', id, null, this.now(), body.isTest === true);
    this.publish(room, packetEnvelope(packet, room.key), packet);
    return { alertId: id };
  }

  mobileAck(session, body) {
    this.rateLimit(`ack:${session.tokenHash}`, 100, 1000);
    if (typeof body.alertId !== 'string' || !/^[a-fA-F0-9]{32}$/u.test(body.alertId) || typeof body.seen !== 'boolean')
      reject(400, 'invalid_ack', '回执字段无效。');
    const room = this.rooms.get(session.roomId);
    const alert = room.alerts.get(body.alertId);
    if (!alert) reject(404, 'alert_not_found', '提醒不存在或已过期。');
    const record = this.mobileByHash.get(session.tokenHash);
    const packet = this.mobilePacket(record, body.seen ? 'seen' : 'ack', body.alertId, alert.senderId, alert.alertTime, alert.isTest);
    this.publish(room, packetEnvelope(packet, room.key), packet);
    return { ok: true };
  }

  framesSince(room, cursor, kind) {
    const effectiveCursor = cursor > room.sequence ? 0 : cursor;
    const eligible = room.frames.filter((frame) => frame.cursor > effectiveCursor && this.now() - frame.receivedAt <= PACKET_LIFETIME_MS && (kind !== 'mobile' || frame.type !== 'hello'));
    const selected = eligible.slice(0, this.maxPollPackets);
    return {
      cursor: eligible.length > selected.length ? selected.at(-1).cursor : room.sequence,
      packets: selected.map((frame) => frame.raw),
    };
  }

  poll(session, cursor, response) {
    const room = this.rooms.get(session.roomId);
    const payload = this.framesSince(room, cursor, session.kind);
    if (session.kind === 'mobile') {
      let activity = this.mobileActivity.get(session.tokenHash);
      if (!activity) { activity = { polls: 0, lastUsed: this.now() }; this.mobileActivity.set(session.tokenHash, activity); }
      activity.lastUsed = this.now();
    }
    if (payload.packets.length > 0 || cursor > room.sequence) { sendJson(response, 200, payload); return; }
    if (room.waiters.size >= 256) reject(503, 'capacity', '连接数量过多，请稍后再试。');
    if ([...room.waiters].filter((waiter) => waiter.session.tokenHash === session.tokenHash).length >= 2)
      reject(429, 'duplicate_poll', '请等待上一条连接返回。');
    let completed = false;
    const activity = session.kind === 'mobile' ? this.mobileActivity.get(session.tokenHash) : undefined;
    if (activity) activity.polls += 1;
    const waiter = { session, cursor, finish: (status, result, respond = true) => {
      if (completed) return;
      completed = true;
      clearTimeout(timer);
      room.waiters.delete(waiter);
      if (activity) { activity.polls = Math.max(0, activity.polls - 1); activity.lastUsed = this.now(); }
      response.off('close', aborted);
      if (respond && !response.destroyed) sendJson(response, status, result);
    } };
    const timer = setTimeout(() => waiter.finish(200, this.framesSince(room, cursor, session.kind)), this.pollTimeoutMs);
    const aborted = () => waiter.finish(200, null, false);
    response.once('close', aborted);
    room.waiters.add(waiter);
  }

  closeSessionWaiters(session) {
    const room = this.rooms.get(session.roomId);
    if (!room) return;
    for (const waiter of [...room.waiters]) {
      if (waiter.session.tokenHash === session.tokenHash) waiter.finish(401, { error: 'unauthorized', message: '请重新绑定。' });
    }
  }

  maintenance() {
    const now = this.now();
    for (const [key, entry] of this.limits) if (entry.until <= now) this.limits.delete(key);
    for (const [code, pair] of this.pairCodes) if (pair.until <= now) this.pairCodes.delete(code);
    for (const [hash, desktop] of this.desktops) if (now - desktop.lastUsed > SESSION_LIFETIME_MS) { this.desktops.delete(hash); this.closeSessionWaiters(desktop); }
    for (const room of this.rooms.values()) {
      room.frames = room.frames.filter((frame) => now - frame.receivedAt <= PACKET_LIFETIME_MS);
      for (const [hash, time] of room.packetHashes) if (now - time > PACKET_LIFETIME_MS) room.packetHashes.delete(hash);
      for (const [id, alert] of room.alerts) if (now - alert.receivedAt > ALERT_LIFETIME_MS) room.alerts.delete(id);
    }
    for (const [hash, activity] of this.mobileActivity) {
      const record = this.mobileByHash.get(hash);
      if (!record || (activity.polls === 0 && now - activity.lastUsed > 40_000)) { this.mobileActivity.delete(hash); continue; }
      this.publishMobileHello(record);
    }
  }

  close() {
    clearInterval(this.heartbeatTimer);
    for (const room of this.rooms.values()) for (const waiter of [...room.waiters]) waiter.finish(503, { error: 'service_restarting', message: '服务正在重启，请重连。' });
  }
}

function sendJson(response, status, value) {
  response.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
  response.end(JSON.stringify(value));
}

function streamFile(response, filePath) {
  const stream = fs.createReadStream(filePath);
  stream.once('error', () => response.destroy());
  response.once('close', () => stream.destroy());
  stream.pipe(response);
}

async function readBody(request) {
  const contentType = (request.headers['content-type'] ?? '').split(';', 1)[0].trim().toLowerCase();
  if (contentType !== 'application/json') reject(415, 'content_type', '请使用 JSON 请求。');
  const chunks = await new Promise((resolve, rejectBody) => {
    let length = 0;
    let excessive = false;
    const received = [];
    request.on('data', (chunk) => {
      length += chunk.length;
      if (length > MAX_BODY_BYTES) { excessive = true; received.length = 0; return; }
      if (!excessive) received.push(chunk);
    });
    request.once('end', () => excessive ? rejectBody(new RequestError(413, 'body_too_large', '请求内容过长。')) : resolve(received));
    request.once('error', rejectBody);
  });
  let body;
  try { body = JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch { reject(400, 'invalid_json', '请求内容无效。'); }
  if (!body || typeof body !== 'object' || Array.isArray(body)) reject(400, 'invalid_json', '请求需要 JSON 对象。');
  return body;
}

export function createRelayServer(options = {}) {
  const hub = new RelayHub(options);
  const basePath = (options.basePath ?? '/mail-reminder').replace(/\/+$/u, '');
  if (!/^\/[a-zA-Z0-9/_-]+$/u.test(basePath)) throw new Error('Invalid BASE_PATH.');
  const allowedOrigin = new URL(hub.publicUrl).origin;
  const releaseHashes = new Map();
  const releases = {
    windows: { fileName: '邮件提醒助手-1.3.0.zip', contentType: 'application/zip', asciiName: 'mail-reminder-windows-1.3.0.zip' },
    android: { fileName: 'mail-reminder-android-1.3.0.apk', contentType: 'application/vnd.android.package-archive', asciiName: 'mail-reminder-android-1.3.0.apk' },
  };
  function releaseFile(platform) {
    const release = releases[platform];
    const filePath = options.releasesDirectory ? path.join(options.releasesDirectory, release.fileName) : platform === 'android' ? options.apkFile : undefined;
    if (!filePath || !fs.existsSync(filePath) || !fs.statSync(filePath).isFile())
      reject(503, 'release_unavailable', '此平台安装包尚未发布，请稍后重试。');
    const stat = fs.statSync(filePath);
    let cached = releaseHashes.get(platform);
    if (!cached || cached.size !== stat.size || cached.mtimeMs !== stat.mtimeMs || cached.filePath !== filePath) {
      cached = { filePath, size: stat.size, mtimeMs: stat.mtimeMs, sha256: crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex') };
      releaseHashes.set(platform, cached);
    }
    return { ...release, ...cached };
  }
  const server = http.createServer(async (request, response) => {
    response.setHeader('x-content-type-options', 'nosniff');
    response.setHeader('referrer-policy', 'no-referrer');
    response.setHeader('x-frame-options', 'DENY');
    response.setHeader('content-security-policy', "default-src 'self'; style-src 'self'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'; form-action 'self'");
    try {
      if (request.headers.origin && request.headers.origin !== allowedOrigin) reject(403, 'origin', '此来源不受支持。');
      const url = new URL(request.url, 'http://relay.local');
      if (url.pathname === basePath && request.method === 'GET') { response.writeHead(308, { location: `${basePath}/` }); response.end(); return; }
      if (!url.pathname.startsWith(`${basePath}/`)) reject(404, 'not_found', '路径不存在。');
      let route;
      try { route = decodeURIComponent(url.pathname.slice(basePath.length)); }
      catch { reject(400, 'invalid_path', '请求路径无效。'); }
      if (request.method === 'GET' && route === '/api/health') { sendJson(response, 200, { status: 'ok', service: 'mail-reminder', version: '1.3.0' }); return; }
      const update = /^\/api\/updates\/(windows|android)$/u.exec(route);
      if (request.method === 'GET' && update) {
        const release = releaseFile(update[1]);
        sendJson(response, 200, {
          version: '1.3.0', versionCode: 130,
          url: new URL(`download/${encodeURIComponent(release.fileName)}`, hub.publicUrl).href,
          sha256: release.sha256, size: release.size,
          notes: '即时同步中文名称、手机与桌面联动提醒、弹窗五秒关闭，并支持在线更新。',
        });
        return;
      }
      if (['GET', 'HEAD'].includes(request.method) && ['/','/styles.css'].includes(route)) {
        const filePath = path.join(publicDirectory, route === '/' ? 'index.html' : 'styles.css');
        response.writeHead(200, { 'content-type': route === '/' ? 'text/html; charset=utf-8' : 'text/css; charset=utf-8', 'cache-control': 'no-cache' });
        if (request.method === 'HEAD') response.end();
        else streamFile(response, filePath);
        return;
      }
      const downloadPlatform = Object.keys(releases).find((platform) => route === `/download/${releases[platform].fileName}`);
      if (['GET', 'HEAD'].includes(request.method) && downloadPlatform) {
        const release = releaseFile(downloadPlatform);
        response.writeHead(200, {
          'content-type': release.contentType, 'content-length': release.size,
          'content-disposition': `attachment; filename="${release.asciiName}"; filename*=UTF-8''${encodeURIComponent(release.fileName)}`,
          'cache-control': 'no-cache', etag: `"${release.sha256}"`,
        });
        if (request.method === 'HEAD') response.end();
        else streamFile(response, release.filePath);
        return;
      }
      const localAddress = request.socket.remoteAddress;
      const trustedProxy = ['127.0.0.1', '::1', '::ffff:127.0.0.1'].includes(localAddress);
      const ip = trustedProxy && request.headers['cf-connecting-ip'] ? String(request.headers['cf-connecting-ip']).slice(0, 80) : localAddress;
      if (request.method === 'POST' && route === '/api/desktop/register') { sendJson(response, 200, hub.registerDesktop(await readBody(request), ip)); return; }
      if (request.method === 'POST' && route === '/api/mobile/pair') { sendJson(response, 200, hub.pairMobile(await readBody(request), ip)); return; }
      const kind = route.startsWith('/api/desktop/') ? 'desktop' : route.startsWith('/api/mobile/') ? 'mobile' : null;
      if (!kind) reject(404, 'not_found', '路径不存在。');
      const session = hub.authenticate(request.headers.authorization, kind);
      if (request.method === 'GET' && route === `/api/${kind}/poll`) {
        const value = url.searchParams.get('cursor');
        if (!/^(0|[1-9]\d{0,15})$/u.test(value ?? '') || !Number.isSafeInteger(Number(value))) reject(400, 'invalid_cursor', '消息位置无效。');
        hub.poll(session, Number(value), response); return;
      }
      if (request.method !== 'POST') reject(405, 'method', '请求方法不受支持。');
      const body = await readBody(request);
      let result;
      if (route === '/api/desktop/packet') result = hub.publishDesktopPacket(session, body);
      else if (route === '/api/desktop/pair-code') result = hub.createPairCode(session);
      else if (route === '/api/mobile/alert') result = hub.mobileAlert(session, body);
      else if (route === '/api/mobile/ack') result = hub.mobileAck(session, body);
      else if (route === '/api/mobile/name') result = hub.renameMobile(session, body);
      else if (route === '/api/mobile/unbind') result = hub.unbindMobile(session);
      else reject(404, 'not_found', '路径不存在。');
      sendJson(response, 200, result);
    } catch (error) {
      if (response.headersSent || response.destroyed) return;
      if (error instanceof RequestError) sendJson(response, error.status, { error: error.code, message: error.message });
      else {
        // Error objects may contain request data. Keep logs limited to the failure class.
        console.error('mail-reminder request failed', error.code ?? error.name ?? 'Error');
        sendJson(response, 500, { error: 'server_error', message: '服务暂时无法完成请求，请稍后再试。' });
      }
    }
  });
  server.requestTimeout = 35_000;
  server.headersTimeout = 10_000;
  server.keepAliveTimeout = 5000;
  return { server, hub, close: () => new Promise((resolve, rejectClose) => {
    hub.close();
    server.close((error) => error ? rejectClose(error) : resolve());
    server.closeIdleConnections();
  }) };
}
