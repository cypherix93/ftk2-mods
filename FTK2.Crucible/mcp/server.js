#!/usr/bin/env node
'use strict';

// FTK2.Crucible MCP server.
//
// No npm dependencies by design (repo rule: BCL/built-ins only): raw JSON-RPC over stdio, node:http
// for the RPC calls. Talks to the Crucible plugin's loopback RPC inside one or more running game
// instances.
//
// Multi-instance: CRUCIBLE_INSTANCES="p1=8787,p2=8788" declares the peers. Every tool takes an
// optional `instance` argument; ftk2_compare_state digests all peers at once and reports the first
// divergence, which is the desync oracle.

const http = require('node:http');
const readline = require('node:readline');

const PROTOCOL_VERSION = '2024-11-05';
const HOST = process.env.CRUCIBLE_HOST || '127.0.0.1';
const TOKEN = process.env.CRUCIBLE_TOKEN || '';

function parseInstances() {
  const raw = process.env.CRUCIBLE_INSTANCES;
  if (!raw) {
    return [{ name: 'p1', port: parseInt(process.env.CRUCIBLE_PORT || '8787', 10) }];
  }
  return raw.split(',').map((entry, i) => {
    const trimmed = entry.trim();
    const eq = trimmed.indexOf('=');
    if (eq < 0) return { name: 'p' + (i + 1), port: parseInt(trimmed, 10) };
    return { name: trimmed.slice(0, eq).trim(), port: parseInt(trimmed.slice(eq + 1), 10) };
  }).filter((x) => Number.isInteger(x.port));
}

const INSTANCES = parseInstances();

function resolveInstance(name) {
  if (!name) return INSTANCES[0];
  const found = INSTANCES.find((i) => i.name === name);
  if (!found) {
    throw new Error(
      'unknown instance "' + name + '". Known: ' + INSTANCES.map((i) => i.name).join(', ') +
      '. Set CRUCIBLE_INSTANCES="p1=8787,p2=8788" to declare more.'
    );
  }
  return found;
}

function rpc(instance, method, path, body) {
  return new Promise((resolve, reject) => {
    const payload = body ? JSON.stringify(body) : null;
    const headers = {};
    if (payload) {
      headers['Content-Type'] = 'application/json';
      headers['Content-Length'] = Buffer.byteLength(payload);
    }
    if (TOKEN) headers['X-Crucible-Token'] = TOKEN;

    const req = http.request(
      { host: HOST, port: instance.port, path, method, headers, timeout: 30000 },
      (res) => {
        let data = '';
        res.on('data', (chunk) => { data += chunk; });
        res.on('end', () => {
          try { resolve(JSON.parse(data)); }
          catch (e) { reject(new Error('bad response from ' + instance.name + ': ' + data.slice(0, 200))); }
        });
      }
    );
    req.on('timeout', () => { req.destroy(new Error('instance ' + instance.name + ' did not respond within 30s')); });
    req.on('error', (e) => {
      reject(new Error(
        'cannot reach instance "' + instance.name + '" on ' + HOST + ':' + instance.port + ' (' + e.message + '). ' +
        'Is that game instance running with [General] Enabled=true and [Rpc] Enabled=true?'
      ));
    });
    if (payload) req.write(payload);
    req.end();
  });
}

const INSTANCE_ARG = {
  instance: { type: 'string', description: 'Which game instance (default: the first). See ftk2_list_instances.' }
};

const TOOLS = [
  { name: 'ftk2_list_instances',
    description: 'List the configured game instances (peers) and whether each is reachable.',
    inputSchema: { type: 'object', properties: {} } },
  { name: 'ftk2_health',
    description: 'Check whether a game instance is running and its Crucible bridge is alive.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_list_commands',
    description: 'List every dev command the running game exposes (EndPhase, SetStat, GetSpecificThing, ...).',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_exec',
    description: 'Execute a game dev command on an instance.',
    inputSchema: { type: 'object', required: ['command'], properties: {
      command: { type: 'string', description: 'Command name, e.g. "EndPhase"' },
      args: { type: 'array', items: { type: 'string' }, description: 'String arguments' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_end_phase',
    description: 'Advance the game one phase (next turn / skip turn).',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_state',
    description: 'Read a snapshot of the current run state plus a deterministic digest.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_screenshot',
    description: 'Capture what the game is showing right now and return it as an image.',
    inputSchema: { type: 'object', properties: {
      label: { type: 'string', description: 'Optional label used in the filename' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_read_trace',
    description: 'Read the most recent entries from an instance session trace.',
    inputSchema: { type: 'object', properties: {
      n: { type: 'number', description: 'How many entries (default 50)' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_compare_state',
    description: 'Desync oracle: snapshot every instance, compare digests, and report the first diverging field.',
    inputSchema: { type: 'object', properties: {} } }
];

function textResult(obj) {
  return { content: [{ type: 'text', text: typeof obj === 'string' ? obj : JSON.stringify(obj, null, 2) }] };
}

/** Recursively finds paths whose values differ between two snapshots. */
function diffPaths(a, b, prefix, out) {
  const keys = new Set([...Object.keys(a || {}), ...Object.keys(b || {})]);
  for (const key of keys) {
    const path = prefix ? prefix + '.' + key : key;
    const av = a ? a[key] : undefined;
    const bv = b ? b[key] : undefined;
    const bothObjects = av && bv && typeof av === 'object' && typeof bv === 'object'
      && !Array.isArray(av) && !Array.isArray(bv);
    if (bothObjects) { diffPaths(av, bv, path, out); continue; }
    if (JSON.stringify(av) !== JSON.stringify(bv)) {
      out.push({ path, baselineValue: av, peerValue: bv });
    }
  }
  return out;
}

async function compareState() {
  const results = [];
  for (const inst of INSTANCES) {
    try {
      const res = await rpc(inst, 'GET', '/state');
      results.push({ instance: inst.name, port: inst.port, ok: !!res.ok, digest: res.digest, snapshot: res.snapshot });
    } catch (e) {
      results.push({ instance: inst.name, port: inst.port, ok: false, error: e.message });
    }
  }

  const reachable = results.filter((r) => r.ok);
  if (reachable.length < 2) {
    return textResult({
      verdict: 'inconclusive',
      reason: 'need at least 2 reachable instances to compare; got ' + reachable.length,
      instances: results.map((r) => ({ instance: r.instance, ok: r.ok, error: r.error, digest: r.digest }))
    });
  }

  const baseline = reachable[0];
  const mismatches = [];
  for (let i = 1; i < reachable.length; i++) {
    const peer = reachable[i];
    if (peer.digest !== baseline.digest) {
      // Note: `instance` is redacted from the digest server-side, so a difference here is real.
      const fields = diffPaths(baseline.snapshot, peer.snapshot, '', [])
        .filter((d) => d.path !== 'instance');
      mismatches.push({ baseline: baseline.instance, peer: peer.instance, divergingFields: fields });
    }
  }

  return textResult({
    verdict: mismatches.length === 0 ? 'in_sync' : 'DESYNC',
    digests: reachable.map((r) => ({ instance: r.instance, digest: r.digest })),
    mismatches
  });
}

async function callTool(name, args) {
  args = args || {};

  if (name === 'ftk2_list_instances') {
    const rows = [];
    for (const inst of INSTANCES) {
      try {
        const res = await rpc(inst, 'GET', '/health');
        rows.push({ instance: inst.name, port: inst.port, reachable: true, online: res.online, bridge: res.bridgeAvailable });
      } catch (e) {
        rows.push({ instance: inst.name, port: inst.port, reachable: false, error: e.message });
      }
    }
    return textResult({ instances: rows });
  }

  if (name === 'ftk2_compare_state') return compareState();

  const inst = resolveInstance(args.instance);

  switch (name) {
    case 'ftk2_health':        return textResult(await rpc(inst, 'GET', '/health'));
    case 'ftk2_list_commands': return textResult(await rpc(inst, 'GET', '/commands'));
    case 'ftk2_exec':
      return textResult(await rpc(inst, 'POST', '/exec', { command: args.command, args: args.args || [] }));
    case 'ftk2_end_phase':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'EndPhase', args: [] }));
    case 'ftk2_state':         return textResult(await rpc(inst, 'GET', '/state'));
    case 'ftk2_read_trace':    return textResult(await rpc(inst, 'GET', '/trace?n=' + (args.n || 50)));
    case 'ftk2_screenshot': {
      const res = await rpc(inst, 'POST', '/screenshot', { label: args.label || null });
      if (!res.ok) return textResult(res);
      const content = [{ type: 'text', text: '[' + inst.name + '] ' + res.path }];
      if (res.base64) content.push({ type: 'image', data: res.base64, mimeType: 'image/png' });
      return { content };
    }
    default: throw new Error('unknown tool: ' + name);
  }
}

function send(msg) { process.stdout.write(JSON.stringify(msg) + '\n'); }

async function handle(msg) {
  if (msg.method === 'initialize') {
    send({ jsonrpc: '2.0', id: msg.id, result: {
      protocolVersion: PROTOCOL_VERSION,
      capabilities: { tools: {} },
      serverInfo: { name: 'ftk2-crucible', version: '0.1.0' } } });
    return;
  }

  if (msg.method === 'notifications/initialized') return;

  if (msg.method === 'tools/list') {
    send({ jsonrpc: '2.0', id: msg.id, result: { tools: TOOLS } });
    return;
  }

  if (msg.method === 'tools/call') {
    try {
      const result = await callTool(msg.params.name, msg.params.arguments);
      send({ jsonrpc: '2.0', id: msg.id, result });
    } catch (e) {
      send({ jsonrpc: '2.0', id: msg.id, result: {
        content: [{ type: 'text', text: 'Error: ' + e.message }], isError: true } });
    }
    return;
  }

  if (msg.id !== undefined) {
    send({ jsonrpc: '2.0', id: msg.id, error: { code: -32601, message: 'method not found: ' + msg.method } });
  }
}

const rl = readline.createInterface({ input: process.stdin });
rl.on('line', (line) => {
  if (!line.trim()) return;
  let msg;
  try { msg = JSON.parse(line); } catch (e) { return; }
  handle(msg).catch(() => {});
});
