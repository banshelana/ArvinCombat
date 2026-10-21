#!/usr/bin/env node
/*
 * ArvinCombat game server (no npm packages needed, Node 18+).
 *
 *  - serves the Unity WebGL build from ./public   (http://host:PORT/)
 *  - WebSocket endpoint /ws for online 1v1 battles
 *  - matchmaking pairs players with a similar power rating (upgrade level),
 *    widening the range the longer they wait
 *  - runs rounds (best of 3, timer), relays fighter state, sanity-checks hits
 *
 *  Run:  PORT=8090 node arvincombat-server.js
 */
'use strict';
const http = require('http');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const PORT = parseInt(process.env.PORT || '8090', 10);
const PUBLIC_DIR = path.resolve(process.env.PUBLIC_DIR || path.join(__dirname, 'public'));
const PROTOCOL_VERSION = 2;
const ROUNDS_TO_WIN = 2;
const ROUND_TIME = 75;          // seconds
const COUNTDOWN = 2.5;          // seconds between "round" and "fight"
const BETWEEN_ROUNDS = 3.5;     // seconds after a round ends
const MAX_HIT_DISTANCE = 5.5;   // meters (longest reach + lunge + lag margin)
const MAX_HIT_DAMAGE = 900;
const MAX_HITS_PER_SECOND = 8;

const log = (...a) => console.log(new Date().toISOString(), ...a);

// ------------------------------------------------------------------ static files (WebGL build)

const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'application/javascript', '.wasm': 'application/wasm',
  '.data': 'application/octet-stream', '.json': 'application/json', '.css': 'text/css',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.ico': 'image/x-icon', '.svg': 'image/svg+xml',
  '.txt': 'text/plain', '.unityweb': 'application/octet-stream', '.symbols.json': 'application/json'
};

function serveStatic(req, res) {
  let urlPath = decodeURIComponent((req.url || '/').split('?')[0]);
  if (urlPath === '/status') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ ok: true, online: clients.size, queued: queue.length, rooms: rooms.size }));
    return;
  }
  if (urlPath.endsWith('/')) urlPath += 'index.html';
  const file = path.normalize(path.join(PUBLIC_DIR, urlPath));
  if (!file.startsWith(PUBLIC_DIR)) { res.writeHead(403); res.end(); return; }

  fs.stat(file, (err, st) => {
    if (err || !st.isFile()) {
      if (urlPath === '/index.html') {
        res.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8' });
        res.end('ArvinCombat server is running.\nUpload the WebGL build into ' + PUBLIC_DIR + '\n');
      } else { res.writeHead(404); res.end('Not found'); }
      return;
    }
    const headers = { 'Content-Length': st.size, 'Cache-Control': urlPath.endsWith('.html') ? 'no-cache' : 'public, max-age=86400' };
    let name = path.basename(file);
    // Unity compressed build files: Foo.wasm.gz / Foo.data.br
    if (name.endsWith('.gz')) { headers['Content-Encoding'] = 'gzip'; name = name.slice(0, -3); }
    else if (name.endsWith('.br')) { headers['Content-Encoding'] = 'br'; name = name.slice(0, -3); }
    headers['Content-Type'] = MIME[path.extname(name).toLowerCase()] || 'application/octet-stream';
    res.writeHead(200, headers);
    if (req.method === 'HEAD') { res.end(); return; }
    fs.createReadStream(file).pipe(res);
  });
}

// ------------------------------------------------------------------ minimal WebSocket (RFC 6455)

const WS_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

function encodeFrame(opcode, payload) {
  const len = payload.length;
  let header;
  if (len < 126) { header = Buffer.alloc(2); header[1] = len; }
  else if (len < 65536) { header = Buffer.alloc(4); header[1] = 126; header.writeUInt16BE(len, 2); }
  else { header = Buffer.alloc(10); header[1] = 127; header.writeBigUInt64BE(BigInt(len), 2); }
  header[0] = 0x80 | opcode;
  return Buffer.concat([header, payload]);
}

function parseFrame(buf) {
  if (buf.length < 2) return null;
  const fin = (buf[0] & 0x80) !== 0;
  const opcode = buf[0] & 0x0f;
  const masked = (buf[1] & 0x80) !== 0;
  let len = buf[1] & 0x7f;
  let off = 2;
  if (len === 126) { if (buf.length < 4) return null; len = buf.readUInt16BE(2); off = 4; }
  else if (len === 127) { if (buf.length < 10) return null; len = Number(buf.readBigUInt64BE(2)); off = 10; }
  if (len > 1 << 20) return { error: true };
  let mask = null;
  if (masked) { if (buf.length < off + 4) return null; mask = buf.subarray(off, off + 4); off += 4; }
  if (buf.length < off + len) return null;
  const payload = Buffer.from(buf.subarray(off, off + len));
  if (mask) for (let i = 0; i < payload.length; i++) payload[i] ^= mask[i & 3];
  return { fin, opcode, payload, size: off + len };
}

let nextId = 1;
const clients = new Map();   // id -> Client

class Client {
  constructor(socket) {
    this.id = nextId++;
    this.socket = socket;
    this.buf = Buffer.alloc(0);
    this.fragments = [];
    this.alive = true;
    this.closed = false;
    this.name = 'Player' + this.id;
    this.warrior = 0;
    this.ranks = [0, 0, 0, 0, 0];
    this.rating = 0;
    this.room = null;
    this.side = -1;
    this.last = { x: 0, z: 0, hp: 1, mhp: 1 };
    this.hitTimes = [];
    socket.on('data', d => this.onData(d));
    socket.on('close', () => this.onClose());
    socket.on('error', () => this.onClose());
  }

  send(obj) {
    if (this.closed) return;
    try { this.socket.write(encodeFrame(0x1, Buffer.from(JSON.stringify(obj)))); } catch (e) { this.onClose(); }
  }

  sendRaw(text) {
    if (this.closed) return;
    try { this.socket.write(encodeFrame(0x1, Buffer.from(text))); } catch (e) { this.onClose(); }
  }

  onData(d) {
    this.buf = this.buf.length ? Buffer.concat([this.buf, d]) : d;
    for (;;) {
      const f = parseFrame(this.buf);
      if (!f) break;
      if (f.error) { this.close(); return; }
      this.buf = this.buf.subarray(f.size);
      switch (f.opcode) {
        case 0x0: // continuation
        case 0x1: // text
          this.fragments.push(f.payload);
          if (f.fin) {
            const text = Buffer.concat(this.fragments).toString('utf8');
            this.fragments = [];
            this.onText(text);
          }
          break;
        case 0x8: this.close(); return;
        case 0x9: try { this.socket.write(encodeFrame(0xA, f.payload)); } catch (e) { } break;
        case 0xA: this.alive = true; break;
        default: break; // binary ignored
      }
    }
  }

  onText(text) {
    let m;
    try { m = JSON.parse(text); } catch (e) { return; }
    if (!m || typeof m.t !== 'string') return;
    this.alive = true;
    handleMessage(this, m, text);
  }

  close() {
    if (this.closed) return;
    try { this.socket.write(encodeFrame(0x8, Buffer.alloc(0))); } catch (e) { }
    try { this.socket.end(); } catch (e) { }
    this.onClose();
  }

  onClose() {
    if (this.closed) return;
    this.closed = true;
    clients.delete(this.id);
    removeFromQueue(this);
    if (this.room) this.room.playerLeft(this);
    log('disconnect', this.id, this.name, 'online', clients.size);
  }
}

// ------------------------------------------------------------------ matchmaking

const queue = [];  // { client, since }

function removeFromQueue(c) {
  const i = queue.findIndex(q => q.client === c);
  if (i >= 0) queue.splice(i, 1);
}

function tolerance(waitSeconds) {
  // power rating = sum of upgrade ranks (0..50)
  if (waitSeconds > 25) return Infinity;
  return 3 + Math.floor(waitSeconds / 5) * 4;
}

function matchmake() {
  if (queue.length < 2) return;
  const now = Date.now();
  queue.sort((a, b) => a.since - b.since);
  for (let i = 0; i < queue.length; i++) {
    const a = queue[i];
    const tolA = tolerance((now - a.since) / 1000);
    let best = -1, bestDiff = Infinity;
    for (let j = 0; j < queue.length; j++) {
      if (j === i) continue;
      const b = queue[j];
      const diff = Math.abs(a.client.rating - b.client.rating);
      const tolB = tolerance((now - b.since) / 1000);
      if (diff <= Math.max(tolA, tolB) && diff < bestDiff) { best = j; bestDiff = diff; }
    }
    if (best >= 0) {
      const b = queue[best];
      removeFromQueue(a.client);
      removeFromQueue(b.client);
      new Room(a.client, b.client);
      return matchmake();
    }
  }
}

// ------------------------------------------------------------------ rooms

const rooms = new Set();

class Room {
  constructor(a, b) {
    this.players = [a, b];
    this.scores = [0, 0];
    this.round = 0;
    this.phase = 'starting';
    this.timer = null;
    this.roundEndsAt = 0;
    this.done = false;
    a.room = this; a.side = 0; a.hitTimes = []; a.last = { x: 0, z: -2.8, hp: 1, mhp: 1 };
    b.room = this; b.side = 1; b.hitTimes = []; b.last = { x: 0, z: 2.8, hp: 1, mhp: 1 };
    rooms.add(this);
    for (const p of this.players) {
      const o = this.other(p);
      p.send({ t: 'match', side: p.side, oppName: o.name, oppWarrior: o.warrior, oppRanks: o.ranks, oppRating: o.rating, rounds: ROUNDS_TO_WIN, roundTime: ROUND_TIME });
    }
    log('match', a.name, `(${a.rating})`, 'vs', b.name, `(${b.rating})`);
    this.later(1.5, () => this.startRound());
  }

  other(p) { return this.players[0] === p ? this.players[1] : this.players[0]; }
  broadcast(obj) { for (const p of this.players) p.send(obj); }
  later(sec, fn) { clearTimeout(this.timer); this.timer = setTimeout(fn, sec * 1000); }

  startRound() {
    if (this.done) return;
    this.round++;
    this.phase = 'countdown';
    for (const p of this.players) p.last.hp = p.last.mhp;
    this.broadcast({ t: 'round', n: this.round, sa: this.scores[0], sb: this.scores[1] });
    this.later(COUNTDOWN, () => {
      this.phase = 'fight';
      this.roundEndsAt = Date.now() + ROUND_TIME * 1000;
      this.broadcast({ t: 'fight', roundTime: ROUND_TIME });
      this.later(ROUND_TIME, () => this.timeUp());
    });
  }

  timeUp() {
    if (this.phase !== 'fight') return;
    const f = this.players.map(p => p.last.mhp > 0 ? p.last.hp / p.last.mhp : 0);
    const w = f[0] === f[1] ? -1 : (f[0] > f[1] ? 0 : 1);
    this.endRound(w, 'time');
  }

  onDead(p) {
    if (this.phase !== 'fight') return;
    this.endRound(this.other(p).side, 'ko');
  }

  endRound(winner, reason) {
    this.phase = 'between';
    if (winner >= 0) this.scores[winner]++;
    this.broadcast({ t: 'rend', w: winner, sa: this.scores[0], sb: this.scores[1], reason });
    const over = this.scores[0] >= ROUNDS_TO_WIN || this.scores[1] >= ROUNDS_TO_WIN || this.round >= 7;
    this.later(BETWEEN_ROUNDS, () => over ? this.finish(this.scores[0] === this.scores[1] ? -1 : (this.scores[0] > this.scores[1] ? 0 : 1), 'normal') : this.startRound());
  }

  finish(winner, reason) {
    if (this.done) return;
    this.done = true;
    clearTimeout(this.timer);
    this.broadcast({ t: 'mend', w: winner, sa: this.scores[0], sb: this.scores[1], reason });
    log('match end', this.players[0].name, this.scores[0], '-', this.scores[1], this.players[1].name, reason);
    for (const p of this.players) { p.room = null; p.side = -1; }
    rooms.delete(this);
  }

  playerLeft(p) {
    if (this.done) return;
    const o = this.other(p);
    this.finish(o.side, 'left');
  }

  relay(from, text) {
    const o = this.other(from);
    if (!o.closed) o.sendRaw(text);
  }

  validateHit(from, m) {
    if (this.phase !== 'fight') return false;
    const o = this.other(from);
    const dx = from.last.x - o.last.x, dz = from.last.z - o.last.z;
    if (Math.sqrt(dx * dx + dz * dz) > MAX_HIT_DISTANCE) return false;
    const now = Date.now();
    from.hitTimes = from.hitTimes.filter(t => now - t < 1000);
    if (from.hitTimes.length >= MAX_HITS_PER_SECOND) return false;
    from.hitTimes.push(now);
    const dmg = Number(m.dmg);
    return isFinite(dmg) && dmg >= 0 && dmg <= MAX_HIT_DAMAGE;
  }
}

// ------------------------------------------------------------------ messages

function cleanName(n) {
  n = String(n || '').replace(/[<>\u0000-\u001f]/g, '').trim();
  return (n || 'Player').slice(0, 16);
}

function handleMessage(c, m, text) {
  switch (m.t) {
    case 'hello':
      if ((m.ver | 0) !== PROTOCOL_VERSION) { c.send({ t: 'err', txt: 'Game version is out of date. Please refresh the page.' }); return; }
      c.name = cleanName(m.name);
      c.warrior = Math.max(0, Math.min(5, m.warrior | 0));
      c.ranks = Array.isArray(m.ranks) ? m.ranks.slice(0, 5).map(r => Math.max(0, Math.min(10, r | 0))) : [0, 0, 0, 0, 0];
      while (c.ranks.length < 5) c.ranks.push(0);
      c.rating = c.ranks.reduce((s, r) => s + r, 0);
      c.send({ t: 'welcome', online: clients.size });
      break;
    case 'queue':
      if (c.room) return;
      removeFromQueue(c);
      queue.push({ client: c, since: Date.now() });
      c.send({ t: 'queued', online: clients.size });
      matchmake();
      break;
    case 'cancel':
      removeFromQueue(c);
      break;
    case 'leave':
      removeFromQueue(c);
      if (c.room) c.room.playerLeft(c);
      break;
    case 'st':
      if (!c.room) return;
      c.last.x = +m.x || 0; c.last.z = +m.z || 0;
      if (typeof m.hp === 'number') c.last.hp = m.hp;
      if (typeof m.mhp === 'number' && m.mhp > 0) c.last.mhp = m.mhp;
      c.room.relay(c, text);
      break;
    case 'hit':
      if (c.room && c.room.validateHit(c, m)) c.room.relay(c, text);
      break;
    case 'res': case 'pop': case 'cmd':
      if (c.room) c.room.relay(c, text);
      break;
    case 'dead':
      if (c.room) c.room.onDead(c);
      break;
    default: break;
  }
}

// ------------------------------------------------------------------ server

const server = http.createServer(serveStatic);

server.on('upgrade', (req, socket) => {
  const url = (req.url || '').split('?')[0];
  const key = req.headers['sec-websocket-key'];
  if (!key || (req.headers.upgrade || '').toLowerCase() !== 'websocket' || !(url === '/ws' || url.endsWith('/ws'))) {
    socket.write('HTTP/1.1 400 Bad Request\r\n\r\n'); socket.destroy(); return;
  }
  const accept = crypto.createHash('sha1').update(key + WS_GUID).digest('base64');
  socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ' + accept + '\r\n\r\n');
  socket.setNoDelay(true);
  const c = new Client(socket);
  clients.set(c.id, c);
  log('connect', c.id, 'online', clients.size);
});

setInterval(matchmake, 1000);

// heartbeat: drop dead connections
setInterval(() => {
  for (const c of clients.values()) {
    if (!c.alive) { c.close(); continue; }
    c.alive = false;
    try { c.socket.write(encodeFrame(0x9, Buffer.alloc(0))); } catch (e) { c.onClose(); }
  }
}, 20000);

server.listen(PORT, () => log(`ArvinCombat server on port ${PORT}, serving ${PUBLIC_DIR}`));

module.exports = { server };
