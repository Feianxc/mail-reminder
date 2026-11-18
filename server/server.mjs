import { createRelayServer } from './relay.mjs';

const port = Number(process.env.PORT ?? 45822);
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Invalid PORT.');
const application = createRelayServer({
  dataDirectory: process.env.MAIL_DATA_DIR,
  publicUrl: process.env.PUBLIC_URL ?? 'https://blog.feian.online/mail-reminder/',
  basePath: process.env.BASE_PATH ?? '/mail-reminder',
  apkFile: process.env.APK_FILE,
  releasesDirectory: process.env.RELEASES_DIR,
});
application.server.listen(port, process.env.HOST ?? '127.0.0.1', () => console.log(`mail-reminder listening on ${process.env.HOST ?? '127.0.0.1'}:${port}`));
let stopping = false;
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, async () => {
  if (stopping) return;
  stopping = true;
  const forced = setTimeout(() => process.exit(1), 8000);
  forced.unref();
  try { await application.close(); process.exit(0); } catch { process.exit(1); }
});
