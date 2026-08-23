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
    description: 'Read a snapshot of the current run state plus a deterministic digest. schema="v2" ' +
      '(the default) adds the combat assertion surface: combatants with hp/maxHp/classId/alive, their ' +
      'status effects with durations, and base stats. Pass schema="v1" only for the legacy shape. ' +
      'Assertion traps: statuses null means UNREADABLE (paired with a warnings entry), [] means none; ' +
      'stats are BASE stats so a buff will not move them; combat.round is not monotonic (starts at -1 ' +
      'and resets per wave) so assert on turn; turn null means the hooks failed, not turn 0.',
    inputSchema: { type: 'object', properties: {
      schema: { type: 'string', enum: ['v1', 'v2'], description: 'Snapshot schema (default v2)' },
      ...INSTANCE_ARG } } },
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
    description: 'Desync oracle: snapshot every instance, compare digests, and report the first diverging field. ' +
      'Uses the v2 schema, so hp, status effects and stats are inside the compared digest.',
    inputSchema: { type: 'object', properties: {} } },
  { name: 'ftk2_screen',
    description: 'The semantic "where am I" tool. Returns route (from /state), the set of active on-screen ' +
      'UI document names, on-screen Buttons/Labels, and which element is FOCUSED. Route alone is not ' +
      'trustworthy (it has reported MAIN_MENU while a multiplayer browser and modal were actually on ' +
      'screen) so this cross-checks route against the actual document dump and flags disagreement. Call ' +
      'this before deciding anything. Read-only; never clicks anything, including continue-btn/load-btn.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_saves',
    description: 'Reports whether a save exists and what it is: reads RouterHelper.Env.GameRuns (the run-id ' +
      'list) and, if the LoadGameUIDocument save panel is currently on screen, parses its labels (adventure ' +
      'name, difficulty, party classes, round, date, version). If the save panel is NOT on screen this is ' +
      'reported explicitly and is NOT the same as "no saves exist" -- check gameRunsCount instead. Read-only; ' +
      'never clicks anything, including continue-btn/load-btn.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_new_game',
    description: 'Starts new-game setup: clicks one of the three adventure-category buttons (\'Age of ' +
      'Rebellion\', \'Age of Omus\', \'Challenge Modes\') and dumps what appeared so the caller can choose ' +
      'the next step. If `adventure` is given it is only used to search the resulting dump for a matching ' +
      'element -- it is NOT clicked. SAFETY: this tool never clicks continue-btn or load-btn and never ' +
      'resumes/loads the existing save; it only opens category selection.',
    inputSchema: { type: 'object', required: ['category'], properties: {
      category: { type: 'string', description: 'One of: "Age of Rebellion", "Age of Omus", "Challenge Modes"' },
      adventure: { type: 'string', description: 'Optional adventure name to search for in the resulting dump (not clicked)' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_pick',
    description: 'Thin, honest wrapper over crucible_ui_click: dumps before and after clicking `selector`, and ' +
      'reports which UI documents appeared/disappeared. SAFETY: refuses to click if `selector` is, or would ' +
      'match, the continue-btn or load-btn elements -- those resume/load the owner\'s existing save and this ' +
      'tool will never trigger that path, even indirectly. A refusal returns the on-screen elements instead ' +
      'of clicking.',
    inputSchema: { type: 'object', required: ['selector'], properties: {
      selector: { type: 'string', description: 'Substring matched against element name/text (same rules as crucible_ui_click)' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_wait_screen',
    description: 'Polls crucible_ui_dump (on the Node side, not in the game plugin, so it cannot block ' +
      'MainThreadPump and freeze the game) until a dump containing `expect` as a substring is seen, or ' +
      'timeoutMs elapses. Use this instead of trusting route, which is unreliable. Read-only; never clicks ' +
      'anything.',
    inputSchema: { type: 'object', required: ['expect'], properties: {
      expect: { type: 'string', description: 'Substring that must appear in a crucible_ui_dump for the wait to succeed' },
      timeoutMs: { type: 'number', description: 'Max time to poll, in ms (default 10000)' },
      ...INSTANCE_ARG } } },

  // ---- Debug / cheat verbs (crucible_*). Every one drives a real game API (never a bare state
  // write left to the game to notice on its own -- a direct AdventureState.CurrentTimeOfDayIndex
  // write was measured live not to update the HUD) and reports an observable read before AND after
  // plus an explicit changed=true/false, so a dispatch receipt is never mistaken for a verified
  // effect. Some requested verbs (crucible_teleport, crucible_win_combat, crucible_force_roll) were
  // deliberately NOT built -- see the task report for why each is infeasible from confirmed APIs.
  { name: 'ftk2_kill_all',
    description: 'DEBUG/CHEAT: kills every combatant on BOTH sides of the current fight via ' +
      'CharacterHelper.TryKillCharacter/.KillCharacter. Not a "make me win" verb -- everyone dies. ' +
      'Verifies its own post-condition via CharacterHelper.IsDead(Entity) read before and after every ' +
      'combatant; reports deadBefore/deadAfter/changed.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_heal_party',
    description: 'DEBUG/CHEAT: heals every party member to full via CharacterHelper.SetToMaxHealth(Entity). ' +
      'Verifies via CharacterComponent.CurrentHealth read before and after each party member; reports ' +
      'per-character hpBefore/hpAfter/changed.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_debug_end_phase',
    description: 'DEBUG/CHEAT: force-ends the named phase via the live phase owner\'s own ' +
      '_debugEndPhase() (RestPhase\'s only overload takes an Int32 option; every other phase is ' +
      'zero-arg -- handled automatically). phase must be one of: combat, encounter, fortune, treasure, ' +
      'trap, wheel, rest -- anything else is refused, never defaulted. Verifies via ' +
      'RouterHelper.GetCurrentRoute() read before and after (those phase names are themselves live ' +
      'eRoutes values, so ending one should move the route).',
    inputSchema: { type: 'object', required: ['phase'], properties: {
      phase: { type: 'string', description: 'combat | encounter | fortune | treasure | trap | wheel | rest' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_set_level',
    description: 'DEBUG/CHEAT: progresses a party member to a target level via ' +
      'CharacterHelper.TryProgressCharacterEntityToLevel(Entity, Int32), resolved by matching the live ' +
      'overload\'s parameter types (never a guessed signature). slot indexes into the current run\'s ' +
      'PlayerComponent-bearing entities in enumeration order -- NOT a guaranteed persistent slot id ' +
      'outside PARTY_MANAGEMENT, so the result names the resolved character so you can confirm identity. ' +
      'Verifies via CharacterComponent.ExtraLevel read before and after (the closest documented ' +
      'level-shaped field; reported explicitly as a proxy since no direct GetLevel accessor was found).',
    inputSchema: { type: 'object', required: ['slot', 'level'], properties: {
      slot: { type: 'number', description: 'Party slot index, 0-5' },
      level: { type: 'number', description: 'Target level (non-negative integer)' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_give',
    description: 'DEBUG/CHEAT: grants an item via the shipped GetSpecificThing console command (already ' +
      'confirmed working). Verifies via the SUM of CharacterComponent.Things.Count across the whole party ' +
      'read before and after -- the shipped command\'s own targeting decides which character receives it, ' +
      'so a whole-party sum is the honest observable rather than guessing a recipient slot.',
    inputSchema: { type: 'object', required: ['configId', 'qty'], properties: {
      configId: { type: 'string', description: 'Item config id' },
      qty: { type: 'number', description: 'Quantity, positive integer' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_time_advance',
    description: 'DEBUG/CHEAT: advances the overworld by calling the real turn-advance method, ' +
      'AdventureDirector._doEndTurn(), `steps` times (max 20 per call) -- NOT a direct write to ' +
      'AdventureState.CurrentTimeOfDayIndex, which was measured live not to update the HUD. Verifies via ' +
      'AdventureState.CurrentTimeOfDayIndex and GameRunData.RoundCount read before and after. Caveat: ' +
      '_doEndTurn returns Task and is not awaited by the bridge, so an immediate read can race the game\'s ' +
      'own async continuation -- if changed=false, re-check a moment later via ftk2_exec crucible_get ' +
      'before concluding it failed.',
    inputSchema: { type: 'object', required: ['steps'], properties: {
      steps: { type: 'number', description: 'How many end-turns to dispatch, 1-20' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_pin_seed',
    description: 'DEBUG/CHEAT: writes GameRandom.Seed on every live GameRandom instance the bridge can ' +
      'reach (CombatState.Random, AdventureDirector._gameRandom, CombatPhase._gameRandom). Unlike the ' +
      'time-of-day field, GameRandom.Seed IS the value the RNG itself reads on every draw -- there is no ' +
      'separate cached UI state to fall out of sync with -- so a direct field write is correct here, not ' +
      'a shortcut. Verifies by reading .Seed back on each instance found; reports scopesFound/scopesChanged.',
    inputSchema: { type: 'object', required: ['seed'], properties: {
      seed: { type: 'number', description: 'RNG seed (integer)' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_quest_state',
    description: 'Read-only: active/completed/failed quest counts, plus each active quest\'s id and its ' +
      'CompletedObjectives bitmap (from QuestState.CompletedObjectives).',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_quest_complete',
    description: 'DEBUG/CHEAT: marks one objective of one active quest complete. Sets ' +
      'QuestState.CompletedObjectives[objectiveIndex] = true (the one public mutator field found), THEN ' +
      'drives the game\'s own resolution pass, AdventureDirector._tryCompleteQuests(), so the flag is ' +
      'actually picked up rather than sitting inert. Verifies the flag flipped AND, more strongly, whether ' +
      'the quest actually left ActiveQuests (questStillActive/activeCountAfter/completedCountAfter).',
    inputSchema: { type: 'object', required: ['questIndex', 'objectiveIndex'], properties: {
      questIndex: { type: 'number', description: 'Index into GameRunData.ActiveQuests' },
      objectiveIndex: { type: 'number', description: 'Index into that quest\'s CompletedObjectives array' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_chaos_freeze',
    description: 'DEBUG/CHEAT: no-chaos mode for long automated soaks (every turn otherwise raises Chaos, ' +
      'escalating enemy difficulty and eventually ending the run, which poisons comparisons between soak ' +
      'runs). Toggles a Harmony Prefix on AdventureHelper.ModifyChaosLevel -- the confirmed leaf mutator, ' +
      'not just its caller -- that skips the original call while frozen. Reports frozenBefore/frozenAfter/ ' +
      'changed and the current chaosHistoryCount for reference. See ftk2_chaos_state for the verification ' +
      'protocol: read it, freeze, drive turns, read it again -- chaosHistoryCount must not move.',
    inputSchema: { type: 'object', required: ['onOff'], properties: {
      onOff: { type: 'string', description: '"on" or "off" (also accepts true/false, 1/0)' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_chaos_state',
    description: 'Read-only: whether chaos is currently frozen, whether the freeze patch is active, and ' +
      'the chaos observables -- chaosHistoryCount (no scalar "current chaos level" field exists on ' +
      'ChaosState, so this is the best available proxy), maxChaos, lastChaosRoundAdded, startedAtRound, ' +
      'and the current round count for correlation.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } }
];

// Elements this server will never allow a click to reach, directly or as an incidental substring
// match: they resume/load the owner's existing save.
const FORBIDDEN_CLICK_NAMES = ['continue-btn', 'load-btn'];

/** Parses one crucible_ui_dump line into a plain object. Returns null for non-element lines
 *  (the "(no matching visible elements)" placeholder or the "... truncated" trailer). */
function parseUiDumpLine(line) {
  const m = /^(\S+) name=(?:'([^']*)'|\(null\)) text=(?:'([^']*)'|\(null\)) visible=(True|False) enabled=(True|False) doc=(?:'([^']*)'|\(null\))( \[FOCUSED\])?$/.exec(line);
  if (!m) return null;
  return {
    type: m[1],
    name: m[2] ?? null,
    text: m[3] ?? null,
    visible: m[4] === 'True',
    enabled: m[5] === 'True',
    doc: m[6] ?? null,
    focused: !!m[7]
  };
}

function parseUiDump(text) {
  return (text || '').split('\n').map(parseUiDumpLine).filter(Boolean);
}

/** Runs crucible_ui_dump and returns { ok, elements, raw } or { ok: false, error }. */
async function dumpElements(inst, filter, kinds) {
  const res = await rpc(inst, 'POST', '/exec', { command: 'crucible_ui_dump', args: [filter || '-', kinds || '-'] });
  if (!res.ok) return { ok: false, error: res.error || 'exec failed' };
  const raw = res.result || '';
  if (raw.indexOf('error:') === 0) return { ok: false, error: raw };
  return { ok: true, elements: parseUiDump(raw), raw };
}

function matchesSelector(e, selector) {
  const sel = selector.toLowerCase();
  return (e.name && e.name.toLowerCase().indexOf(sel) >= 0) || (e.text && e.text.toLowerCase().indexOf(sel) >= 0);
}

/** True if `selector` is itself a forbidden name, or would match any forbidden element currently
 *  on screen (the plugin's crucible_ui_click matches by the same name/text substring rule, and we
 *  cannot rely on traversal order to dodge a match, so any match at all is treated as unsafe). */
function selectorReachesForbiddenClick(elements, selector) {
  const sel = (selector || '').trim().toLowerCase();
  if (FORBIDDEN_CLICK_NAMES.includes(sel)) return true;
  return elements.some((e) => e.name && FORBIDDEN_CLICK_NAMES.includes(e.name) && matchesSelector(e, selector));
}

function uniqueDocs(elements) {
  return [...new Set(elements.map((e) => e.doc).filter(Boolean))];
}

function diffDocs(beforeDocs, afterDocs) {
  const b = new Set(beforeDocs), a = new Set(afterDocs);
  return { appeared: [...a].filter((x) => !b.has(x)), disappeared: [...b].filter((x) => !a.has(x)) };
}

const NEW_GAME_CATEGORIES = ['Age of Rebellion', 'Age of Omus', 'Challenge Modes'];

function sleep(ms) { return new Promise((resolve) => setTimeout(resolve, ms)); }

async function screenTool(inst) {
  const stateRes = await rpc(inst, 'GET', '/state');
  const route = stateRes.ok ? (stateRes.snapshot ? stateRes.snapshot.route : null) : null;
  const dump = await dumpElements(inst, '-', '-');
  if (!dump.ok) {
    return { route, stateOk: !!stateRes.ok, uiDumpError: dump.error, note: 'ui dump failed; cannot report on-screen documents' };
  }

  const activeDocuments = uniqueDocs(dump.elements);
  const buttons = dump.elements.filter((e) => e.type === 'Button');
  const labels = dump.elements.filter((e) => e.type === 'Label');
  const focused = dump.elements.find((e) => e.focused) || null;

  const routeWords = route ? String(route).toLowerCase().split(/[^a-z0-9]+/).filter(Boolean) : [];
  const docsConcat = activeDocuments.join(' ').toLowerCase();
  const routeMatchesUi = routeWords.length > 0 && routeWords.every((w) => docsConcat.indexOf(w) >= 0);

  return {
    route,
    activeDocuments,
    routeMatchesUi,
    warning: (routeWords.length > 0 && !routeMatchesUi)
      ? 'route (' + JSON.stringify(route) + ') does not obviously match any on-screen document -- do not trust route alone here'
      : null,
    focused,
    buttons,
    labels
  };
}

async function savesTool(inst) {
  const gr = await rpc(inst, 'POST', '/exec', { command: 'crucible_get', args: ['RouterHelper.Env.GameRuns'] });
  let gameRunsCount = null, gameRunsSample = [];
  const grText = gr.ok ? (gr.result || '') : null;
  if (grText) {
    const m = /^Count=(\d+)\s*\[(.*)\]/.exec(grText);
    if (m) {
      gameRunsCount = parseInt(m[1], 10);
      gameRunsSample = m[2].split(',').map((s) => s.trim()).filter((s) => s && s !== '...');
    }
  }

  const dump = await dumpElements(inst, '-', '-');
  const savePanelOnScreen = dump.ok && dump.elements.some((e) => e.doc === 'LoadGameUIDocument');

  let currentSave = null;
  if (savePanelOnScreen) {
    const panel = dump.elements.filter((e) => e.doc === 'LoadGameUIDocument');
    const byName = (n) => { const e = panel.find((x) => x.name === n); return e ? e.text : null; };
    currentSave = {
      adventureName: byName('adventure-name-label'),
      difficulty: byName('difficulty-label'),
      partyClasses: panel.filter((e) => e.name === 'name-label').map((e) => e.text),
      round: byName('round-label'),
      date: byName('date-label'),
      version: byName('version-label'),
      description: byName('adventure-description-text')
    };
  }

  return {
    gameRunsCount,
    gameRunsSample,
    gameRunsRaw: grText,
    savePanelOnScreen,
    currentSave,
    note: savePanelOnScreen
      ? null
      : 'LoadGameUIDocument is not currently on screen -- this does NOT mean no saves exist. ' +
        'Check gameRunsCount, or use ftk2_screen/ftk2_pick to navigate to where "Load Game" can be opened ' +
        '(but do not click load-btn/continue-btn).'
  };
}

async function newGameTool(inst, category, adventure) {
  if (!NEW_GAME_CATEGORIES.includes(category)) {
    return { ok: false, error: 'category must be one of: ' + NEW_GAME_CATEGORIES.join(', ') + ' (got ' + JSON.stringify(category) + ')' };
  }

  const before = await dumpElements(inst, '-', '-');
  if (!before.ok) return { ok: false, error: before.error, phase: 'before-dump' };

  if (selectorReachesForbiddenClick(before.elements, category)) {
    return { ok: false, blocked: true, reason: 'refusing: this would reach continue-btn/load-btn', onScreenElements: before.elements };
  }

  const clickRes = await rpc(inst, 'POST', '/exec', { command: 'crucible_ui_click', args: [category] });
  const after = await dumpElements(inst, '-', '-');

  const documentsBefore = uniqueDocs(before.elements);
  const documentsAfter = after.ok ? uniqueDocs(after.elements) : [];
  const diff = diffDocs(documentsBefore, documentsAfter);

  const adventureMatches = (adventure && after.ok)
    ? after.elements.filter((e) => matchesSelector(e, adventure))
    : [];

  return {
    ok: !!clickRes.ok,
    category,
    clickResult: clickRes.ok ? clickRes.result : clickRes.error,
    documentsBefore,
    documentsAfter,
    appeared: diff.appeared,
    disappeared: diff.disappeared,
    onScreenAfter: after.ok ? after.elements : null,
    adventureMatches,
    guidance: 'Choose the next selector from onScreenAfter/adventureMatches and call ftk2_pick. ' +
      'Never click continue-btn or load-btn.'
  };
}

async function pickTool(inst, selector) {
  if (!selector) throw new Error('missing selector');

  const before = await dumpElements(inst, '-', '-');
  if (!before.ok) return { ok: false, error: before.error, phase: 'before-dump' };

  if (selectorReachesForbiddenClick(before.elements, selector)) {
    return {
      ok: false,
      blocked: true,
      reason: 'refusing to click "' + selector + '": it is, or would match, continue-btn/load-btn, ' +
        'which would resume/load the existing save. This tool will never do that.',
      onScreenElements: before.elements
    };
  }

  const clickRes = await rpc(inst, 'POST', '/exec', { command: 'crucible_ui_click', args: [selector] });
  const after = await dumpElements(inst, '-', '-');

  const documentsBefore = uniqueDocs(before.elements);
  const documentsAfter = after.ok ? uniqueDocs(after.elements) : [];
  const diff = diffDocs(documentsBefore, documentsAfter);

  return {
    ok: !!clickRes.ok,
    selector,
    clickResult: clickRes.ok ? clickRes.result : clickRes.error,
    documentsBefore,
    documentsAfter,
    appeared: diff.appeared,
    disappeared: diff.disappeared,
    onScreenAfter: after.ok ? after.elements : null
  };
}

async function waitScreenTool(inst, expect, timeoutMs) {
  if (!expect) throw new Error('missing expect');
  timeoutMs = timeoutMs || 10000;
  const start = Date.now();
  const pollIntervalMs = 500;
  let lastDump = null, lastError = null, polls = 0;

  for (;;) {
    polls++;
    const dump = await dumpElements(inst, '-', '-');
    if (dump.ok) {
      lastDump = dump.raw;
      if (dump.raw.toLowerCase().indexOf(expect.toLowerCase()) >= 0) {
        return { matched: true, elapsedMs: Date.now() - start, polls, expect };
      }
    } else {
      lastError = dump.error;
    }

    const elapsed = Date.now() - start;
    if (elapsed >= timeoutMs) {
      return { matched: false, elapsedMs: elapsed, polls, expect, lastDump, lastError };
    }
    await sleep(Math.min(pollIntervalMs, timeoutMs - elapsed));
  }
}

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

/**
 * Builds the /state path for a schema. v2 is the default because it is the only schema carrying the
 * combat assertion surface (combatants, hp, statuses, stats); v1 omits all of it, so a test that
 * silently got v1 would assert against a snapshot that cannot express the thing under test.
 * An unrecognized value falls back to v2 rather than passing junk through to the query string.
 */
function statePath(schema) {
  return schema === 'v1' ? '/state' : '/state?schema=v2';
}

async function compareState() {
  const results = [];
  for (const inst of INSTANCES) {
    try {
      const res = await rpc(inst, 'GET', statePath('v2'));
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
    case 'ftk2_state':         return textResult(await rpc(inst, 'GET', statePath(args.schema)));
    case 'ftk2_screen':        return textResult(await screenTool(inst));
    case 'ftk2_saves':         return textResult(await savesTool(inst));
    case 'ftk2_new_game':      return textResult(await newGameTool(inst, args.category, args.adventure));
    case 'ftk2_pick':          return textResult(await pickTool(inst, args.selector));
    case 'ftk2_wait_screen':   return textResult(await waitScreenTool(inst, args.expect, args.timeoutMs));
    case 'ftk2_read_trace':    return textResult(await rpc(inst, 'GET', '/trace?n=' + (args.n || 50)));
    case 'ftk2_kill_all':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_kill_all', args: [] }));
    case 'ftk2_heal_party':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_heal_party', args: [] }));
    case 'ftk2_debug_end_phase':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_end_phase', args: [String(args.phase)] }));
    case 'ftk2_set_level':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_set_level', args: [String(args.slot), String(args.level)] }));
    case 'ftk2_give':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_give', args: [String(args.configId), String(args.qty)] }));
    case 'ftk2_time_advance':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_time_advance', args: [String(args.steps)] }));
    case 'ftk2_pin_seed':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_pin_seed', args: [String(args.seed)] }));
    case 'ftk2_quest_state':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_quest_state', args: [] }));
    case 'ftk2_quest_complete':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_quest_complete', args: [String(args.questIndex), String(args.objectiveIndex)] }));
    case 'ftk2_chaos_freeze':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_chaos_freeze', args: [String(args.onOff)] }));
    case 'ftk2_chaos_state':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_chaos_state', args: [] }));
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
