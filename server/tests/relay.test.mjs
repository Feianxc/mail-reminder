import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { once } from 'node:events';
import { test } from 'node:test';
import { createRelayServer, deriveRoomId } from '../relay.mjs';

async function fixture(t, options = {}) {
  const directory = options.dataDirectory ?? fs.mkdtempSync(path.join(os.tmpdir(), 'mail-relay-test-'));
  const app = createRelayServer({ dataDirectory: directory, pollTimeoutMs: 45, heartbeatMs: 60_000, ...options });
  app.server.listen(0, '127.0.0.1');
  await once(app.server, 'listening');
  const base = `http://127.0.0.1:${app.server.address().port}/mail-reminder`;
  let stopped = false;
  const close = async () => { if (!stopped) { stopped = true; await app.close(); } };
  t.after(async () => { await close(); if (!options.keepData) fs.rmSync(directory, { recursive: true, force: true }); });
  return { ...app, directory, base, close };
}

async function request(app, route, { token, body, headers = {}, method } = {}) {
  const response = await fetch(`${app.base}${route}`, {
    method: method ?? (body === undefined ? 'GET' : 'POST'),
    headers: { ...(body === undefined ? {} : { 'content-type': 'application/json' }), ...(token ? { authorization: `Bearer ${token}` } : {}), ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  return { status: response.status, body: await response.json() };
}

function signed(key, fields = {}) {
  const packet = {
    Version: 1, Type: 'alert', SenderId: 'desktop-one', Name: '张三的电脑',
    Id: crypto.randomBytes(16).toString('hex'), TargetId: null,
    Time: Date.now(), AlertTime: Date.now(), IsTest: false, ...fields,
  };
  const payload = JSON.stringify(packet);
  return JSON.stringify({ Payload: payload, Signature: crypto.createHmac('sha256', key).update(payload, 'utf8').digest('base64') });
}

async function register(app, fields = {}) {
  const key = fields.key ?? crypto.randomBytes(32);
  const result = await request(app, '/api/desktop/register', { body: {
    roomKey: key.toString('base64'), subnet: '192.168.20.0/24', nodeId: 'desktop-one', name: '张三的电脑', roomName: '研发办公室', ...fields.body,
  } });
  assert.equal(result.status, 200);
  return { ...result.body, key };
}

async function pair(app, desktop, name = '李四的手机', previousToken) {
  const code = await request(app, '/api/desktop/pair-code', { token: desktop.token, body: {} });
  assert.equal(code.status, 200);
  assert.match(code.body.code, /^\d{8}$/u);
  assert.equal(code.body.expiresIn, 300);
  const result = await request(app, '/api/mobile/pair', { body: { code: code.body.code, name, previousToken } });
  assert.equal(result.status, 200);
  return { ...result.body, usedCode: code.body.code };
}

test('a new alert wakes two pending mobile polls immediately while another mobile delays its receipt', { timeout: 5000 }, async (t) => {
  const app = await fixture(t, { pollTimeoutMs: 25_000 });
  const desktop = await register(app);
  const first = await pair(app, desktop, '延迟验收手机甲');
  const second = await pair(app, desktop, '延迟验收手机乙');
  const cursor = second.cursor;
  const room = app.hub.rooms.get(desktop.roomId);
  const firstPoll = request(app, `/api/mobile/poll?cursor=${cursor}`, { token: first.token });
  const secondPoll = request(app, `/api/mobile/poll?cursor=${cursor}`, { token: second.token });
  const pendingDeadline = Date.now() + 1500;
  while (room.waiters.size !== 2 && Date.now() < pendingDeadline) await new Promise((resolve) => setTimeout(resolve, 1));
  assert.equal(room.waiters.size, 2, 'both mobile requests are pending before publication');
  await request(app, '/api/desktop/packet', { token: desktop.token, body: { packet: signed(desktop.key, { Type: 'hello', Id: null }) } });
  assert.equal(room.waiters.size, 2, 'desktop presence keeps both mobile polls pending');
  const raw = signed(desktop.key);
  const alert = decode(raw, desktop.key);
  const started = performance.now();
  await request(app, '/api/desktop/packet', { token: desktop.token, body: { packet: raw } });
  const received = await Promise.all([firstPoll, secondPoll]);
  const firstDeliveryMs = performance.now() - started;
  assert.ok(firstDeliveryMs < 1000, `pending mobile delivery took ${firstDeliveryMs.toFixed(1)}ms`);
  for (const response of received) {
    assert.equal(response.status, 200);
    assert.ok(Array.isArray(response.body.packets));
    assert.equal(response.body.packets.length, 1);
    assert.equal(decode(response.body.packets[0], desktop.key).Id, alert.Id);
  }
  // The first mobile intentionally sends no ACK while the second waits for a new alert.
  const nextCursor = received[1].body.cursor;
  const nextPoll = request(app, `/api/mobile/poll?cursor=${nextCursor}`, { token: second.token });
  const nextDeadline = Date.now() + 1500;
  while (room.waiters.size !== 1 && Date.now() < nextDeadline) await new Promise((resolve) => setTimeout(resolve, 1));
  assert.equal(room.waiters.size, 1);
  const nextRaw = signed(desktop.key);
  const nextStarted = performance.now();
  await request(app, '/api/desktop/packet', { token: desktop.token, body: { packet: nextRaw } });
  const next = await nextPoll;
  const secondDeliveryMs = performance.now() - nextStarted;
  assert.ok(secondDeliveryMs < 1000, `second mobile delivery took ${secondDeliveryMs.toFixed(1)}ms`);
  assert.equal(next.body.packets.length, 1);
  assert.equal(decode(next.body.packets[0], desktop.key).Id, decode(nextRaw, desktop.key).Id);
  console.log(JSON.stringify({ pendingTwoMobileMs: Number(firstDeliveryMs.toFixed(1)), unacknowledgedOtherMobileMs: Number(secondDeliveryMs.toFixed(1)) }));
});

function decode(raw, key) {
  const envelope = JSON.parse(raw);
  assert.equal(envelope.Signature, crypto.createHmac('sha256', key).update(envelope.Payload, 'utf8').digest('base64'));
  return JSON.parse(envelope.Payload);
}

test('registration derives an isolated room from the key and canonical subnet', async (t) => {
  const app = await fixture(t);
  const first = await register(app);
  assert.equal(first.roomId, deriveRoomId(first.key, '192.168.20.0/24'));
  assert.equal(first.subnet, '192.168.20.0/24');
  assert.equal(first.cursor, 0);
  const same = await register(app, { key: first.key, body: { nodeId: 'desktop-two', name: '中文同事' } });
  assert.equal(same.roomId, first.roomId);
  const otherKey = await register(app, { body: { nodeId: 'desktop-three' } });
  assert.notEqual(otherKey.roomId, first.roomId);
  const otherSubnet = await register(app, { key: first.key, body: { nodeId: 'desktop-four', subnet: '192.168.21.0/24' } });
  assert.notEqual(otherSubnet.roomId, first.roomId);
  for (const subnet of ['192.168.20.1/24', '0.0.0.0/0', '8.8.8.0/24', '192.168.020.0/24', '192.168.20.0/32']) {
    const result = await request(app, '/api/desktop/register', { body: { roomKey: first.key.toString('base64'), subnet, nodeId: 'bad', name: '电脑' } });
    assert.equal(result.status, 400, subnet);
  }
  const state = JSON.parse(fs.readFileSync(path.join(app.directory, 'state.json'), 'utf8'));
  assert.ok(Array.isArray(state.rooms));
  assert.ok(Array.isArray(state.mobiles));
  assert.equal(state.mobiles.length, 0);
  if (process.platform !== 'win32') assert.equal(fs.statSync(path.join(app.directory, 'state.json')).mode & 0o777, 0o600);
});

test('long polling preserves 0, 1, N packet arrays, signed Chinese names, and packet deduplication', async (t) => {
  const app = await fixture(t);
  const desktop = await register(app);
  const empty = await request(app, '/api/desktop/poll?cursor=0', { token: desktop.token });
  assert.equal(empty.status, 200);
  assert.deepEqual(empty.body.packets, []);
  const first = signed(desktop.key, { Name: '中文姓名 · 工位甲' });
  assert.equal((await request(app, '/api/desktop/packet', { token: desktop.token, body: { packet: first } })).status, 200);
  const one = await request(app, '/api/desktop/poll?cursor=0', { token: desktop.token });
  assert.equal(one.body.packets.length, 1);
  assert.equal(decode(one.body.packets[0], desktop.key).Name, '中文姓名 · 工位甲');
  const second = signed(desktop.key, { Type: 'hello', Id: null, Name: '重命名后的电脑' });
  await request(app, '/api/desktop/packet', { token: desktop.token, body: { packet: second } });
  await request(app, '/api/desktop/packet', { token: desktop.token, body: { packet: first } });
  const multiple = await request(app, '/api/desktop/poll?cursor=0', { token: desktop.token });
  assert.equal(multiple.body.packets.length, 2);
  assert.equal(multiple.body.cursor, 2);
  const observer = await register(app, { key: desktop.key, body: { nodeId: 'new-observer' } });
  assert.equal(observer.cursor, 2);
  const fresh = await request(app, '/api/desktop/poll?cursor=2', { token: observer.token });
  assert.deepEqual(fresh.body.packets, []);
});

test('different room polling cannot receive another office reminder', async (t) => {
  const app = await fixture(t);
  const first = await register(app);
  const other = await register(app, { body: { nodeId: 'other-office' } });
  await request(app, '/api/desktop/packet', { token: first.token, body: { packet: signed(first.key) } });
  const result = await request(app, '/api/desktop/poll?cursor=0', { token: other.token });
  assert.deepEqual(result.body.packets, []);
  assert.equal(result.body.cursor, 0);
});

test('one-time pairing, mobile reminders, software and human receipts use matching signatures', async (t) => {
  const app = await fixture(t);
  const desktop = await register(app);
  const phone = await pair(app, desktop);
  assert.match(phone.nodeId, /^mobile-[a-f0-9]{32}$/u);
  assert.equal(phone.name, '李四的手机');
  const repeat = await request(app, '/api/mobile/pair', { body: { code: phone.usedCode, name: '另一台手机' } });
  assert.equal(repeat.status, 400);
  const alert = await request(app, '/api/mobile/alert', { token: phone.token, body: {} });
  assert.equal(alert.status, 200);
  const downlink = await request(app, `/api/desktop/poll?cursor=${phone.cursor}`, { token: desktop.token });
  const packet = decode(downlink.body.packets[0], desktop.key);
  assert.equal(packet.Id, alert.body.alertId);
  assert.equal(packet.SenderId, phone.nodeId);
  assert.equal(packet.Name, '李四的手机');
  const desktopRaw = signed(desktop.key);
  const desktopPacket = JSON.parse(JSON.parse(desktopRaw).Payload);
  await request(app, '/api/desktop/packet', { token: desktop.token, body: { packet: desktopRaw } });
  for (const seen of [false, true]) {
    const result = await request(app, '/api/mobile/ack', { token: phone.token, body: { alertId: desktopPacket.Id, seen } });
    assert.equal(result.status, 200);
  }
  const receipts = await request(app, `/api/desktop/poll?cursor=${downlink.body.cursor}`, { token: desktop.token });
  const receiptPackets = receipts.body.packets.map((raw) => decode(raw, desktop.key));
  assert.equal(receiptPackets.length, 3);
  assert.deepEqual(receiptPackets.slice(1).map((item) => item.Type), ['ack', 'seen']);
  for (const receipt of receiptPackets.slice(1)) {
    assert.equal(receipt.Id, desktopPacket.Id);
    assert.equal(receipt.TargetId, desktopPacket.SenderId);
    assert.equal(receipt.AlertTime, desktopPacket.AlertTime);
  }
  assert.equal((await request(app, '/api/mobile/alert', { token: phone.token, body: {} })).status, 429);
});

test('mobile poll filters hello and Chinese rename broadcasts immediately to desktop', async (t) => {
  const app = await fixture(t);
  const desktop = await register(app);
  const phone = await pair(app, desktop);
  const renamed = await request(app, '/api/mobile/name', { token: phone.token, body: { name: '研发部 · 小米手机' } });
  assert.equal(renamed.status, 200);
  const desktopPoll = await request(app, `/api/desktop/poll?cursor=${phone.cursor}`, { token: desktop.token });
  assert.equal(decode(desktopPoll.body.packets[0], desktop.key).Name, '研发部 · 小米手机');
  const mobilePoll = await request(app, `/api/mobile/poll?cursor=${phone.cursor}`, { token: phone.token });
  assert.deepEqual(mobilePoll.body.packets, []);
  assert.ok(mobilePoll.body.cursor > phone.cursor);
  app.hub.maintenance();
  const heartbeat = await request(app, `/api/desktop/poll?cursor=${mobilePoll.body.cursor}`, { token: desktop.token });
  assert.equal(decode(heartbeat.body.packets[0], desktop.key).Type, 'hello');
});

test('rebinding revokes the old token and unbinding terminates its pending poll', async (t) => {
  const app = await fixture(t);
  const officeA = await register(app);
  const oldPhone = await pair(app, officeA);
  const officeB = await register(app, { body: { nodeId: 'another-office' } });
  const phone = await pair(app, officeB, '调动后的手机', oldPhone.token);
  assert.notEqual(phone.roomId, oldPhone.roomId);
  assert.equal((await request(app, `/api/mobile/poll?cursor=${oldPhone.cursor}`, { token: oldPhone.token })).status, 401);
  const poll = request(app, `/api/mobile/poll?cursor=${phone.cursor}`, { token: phone.token });
  await new Promise((resolve) => setTimeout(resolve, 10));
  assert.equal((await request(app, '/api/mobile/unbind', { token: phone.token, body: {} })).status, 200);
  assert.equal((await poll).status, 401);
  assert.equal((await request(app, '/api/mobile/alert', { token: phone.token, body: {} })).status, 401);
  const persisted = JSON.parse(fs.readFileSync(path.join(app.directory, 'state.json'), 'utf8'));
  assert.deepEqual(persisted.mobiles, []);
});

test('expired pairing codes and repeated guesses fail without consuming another valid code', async (t) => {
  let now = Date.now();
  const app = await fixture(t, { now: () => now });
  const desktop = await register(app);
  const code = await request(app, '/api/desktop/pair-code', { token: desktop.token, body: {} });
  now += 300_001;
  const expired = await request(app, '/api/mobile/pair', { body: { code: code.body.code, name: '手机' } });
  assert.equal(expired.status, 400);
  for (let index = 0; index < 4; index++)
    assert.equal((await request(app, '/api/mobile/pair', { body: { code: 'not-code', name: '手机' } })).status, 400);
  assert.equal((await request(app, '/api/mobile/pair', { body: { code: '12345678', name: '手机' } })).status, 429);
});

test('mobile token and bound subnet survive restart, with a safely reset frame cursor', async (t) => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'mail-relay-restart-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const first = await fixture(t, { dataDirectory: directory, keepData: true });
  const desktop = await register(first);
  const phone = await pair(first, desktop);
  const persistedText = fs.readFileSync(path.join(directory, 'state.json'), 'utf8');
  assert.equal(persistedText.includes(phone.token), false);
  assert.equal(persistedText.includes(desktop.token), false);
  await first.close();
  const second = await fixture(t, { dataDirectory: directory, keepData: true });
  const poll = await request(second, `/api/mobile/poll?cursor=${phone.cursor}`, { token: phone.token });
  assert.equal(poll.status, 200);
  assert.deepEqual(poll.body.packets, []);
  assert.equal(poll.body.cursor, 0);
  const alert = await request(second, '/api/mobile/alert', { token: phone.token, body: { isTest: true } });
  assert.equal(alert.status, 200);
  const roomId = deriveRoomId(desktop.key, '192.168.20.0/24');
  assert.ok(second.hub.rooms.has(roomId));
});

test('invalid JSON, oversize bodies, foreign origins, invalid signatures, stale and malformed packets are rejected', async (t) => {
  const app = await fixture(t);
  const desktop = await register(app);
  const invalidBodies = [null, [], 'string'];
  for (const body of invalidBodies)
    assert.equal((await request(app, '/api/mobile/pair', { body })).status, 400);
  const malformed = await fetch(`${app.base}/api/mobile/pair`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{' });
  assert.equal(malformed.status, 400);
  await malformed.arrayBuffer();
  const huge = await request(app, '/api/mobile/pair', { body: { name: 'x'.repeat(20_000) } });
  assert.equal(huge.status, 413);
  const origin = await request(app, '/api/health', { headers: { origin: 'https://unrelated.example' } });
  assert.equal(origin.status, 403);
  const packets = [
    'null', '{}', '[]', signed(crypto.randomBytes(32)),
    signed(desktop.key, { Time: Date.now() - 300_000 }),
    signed(desktop.key, { AlertTime: Date.now() + 300_000 }),
    signed(desktop.key, { Name: '不合法\n名称' }),
    signed(desktop.key, { Type: 'command' }),
    signed(desktop.key, { Id: 'invalid' }),
    signed(desktop.key, { Type: 'seen', TargetId: 'invalid\n' }),
  ];
  for (const packet of packets)
    assert.equal((await request(app, '/api/desktop/packet', { token: desktop.token, body: { packet } })).status, 400);
  const poll = await request(app, '/api/desktop/poll?cursor=0', { token: desktop.token });
  assert.deepEqual(poll.body.packets, []);
  assert.equal((await request(app, '/api/desktop/poll?cursor=-1', { token: desktop.token })).status, 400);
  assert.equal((await request(app, '/api/desktop/poll?cursor=0')).status, 401);
  assert.equal((await request(app, '/api/mobile/alert', { token: desktop.token, body: {} })).status, 401);
});

test('retention is bounded, expired frames disappear and application close wakes pending clients', async (t) => {
  let now = Date.now();
  const app = await fixture(t, { now: () => now, maxFrames: 2, pollTimeoutMs: 1000 });
  const desktop = await register(app);
  for (let i = 0; i < 5; i++)
    await request(app, '/api/desktop/packet', { token: desktop.token, body: { packet: signed(desktop.key) } });
  let poll = await request(app, '/api/desktop/poll?cursor=0', { token: desktop.token });
  assert.equal(poll.body.packets.length, 2);
  assert.equal(poll.body.cursor, 5);
  now += 120_001;
  app.hub.maintenance();
  assert.equal(app.hub.rooms.get(desktop.roomId).frames.length, 0);
  const waiting = request(app, '/api/desktop/poll?cursor=5', { token: desktop.token });
  await new Promise((resolve) => setTimeout(resolve, 20));
  await app.close();
  poll = await waiting;
  assert.equal(poll.status, 503);
});

test('public landing page and APK path expose only the configured download', async (t) => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'mail-relay-apk-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const apk = path.join(directory, 'example.apk');
  fs.writeFileSync(apk, Buffer.from('test-apk'));
  const app = await fixture(t, { apkFile: apk });
  const page = await fetch(`${app.base}/`);
  assert.equal(page.status, 200);
  assert.match(await page.text(), /邮件提醒助手/u);
  assert.equal(page.headers.get('x-frame-options'), 'DENY');
  const head = await fetch(`${app.base}/`, { method: 'HEAD' });
  assert.equal(head.status, 200);
  assert.equal((await head.arrayBuffer()).byteLength, 0);
  const download = await fetch(`${app.base}/download/mail-reminder-android-1.3.0.apk`);
  assert.equal(download.status, 200);
  assert.equal(await download.text(), 'test-apk');
  const traversal = await request(app, '/state.json');
  assert.equal(traversal.status, 404);
});

test('update manifests hash exact whitelisted files and downloads support HEAD', async (t) => {
  const releases = fs.mkdtempSync(path.join(os.tmpdir(), 'mail-relay-updates-'));
  t.after(() => fs.rmSync(releases, { recursive: true, force: true }));
  const app = await fixture(t, { releasesDirectory: releases });
  assert.equal((await request(app, '/api/updates/windows')).status, 503);
  assert.equal((await request(app, '/api/updates/android')).status, 503);
  const releaseFiles = {
    windows: ['邮件提醒助手-1.3.0.zip', Buffer.from('windows-update-without-team-key')],
    android: ['mail-reminder-android-1.3.0.apk', Buffer.from('signed-android-apk')],
  };
  for (const [platform, [fileName, bytes]] of Object.entries(releaseFiles)) {
    fs.writeFileSync(path.join(releases, fileName), bytes);
    const manifest = await request(app, `/api/updates/${platform}`);
    assert.equal(manifest.status, 200);
    assert.equal(manifest.body.version, '1.3.0');
    assert.equal(manifest.body.versionCode, 130);
    assert.equal(manifest.body.size, bytes.length);
    assert.equal(manifest.body.sha256, crypto.createHash('sha256').update(bytes).digest('hex'));
    const downloadUrl = new URL(manifest.body.url);
    assert.equal(downloadUrl.origin, 'https://blog.feian.online');
    const relativeRoute = downloadUrl.pathname.slice('/mail-reminder'.length);
    const download = await fetch(`${app.base}${relativeRoute}`);
    assert.equal(download.status, 200);
    assert.deepEqual(Buffer.from(await download.arrayBuffer()), bytes);
    assert.equal(download.headers.get('x-content-type-options'), 'nosniff');
    const head = await fetch(`${app.base}${relativeRoute}`, { method: 'HEAD' });
    assert.equal(head.status, 200);
    assert.equal(Number(head.headers.get('content-length')), bytes.length);
    assert.equal((await head.arrayBuffer()).byteLength, 0);
  }
  assert.equal((await request(app, '/api/updates/macos')).status, 404);
  assert.equal((await request(app, '/download/state.json')).status, 404);
});

test('large polling batches advance by the final returned frame without dropping the remainder', async (t) => {
  const app = await fixture(t, { maxPollPackets: 2 });
  const desktop = await register(app);
  for (let index = 0; index < 3; index++)
    await request(app, '/api/desktop/packet', { token: desktop.token, body: { packet: signed(desktop.key) } });
  const first = await request(app, '/api/desktop/poll?cursor=0', { token: desktop.token });
  assert.equal(first.body.packets.length, 2);
  assert.equal(first.body.cursor, 2);
  const next = await request(app, `/api/desktop/poll?cursor=${first.body.cursor}`, { token: desktop.token });
  assert.equal(next.body.packets.length, 1);
  assert.equal(next.body.cursor, 3);
  assert.notEqual(next.body.packets[0], first.body.packets[0]);
});

test('successful phones behind the same office NAT can bind without consuming the failed-code budget', async (t) => {
  const app = await fixture(t);
  const desktop = await register(app);
  for (let index = 0; index < 6; index++) {
    const phone = await pair(app, desktop, `办公室手机${index}`);
    assert.equal(phone.roomId, desktop.roomId);
  }
  assert.equal(app.hub.state.mobiles.length, 6);
});
