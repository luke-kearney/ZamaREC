// A minimal WebSocket client for Node versions without a built-in one (the Stream Deck app runs plugins on
// Node 20). Text messages only, which is all the deck protocol uses. Same shape as the browser API as far as
// plugin.js needs it: readyState, send, onopen, onmessage, onclose.
"use strict";

const net = require("net");
const crypto = require("crypto");

class WebSocketLite {
  constructor(url) {
    const u = new URL(url);
    this.readyState = 0;
    this.onopen = this.onmessage = this.onclose = null;
    this._buf = Buffer.alloc(0);
    this._parts = [];
    const key = crypto.randomBytes(16).toString("base64");
    this._sock = net.connect(Number(u.port) || 80, u.hostname, () => {
      this._sock.write(`GET ${u.pathname || "/"} HTTP/1.1\r\nHost: ${u.host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
        `Sec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`);
    });
    this._sock.on("data", d => this._data(d));
    this._sock.on("close", () => { this.readyState = 3; this.onclose?.({}); });
    this._sock.on("error", () => this._sock.destroy());
  }

  _data(d) {
    this._buf = Buffer.concat([this._buf, d]);
    if (this.readyState === 0) {
      const end = this._buf.indexOf("\r\n\r\n");
      if (end < 0) return;
      if (!/^HTTP\/1\.1 101/.test(this._buf.subarray(0, end).toString())) return this._sock.destroy();
      this._buf = this._buf.subarray(end + 4);
      this.readyState = 1;
      this.onopen?.({});
    }
    for (;;) {
      const b = this._buf;
      if (b.length < 2) return;
      let len = b[1] & 0x7f, at = 2;
      if (len === 126) { if (b.length < 4) return; len = b.readUInt16BE(2); at = 4; }
      else if (len === 127) { if (b.length < 10) return; len = Number(b.readBigUInt64BE(2)); at = 10; }
      const masked = b[1] & 0x80;
      if (masked) at += 4;
      if (b.length < at + len) return;
      let body = b.subarray(at, at + len);
      if (masked) { const m = b.subarray(at - 4, at); body = Buffer.from(body.map((x, i) => x ^ m[i % 4])); }
      this._buf = b.subarray(at + len);
      const fin = b[0] & 0x80, op = b[0] & 0x0f;
      if (op === 0x8) { this._sock.end(); return; }
      if (op === 0x9) { this._frame(0xa, body); continue; }
      if (op === 0x1 || op === 0x0) {
        this._parts.push(body);
        if (fin) { const text = Buffer.concat(this._parts).toString(); this._parts = []; this.onmessage?.({ data: text }); }
      }
    }
  }

  _frame(op, payload) {
    const mask = crypto.randomBytes(4);
    const n = payload.length;
    const head = n < 126 ? Buffer.from([0x80 | op, 0x80 | n])
      : n < 65536 ? Buffer.from([0x80 | op, 0x80 | 126, n >> 8, n & 0xff])
      : Buffer.concat([Buffer.from([0x80 | op, 0x80 | 127]), (() => { const l = Buffer.alloc(8); l.writeBigUInt64BE(BigInt(n)); return l; })()]);
    this._sock.write(Buffer.concat([head, mask, Buffer.from(payload.map((x, i) => x ^ mask[i % 4]))]));
  }

  send(text) { this._frame(0x1, Buffer.from(text)); }
}

module.exports = WebSocketLite;
