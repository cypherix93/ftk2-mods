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
  { name: 'ftk2_clear_gates',
    description: 'Clear every blocking "Click to Continue" gate until the overworld is actually ' +
      'interactive. The post-load gate and the intro story pages BOTH use the element name ' +
      'continue-label in LoadingUIDocument, and the intro has SEVERAL pages, so one press is not ' +
      'enough. While any gate is up, nothing on the overworld responds: end-turn reports no change, ' +
      'hex clicks do nothing, and the game looks frozen. Call this after any load before driving.',
    inputSchema: { type: 'object', properties: {
      maxPresses: { type: 'number', description: 'Safety cap on presses (default 20)' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_boot_to_run',
    description: 'THE way to get from wherever the game is to a loaded, interactive overworld. ' +
      'Opens the campaign screen (AdventureSelectionDirector._loadGameRun silently does nothing ' +
      'unless that screen exists), escapes the multiplayer lobby the game routes itself to on boot, ' +
      'loads the run by id, then clears every gate and confirms the overworld is interactive. ' +
      'Verifies each step instead of assuming it, because an unverified step fails later somewhere ' +
      'that looks unrelated. NEVER pass a run id you did not create: the save folder also holds the ' +
      'owner live co-op campaign.',
    inputSchema: { type: 'object', properties: {
      runId: { type: 'string', description: 'The run id (GUID) to load' },
      ...INSTANCE_ARG },
      required: ['runId'] } },
  { name: 'ftk2_party',
    description: 'List the loaded party: slot, class id, display name, current health, level, guid. ' +
      'Slot order is the enumeration order of GameRunData.Entities and is NOT a persistent id, so it ' +
      'must not be cached across a reload.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_set_class',
    description: 'Swap one party member class by rewriting CharacterComponent.ConfigName, then heal ' +
      'to the new maximum. This is how a test party is built: mutating a loaded run instead of ' +
      'driving roughly twenty UI steps through character creation. Only the class id changes - ' +
      'display name, equipment and carried things keep their previous values, and abilities come ' +
      'from the EQUIPPED WEAPON, so a class test must equip that class weapon too.',
    inputSchema: { type: 'object', properties: {
      slot: { type: 'number', description: 'Party slot index (see ftk2_party)' },
      classId: { type: 'string', description: 'Class config id, e.g. CF_EOR_ARCANIST' },
      ...INSTANCE_ARG },
      required: ['slot', 'classId'] } },
  { name: 'ftk2_fixture_save',
    description: 'Persist the live run as a NEW save under a freshly generated run id, and return ' +
      'that id. Never overwrites: this folder also holds the owner live co-op campaign. The write is ' +
      'asynchronous and not awaited, and Env.GameRuns is an in-memory cache that will not show the ' +
      'new file - confirm on disk or by loading the returned id.',
    inputSchema: { type: 'object', properties: {
      label: { type: 'string', description: 'Human label recorded in the result' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_set_hex',
    description: 'Teleport the whole party to a hex by writing AdventureComponent.HexPosition. ' +
      'CAUTION: this writes state directly and does NOT run the game arrival logic - no hex reveal, ' +
      'no encounter trigger, no movement cost. Teleporting into unexplored map leaves the party ' +
      'surrounded by fog-of-war clouds with no clickable hexes, which looks exactly like broken input.',
    inputSchema: { type: 'object', properties: {
      x: { type: 'number' }, y: { type: 'number' }, ...INSTANCE_ARG },
      required: ['x', 'y'] } },
  { name: 'ftk2_reveal_map',
    description: 'Remove fog of war by setting every hex visibility state (default Visible). Fog is ' +
      'not cosmetic here: an unexplored hex has no clickable target, so a party teleported into ' +
      'unrevealed map is surrounded by cloud and every movement click silently does nothing, which ' +
      'looks exactly like broken input.',
    inputSchema: { type: 'object', properties: {
      state: { type: 'string', enum: ['Visible', 'Revealed', 'Hidden'] },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_map_encounters',
    description: 'Scan the hex grid and report every encounter entity with its position, plus the ' +
      'party current hex. Use it to find somewhere worth going.',
    inputSchema: { type: 'object', properties: {
      filter: { type: 'string', description: 'Substring filter on the encounter id' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_abilities',
    description: 'Abilities, passive skills and equipped weapon for one party slot. Reads both ' +
      'GetPassiveSkills overloads: the Entity one reports what the character HAS and the String one ' +
      'what its class config DECLARES, so a divergence identifies an entity not rebuilt for its ' +
      'class. NOTE custom SKILL_CF_* ids never appear here - that API maps to the eSkills enum, which ' +
      'cannot gain members at runtime. Assert custom skills with ftk2_class_config instead.',
    inputSchema: { type: 'object', properties: {
      slot: { type: 'number' }, ...INSTANCE_ARG }, required: ['slot'] } },
  { name: 'ftk2_class_config',
    description: 'Read the LIVE merged character config for a class id: passives, things, stats. ' +
      'This is the authoritative check that authored content actually reached the game, as opposed ' +
      'to being correct on disk. PRESENT=False means the pack did not merge.',
    inputSchema: { type: 'object', properties: {
      classId: { type: 'string' }, ...INSTANCE_ARG }, required: ['classId'] } },
  { name: 'ftk2_status_config',
    description: 'Read the LIVE Configs.StatusEffects entry for a status id. This is the check that ' +
      'would have caught two shipped recipes applying "CURSE", an eStatusEffectTypes member rather ' +
      'than a StatusEffects key: anything applying an id that is not present silently does nothing.',
    inputSchema: { type: 'object', properties: {
      statusId: { type: 'string' }, ...INSTANCE_ARG }, required: ['statusId'] } },
  { name: 'ftk2_spawn',
    description: 'Drive the game own F6 debug spawn menus (2332 enemies, 170 encounters). Pass ' +
      'selector "-" to LIST without spawning anything. Spawning places an entity on the map; it does ' +
      'not start a fight by itself.',
    inputSchema: { type: 'object', properties: {
      kind: { type: 'string', enum: ['enemies', 'encounters'] },
      selector: { type: 'string', description: 'Id or name substring, or "-" to list' },
      ...INSTANCE_ARG },
      required: ['kind'] } },
  { name: 'ftk2_refresh_saves',
    description: 'Re-scan the save folder so newly written .ftk2 files become loadable. Env.GameRuns ' +
      'is cached at startup, so a fixture created while the game is running is invisible and the ' +
      'load silently does nothing on the correct screen with no error.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_dialogs',
    description: 'List every blocking dialogue, modal or gate currently on screen, with the ' +
      'document that owns it and whether it is safe to dismiss. Read-only; presses nothing. ' +
      'Use this when the game appears frozen or a load "did not take" - almost always something ' +
      'is sitting on top of the screen swallowing input.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_dismiss',
    description: 'Dismiss one blocking element BY EXACT NAME (see ftk2_dialogs). Refuses any name ' +
      'not on the safe list, because "Continue" appears on several unrelated controls including ' +
      'continue-btn, which resumes the owner live co-op campaign.',
    inputSchema: { type: 'object', properties: {
      name: { type: 'string', description: 'Exact element name, e.g. ok-btn' },
      ...INSTANCE_ARG }, required: ['name'] } },
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
  { name: 'ftk2_godmode',
    description: 'Keep the party alive so a trait can be observed instead of the party dying first. ' +
      'Implemented as a per-tick top-up of party health rather than an invulnerability flag, which ' +
      'matters: damage still LANDS and still fires ON_DAMAGE_TAKEN, so reflect and thorns traits stay ' +
      'observable. A real invulnerability flag would suppress the very trigger under test. Party only ' +
      '(group 0); enemies are untouched so a fight still behaves like a fight.',
    inputSchema: { type: 'object', properties: {
      mode: { type: 'string', enum: ['on', 'off', 'status'], description: 'default: status' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_combat_spawn',
    description: 'Drop creatures straight into the CURRENT fight, wired the way the game wires its own ' +
      'summons: tile position, initiative, actions and the 3D model. Use this when a fight is too small ' +
      'to exercise something - a Bond-gated summon that unlocks at 3 and 6 kills cannot be reached in a ' +
      'fight that ships one spider. NOTE the tile decides ALLEGIANCE (TryCreateSummon copies the tile ' +
      'GroupIndex onto the new character), so group 1 needs a free enemy tile and group 0 a free player ' +
      'one; asking for more than there are free tiles places as many as fit and says so.',
    inputSchema: { type: 'object', required: ['characterConfig'], properties: {
      characterConfig: { type: 'string', description: 'e.g. BEE_WORKER_01, WOLF_CHAOSHOUND_01' },
      count: { type: 'number', description: 'default 1, capped at 12' },
      group: { type: 'number', description: '1 = enemies (default), 0 = allies' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_combat_wipe_enemies',
    description: 'Kill every combatant in a group instantly - the fast way to end a test fight once the ' +
      'thing under test has been observed. Use INSTEAD OF ftk2_kill_all, which does nothing in combat: ' +
      'it walks GameRunData.Entities while the fight runs on CombatState.Entities, and reports ' +
      'changed=False against 74 live combatants. Defaults to group 1 so an obvious typo cannot wipe the ' +
      'party being tested.',
    inputSchema: { type: 'object', properties: {
      group: { type: 'number', description: '1 = enemies (default), 0 = party' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_combat_restore_actions',
    description: 'Refill primary/secondary actions so a character can act again this turn. Needed because ' +
      'an ability with no actions left does NOT fail loudly: the use-ability call reports the ability and ' +
      'target exactly as it does on success, and the only tell is pa=0 in the snapshot. A trait test that ' +
      'lost its action to an earlier auto-play therefore reads as "the trait never fired".',
    inputSchema: { type: 'object', properties: {
      group: { type: 'number', description: '0 = party (default), 1 = enemies' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_loc',
    description: 'Resolve a localization key exactly as the UI does, against the game own ' +
      'Lang.__translations. Use it to prove a pack authored names AND descriptions that players will ' +
      'actually see: a label looking right in a screenshot only proves the NAME resolved, while ' +
      'descriptions live behind hover tooltips and encyclopedia panels a harness cannot easily open. An ' +
      'unresolved key renders as the raw id. A trailing * matches by prefix, so a whole pack can be ' +
      'checked in one call (e.g. SKILL_CF_VAMPIRIC*).',
    inputSchema: { type: 'object', required: ['key'], properties: {
      key: { type: 'string', description: 'Exact key, or a prefix ending in *' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_summary_dismiss',
    description: 'Dismiss the ADVENTURE COMPLETE / adventure-summary screen by pressing its Continue button ' +
      '(next-btn). _endAdventure AWAITS the Task that this button completes, so until it is pressed the run ' +
      'never finishes unwinding and the game sits on the summary forever. Pressing the button rather than ' +
      'completing the Task directly is deliberate: the button handler sets the Task RESULT, and that result ' +
      'decides whether the save is removed. Note the other button on that screen, load-game-btn, LOADS A SAVE ' +
      '- this tool only ever presses next-btn.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_endadventure_watch',
    description: 'Read the latched outcome of the last adventure: whether it ended, and whether it was a ' +
      'VICTORY or a DEFEAT. This is the ONLY sound way to assert an outcome - _endAdventure routes a win and ' +
      'a loss to the SAME screen, so route tells you nothing. Backed by a Harmony prefix that also SNAPSHOTS ' +
      'the save, which matters because a victorious run DELETES its own save on the way out; the snapshot run ' +
      'id is returned so the finished state can be reloaded. Also reports how many camera faults were ' +
      'suppressed during resolution.',
    inputSchema: { type: 'object', properties: {
      action: { type: 'string', enum: ['read', 'reset', 'snapshot'], description: 'read (default), reset the latch, or snapshot on/off' },
      value: { type: 'string', enum: ['on', 'off'], description: 'For action=snapshot' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_run_status',
    description: 'The assertion surface for a whole run: map, round, stage, time of day, active/completed/' +
      'failed/future quests with their objective bitmaps, whether the party is in a dungeon, whether the ' +
      'summary screen is up, and whether a quest carrying AdventureEndTrigger=WIN has completed. Also reports ' +
      'the state of the last quest-completion pump INCLUDING its exception and stack trace - those run as ' +
      'async Tasks whose faults are otherwise swallowed, so a pump that died half-way looks exactly like a ' +
      'pump that found nothing to do. Warns when a STORY quest hits 0 rounds left, which ends the run in ' +
      'DEFEAT with no combat involved.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_quest_activate',
    description: 'DEBUG: put any quest straight into ActiveQuests, built the way the game builds it ' +
      '(QuestHelper.CreateQuestState), including stamping the ACTIVE map id - a quest with the wrong MapID ' +
      'can never complete. Needed because the quest chain CANNOT be walked by completing objectives alone: ' +
      'promotion out of FutureQuests is gated on QuestStartWorldTriggers (the route to chapter 1-1s WIN ' +
      'quest needs chaos stage 3), so a harness would otherwise have to play the game to get there.',
    inputSchema: { type: 'object', required: ['questId'], properties: {
      questId: { type: 'string', description: 'e.g. STORY_1_1_CLEAR_BANDIT_KING' }, ...INSTANCE_ARG } } },
  { name: 'ftk2_quest_complete_objective',
    description: 'Complete quest objectives BY ID (unlike ftk2_quest_complete, which takes an index), then ' +
      'pump the game own resolution pass. Flags EVERY active quest carrying that id, because ActiveQuests ' +
      'can hold duplicates and the duplicate is otherwise unreachable - and a duplicate that throws inside ' +
      'CheckObjectiveCompletion aborts the completion pass for EVERY quest ordered after it. Expect queued ' +
      'Choose Reward prompts afterwards: resolution awaits them, and unanswered they look exactly like a hang.',
    inputSchema: { type: 'object', required: ['questId'], properties: {
      questId: { type: 'string' },
      objective: { type: 'string', description: 'Objective index, or "all" (default)' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_tutorials_suppress',
    description: 'Mark every tutorial as already seen and switch the tutorial system off. Ids are harvested ' +
      'from the game own Tutorials.json rather than hard-coded, so a game update cannot silently ' +
      'reintroduce one. Worth calling before any unattended run: several Royal Tutor popups fire from ' +
      '_tryProceed itself (low health, end turn, receiving a portal scroll) and each one blocks the overworld.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_party_gain_xp',
    description: 'Grant XP through the game own ProgressionHelper.EntitiesGainXP, so levelling also heals ' +
      'to full, grants focus and emits LEVELED_UP. Level is DERIVED from an XP inventory Thing, so writing ' +
      'that stack by hand skips all three and desyncs HP. Reports levels before and after.',
    inputSchema: { type: 'object', required: ['xpEach'], properties: {
      xpEach: { type: 'number' },
      slot: { type: 'string', description: 'Party slot, or "all" (default)' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_encounter_leave',
    description: 'Close whatever encounter UI is open, via _stopEncounterAsync - the universal un-wedge. ' +
      'Needed because the market, town-services and quest-board branches never call _closeEncounterMenuAsync, ' +
      'so an encounter opened through them stays open forever. Also re-pumps quest completion on the way out.',
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

/**
 * Presses through every blocking gate until none is showing.
 *
 * Both the post-load gate and each intro story page render as `continue-label` inside
 * LoadingUIDocument, so "is a gate showing" cannot distinguish them and the only correct
 * behaviour is to keep pressing until none remains. Focus is set explicitly before each
 * press: a key press acts only on a focused target and focus does not persist between calls.
 */
/**
 * Blocking elements that are safe to dismiss, by EXACT name.
 *
 * Never match on text: "Continue" appears on several unrelated controls including
 * continue-btn, which resumes the owner's live co-op campaign.
 *
 * ok-btn and royal-tutor-container were observed blocking adventure selection - a tutorial
 * modal and the Royal Tutor overlay sit above the screen and silently prevent a fixture load,
 * which looks like "the load did not take" rather than like a modal.
 */
const DISMISSABLE = [
  'continue-label',
  'dialogue-container',
  'ok-btn',
  'royal-tutor-container',
  'sys-dialog-ok-btn',
  'online-quit-btn',
];

/**
 * Everything currently on screen that can block progress, annotated.
 *
 * The reason each entry exists is carried with it: a bare list of names does not tell a caller
 * whether an "ok-btn" is a harmless tutorial prompt or something that matters.
 */
const DISMISSABLE_REASONS = {
  'continue-label': 'post-load gate, and each intro story page (there are several)',
  'dialogue-container': 'story dialogue page; quest resolution AWAITS this, so leaving it up '
    + 'stalls _resolveQuests and the adventure never reaches its end-of-run check',
  'ok-btn': 'tutorial or system prompt, the "Understood" button',
  'royal-tutor-container': 'Royal Tutor tutorial overlay',
  'sys-dialog-ok-btn': 'system dialog confirm (safe; NOT continue-btn despite also reading Continue)',
  'online-quit-btn': 'boot-time online error modal',
};

async function listDialogs(inst) {
  const dump = await execText(inst, 'crucible_ui_dump', ['-', 'button']);
  const blocking = [];
  for (const [name, reason] of Object.entries(DISMISSABLE_REASONS)) {
    if (!dump.includes(`name='${name}'`)) continue;
    let doc = null;
    for (const line of dump.split('\n')) {
      if (line.includes(`name='${name}'`)) {
        const match = line.match(/doc='([^']+)'/);
        doc = match ? match[1] : null;
        break;
      }
    }
    blocking.push({ name, doc, reason, safeToDismiss: true });
  }
  return {
    blocking,
    blockingCount: blocking.length,
    docs: await activeDocs(inst),
    hint: blocking.length
      ? 'Dismiss with ftk2_dismiss, or clear them all with ftk2_clear_gates.'
      : 'Nothing is blocking. If the game still ignores input, check ftk2_state route and interactionEnabled.',
  };
}

async function dismissDialog(inst, name) {
  if (!Object.prototype.hasOwnProperty.call(DISMISSABLE_REASONS, name)) {
    return {
      ok: false,
      error: `refusing to press '${name}': not on the safe list`,
      safeNames: Object.keys(DISMISSABLE_REASONS),
    };
  }
  const before = await activeDocs(inst);
  // Focus first: a key press acts only on a focused target and focus does not persist.
  await execText(inst, 'crucible_ui_focus', [name]);
  await execText(inst, 'crucible_key', ['enter']);
  await sleep(2000);
  const after = await activeDocs(inst);
  const dump = await execText(inst, 'crucible_ui_dump', ['-', 'button']);
  return {
    ok: !dump.includes(`name='${name}'`),
    name,
    docsBefore: before,
    docsAfter: after,
  };
}

async function clearGates(inst, maxRounds) {
  const cap = Math.max(1, Math.min(maxRounds || 12, 40));
  const pressed = [];

  for (let round = 0; round < cap; round++) {
    const dump = await execText(inst, 'crucible_ui_dump', ['-', 'button']);
    const present = DISMISSABLE.filter((name) => dump.includes(`name='${name}'`));
    if (present.length === 0) {
      return { ok: true, pressed, docs: await activeDocs(inst) };
    }
    for (const name of present) {
      // Focus first: a key press acts only on a focused target, and focus does not persist.
      await execText(inst, 'crucible_ui_focus', [name]);
      await execText(inst, 'crucible_key', ['enter']);
      pressed.push(name);
      await sleep(1500);
    }
  }

  const dump = await execText(inst, 'crucible_ui_dump', ['-', 'button']);
  const remaining = DISMISSABLE.filter((name) => dump.includes(`name='${name}'`));
  return {
    ok: remaining.length === 0,
    pressed,
    remaining,
    docs: await activeDocs(inst),
    warning: remaining.length ? `still blocked by: ${remaining.join(', ')}` : undefined,
  };
}

/** Names of UIDocuments with something visible. Screen identity must come from the UI tree, not route. */
async function activeDocs(inst) {
  const dump = await execText(inst, 'crucible_ui_dump', ['-', 'button']);
  const found = new Set();
  for (const part of dump.split("doc='").slice(1)) {
    const name = part.split("'")[0];
    if (name && name.endsWith('UIDocument')) found.add(name);
  }
  return [...found].sort();
}

async function execText(inst, command, args) {
  const res = await rpc(inst, 'POST', '/exec', { command, args: args || [] });
  return res.result || '';
}

/**
 * Cold state -> loaded, interactive overworld. Every step is confirmed rather than assumed:
 * boot is a race (the game reaches MAIN_MENU then routes itself to MULTIPLAYER_LOBBY about two
 * seconds later), a campaign click can land before the menu is interactive, and _loadGameRun
 * silently does nothing when the adventure selection screen is not up.
 */
async function bootToRun(inst, runId) {
  if (!runId) return { ok: false, error: 'runId is required' };
  const steps = [];

  for (let attempt = 1; attempt <= 3; attempt++) {
    steps.push(`attempt ${attempt}`);

    // Already in a run? Return to the main menu first. Adventure selection is only reachable
    // from there and _loadGameRun needs it to exist, so loading a second fixture in one
    // session otherwise silently does nothing.
    let snap0 = (await rpc(inst, 'GET', statePath('v2'))).snapshot || {};
    if (snap0.run && snap0.run.present) {
      await execText(inst, 'crucible_invoke', ['RouterMono', 'Route', 'MAIN_MENU 0 - false true']);
      for (let i = 0; i < 8; i++) {
        await sleep(3000);
        snap0 = (await rpc(inst, 'GET', statePath('v2'))).snapshot || {};
        if (!snap0.run || !snap0.run.present) break;
      }
      steps.push('  returned to the main menu');
    }

    let reached = false;
    for (let i = 0; i < 8; i++) {
      const docs = await activeDocs(inst);
      if (docs.includes('AdventureSelectionUIDocument')) { reached = true; break; }
      if (docs.includes('MainMenuUIDocument')) {
        await execText(inst, 'crucible_ui_click', ['campaign-btn']);
      } else if (docs.some((d) => d.startsWith('Multiplayer') || d.startsWith('Online'))) {
        // The boot auto-rejoin leaves an error modal plus the lobby behind it.
        await execText(inst, 'crucible_ui_click', ['online-quit-btn']);
        await execText(inst, 'crucible_ui_click', ['back-btn']);
      }
      await sleep(4000);
    }
    if (!reached) { steps.push('  never reached adventure selection'); continue; }
    steps.push('  adventure selection is up');

    // Env.GameRuns is cached at startup: a save file created while the game is running is
    // invisible to it and the load silently does nothing. Refresh the list first.
    await execText(inst, 'crucible_refresh_saves', []);
    await execText(inst, 'crucible_invoke',
      ['AdventureSelectionDirector', '_loadGameRun', `${runId} -`]);

    let loaded = false;
    for (let i = 0; i < 15; i++) {
      await sleep(4000);
      const snap = (await rpc(inst, 'GET', statePath('v2'))).snapshot || {};
      if (snap.run && snap.run.present) { loaded = true; break; }
    }
    if (!loaded) { steps.push('  load did not take'); continue; }
    steps.push('  run loaded');

    const gates = await clearGates(inst, 20);
    steps.push(`  gates cleared with ${gates.presses} press(es); docs=${(gates.docs || []).join(', ')}`);

    const snap = (await rpc(inst, 'GET', statePath('v2'))).snapshot || {};
    return {
      ok: true,
      runId,
      steps,
      route: snap.route,
      interactive: (gates.docs || []).includes('AdventureUIDocument'),
      docs: gates.docs,
    };
  }

  return { ok: false, runId, steps, error: 'could not reach a loaded overworld' };
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
    case 'ftk2_clear_gates':   return textResult(await clearGates(inst, args.maxPresses));
    case 'ftk2_refresh_saves': return textResult(await execText(inst, 'crucible_refresh_saves', []));
    case 'ftk2_dialogs':       return textResult(await listDialogs(inst));
    case 'ftk2_dismiss':       return textResult(await dismissDialog(inst, args.name));
    case 'ftk2_party':         return textResult(await execText(inst, 'crucible_party_list', []));
    case 'ftk2_set_class':     return textResult(await execText(inst, 'crucible_party_set_class', [String(args.slot), args.classId]));
    case 'ftk2_fixture_save':  return textResult(await execText(inst, 'crucible_fixture_save', [args.label || 'fixture']));
    case 'ftk2_set_hex':       return textResult(await execText(inst, 'crucible_party_set_hex', [String(args.x), String(args.y)]));
    case 'ftk2_reveal_map':    return textResult(await execText(inst, 'crucible_reveal_map', [args.state || 'Visible']));
    case 'ftk2_map_encounters':return textResult(await execText(inst, 'crucible_map_encounters', [args.filter || '-']));
    case 'ftk2_abilities':     return textResult(await execText(inst, 'crucible_party_abilities', [String(args.slot)]));
    case 'ftk2_class_config':  return textResult(await execText(inst, 'crucible_class_config', [args.classId]));
    case 'ftk2_status_config': return textResult(await execText(inst, 'crucible_status_config', [args.statusId]));
    case 'ftk2_spawn':         return textResult(await execText(inst, 'crucible_debug_spawn', [args.kind, args.selector || '-']));

    case 'ftk2_boot_to_run':   return textResult(await bootToRun(inst, args.runId));
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
    case 'ftk2_godmode':
      return textResult(await execText(inst, 'crucible_godmode', [String(args.mode || 'status')]));
    case 'ftk2_combat_spawn':
      return textResult(await execText(inst, 'crucible_combat_spawn', [
        String(args.characterConfig),
        String(args.count === undefined ? 1 : args.count),
        String(args.group === undefined ? 1 : args.group)]));
    case 'ftk2_combat_wipe_enemies':
      return textResult(await execText(inst, 'crucible_combat_wipe_enemies',
        [String(args.group === undefined ? 1 : args.group)]));
    case 'ftk2_combat_restore_actions':
      return textResult(await execText(inst, 'crucible_combat_restore_actions',
        [String(args.group === undefined ? 0 : args.group)]));
    case 'ftk2_loc':
      return textResult(await execText(inst, 'crucible_loc', [String(args.key)]));
    case 'ftk2_summary_dismiss':
      return textResult(await execText(inst, 'crucible_summary_dismiss', []));
    case 'ftk2_endadventure_watch':
      return textResult(await execText(inst, 'crucible_endadventure_watch',
        [String(args.action === undefined || args.action === 'read' ? '' : args.action), String(args.value || '')]));
    case 'ftk2_run_status':
      return textResult(await execText(inst, 'crucible_run_status', []));
    case 'ftk2_quest_activate':
      return textResult(await execText(inst, 'crucible_quest_activate', [String(args.questId)]));
    case 'ftk2_quest_complete_objective':
      return textResult(await execText(inst, 'crucible_quest_complete_objective',
        [String(args.questId), String(args.objective || 'all')]));
    case 'ftk2_tutorials_suppress':
      return textResult(await execText(inst, 'crucible_tutorials_suppress', []));
    case 'ftk2_party_gain_xp':
      return textResult(await execText(inst, 'crucible_party_gain_xp',
        [String(args.xpEach), String(args.slot || 'all')]));
    case 'ftk2_encounter_leave':
      return textResult(await execText(inst, 'crucible_encounter_leave', []));
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
