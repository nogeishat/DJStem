import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Inbox } from './lib/inbox.mjs';
import { DownloadWorker, findDownloader, toolVersion } from './lib/downloader.mjs';
import { StemSeparator } from './lib/stems.mjs';
import { libraryRoot } from './lib/library.mjs';
import { MusicAnalyzer } from './lib/analysis.mjs';
import { renameArtist, setArtistCover } from './lib/artists.mjs';
import { deleteSong } from './lib/song-delete.mjs';
import { dataPath } from './lib/paths.mjs';

export const ROOT = path.dirname(fileURLToPath(import.meta.url));
const TYPES = { '.mp3': 'audio/mpeg' };

async function readBody(req) {
  if (!req.headers['content-type']?.startsWith('application/json')) throw new Error('Expected application/json.');
  let body = '';
  for await (const chunk of req) {
    body += chunk;
    if (Buffer.byteLength(body) > 65536) throw new Error('Too many links at once. Please submit a smaller batch.');
  }
  return JSON.parse(body);
}

export function createApp({ root = ROOT, dataDir = dataPath(root), downloadsDir = libraryRoot(root), command = findDownloader(root), downloaderVersion, ffmpeg, separator = new StemSeparator({ root, libraryRoot: downloadsDir }), analyzer = new MusicAnalyzer({ root }), workerFactory } = {}) {
  fs.mkdirSync(downloadsDir, { recursive: true });
  const inbox = new Inbox(dataDir, { enableStems: Boolean(separator), enableAnalysis: Boolean(analyzer) });
  const version = downloaderVersion === undefined ? toolVersion(command) : downloaderVersion;
  const hasFfmpeg = ffmpeg === undefined ? Boolean(toolVersion('ffmpeg', ['-version'])) : ffmpeg;
  const worker = workerFactory ? workerFactory({ inbox, downloadsDir, command, ffmpeg: hasFfmpeg, separator, analyzer }) : new DownloadWorker({ inbox, downloadsDir, command, ffmpeg: hasFfmpeg, separator, analyzer });
  const json = (res, code, value) => { res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8' }); res.end(JSON.stringify(value)); };
  const server = http.createServer(async (req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('Content-Security-Policy', "default-src 'self'; img-src 'self' https://i.ytimg.com; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
    const expectedHost = `127.0.0.1:${server.address()?.port}`;
    if (req.headers.host !== expectedHost || (req.headers.origin && req.headers.origin !== `http://${expectedHost}`)) return json(res, 403, { error: 'Use the local StemKit address shown in the terminal.' });
    try {
      const url = new URL(req.url, `http://${expectedHost}`);
      if (req.method === 'GET' && url.pathname === '/api/state') {
        return json(res, 200, { outputMode: 'mp3', supportedSources: ['youtube', 'soundcloud'], libraryLayout: 2, artistFolders: true, songDeletion: true, stemPipeline: Boolean(separator), musicAnalysis: Boolean(analyzer), paused: inbox.state.paused, jobs: [...inbox.state.jobs].reverse(), downloadsDir, downloaderVersion: version, ffmpeg: hasFfmpeg });
      }
      if (req.method === 'POST' && url.pathname === '/api/songs/delete') {
        const body = await readBody(req);
        return json(res, 200, deleteSong({ root: downloadsDir, dataDir, audio: body.audio, state: inbox.state }));
      }
      if (req.method === 'POST' && url.pathname === '/api/artists') {
        const body = await readBody(req);
        if (body.operation === 'rename') {
          if (worker.running) throw new Error('Wait for the current download or stem processing to finish before renaming an artist folder.');
          return json(res, 200, renameArtist({ root: downloadsDir, from: body.name, to: body.newName, state: inbox.state, saveState: () => inbox.save() }));
        }
        if (body.operation === 'cover') return json(res, 200, setArtistCover({ root: downloadsDir, name: body.name, image: body.image }));
        throw new Error('Unknown artist folder operation.');
      }
      if (req.method === 'GET' && url.pathname === '/api/links') {
        res.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8', 'Content-Disposition': 'attachment; filename="links.txt"' });
        return res.end(fs.readFileSync(path.join(dataDir, 'links.txt')));
      }
      if (req.method === 'POST' && url.pathname === '/api/links') {
        const body = await readBody(req);
        const result = inbox.add(body.text);
        json(res, 200, result);
        if (version) void worker.kick();
        return;
      }
      if (req.method === 'POST' && url.pathname === '/api/pause') {
        const body = await readBody(req);
        if (typeof body.paused !== 'boolean') throw new Error('paused must be a boolean.');
        inbox.state.paused = body.paused;
        inbox.save();
        json(res, 200, { paused: body.paused });
        if (!body.paused && version) void worker.kick();
        return;
      }
      if (req.method === 'POST' && /^\/api\/jobs\/[^/]+\/retry$/.test(url.pathname)) {
        await readBody(req);
        inbox.retry(url.pathname.split('/')[3]);
        json(res, 200, { ok: true });
        if (version) void worker.kick();
        return;
      }
      if (req.method === 'GET' && /^\/api\/jobs\/[^/]+\/file$/.test(url.pathname)) {
        const job = inbox.state.jobs.find(job => job.id === url.pathname.split('/')[3]);
        if (!job?.filename || job.status !== 'completed') return json(res, 404, { error: 'No saved MP3 for this item.' });
        const file = path.resolve(downloadsDir, job.filename);
        const relative = path.relative(downloadsDir, file);
        if (relative.startsWith('..') || path.isAbsolute(relative) || !fs.existsSync(file)) return json(res, 404, { error: 'The downloaded file was moved or deleted.' });
        const size = fs.statSync(file).size;
        res.setHeader('Content-Type', TYPES[path.extname(file)] || 'application/octet-stream');
        res.setHeader('Accept-Ranges', 'bytes');
        const range = req.headers.range;
        if (range) {
          const match = /^bytes=(\d+)-(\d*)$/.exec(range);
          const start = match ? Number(match[1]) : NaN;
          const end = match?.[2] ? Math.min(Number(match[2]), size - 1) : size - 1;
          if (!Number.isSafeInteger(start) || start < 0 || start >= size || end < start) { res.writeHead(416, { 'Content-Range': `bytes */${size}` }); return res.end(); }
          res.writeHead(206, { 'Content-Range': `bytes ${start}-${end}/${size}`, 'Content-Length': end - start + 1 });
          return fs.createReadStream(file, { start, end }).on('error', error => res.destroy(error)).pipe(res);
        }
        res.writeHead(200, { 'Content-Length': size });
        return fs.createReadStream(file).on('error', error => res.destroy(error)).pipe(res);
      }
      json(res, 404, { error: 'Not found.' });
    } catch (error) { if (!res.headersSent) json(res, 400, { error: error.message }); else res.destroy(); }
  });
  server.on('close', () => worker.stop());
  return { server, inbox, worker, version, downloadsDir };
}

export async function startServer({ port = Number(process.env.PORT || 4317), ...options } = {}) {
  const app = createApp(options);
  await new Promise((resolve, reject) => {
    app.server.once('error', reject);
    app.server.listen(port, '127.0.0.1', resolve);
  });
  const address = `http://127.0.0.1:${app.server.address().port}`;
  console.log(`StemKit downloader is ready: ${address}\nMusic library: ${app.downloadsDir}`);
  if (app.version) void app.worker.kick();
  return { ...app, address };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const app = await startServer();
  const stop = () => { app.worker.stop(); app.server.close(); setTimeout(() => process.exit(0), 2000).unref(); };
  process.once('SIGINT', stop);
  process.once('SIGTERM', stop);
}
