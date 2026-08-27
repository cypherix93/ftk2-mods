#!/usr/bin/env node
/**
 * Anti-drift check for docs/research/test-checklist.md section B: "the MCP server reads its tool
 * list from a static array ... a Crucible command that exists on the C# side is NOT reachable over
 * MCP until someone hand-adds it to that array."
 *
 * Compares the crucible_* commands actually registered in C# (Crucible.Plugin/*.cs, via
 * RegisterCommand/Register) against every crucible_* string literal referenced anywhere in
 * mcp/server.js (dedicated ftk2_* wrappers AND internal uses by composite tools like ftk2_screen /
 * ftk2_pick). ftk2_exec is a generic passthrough and can always reach any command by name, so it is
 * not itself evidence of a dedicated wrapper -- this script only reports the diff, it does not judge
 * which gaps are deliberate. Run it by hand after adding a RegisterCommand call or an ftk2_* tool:
 *
 *   node FTK2.Crucible/mcp/check-tool-drift.js
 *
 * Exits non-zero (and prints the diff) when either side has a command the other does not know about.
 */
'use strict';
const fs = require('fs');
const path = require('path');

const CS_DIR = path.join(__dirname, '..', 'src', 'Crucible.Plugin');
const SERVER_JS = path.join(__dirname, 'server.js');

function csRegisteredCommands() {
  const names = new Set();
  for (const f of fs.readdirSync(CS_DIR).filter((f) => f.endsWith('.cs'))) {
    const text = fs.readFileSync(path.join(CS_DIR, f), 'utf8');
    const re = /\b(?:GameBridge\.)?Register(?:Command)?\(\s*"(crucible_[a-zA-Z0-9_]+)"/g;
    let m;
    while ((m = re.exec(text))) names.add(m[1]);
  }
  return names;
}

function serverJsReferencedCommands() {
  const text = fs.readFileSync(SERVER_JS, 'utf8');
  const names = new Set();
  const re = /['"](crucible_[a-zA-Z0-9_]+)['"]/g;
  let m;
  while ((m = re.exec(text))) names.add(m[1]);
  return names;
}

const csNames = csRegisteredCommands();
const jsNames = serverJsReferencedCommands();

const missing = [...csNames].filter((n) => !jsNames.has(n)).sort(); // registered in C#, never mentioned in server.js
const dead = [...jsNames].filter((n) => !csNames.has(n)).sort();    // referenced in server.js, no such C# command

console.log(`C# RegisterCommand calls: ${csNames.size}`);
console.log(`crucible_* names referenced in server.js: ${jsNames.size}`);

let ok = true;
if (missing.length) {
  ok = false;
  console.log(`\nMissing from server.js (${missing.length}) -- reachable only via raw ftk2_exec, no dedicated wrapper and no internal use:`);
  for (const n of missing) console.log('  ' + n);
}
if (dead.length) {
  ok = false;
  console.log(`\nDead references in server.js (${dead.length}) -- no matching C# RegisterCommand, will fail at call time:`);
  for (const n of dead) console.log('  ' + n);
}
if (ok) console.log('\nIn sync.');
process.exit(ok ? 0 : 1);
