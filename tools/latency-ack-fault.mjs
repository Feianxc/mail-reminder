import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { createRelayServer } from '../server/relay.mjs';

const dataDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'mail-latency-ack-'));
const application = createRelayServer({ dataDirectory, pollTimeoutMs: 25_000, heartbeatMs: 60_000 });
const ackDelayMs = 15_000;
let delayedToken;
let firstAckStarted;
const ackStarted = new Promise((resolve) => { firstAckStarted = resolve; });
const proxy = http.createServer(async (request, response) => {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  const delayed = request.url.endsWith('/api/mobile/ack') && request.headers.authorization === `Bearer ${delayedToken}`;
  const upstream = await fetch(`http://127.0.0.1:${application.server.address().port}${request.url}`, {
    method: request.method,
    headers: { 'content-type': 'application/json', ...(request.headers.authorization ? { authorization: request.headers.authorization } : {}) },
    body: request.method === 'GET' ? undefined : Buffer.concat(chunks),
  });
  const body = await upstream.text();
  if (delayed) {
    firstAckStarted();
    await new Promise((resolve) => setTimeout(resolve, ackDelayMs));
  }
  response.writeHead(upstream.status, { 'content-type': 'application/json' });
  response.end(body);
});

try {
  application.server.listen(0, '127.0.0.1');
  await once(application.server, 'listening');
  proxy.listen(0, '127.0.0.1');
  await once(proxy, 'listening');
  const base = `http://127.0.0.1:${proxy.address().port}/mail-reminder/`;
  async function call(route, token, body) {
    const response = await fetch(base + route, {
      method: body === undefined ? 'GET' : 'POST',
      headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    assert.equal(response.status, 200);
    return response.json();
  }
  const key = crypto.randomBytes(32);
  const desktop = await call('api/desktop/register', undefined, { roomKey: key.toString('base64'), subnet: '192.168.20.0/24', nodeId: 'fault-desktop', name: '独立本地故障注入', roomName: 'local-only-ack-fault' });
  async function pair(name) {
    const pairCode = await call('api/desktop/pair-code', desktop.token, {});
    return call('api/mobile/pair', undefined, { code: pairCode.code, name });
  }
  const serialized = await pair('串行收取样本');
  const independent = await pair('独立收取样本');
  delayedToken = serialized.token;
  async function alert(id = crypto.randomBytes(16).toString('hex')) {
    const now = Date.now();
    const payload = JSON.stringify({ Version: 1, Type: 'alert', SenderId: 'fault-desktop', Name: '独立本地故障注入', Id: id, TargetId: null, Time: now, AlertTime: now, IsTest: true });
    const packet = JSON.stringify({ Payload: payload, Signature: crypto.createHmac('sha256', key).update(payload).digest('base64') });
    await call('api/desktop/packet', desktop.token, { packet });
    return id;
  }
  const backlog = [await alert(), await alert()];
  const received = await call('api/mobile/poll?cursor=' + serialized.cursor, serialized.token);
  const cursor = received.cursor;
  let wantedId;
  async function receiveUntil(token) {
    let position = cursor;
    for (;;) {
      const batch = await call('api/mobile/poll?cursor=' + position, token);
      position = batch.cursor;
      if (batch.packets.some((raw) => JSON.parse(JSON.parse(raw).Payload).Id === wantedId)) return performance.now();
    }
  }
  const serializedTask = (async () => {
    // Matches the original Android scheduling order. This runs on the host, without Android APIs.
    for (const alertId of backlog) await call('api/mobile/ack', serialized.token, { alertId, seen: false });
    return receiveUntil(serialized.token);
  })();
  await ackStarted;
  const independentTask = receiveUntil(independent.token);
  await new Promise((resolve) => setTimeout(resolve, 100));
  const sentAt = performance.now();
  wantedId = crypto.randomBytes(16).toString('hex');
  await alert(wantedId);
  const [serializedAt, independentAt] = await Promise.all([serializedTask, independentTask]);
  const result = {
    status: 'pass',
    scope: 'Host-only controlled fault injection using real relay HTTP and a delayed ACK-response proxy. This is not a native Android device run.',
    androidApiTimeoutsEmulated: false,
    injectedAckDelayMs: ackDelayMs,
    sequentialAckCount: backlog.length,
    originalSerialSchedulingReceiveMs: Number((serializedAt - sentAt).toFixed(1)),
    independentPollingReceiveMs: Number((independentAt - sentAt).toFixed(1)),
  };
  assert.ok(result.originalSerialSchedulingReceiveMs >= 29_000);
  assert.ok(result.independentPollingReceiveMs < 1000);
  const output = fileURLToPath(new URL('./latency-ack-fault-result.json', import.meta.url));
  fs.writeFileSync(output, JSON.stringify(result, null, 2) + '\n', 'utf8');
  console.log(JSON.stringify(result));
} finally {
  await new Promise((resolve) => { proxy.close(resolve); proxy.closeIdleConnections(); });
  await application.close();
  fs.rmSync(dataDirectory, { recursive: true, force: true });
}
