// ProjectH QA UI (QA-2, D19-D25). Plain JS, no build step. All text from the server goes through textContent (never
// innerHTML), so scenario contents or log lines cannot inject markup.
'use strict';

const COMMON = ['id', 'action', 'actor', 'phase', 'timeoutMilliseconds', 'continueOnFailure', 'saveAs', 'description'];
const OPERATORS = ['equals', 'notEquals', 'greaterThan', 'lessThan', 'between', 'exists', 'notExists', 'approximately', 'tolerance', 'contains'];
const CATEGORIES = ['QA', 'Server', 'Actor', 'Network', 'Assertion'];
const MAX_LOG_LINES = 1000;          // D21: the page keeps the newest 1000 lines
const INSPECT_MS = 1000;             // D22: inspector polling while running or paused

const state = {
  path: null,        // relative to QA/Scenarios
  doc: null,         // parsed scenario (null when the raw text is not plain JSON)
  raw: '',           // the editor text
  savedText: null,
  specs: {},         // action name -> spec
  markers: [],
  run: null,         // last UiRunState
  selected: -1,
  breakpoints: new Set(),
  logSkipped: 0,
  selectedActor: null,
  tab: { editor: 'form', bottom: 'log' },
};

const $ = (id) => document.getElementById(id);

function el(tag, props, ...children) {
  const e = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (k === 'class') e.className = v;
      else if (k === 'text') e.textContent = v;
      else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
      else if (v !== undefined && v !== null && v !== false) e.setAttribute(k, v === true ? '' : v);
    }
  }
  for (const c of children) if (c !== null && c !== undefined) e.append(c);
  return e;
}

async function api(method, url, body) {
  const options = { method, headers: {} };
  if (body !== undefined) {
    options.headers['Content-Type'] = 'application/json';
    options.body = JSON.stringify(body);
  }
  const response = await fetch(url, options);
  let data = null;
  try { data = await response.json(); } catch { data = null; }
  return { ok: response.ok, status: response.status, data };
}

function message(text, kind = 'info') {
  const box = $('messages');
  box.replaceChildren();
  if (!text) return;
  const lines = Array.isArray(text) ? text : [text];
  for (const line of lines) box.append(el('div', { class: kind, text: line }));
}

// ---- scenario text <-> form ----

// One step per line keeps repository diffs readable (the hand-written scenarios use the same layout).
function inline(value) {
  if (Array.isArray(value)) {
    if (value.length === 0) return '[]';
    const parts = value.map(inline).join(', ');
    return value.some((v) => v !== null && typeof v === 'object') ? '[ ' + parts + ' ]' : '[' + parts + ']';
  }
  if (value !== null && typeof value === 'object') {
    const keys = Object.keys(value);
    if (keys.length === 0) return '{}';
    return '{ ' + keys.map((k) => JSON.stringify(k) + ': ' + inline(value[k])).join(', ') + ' }';
  }
  return JSON.stringify(value);
}

function format(doc) {
  const lines = ['{'];
  const keys = Object.keys(doc);
  keys.forEach((key, i) => {
    const last = i === keys.length - 1;
    if (key === 'steps' && Array.isArray(doc.steps)) {
      lines.push('  "steps": [');
      doc.steps.forEach((s, j) => lines.push('    ' + inline(s) + (j === doc.steps.length - 1 ? '' : ',')));
      lines.push('  ]' + (last ? '' : ','));
    } else {
      lines.push('  ' + JSON.stringify(key) + ': ' + inline(doc[key]) + (last ? '' : ','));
    }
  });
  lines.push('}');
  return lines.join('\n') + '\n';
}

function setText(text, fromForm) {
  state.raw = text;
  if (!fromForm) $('raw').value = text;
  try {
    state.doc = JSON.parse(text);
    if (typeof state.doc !== 'object' || state.doc === null || Array.isArray(state.doc)) state.doc = null;
  } catch {
    state.doc = null;
  }
  $('preview').textContent = state.doc ? format(state.doc) : text;
  renderForm();
}

function formChanged() {
  setText(format(state.doc), false);
}

function stepAction(step) {
  return step.action || (step.assert !== undefined ? 'assert' : '?');
}

function stepTitle(step, i) {
  const num = String(i + 1).padStart(2, '0');
  if (step.description) return num + ' ' + step.description;
  const action = stepAction(step);
  let detail = '';
  for (const k of ['assert', 'path', 'condition', 'event', 'position', 'target', 'button', 'weapon', 'piece', 'state', 'milliseconds', 'count']) {
    if (step[k] !== undefined) { detail = ' ' + (typeof step[k] === 'string' ? step[k] : JSON.stringify(step[k])); break; }
  }
  return num + ' ' + action.charAt(0).toUpperCase() + action.slice(1) + (step.actor ? ' ' + step.actor : '') + detail;
}

function parseValue(text) {
  if (text === '') return undefined;
  try { return JSON.parse(text); } catch { return text; }
}

function showValue(v) {
  return typeof v === 'string' ? v : JSON.stringify(v);
}

function renderScenarioFields() {
  const box = $('scenario-fields');
  box.replaceChildren();
  if (!state.doc) return;
  const doc = state.doc;
  const field = (label, key, kind) => {
    box.append(el('label', { text: label }));
    if (kind === 'json') {
      const ta = el('textarea', { spellcheck: 'false' });
      ta.value = doc[key] === undefined ? '' : JSON.stringify(doc[key]);
      ta.addEventListener('change', () => {
        const v = parseValue(ta.value.trim());
        if (v === undefined) delete doc[key]; else doc[key] = v;
        formChanged();
      });
      box.append(ta);
    } else {
      const input = el('input', { type: kind === 'number' ? 'number' : 'text' });
      input.value = doc[key] === undefined ? '' : doc[key];
      input.addEventListener('change', () => {
        if (input.value === '') delete doc[key];
        else doc[key] = kind === 'number' ? Number(input.value) : input.value;
        formChanged();
      });
      box.append(input);
    }
  };
  field('name', 'name');
  field('description', 'description');
  field('seed', 'seed', 'number');
  field('timeoutSeconds', 'timeoutSeconds', 'number');
  field('tags', 'tags', 'json');
  field('server', 'server', 'json');
  field('variables', 'variables', 'json');
  field('actors', 'actors', 'json');
  field('parameters', 'parameters', 'json');   // QA-5 D31: one run per entry
  field('baseline', 'baseline', 'json');       // QA-5 D33: { "values": ["savedName"] }
  field('stress', 'stress', 'json');           // Stress D39: true = headless only, QA events off, quiet live log
}

function runStepFor(i) {
  const run = state.run;
  if (!run || !run.steps || !state.doc || run.steps.length !== (state.doc.steps || []).length) return null;
  if (run.path && state.path && run.path !== state.path) return null;
  return run.steps[i];
}

let dragFrom = -1;

function renderForm() {
  renderScenarioFields();
  const box = $('steps');
  box.replaceChildren();
  if (!state.doc) {
    if (state.raw.trim()) box.append(el('p', { class: 'muted', text: 'Form view needs plain JSON (no comments or trailing commas). Edit in Raw JSON; Validate and Save still work.' }));
    return;
  }
  const steps = Array.isArray(state.doc.steps) ? state.doc.steps : (state.doc.steps = []);
  steps.forEach((step, i) => box.append(renderStep(step, i, steps)));
}

function renderStep(step, i, steps) {
  const run = runStepFor(i);
  const waiting = state.run && state.run.waitingAt === i && state.run.status === 'paused';
  const wrap = el('div', { class: 'step' + (i === state.selected ? ' selected' : ''), draggable: 'true' });
  wrap.addEventListener('dragstart', (e) => { dragFrom = i; e.dataTransfer.effectAllowed = 'move'; });
  wrap.addEventListener('dragover', (e) => { e.preventDefault(); wrap.classList.add('dragover'); });
  wrap.addEventListener('dragleave', () => wrap.classList.remove('dragover'));
  wrap.addEventListener('drop', (e) => {
    e.preventDefault();
    wrap.classList.remove('dragover');
    if (dragFrom < 0 || dragFrom === i) return;
    const [moved] = steps.splice(dragFrom, 1);
    steps.splice(i, 0, moved);
    state.selected = i;
    dragFrom = -1;
    formChanged();
  });

  const bp = el('span', { class: 'bp' + (state.breakpoints.has(i) || step.breakpoint ? ' on' : ''), title: 'Breakpoint (UI run only)' });
  bp.addEventListener('click', (e) => {
    e.stopPropagation();
    if (state.breakpoints.has(i)) state.breakpoints.delete(i); else state.breakpoints.add(i);
    if (busy()) api('POST', '/api/run/breakpoints', { indices: [...state.breakpoints] });
    renderForm();
  });
  const status = run ? run.status : 'Pending';
  const row = el('div', { class: 'row' },
    bp,
    el('span', { class: 'st ' + status, text: run ? status.toUpperCase() : '' }),
    el('span', { class: 'title', text: stepTitle(step, i) }),
    waiting ? el('span', { class: 'paused-here', text: state.run.failedWaiting ? 'held (failed)' : 'paused here' }) : null,
    run && run.durationMs ? el('span', { class: 'muted', text: run.durationMs + ' ms' }) : null,
    el('button', { text: '▲', title: 'Move up', onclick: (e) => { e.stopPropagation(); move(i, -1); } }),
    el('button', { text: '▼', title: 'Move down', onclick: (e) => { e.stopPropagation(); move(i, 1); } }),
    el('button', { text: '✕', title: 'Delete', onclick: (e) => { e.stopPropagation(); steps.splice(i, 1); state.selected = -1; formChanged(); } }),
  );
  row.addEventListener('click', () => { state.selected = state.selected === i ? -1 : i; renderForm(); });
  wrap.append(row);
  if (run && (run.message || run.expected || run.actual)) {
    const detail = el('div', { class: 'detail' });
    const failed = run.status === 'Failed' || run.status === 'Error';
    if (run.message) detail.append(el('div', { class: failed ? 'fail' : 'muted', text: run.message }));
    if (run.expected !== null && run.expected !== undefined) detail.append(el('div', { class: 'fail', text: 'Expected: ' + run.expected }));
    if (run.actual !== null && run.actual !== undefined) detail.append(el('div', { class: 'fail', text: 'Actual:   ' + run.actual }));
    if (run.attempts) detail.append(el('div', { class: 'muted', text: 'retried ' + run.attempts + 'x' }));
    wrap.append(detail);
  }
  if (i === state.selected) wrap.append(renderEditor(step, steps, i));
  return wrap;
}

function move(i, delta) {
  const steps = state.doc.steps;
  const j = i + delta;
  if (j < 0 || j >= steps.length) return;
  [steps[i], steps[j]] = [steps[j], steps[i]];
  state.selected = j;
  formChanged();
}

function renderEditor(step, steps, i) {
  const box = el('div', { class: 'edit' });
  box.addEventListener('click', (e) => e.stopPropagation());
  const spec = state.specs[stepAction(step)];

  // Action picker (keeps the parameters that still make sense).
  const select = el('select');
  for (const name of Object.keys(state.specs).sort()) select.append(el('option', { value: name, text: name, selected: name === stepAction(step) }));
  select.addEventListener('change', () => {
    if (step.assert !== undefined) { step.path = step.assert; delete step.assert; }
    step.action = select.value;
    formChanged();
  });
  box.append(el('span', { class: 'req', text: 'action' }), select, el('span'));

  const required = spec ? spec.required.flatMap((r) => r.split('|')) : [];
  const keys = Object.keys(step).filter((k) => k !== 'action');
  for (const key of keys) {
    const input = el('input');
    input.value = showValue(step[key]);
    input.addEventListener('change', () => {
      const v = parseValue(input.value);
      if (v === undefined) delete step[key]; else step[key] = v;
      formChanged();
    });
    if (spec && spec.positions.includes(key)) input.setAttribute('list', 'marker-list');
    box.append(el('span', { class: required.includes(key) ? 'req' : '', text: key }), input,
      el('button', { text: '✕', title: 'Remove', onclick: () => { delete step[key]; formChanged(); } }));
  }

  // + parameter: the spec's parameters, the common fields, the operators for assertions.
  const add = el('select');
  add.append(el('option', { value: '', text: '+ parameter' }));
  const candidates = new Set([...(spec ? [...required, ...spec.optional] : []), ...COMMON.filter((c) => c !== 'action'), 'breakpoint']);
  if (spec && (spec.name === 'assert' || spec.name === 'waitFor')) OPERATORS.forEach((o) => candidates.add(o));
  for (const c of candidates) if (!(c in step)) add.append(el('option', { value: c, text: c + (required.includes(c) ? ' (required)' : '') }));
  add.addEventListener('change', () => {
    if (!add.value) return;
    step[add.value] = add.value === 'continueOnFailure' || add.value === 'breakpoint' ? true : '';
    formChanged();
  });
  box.append(el('span', { class: 'muted', text: spec ? (spec.serverCommand ? 'server QA command (Arrange)' : '') : 'unknown action' }), add, el('span'));
  return box;
}

function renderMarkerList() {
  let list = document.getElementById('marker-list');
  if (!list) { list = el('datalist', { id: 'marker-list' }); document.body.append(list); }
  list.replaceChildren(...state.markers.map((m) => el('option', { value: m })));
}

// ---- tree ----

async function loadTree() {
  const { data } = await api('GET', '/api/tree');
  const tree = $('tree');
  tree.replaceChildren();
  if (!data) return;
  for (const cat of data.categories) {
    tree.append(el('h4', { text: cat.name }));
    for (const f of cat.files) {
      const item = el('div', { class: 'item' + (f.path === state.path ? ' current' : ''), title: f.path + (f.tags.length ? ' [' + f.tags.join(', ') + ']' : ''), text: f.name + (f.malformed ? ' (malformed)' : '') });
      item.addEventListener('click', () => openScenario(f.path));
      tree.append(item);
    }
  }
  tree.append(el('h4', { text: 'Suites' }));
  for (const s of data.suites) {
    tree.append(el('div', { class: 'muted', text: s.name }));
    const items = el('div', { class: 'suite-items' });
    for (const p of s.scenarios) {
      const item = el('div', { class: 'item', text: p });
      item.addEventListener('click', () => openScenario(p));
      items.append(item);
    }
    tree.append(items);
  }
}

async function openScenario(path) {
  if (state.raw !== (state.savedText ?? '') && state.raw && !confirm('Discard unsaved changes?')) return;
  const { ok, data } = await api('GET', '/api/scenario?path=' + encodeURIComponent(path));
  if (!ok) { message(data ? data.error : 'Could not open ' + path, 'error'); return; }
  state.path = path;
  state.savedText = data.text;
  state.selected = -1;
  state.breakpoints.clear();
  $('file-label').textContent = path;
  setText(data.text, false);
  message('');
  loadTree();
}

// ---- validate / save ----

async function validate() {
  const { data } = await api('POST', '/api/validate', { text: state.raw });
  if (!data) return false;
  if (data.ok) message(['Valid.'].concat(data.warnings), data.warnings.length ? 'warning' : 'info');
  else message(data.errors.concat(data.warnings), 'error');
  return data.ok;
}

async function save() {
  let path = state.path;
  if (!path) {
    path = prompt('Save as (under QA/Scenarios, e.g. Smoke/my_test.json):', 'Smoke/new_scenario.json');
    if (!path) return;
  }
  const { ok, status, data } = await api('POST', '/api/save', { path, text: state.raw });
  if (ok) {
    state.path = path;
    state.savedText = state.raw;
    $('file-label').textContent = path;
    message(['Saved ' + path + '.'].concat(data.warnings), data.warnings.length ? 'warning' : 'info');
    loadTree();
  } else if (status === 422) {
    message(['Not saved: fix the errors first.'].concat(data.errors), 'error');
  } else {
    message(data ? data.error : 'Save failed.', 'error');
  }
}

function newScenario() {
  state.path = null;
  state.savedText = null;
  state.selected = -1;
  state.breakpoints.clear();
  $('file-label').textContent = '(new, unsaved)';
  setText(format({ schemaVersion: 1, name: 'New Scenario', description: '', tags: [], seed: 1, timeoutSeconds: 60,
    actors: [{ id: 'playerA', type: 'HeadlessClient' }], steps: [{ id: 'a_connect', action: 'connect', actor: 'playerA' }] }), false);
}

function addStep() {
  if (!state.doc) { message('Form view needs plain JSON.', 'error'); return; }
  const action = $('add-action').value;
  const spec = state.specs[action];
  const step = { action };
  if (spec && spec.actor === 'Required') step.actor = ((state.doc.actors || [])[0] || {}).id || '';
  if (spec) for (const r of spec.required) step[r.split('|')[0]] = '';
  if (action === 'assert' || action === 'waitFor') step.equals = '';
  const steps = state.doc.steps || (state.doc.steps = []);
  const at = state.selected >= 0 ? state.selected + 1 : steps.length;
  steps.splice(at, 0, step);
  state.selected = at;
  formChanged();
}

// ---- run ----

function busy() {
  return state.run && ['starting', 'running', 'paused'].includes(state.run.status);
}

async function startRun(mode) {
  if (!state.raw.trim()) { message('Open or write a scenario first.', 'error'); return; }
  if ((mode === 'from' || mode === 'until') && state.selected < 0) { message('Select a step first (click it).', 'error'); return; }
  if (mode === 'from' && state.selected > 0 &&
      !confirm('Run From Step starts a new server: steps before it do not run, so their Arrange setup and saved variables are missing. Continue?')) return;
  const seedText = $('seed').value.trim();
  const body = {
    path: state.path, text: state.raw, mode, stepIndex: Math.max(0, state.selected),
    seed: seedText === '' ? null : Number(seedText), debug: $('debug').checked, breakpoints: [...state.breakpoints],
  };
  // QA-5: repeat / seed sweep / stop on fail (Run only; the server checks the ranges).
  if (mode === 'run') {
    const repeat = Number($('repeat').value || '1');
    if (repeat > 1) body.repeat = repeat;
    const sweep = $('seed-sweep').value.trim();
    if (sweep) body.seedSweep = sweep;
  }
  if ($('stop-on-fail').checked) body.stopOnFail = true;
  const { ok, data } = await api('POST', '/api/run', body);
  if (!ok) message(data && data.errors ? ['Not started:'].concat(data.errors) : (data ? data.error : 'Run failed to start.'), 'error');
  else { message(''); clearLog(); applyState(data); }
}

async function command(name) {
  const { ok, data } = await api('POST', '/api/run/' + name, {});
  if (!ok) message(data ? data.error : name + ' failed', 'error');
  else if (data && data.warning) message(data.warning, 'warning');
}

function applyState(run) {
  state.run = run;
  const badge = $('run-status');
  let text = run.status;
  if (run.status === 'finished' && run.runStatus) text = run.runStatus + (run.exitCode !== null && run.exitCode !== undefined ? ' (exit ' + run.exitCode + ')' : '');
  if (run.status === 'paused') text = run.manualCheck ? 'manual check at step ' + (run.waitingAt + 1) : run.failedWaiting ? 'held at failed step ' + (run.waitingAt + 1) : 'paused before step ' + (run.waitingAt + 1);
  // D30: a manual check waits for PASS / FAIL from a person.
  $('manual-panel').classList.toggle('hidden', !run.manualCheck);
  if (run.manualCheck) $('manual-text').textContent = run.manualCheck;
  const batch = run.batch;
  if (batch && batch.total > 1 && busy()) text += '  run ' + batch.current + '/' + batch.total;
  if (batch && run.status === 'finished' && batch.total > 1) text = 'batch ' + batch.done + '/' + batch.total + ': ' + batch.passed + ' passed, ' + (batch.failed + batch.errors) + ' not passed';
  if (batch && batch.currentParameters && busy()) text += '  ' + batch.currentParameters;
  badge.textContent = text + (run.seed !== null && run.seed !== undefined ? '  seed ' + run.seed : '');
  renderBatch(batch);
  badge.className = 'badge ' + run.status + ' ' + (run.runStatus || '');
  const b = busy();
  $('btn-run').disabled = b;
  $('btn-run-from').disabled = b;
  $('btn-run-until').disabled = b;
  $('btn-step').disabled = b && run.status !== 'paused';
  $('btn-pause').disabled = run.status !== 'running';
  $('btn-resume').disabled = run.status !== 'paused';
  $('btn-stop').disabled = !b;
  $('btn-retry').disabled = !run.failedWaiting;
  $('btn-retry-scenario').disabled = b || !run.scenario;
  const notes = [];
  if (run.status === 'paused') notes.push('Paused: the runner waits, but the game server keeps simulating (the zone shrinks, grace runs out, a finished round resets).');
  if (run.failedWaiting) notes.push('The failed step is held: Retry Failed Step runs it again in the same game (its state may have changed); Resume finishes the run as failed.');
  if (run.status === 'finished' && run.error) notes.push(run.error);
  if (run.status === 'finished' && run.reportUrl) notes.push('Report: ' + run.reportUrl);
  if (run.unsaved) notes.push('Ran from unsaved editor text.');
  if (notes.length || run.status === 'finished') message(notes.concat(run.status === 'finished' ? run.warnings : []), run.runStatus === 'Passed' ? 'info' : 'warning');
  renderForm();
}

// QA-5 D31-D32: one row per run of the batch (parameter set, seed, result, report) and the end summary.
function renderBatch(batch) {
  const box = $('batch');
  if (!batch) return;
  box.replaceChildren();
  box.append(el('div', { text: `${batch.done}/${batch.total} runs: ${batch.passed} passed, ${batch.failed} failed, ${batch.skipped} skipped, ${batch.errors} errors` + (batch.stopOnFail ? '  (stop on fail)' : '') }));
  if (batch.currentParameters && busy()) box.append(el('div', { class: 'muted', text: 'Now: run ' + batch.current + ' ' + batch.currentParameters }));
  // Stress D40: the judged phase of each run (players, tick p50/p95/p99/max, CPU, memory, GC, network, DB queue, build).
  const stress = batch.rows.some((r) => r.stress);
  const fmt = (v) => (typeof v === 'number' ? (Math.abs(v) >= 100 ? v.toFixed(1) : String(Math.round(v * 1000) / 1000)) : '');
  const stressHeads = ['Players', 'Tick P50', 'P95', 'P99', 'Max', 'CPU %', 'Managed MB', 'WS MB', 'GC 0/1/2', 'Send KB/s', 'Recv KB/s', 'DB Queue', 'Build', 'Stalls', 'R1 P95'];
  const stressCells = (x) => !x ? stressHeads.map(() => '') : [String(x.players), fmt(x.tickP50Ms), fmt(x.tickP95Ms), fmt(x.tickP99Ms), fmt(x.tickMaxMs) + (x.ticksExact ? '' : '*'),
    fmt(x.cpuPercent), fmt(x.managedMB), fmt(x.workingSetMB), x.gen0 + '/' + x.gen1 + '/' + x.gen2, fmt(x.sendKBps), fmt(x.recvKBps), String(x.dbQueueMax),
    String(x.buildPieces), String(x.stalls), x.inputLatencyP95Ms == null ? 'n/a' : fmt(x.inputLatencyP95Ms)];
  const heads = [el('th', { text: '#' }), el('th', { text: 'Iteration' }), el('th', { text: 'Parameters' }), el('th', { text: 'Seed' })];
  if (stress) for (const h of stressHeads) heads.push(el('th', { text: h }));
  heads.push(el('th', { text: 'Result' }), el('th', { text: 'Run' }));
  const table = el('table', null, el('tr', null, ...heads));
  for (const r of batch.rows) {
    const cells = [el('td', { text: String(r.number) }), el('td', { text: r.iteration ? String(r.iteration) : '' }),
      el('td', { text: r.parameterSet ? '[' + r.parameterSet + '] ' + (r.parameters || '') : '' }), el('td', { text: String(r.seed) })];
    if (stress) for (const c of stressCells(r.stress)) cells.push(el('td', { text: c }));
    cells.push(el('td', { class: 'st ' + r.status, text: r.status }),
      el('td', null, r.reportUrl ? el('a', { href: r.reportUrl, target: '_blank', rel: 'noopener', text: r.runId }) : el('span', { text: r.runId })));
    table.append(el('tr', null, ...cells));
  }
  box.append(table);
  if (batch.summary && batch.summary.length) box.append(el('pre', { text: batch.summary.join('\n') }));
}

function applyStep(step) {
  if (!state.run || !state.run.steps || step.index >= state.run.steps.length) return;
  state.run.steps[step.index] = step;
  renderForm();
}

// ---- live log (D21) ----

function buildLogFilters() {
  const box = $('log-filters');
  for (const c of CATEGORIES) {
    const cb = el('input', { type: 'checkbox', checked: true });
    cb.addEventListener('change', () => { $('log').classList.toggle('hide-' + c, !cb.checked); });
    box.append(el('label', null, cb, ' ' + c));
  }
  const style = el('style', { text: CATEGORIES.map((c) => `#log.hide-${c} .${c} { display: none; }`).join('\n') });
  document.head.append(style);
}

function clearLog() {
  $('log').replaceChildren();
  state.logSkipped = 0;
  $('log-skipped').textContent = '';
}

function appendLog(batch) {
  const log = $('log');
  const nearBottom = log.parentElement.scrollTop + log.parentElement.clientHeight >= log.parentElement.scrollHeight - 30;
  for (const line of batch.lines) {
    const time = new Date(line.time).toLocaleTimeString();
    log.append(el('div', { class: 'line ' + line.category }, el('span', { class: 'muted', text: time + ' ' }), el('span', { class: 'cat', text: line.category }), line.text));
  }
  while (log.childElementCount > MAX_LOG_LINES) log.firstElementChild.remove();
  if (batch.skipped) {
    state.logSkipped += batch.skipped;
    $('log-skipped').textContent = state.logSkipped + ' lines skipped (flood limit; see the report / server log)';
  }
  if (nearBottom) log.parentElement.scrollTop = log.parentElement.scrollHeight;
}

// ---- inspectors (D22) ----

async function pollInspectors() {
  if (!busy()) return;
  const tab = state.tab.bottom;
  if (tab === 'actors') {
    const { ok, data } = await api('GET', '/api/inspect/actors');
    const list = $('actor-list');
    list.replaceChildren();
    if (!ok) { list.append(el('div', { class: 'muted', text: data ? data.error : 'unavailable' })); return; }
    for (const a of data) {
      const row = el('div', { class: 'actor' + (a.alias === state.selectedActor ? ' selected' : ''),
        text: `${a.alias.padEnd(14)} ${a.status.padEnd(12)} hp ${a.health} sh ${a.shield}${a.alive ? '' : ' dead'}` });
      row.addEventListener('click', () => { state.selectedActor = a.alias; pollInspectors(); });
      list.append(row);
    }
    if (state.selectedActor) {
      const r = await api('GET', '/api/inspect/actor?alias=' + encodeURIComponent(state.selectedActor));
      $('actor-detail').textContent = r.ok ? describeActor(r.data) : (r.data ? r.data.error : 'unavailable');
    }
  } else if (tab === 'match') {
    const r = await api('GET', '/api/inspect/match');
    $('match-view').textContent = r.ok ? describeMatch(r.data) : (r.data ? r.data.error : 'unavailable');
  } else if (tab === 'server') {
    const r = await api('GET', '/api/inspect/server');
    $('server-view').textContent = r.ok ? describeServer(r.data) : (r.data ? r.data.error : 'unavailable');
  }
}

function describeActor(d) {
  const p = d.player || {};
  const a = d.actor || {};
  const pos = p.position ? `${p.position.x.toFixed(2)}, ${p.position.y.toFixed(2)}, ${p.position.z.toFixed(2)}` : '-';
  const vel = p.velocity ? `${p.velocity.x.toFixed(2)}, ${p.velocity.y.toFixed(2)}, ${p.velocity.z.toFixed(2)}` : '-';
  const w = p.weapon ? `${p.weapon.name} (slot ${p.weapon.slot}, mag ${p.weapon.magAmmo})` : 'none';
  return [
    `PlayerId      ${p.devPlayerId || a.devPlayerId}   entity ${p.entityId ?? a.entityId}`,
    `Connection    server: ${p.connected ? 'connected' : 'not connected'}${p.graced ? ' (graced)' : ''}   client: ${a.status} rtt ${a.rttMs} ms${a.disconnectReason ? '  ' + a.disconnectReason : ''}`,
    `Position      ${pos}   yaw ${p.yaw !== undefined ? p.yaw.toFixed(1) : '-'}`,
    `Velocity      ${vel}`,
    `Health/Shield ${p.health} / ${p.shield}   alive ${p.alive}`,
    `Weapon        ${w}   tool ${p.tool}`,
    `Ammo          ${p.ammo ? `light ${p.ammo.light} medium ${p.ammo.medium} heavy ${p.ammo.heavy}` : '-'}`,
    `Inventory     medkits ${p.medkits} shieldCells ${p.shieldCells}   weapons ${(p.weapons || []).map((x) => x.name).join(', ')}`,
    `Resources     ${p.resources ? `wood ${p.resources.wood} stone ${p.resources.stone} metal ${p.resources.metal}` : '-'}`,
    `Movement      ${p.mode}   grounded ${p.grounded}`,
    `Combat        kills ${p.kills} placement ${p.placement} damageDealt ${p.damageDealt}   hits confirmed (client) ${a.hitsLanded}`,
    '',
    'Server player DTO:', JSON.stringify(p, null, 2),
  ].join('\n');
}

function describeMatch(m) {
  const z = m.zone || {};
  return [
    `State     ${m.state}   round ${m.round}`,
    `Tick      ${m.tick}   elapsed ${m.elapsedSeconds !== undefined ? m.elapsedSeconds.toFixed(1) : '-'} s`,
    `Players   ${m.players} (connected ${m.connected}, graced ${m.graced}, participants ${m.participants})`,
    `Alive     ${m.alive}   winner ${m.winner ?? '-'}`,
    `Zone      phase ${z.phase}/${z.phaseCount}  center ${z.center ? z.center.x.toFixed(1) + ', ' + z.center.z.toFixed(1) : '-'}  radius ${z.radius !== undefined ? z.radius.toFixed(1) : '-'}  dps ${z.damagePerSecond}`,
    `World     build pieces ${m.buildPieces}   items ${m.worldItems}`,
  ].join('\n');
}

function describeServer(d) {
  const m = d.metrics || {};
  const h = d.health || {};
  const t = m.ticks || {};
  const qa = h.qa || {};
  return [
    `CPU       ${m.cpuPercent !== undefined ? m.cpuPercent.toFixed(1) : '-'} %`,
    `Memory    ${m.workingSetMB !== undefined ? m.workingSetMB.toFixed(1) : '-'} MB working set   managed ${fmt(m.managedMB)} MB   GC ${m.gc ? `${m.gc.gen0}/${m.gc.gen1}/${m.gc.gen2}` : '-'}   pause ${fmt(m.gcPauseMsTotal)} ms   allocated ${fmt(m.allocatedMBTotal)} MB`,
    `Tick ms   p50 ${fmt(m.tickP50Ms)}  p95 ${fmt(m.tickP95Ms)}  p99 ${fmt(m.tickP99Ms)}  max ${fmt(m.tickMaxMs)}   (window ${t.windowSeconds ?? '-'} s)`,
    `Packets/s in ${fmt(m.pktInPerSec)}  out ${fmt(m.pktOutPerSec)}   KB/s in ${m.bytesInPerSec !== undefined ? fmt(m.bytesInPerSec / 1024) : '-'}  out ${m.bytesOutPerSec !== undefined ? fmt(m.bytesOutPerSec / 1024) : '-'}`,
    `Sessions  ${m.activeSessions}   players ${m.players}   alive ${m.alive ?? '-'}   build pieces ${m.buildPieces ?? '-'}   stalls ${m.stalls ?? '-'}`,
    `QA queue  command ms ${fmt(m.qaCommandMs)}   rejected ${qa.rejected ?? '-'} timedOut ${qa.timedOut ?? '-'} failed ${qa.failed ?? '-'}`,
    `Health    ${m.health ? JSON.stringify(m.health) : '-'}`,
  ].join('\n');
}

function fmt(v) {
  return typeof v === 'number' ? v.toFixed(2) : '-';
}

async function loadReports() {
  const { data } = await api('GET', '/api/reports');
  const box = $('reports');
  box.replaceChildren();
  if (!data || !data.length) { box.append(el('p', { class: 'muted', text: 'No reports yet.' })); return; }
  const table = el('table', null, el('tr', null, el('th', { text: 'Run' }), el('th', { text: 'Scenario' }), el('th', { text: 'Result' }), el('th', { text: 'ms' })));
  for (const r of data) {
    table.append(el('tr', null,
      el('td', null, el('a', { href: r.url, target: '_blank', rel: 'noopener', text: r.runId })),
      el('td', { text: r.scenario || '' }), el('td', { text: r.status || '' }), el('td', { text: r.durationMs ?? '' })));
  }
  box.append(table);
}

// ---- tabs, stream, start ----

function bindTabs() {
  for (const button of document.querySelectorAll('.tabs button[data-tab]')) {
    button.addEventListener('click', () => {
      const group = button.parentElement.dataset.group;
      const tab = button.dataset.tab;
      state.tab[group] = tab;
      for (const b of button.parentElement.querySelectorAll('button[data-tab]')) b.classList.toggle('active', b === button);
      for (const body of document.querySelectorAll(`.tab-body[data-group="${group}"]`)) body.classList.toggle('hidden', body.dataset.tab !== tab);
      if (group === 'editor' && tab !== 'raw') setText($('raw').value, true);
      if (group === 'bottom' && tab === 'reports') loadReports();
      if (group === 'bottom') pollInspectors();
    });
  }
}

function connectStream() {
  const source = new EventSource('/api/stream');
  source.addEventListener('state', (e) => applyState(JSON.parse(e.data)));
  source.addEventListener('step', (e) => applyStep(JSON.parse(e.data)));
  source.addEventListener('log', (e) => appendLog(JSON.parse(e.data)));
  source.addEventListener('run', (e) => { const d = JSON.parse(e.data); if (d.phase === 'finished') loadReports(); });
  source.onerror = () => { $('run-status').textContent = 'disconnected (retrying)'; };
}

async function init() {
  bindTabs();
  buildLogFilters();
  const specs = await api('GET', '/api/actions');
  for (const s of specs.data || []) state.specs[s.name] = s;
  const select = $('add-action');
  for (const name of Object.keys(state.specs).sort()) select.append(el('option', { value: name, text: name }));
  const markers = await api('GET', '/api/markers');
  state.markers = markers.data || [];
  renderMarkerList();
  $('raw').addEventListener('input', () => { state.raw = $('raw').value; });
  $('btn-new').addEventListener('click', newScenario);
  $('btn-validate').addEventListener('click', () => { setText($('raw').value, true); validate(); });
  $('btn-save').addEventListener('click', () => { setText($('raw').value, true); save(); });
  $('btn-add-step').addEventListener('click', addStep);
  $('btn-run').addEventListener('click', () => startRun('run'));
  $('btn-run-from').addEventListener('click', () => startRun('from'));
  $('btn-run-until').addEventListener('click', () => startRun('until'));
  $('btn-step').addEventListener('click', () => (state.run && state.run.status === 'paused' ? command('step') : startRun('single')));
  $('btn-pause').addEventListener('click', () => command('pause'));
  $('btn-resume').addEventListener('click', () => command('resume'));
  $('btn-stop').addEventListener('click', () => command('stop'));
  $('btn-retry').addEventListener('click', () => {
    if (confirm('Run the failed step again in the same game? Its state may have changed since it failed.')) command('retry');
  });
  $('btn-retry-scenario').addEventListener('click', () => command('retry-scenario'));
  const answer = async (passed) => {
    const { ok, data } = await api('POST', '/api/run/manual', { passed, note: $('manual-note').value });
    if (!ok) message(data ? data.error : 'answer failed', 'error');
    else $('manual-note').value = '';
  };
  $('btn-manual-pass').addEventListener('click', () => answer(true));
  $('btn-manual-fail').addEventListener('click', () => answer(false));
  window.addEventListener('beforeunload', (e) => { if (state.raw && state.raw !== (state.savedText ?? '')) e.preventDefault(); });
  await loadTree();
  const run = await api('GET', '/api/run');
  // A page opened (or reloaded) during a run shows that run's scenario, so the timeline has its steps.
  if (run.data && run.data.path && !state.path) await openScenario(run.data.path);
  if (run.data) applyState(run.data);
  connectStream();
  setInterval(pollInspectors, INSPECT_MS);
}

init();
