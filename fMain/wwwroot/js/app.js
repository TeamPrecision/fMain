// ═══════════════════════════════════════════════════════════════════════════
//  fMain — PCBA Test System  |  Phase 4 client
// ═══════════════════════════════════════════════════════════════════════════

'use strict';

// ── State ─────────────────────────────────────────────────────────────────────
let conn = null;
let mySessionId = null;
let myMode = 'monitor';
let isAdmin = false;
let isSpecialIp = false;
let pendingRequestId = null;
let pendingApprovalId = null;
let currentPage = 'dashboard';
let numHeads = 1;
let serverConfig = null;
let clockInterval = null;
let allowRetest = false;
let autoStart = false;
let autoClearSn = false;
let snValidationRegex = '';
let prismMode = 'Debug';
let logoClickCount = 0;
let logoClickTimer = null;
let bypassMode = false;

const headStates = {};   // { [headNum]: HeadState }
let currentPlan = null;  // TestPlan currently loaded in editor
let planDirty = false;
let allModules = [];     // cache for function browser

// ── SignalR connection ─────────────────────────────────────────────────────────
function buildConnection() {
  return new signalR.HubConnectionBuilder()
    .withUrl('/hub/test', {
      transport: signalR.HttpTransportType.WebSockets
             | signalR.HttpTransportType.ServerSentEvents
             | signalR.HttpTransportType.LongPolling
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}

async function connectToServer() {
  const raw = document.getElementById('inputName').value.trim();

  if (!bypassMode) {
    if (!raw || raw.length !== 5 || !/^\d{5}$/.test(raw)) {
      showLoginError('Employee ID must be exactly 5 digits.'); return;
    }
    if (prismMode === 'Operation') {
      const res = await fetch(`/api/validate-employee?id=${encodeURIComponent(raw)}`);
      const data = await res.json();
      if (!data.valid) { showLoginError('PRISM: ' + data.message); return; }
      // UserLogin check via cUsers.UserLogin(emp_no)
      try {
        const ulRes = await fetch(`/api/user-login?id=${encodeURIComponent(raw)}`);
        const ulData = await ulRes.json();
        if (!ulData.ok || String(ulData.result) !== 'true') {
          showLoginError('PRISM: User not found in system'); return;
        }
      } catch (e) {
        showLoginError('PRISM: UserLogin error — ' + e.message); return;
      }
    }
  } else if (!raw) {
    showLoginError('Please enter a name.'); return;
  }

  const btn = document.getElementById('btnConnect');
  btn.disabled = true;
  btn.textContent = 'Connecting…';

  try {
    conn = buildConnection();
    registerHubHandlers();

    conn.onreconnecting(() => setConnDot('connecting'));
    conn.onreconnected(() => setConnDot('on'));
    conn.onclose(() => { setConnDot('off'); updateFooter('Disconnected', '', ''); });

    await conn.start();
    setConnDot('on');
    await conn.invoke('Register', raw, myMode);

    document.getElementById('loginModal').classList.add('hidden');
    document.getElementById('mainApp').classList.remove('hidden');
    document.getElementById('hdrName').textContent = raw;

    startClock();
    await loadSettings();
  } catch (err) {
    showLoginError('Connection failed: ' + err.message);
    btn.disabled = false;
    btn.textContent = 'Connect';
  }
}

// ── Hub event handlers ─────────────────────────────────────────────────────────
function registerHubHandlers() {

  conn.on('Registered', (info) => {
    mySessionId = info.id;
    isAdmin = info.isAdmin;
    isSpecialIp = info.isSpecialIp;
    serverConfig = info.config;
    allowRetest = info.config.allowRetest;
    autoStart   = info.config.autoStart;
    numHeads = info.config.numHeads;
    if (isAdmin) document.querySelectorAll('.admin-el').forEach(el => el.classList.remove('hidden'));
  });

  conn.on('ControlGranted', () => {
    myMode = 'control';
    setAccessBadge('control');
    document.getElementById('pendingModal').classList.add('hidden');
    updateFooter('In control', '', '');
    enableControls(true);
  });

  conn.on('ControlPending', (reqId) => {
    pendingRequestId = reqId;
    myMode = 'pending';
    setAccessBadge('pending');
    document.getElementById('pendingModal').classList.remove('hidden');
  });

  conn.on('ControlDenied', (reason) => {
    myMode = 'monitor';
    setAccessBadge('monitor');
    pendingRequestId = null;
    document.getElementById('pendingModal').classList.add('hidden');
    alert('Control access denied: ' + (reason || 'Server refused.'));
    enableControls(false);
  });

  conn.on('ControlReleased', (connId) => {
    if (connId === conn.connectionId) {
      myMode = 'monitor';
      setAccessBadge('monitor');
      enableControls(false);
    }
    document.getElementById('footController').textContent = '';
  });

  conn.on('ControlChanged', (connId, displayName, ip) => {
    document.getElementById('footController').textContent = `Controller: ${displayName} (${ip})`;
  });

  conn.on('SessionLeft', () => {
    if (currentPage === 'connections') refreshSessions();
  });

  conn.on('ControlRequested', (req) => {
    pendingApprovalId = req.requestId;
    document.getElementById('aprName').textContent = req.displayName;
    document.getElementById('aprIP').textContent = req.ipAddress;
    document.getElementById('aprTime').textContent = new Date().toLocaleTimeString();
    document.getElementById('approvalModal').classList.remove('hidden');
  });

  conn.on('SessionList', (data) => renderSessions(data));

  conn.on('HeadUpdate', (headNum, state) => {
    headStates[headNum] = state;
    renderHead(headNum, state);
  });

  conn.on('ModulesList', (modules) => {
    allModules = modules;
    renderModules(modules);
    if (currentPage === 'testplan') buildFunctionBrowser(modules);
  });

  conn.on('ModuleLoaded', () => {});

  conn.on('PlanLoaded', (plan) => {
    currentPlan = plan;
    planDirty = false;
    if (currentPage === 'testplan') renderPlanTable(plan);
    updateFooter(`Plan loaded: ${plan.name}`, '', '');
  });

  conn.on('PlanSaved', (filePath) => {
    planDirty = false;
    document.getElementById('tpDirty').classList.add('hidden');
    updateFooter(`Plan saved: ${filePath}`, '', '');
  });

  conn.on('Error', (msg) => {
    console.warn('Server error:', msg);
    updateFooter('⚠ ' + msg, '', '');
  });

  conn.on('DatalogSaved', (logId, headNum, sn, result) => {
    updateFooter(`Datalog #${logId} saved — H${headNum} SN=${sn||'—'} ${result}`, '', '');
  });

  conn.on('WorkOrderUpdated', (head, wo, info) => {
    if (head === -1) {
      // All heads
      document.querySelectorAll('.head-wo-badge').forEach(el => el.textContent = wo ? `WO: ${wo}` : '');
    } else {
      const card = document.querySelector(`.head-card[data-head="${head}"]`);
      if (card) {
        const badge = card.querySelector('.head-wo-badge');
        if (badge) badge.textContent = wo ? `WO: ${wo}` : '';
      }
    }
  });

  conn.on('WorkOrderInfo', (head, wo, info) => {
    // Optional: update WO display for one head
  });
}

// ── Login UI ───────────────────────────────────────────────────────────────────
function selectMode(mode) {
  myMode = mode;
  document.querySelectorAll('.mode-card').forEach(c =>
    c.classList.toggle('active', c.dataset.mode === mode));
}

function showLoginError(msg) {
  const el = document.getElementById('loginError');
  el.textContent = msg;
  el.classList.remove('hidden');
}

function logoClick() {
  logoClickCount++;
  if (logoClickTimer) clearTimeout(logoClickTimer);
  logoClickTimer = setTimeout(() => { logoClickCount = 0; }, 800);
  if (logoClickCount >= 5) {
    logoClickCount = 0;
    document.getElementById('bypassWrap').classList.remove('hidden');
    document.getElementById('bypassPwd').focus();
  }
}

function tryBypass() {
  const pwd = document.getElementById('bypassPwd').value;
  if (pwd === 'TeamDesign@1234') {
    bypassMode = true;
    document.getElementById('bypassWrap').classList.add('hidden');
    document.getElementById('bypassPwd').value = '';
    document.getElementById('inputName').placeholder = 'Enter display name…';
    document.getElementById('inputName').maxLength = 32;
    document.getElementById('inputName').removeAttribute('oninput');
    showLoginError('Bypass mode: enter any display name.');
  } else {
    document.getElementById('bypassPwd').value = '';
    showLoginError('Incorrect bypass password.');
  }
}

// ── Access control ─────────────────────────────────────────────────────────────
async function cancelControl() {
  document.getElementById('pendingModal').classList.add('hidden');
  myMode = 'monitor';
  setAccessBadge('monitor');
  if (conn) await conn.invoke('ReleaseControl');
}

async function releaseControl() {
  const anyRunning = Object.values(headStates).some(s =>
    (s.status || '').toLowerCase() === 'testing');
  if (anyRunning) {
    if (!confirm('A test is currently running on one or more heads.\nRelease and return to login anyway?')) return;
  }

  if (conn) {
    try { await conn.invoke('ReleaseControl'); } catch { /* ignore */ }
    try { await conn.stop(); } catch { /* ignore */ }
    conn = null;
  }

  // Reset client state
  myMode = 'monitor';
  mySessionId = null;
  isAdmin = false;
  bypassMode = false;
  logoClickCount = 0;
  if (clockInterval) { clearInterval(clockInterval); clockInterval = null; }

  // Return to login
  document.getElementById('mainApp').classList.add('hidden');
  document.getElementById('loginModal').classList.remove('hidden');
  document.getElementById('inputName').value = '';
  const errEl = document.getElementById('loginError');
  errEl.textContent = '';
  errEl.classList.add('hidden');
  document.getElementById('bypassWrap').classList.add('hidden');
  setConnDot('off');
  updateFooter('Disconnected', '', '');

  // Re-apply login state for localhost
  const isLocalhost = ['localhost','127.0.0.1','::1'].includes(window.location.hostname);
  if (isLocalhost) {
    myMode = 'control';
    selectMode('control');
  }
  document.getElementById('inputName').focus();
}

async function approveControl() {
  if (!pendingApprovalId || !conn) return;
  await conn.invoke('ApproveControl', pendingApprovalId);
  document.getElementById('approvalModal').classList.add('hidden');
  pendingApprovalId = null;
}

async function denyControl() {
  if (!pendingApprovalId || !conn) return;
  await conn.invoke('DenyControl', pendingApprovalId);
  document.getElementById('approvalModal').classList.add('hidden');
  pendingApprovalId = null;
}

// ── Heads grid ─────────────────────────────────────────────────────────────────
function buildHeadsGrid(count) {
  const grid = document.getElementById('headsGrid');
  grid.innerHTML = '';
  for (let i = 1; i <= count; i++) grid.appendChild(createHeadCard(i));
}

function createHeadCard(num) {
  const tmpl = document.getElementById('headTemplate');
  const clone = tmpl.content.cloneNode(true);
  const card = clone.querySelector('.head-card');
  card.dataset.head = num;
  card.querySelector('.head-num').textContent = 'HEAD ' + num;
  ['sn-input','btn-test','btn-stop','btn-retest','btn-clear'].forEach(cls => {
    const el = card.querySelector('.' + cls);
    if (el) el.dataset.head = num;
  });
  return card;
}

function renderHead(headNum, state) {
  const card = document.querySelector(`.head-card[data-head="${headNum}"]`);
  if (!card) return;

  const s = state.status.toLowerCase();
  card.querySelector('.head-status').className = 'head-status ' + s;
  card.querySelector('.head-status').textContent = state.status.toUpperCase();
  card.className = 'head-card status-' + s;

  const snInput = card.querySelector('.sn-input');
  if (document.activeElement !== snInput) snInput.value = state.serialNumber || '';

  // WO badge
  const woBadge = card.querySelector('.head-wo-badge');
  if (woBadge) woBadge.textContent = state.workOrder ? `WO: ${state.workOrder}` : '';

  const pbar = card.querySelector('.progress-bar');
  const ppct = card.querySelector('.progress-pct');
  pbar.style.width = (state.progress || 0) + '%';
  pbar.className = 'progress-bar' + (s === 'testing' ? ' bar-running' : s === 'pass' ? ' bar-pass' : s === 'fail' ? ' bar-fail' : '');
  ppct.textContent = Math.round(state.progress || 0) + '%';
  card.querySelector('.test-time').textContent = state.testTime || '00:00';
  card.querySelector('.in-out-time').textContent = 'IN: ' + (state.inOutTime || '--:--');

  // Button visibility by status
  const isRunning = s === 'testing';
  const isDone = s === 'pass' || s === 'fail';
  card.querySelector('.btn-test').style.display  = isRunning ? 'none' : '';
  card.querySelector('.btn-stop').style.display  = isRunning ? '' : 'none';
  card.querySelector('.btn-retest').style.display = (isDone && allowRetest) ? '' : 'none';

  // Render steps table (6 columns: #, Detail, Limit, Measure, Result, Time)
  const tbody = card.querySelector('tbody');
  tbody.innerHTML = '';
  let stepNum = 0;
  (state.steps || []).forEach((step, i) => {
    const tr = document.createElement('tr');
    const rt = (step.rowType || 'normal').toLowerCase();

    if (rt === 'header') {
      tr.className = 'step-header-row';
      tr.innerHTML = `<td colspan="6" class="step-header-cell">${esc(step.step)}</td>`;
      tbody.appendChild(tr);
      return;
    }

    if (rt !== 'skip') stepNum++;

    const isCurrent = i === state.currentStepIndex;
    tr.className = 'step-data-row' + (isCurrent ? ' step-current' : '') +
                   (rt === 'skip' ? ' step-skip-row' : '') +
                   (rt === 'serial' ? ' step-serial-row' : '');

    const r = step.result || '';
    const rc = r === 'PASS' ? 'result-pass' :
               r === 'FAIL' ? 'result-fail' :
               r === 'RUNNING' ? 'result-running' :
               r === 'SKIP' ? 'result-skip' : '';

    const timeStr = (step.durationMs > 0) ? step.durationMs + ' ms' : '';

    tr.innerHTML = `<td class="step-num-cell">${rt === 'skip' ? '—' : stepNum}</td>` +
                   `<td class="step-detail-cell" title="${esc(step.function)}">${esc(step.step)}</td>` +
                   `<td>${esc(step.limit)}</td>` +
                   `<td class="mono">${esc(step.measure)}</td>` +
                   `<td class="result-cell ${rc}">${esc(r)}</td>` +
                   `<td class="step-time-cell">${timeStr}</td>`;
    tbody.appendChild(tr);
  });

  if (isCurrent(state)) card.querySelector('.head-table-wrap').scrollTop = 9999;
}

function isCurrent(state) {
  return state.currentStepIndex >= 0;
}

// ── Head controls ──────────────────────────────────────────────────────────────
async function startHead(btn) {
  if (!canControl()) return noControl();
  const head = parseInt(btn.dataset.head, 10);
  if (conn) await conn.invoke('StartHead', head);
}

async function stopHead(btn) {
  if (!canControl()) return noControl();
  const head = parseInt(btn.dataset.head, 10);
  if (conn) await conn.invoke('StopHead', head);
}

async function retestHead(btn) {
  if (!canControl()) return noControl();
  const head = parseInt(btn.dataset.head, 10);
  if (conn) await conn.invoke('RetestHead', head, 0);
}

async function clearHead(btn) {
  if (!canControl()) return noControl();
  const head = parseInt(btn.dataset.head, 10);
  if (conn) await conn.invoke('ClearHead', head);
}

function clearSN(btn) {
  if (!canControl()) return noControl();
  const card = btn.closest('.head-card');
  const head = parseInt(card.dataset.head, 10);
  card.querySelector('.sn-input').value = '';
  if (conn) conn.invoke('SetSerialNumber', head, '');
}

function onSNInput(input) {
  if (!canControl()) return;
  const head = parseInt(input.dataset.head, 10);
  const sn = input.value;
  if (conn) conn.invoke('SetSerialNumber', head, sn);
  // Validate regex if configured
  validateSNField(input, sn);
}

function onSNKeyDown(event, input) {
  if (event.key !== 'Enter') return;
  if (!canControl()) return;
  const head = parseInt(input.dataset.head, 10);
  const sn = input.value.trim();
  if (!sn) return;
  // Validate before auto-start
  if (snValidationRegex) {
    try {
      if (!new RegExp(snValidationRegex).test(sn)) {
        updateFooter(`⚠ SN format invalid for H${head}`, '', '');
        return;
      }
    } catch { /* bad regex — ignore */ }
  }
  if (autoStart && headStates[head]?.status?.toLowerCase() !== 'testing') {
    if (conn) conn.invoke('StartHead', head);
  }
}

function validateSNField(input, sn) {
  const icon = input.closest('.head-sn-row')?.querySelector('.sn-valid-icon');
  if (!icon) return;
  if (!sn || !snValidationRegex) { icon.textContent = ''; return; }
  try {
    const ok = new RegExp(snValidationRegex).test(sn);
    icon.textContent = ok ? '✓' : '✗';
    icon.style.color = ok ? 'var(--green)' : 'var(--red)';
    icon.title = ok ? 'SN format OK' : 'SN format invalid';
  } catch { icon.textContent = ''; }
}

async function startAll() {
  if (!canControl()) return noControl();
  if (conn) await conn.invoke('StartAll');
}

async function stopAll() {
  if (!canControl()) return noControl();
  if (conn) await conn.invoke('StopAll');
}

function clearAll() {
  if (!canControl()) return noControl();
  document.querySelectorAll('.btn-clear').forEach(btn => clearHead(btn));
}

function applyHeadCountFromSettings() {
  const n = parseInt(getV('s-numHeads'), 10);
  if (isNaN(n) || n < 1 || n > 36) return;
  numHeads = n;
  buildHeadsGrid(n);
}

function onWOChange() {
  // Debounced — applied by pressing Enter or "Set WO" button
}

async function applyWO() {
  if (!canControl()) return noControl();
  const wo = document.getElementById('inputWO').value.trim();
  if (conn) await conn.invoke('SetWorkOrderAll', wo);
  updateFooter(`Work order set: ${wo||'(cleared)'}`, '', '');
}

// ── Test Plan Editor ───────────────────────────────────────────────────────────

function tpNewPlan() {
  if (planDirty && !confirm('Discard unsaved changes?')) return;
  currentPlan = { name: 'New Plan', version: '1.0', filePath: '', sameStepMode: false, defaultTimeoutMs: 30000, steps: [] };
  planDirty = false;
  document.getElementById('tpName').value = currentPlan.name;
  document.getElementById('tpFilePath').value = '';
  document.getElementById('tpDirty').classList.add('hidden');
  renderPlanTable(currentPlan);
}

async function tpLoadFile() {
  if (!canControl()) return noControl();
  const path = document.getElementById('tpFilePath').value.trim();
  if (!path) { alert('Enter a file path.'); return; }
  if (conn) await conn.invoke('LoadPlanFromFile', path);
}

async function tpSaveFile() {
  if (!canControl()) return noControl();
  const path = document.getElementById('tpFilePath').value.trim();
  if (!path) { alert('Enter a file path to save to.'); return; }
  const plan = getPlanFromEditor();
  if (conn) await conn.invoke('SavePlanToFile', plan, path);
}

function markPlanDirty() {
  planDirty = true;
  document.getElementById('tpDirty').classList.remove('hidden');
}

function getPlanFromEditor() {
  const steps = [];
  document.querySelectorAll('#tpBody .tp-row').forEach(tr => {
    steps.push({
      id: tr.dataset.id || '',
      rowType: tr.querySelector('.tp-type').value,
      stepNum: 0,
      description: tr.querySelector('.tp-desc').value,
      function:    tr.querySelector('.tp-fn').value,
      param1:      tr.querySelector('.tp-p1').value,
      param2:      tr.querySelector('.tp-p2').value,
      param3:      tr.querySelector('.tp-p3').value,
      param4:      tr.querySelector('.tp-p4').value,
      min:         tr.querySelector('.tp-min').value,
      max:         tr.querySelector('.tp-max').value,
      unit:        tr.querySelector('.tp-unit').value,
      failBehavior: tr.querySelector('.tp-fail').value,
      timeoutMs:   parseInt(tr.querySelector('.tp-tmo').value, 10) || 30000
    });
  });
  return {
    name: document.getElementById('tpName').value || 'Untitled',
    version: '1.0',
    filePath: document.getElementById('tpFilePath').value,
    sameStepMode: document.getElementById('tpSameStep').checked,
    defaultTimeoutMs: 30000,
    steps
  };
}

function renderPlanTable(plan) {
  if (!plan) return;
  document.getElementById('tpName').value = plan.name || '';
  document.getElementById('tpFilePath').value = plan.filePath || '';
  document.getElementById('tpSameStep').checked = plan.sameStepMode || false;
  document.getElementById('tpDirty').classList.toggle('hidden', !planDirty);

  const tbody = document.getElementById('tpBody');
  tbody.innerHTML = '';
  (plan.steps || []).forEach(step => tbody.appendChild(createStepRow(step)));
  tpRenumber();
  buildOverrideRows();
}

function createStepRow(step) {
  const tr = document.createElement('tr');
  tr.className = 'tp-row tp-row-' + (step.rowType || 'Normal').toLowerCase();
  tr.dataset.id = step.id || '';
  tr.draggable = true;
  tr.addEventListener('dragstart', rowDragStart);
  tr.addEventListener('dragover', rowDragOver);
  tr.addEventListener('drop', rowDrop);
  tr.addEventListener('dragend', rowDragEnd);

  const isHdr = step.rowType === 'Header';
  tr.innerHTML = `
    <td><input type="checkbox" class="tp-sel"></td>
    <td><select class="tp-type" onchange="tpTypeChange(this)">
      <option${step.rowType==='Normal'?' selected':''}>Normal</option>
      <option${step.rowType==='Header'?' selected':''}>Header</option>
      <option${step.rowType==='Skip'  ?' selected':''}>Skip</option>
      <option${step.rowType==='Serial'?' selected':''}>Serial</option>
    </select></td>
    <td class="tp-num-cell">${isHdr?'':step.stepNum||''}</td>
    <td><input type="text" class="tp-desc tp-input" value="${esc(step.description||'')}" oninput="markPlanDirty()"></td>
    <td><input type="text" class="tp-fn tp-input tp-fn-cell" value="${esc(step.function||'')}"
         placeholder="drag or type…" oninput="markPlanDirty()"
         ondragover="event.preventDefault()" ondrop="fnDrop(event, this)"></td>
    <td><input type="text" class="tp-p1 tp-input tp-p" value="${esc(step.param1||'')}"></td>
    <td><input type="text" class="tp-p2 tp-input tp-p" value="${esc(step.param2||'')}"></td>
    <td><input type="text" class="tp-p3 tp-input tp-p" value="${esc(step.param3||'')}"></td>
    <td><input type="text" class="tp-p4 tp-input tp-p" value="${esc(step.param4||'')}"></td>
    <td><input type="text" class="tp-min tp-input tp-mm" value="${esc(step.min||'')}"></td>
    <td><input type="text" class="tp-max tp-input tp-mm" value="${esc(step.max||'')}"></td>
    <td><input type="text" class="tp-unit tp-input tp-unit-cell" value="${esc(step.unit||'')}"></td>
    <td><select class="tp-fail">
      <option${step.failBehavior==='Stop'||!step.failBehavior?' selected':''}>Stop</option>
      <option value="ContinueCells"${step.failBehavior==='ContinueCells'?' selected':''}>Cont.Cells</option>
      <option value="ContinueAll"${step.failBehavior==='ContinueAll'?' selected':''}>Cont.All</option>
    </select></td>
    <td><input type="number" class="tp-tmo tp-input" value="${step.timeoutMs||30000}" min="100" step="1000"></td>
    <td><button class="btn-icon tp-del" onclick="tpDeleteRow(this)" title="Delete row">✕</button></td>`;

  tpApplyRowStyle(tr, step.rowType || 'Normal');
  return tr;
}

function tpTypeChange(sel) {
  const tr = sel.closest('.tp-row');
  tr.className = 'tp-row tp-row-' + sel.value.toLowerCase();
  tpApplyRowStyle(tr, sel.value);
  tpRenumber();
  markPlanDirty();
}

function tpApplyRowStyle(tr, rowType) {
  const isHdr = rowType === 'Header';
  const isSkip = rowType === 'Skip';
  const disableCols = '.tp-fn,.tp-p1,.tp-p2,.tp-p3,.tp-p4,.tp-min,.tp-max,.tp-unit-cell,.tp-fail,.tp-tmo';
  tr.querySelectorAll(disableCols).forEach(el => {
    el.disabled = isHdr;
    el.style.opacity = (isHdr || isSkip) ? '0.4' : '';
  });
}

function tpRenumber() {
  let n = 1;
  document.querySelectorAll('#tpBody .tp-row').forEach(tr => {
    const isHdr = tr.querySelector('.tp-type').value === 'Header';
    const cell = tr.querySelector('.tp-num-cell');
    cell.textContent = isHdr ? '' : n++;
  });
}

function tpAddStep(rowType) {
  const tbody = document.getElementById('tpBody');
  const step = { id: Math.random().toString(36).slice(2,10), rowType, stepNum: 0,
    description: '', function: '', param1: '', param2: '', param3: '', param4: '',
    min: '', max: '', unit: '', failBehavior: 'Stop', timeoutMs: 30000 };
  tbody.appendChild(createStepRow(step));
  tpRenumber();
  markPlanDirty();
  tbody.lastElementChild.querySelector('.tp-desc').focus();
}

function tpDeleteRow(btn) {
  btn.closest('.tp-row').remove();
  tpRenumber();
  markPlanDirty();
}

function tpDeleteSelected() {
  document.querySelectorAll('#tpBody .tp-sel:checked').forEach(cb => cb.closest('.tp-row').remove());
  tpRenumber();
  markPlanDirty();
}

function tpSelectAll(checked) {
  document.querySelectorAll('#tpBody .tp-sel').forEach(cb => cb.checked = checked);
}

// ── Row drag-and-drop (reorder rows) ──────────────────────────────────────────
let dragSrcRow = null;

function rowDragStart(e) {
  dragSrcRow = this;
  e.dataTransfer.effectAllowed = 'move';
  this.classList.add('tp-dragging');
}

function rowDragOver(e) {
  e.preventDefault();
  e.dataTransfer.dropEffect = 'move';
  this.classList.add('tp-drag-over');
}

function rowDrop(e) {
  e.preventDefault();
  if (dragSrcRow && dragSrcRow !== this) {
    const tbody = document.getElementById('tpBody');
    const rows = [...tbody.querySelectorAll('.tp-row')];
    const srcIdx = rows.indexOf(dragSrcRow);
    const dstIdx = rows.indexOf(this);
    if (srcIdx < dstIdx) tbody.insertBefore(dragSrcRow, this.nextSibling);
    else tbody.insertBefore(dragSrcRow, this);
    tpRenumber();
    markPlanDirty();
  }
  this.classList.remove('tp-drag-over');
}

function rowDragEnd() {
  this.classList.remove('tp-dragging');
  document.querySelectorAll('.tp-drag-over').forEach(el => el.classList.remove('tp-drag-over'));
}

// ── Function browser ──────────────────────────────────────────────────────────
function buildFunctionBrowser(modules) {
  const fnList = document.getElementById('fnList');
  const categories = {};

  modules.filter(m => m.isLoaded).forEach(m => {
    (m.functions || []).forEach(f => {
      const cat = f.category || 'General';
      if (!categories[cat]) categories[cat] = [];
      categories[cat].push({ ...f, moduleName: m.name });
    });
  });

  fnList.innerHTML = '';
  if (Object.keys(categories).length === 0) {
    fnList.innerHTML = '<div class="empty-state" style="padding:20px;font-size:12px">No functions found.<br>Load modules first.</div>';
    return;
  }

  Object.keys(categories).sort().forEach(cat => {
    const catDiv = document.createElement('div');
    catDiv.className = 'fn-category';

    const hdr = document.createElement('div');
    hdr.className = 'fn-cat-hdr';
    hdr.textContent = cat;
    hdr.onclick = () => items.classList.toggle('hidden');
    catDiv.appendChild(hdr);

    const items = document.createElement('div');
    items.className = 'fn-items';
    categories[cat].forEach(f => {
      const item = document.createElement('div');
      item.className = 'fn-item';
      item.draggable = true;
      item.dataset.fn = JSON.stringify(f);
      item.title = f.description || '';
      item.innerHTML = `<span class="fn-item-name">${esc(f.name)}</span>` +
                       `<span class="fn-item-desc">${esc(f.description || '')}</span>`;
      item.addEventListener('dragstart', e => {
        e.dataTransfer.setData('fndata', item.dataset.fn);
        e.dataTransfer.effectAllowed = 'copy';
      });
      items.appendChild(item);
    });
    catDiv.appendChild(items);
    fnList.appendChild(catDiv);
  });
}

function filterFunctions(q) {
  const lq = q.toLowerCase();
  document.querySelectorAll('.fn-item').forEach(item => {
    const f = JSON.parse(item.dataset.fn || '{}');
    const match = !lq || f.name.toLowerCase().includes(lq) || (f.description||'').toLowerCase().includes(lq);
    item.style.display = match ? '' : 'none';
  });
}

function fnDrop(event, input) {
  event.preventDefault();
  const raw = event.dataTransfer.getData('fndata');
  if (!raw) return;
  const f = JSON.parse(raw);
  input.value = f.name;
  // Auto-fill params with parameter name hints
  const tr = input.closest('.tp-row');
  const params = f.parameters || [];
  ['tp-p1','tp-p2','tp-p3','tp-p4'].forEach((cls, i) => {
    const el = tr.querySelector('.' + cls);
    if (el && params[i]) el.placeholder = params[i].name + (params[i].defaultValue != null ? '='+params[i].defaultValue : '');
  });
  markPlanDirty();
}

// ── Per-head overrides ─────────────────────────────────────────────────────────
function buildOverrideRows() {
  const container = document.getElementById('tpOverrideRows');
  container.innerHTML = '';
  for (let h = 1; h <= numHeads; h++) {
    const row = document.createElement('div');
    row.className = 'tp-override-row';
    row.innerHTML = `<label>Head ${h}</label>` +
      `<input type="text" id="override-${h}" class="tb-input" style="flex:1" placeholder="[Shared plan]">` +
      `<button class="btn btn-sm" onclick="setHeadOverride(${h})">Set</button>` +
      `<button class="btn btn-sm btn-secondary" onclick="clearHeadOverride(${h})">Clear</button>`;
    container.appendChild(row);
  }
  // Load current overrides
  fetch('/api/plan/overrides').then(r => r.json()).then(overrides => {
    Object.entries(overrides).forEach(([h, path]) => {
      const el = document.getElementById('override-' + h);
      if (el) el.value = path;
    });
  }).catch(() => {});
}

async function setHeadOverride(headNum) {
  const path = document.getElementById('override-' + headNum)?.value.trim() || '';
  await fetch('/api/plan/override', { method: 'POST',
    headers: {'Content-Type':'application/json'},
    body: JSON.stringify({ headNum, filePath: path || null }) });
}

async function clearHeadOverride(headNum) {
  const el = document.getElementById('override-' + headNum);
  if (el) el.value = '';
  await fetch('/api/plan/override', { method: 'POST',
    headers: {'Content-Type':'application/json'},
    body: JSON.stringify({ headNum, filePath: null }) });
}

// ── Modules page ───────────────────────────────────────────────────────────────
async function loadModulePage() {
  if (conn) await conn.invoke('GetModules');
}

async function loadModuleFile() {
  const path = document.getElementById('moduleFilePath').value.trim();
  if (!path) return;
  if (conn) await conn.invoke('LoadModuleFile', path);
}

async function reloadAllModules() {
  if (conn) await conn.invoke('ReloadModules');
}

function renderModules(modules) {
  const grid = document.getElementById('modulesGrid');
  if (!modules || !modules.length) {
    grid.innerHTML = '<div class="empty-state">No modules loaded yet.<br>Add .cs files to <code>D:/svn/ProjectX/Module/</code></div>';
    return;
  }
  grid.innerHTML = '';
  modules.forEach(m => {
    const card = document.createElement('div');
    card.className = 'module-card ' + (m.isLoaded ? 'loaded' : 'error');
    card.innerHTML = `
      <div class="mc-header">
        <span class="mc-name">${esc(m.name)}</span>
        <span class="mc-version">v${esc(m.version)}</span>
      </div>
      <div class="mc-cat">${esc(m.category)}</div>
      <div class="mc-desc">${esc(m.description||'No description')}</div>
      <div class="mc-path">${esc(m.sourcePath)}</div>
      ${m.loadError?`<div class="mc-error">⚠ ${esc(m.loadError)}</div>`:''}
      <div class="mc-funcs">
        ${(m.functions||[]).map(f=>`<span class="func-chip" title="${esc(f.description)}">${esc(f.name)}</span>`).join('')}
      </div>`;
    grid.appendChild(card);
  });
}

// ── Settings page ──────────────────────────────────────────────────────────────
async function loadSettings() {
  try {
    const res = await fetch('/api/config');
    const cfg = await res.json();
    serverConfig = cfg;

    setV('s-numHeads', cfg.tester?.numHeads??1);
    setV('s-failBehavior', cfg.tester?.failBehavior??'Stop');
    setChk('s-allowRetest', cfg.tester?.allowRetest??false);
    setChk('s-autoStart', cfg.tester?.autoStart??false);
    setChk('s-autoClearSn', cfg.tester?.autoClearSnAfterTest??false);
    setV('s-snRegex', cfg.tester?.snValidationRegex??'');
    setChk('s-useRelay', cfg.tester?.useRelayCard??false);
    setV('s-numCards', cfg.tester?.numCardRelay??1);
    setV('s-port', cfg.port??5000);
    setV('s-specialIPs', (cfg.specialIPs||[]).join(', '));
    setV('s-modulesPath', cfg.modulesBasePath??'D:/svn');
    setV('s-dbConn', cfg.mySql?.connectionString??'');
    setV('s-prismMode', cfg.prism?.mode??'Debug');
    setV('s-prismDll', cfg.prism?.dllPath??'');
    setV('s-prismProcess', cfg.prism?.processName??'FCT');
    setV('s-prismComputer', cfg.prism?.computerName??'');
    setV('s-prismStation', cfg.prism?.stationName??'');
    setV('s-prismSnDigits', cfg.prism?.snDigits??0);
    setV('s-barcodeMode', cfg.ui?.barcodeMode??'Scanner');
    setV('s-headMinWidth', cfg.ui?.headMinWidth??260);
    setV('s-pyVersion', cfg.script?.pyVersion??'Python313');
    setV('s-pyFolder', cfg.script?.pyFolder??'');

    setV('s-fgPlansFolder', cfg.tester?.fgPlansFolder??'');

    numHeads = cfg.tester?.numHeads??1;
    allowRetest = cfg.tester?.allowRetest??false;
    autoStart   = cfg.tester?.autoStart??false;
    autoClearSn = cfg.tester?.autoClearSnAfterTest??false;
    snValidationRegex = cfg.tester?.snValidationRegex??'';
    prismMode   = cfg.prism?.mode??'Debug';
    buildHeadsGrid(numHeads);
    applyModeClass();
    updateModeBadge();
    updateFgGroupVisibility();
    applyHeadMinWidth(cfg.ui?.headMinWidth ?? 260);
    if (prismMode === 'Debug') loadFgPlans();
  } catch (e) { console.warn('loadSettings failed:', e); }
}

function applyModeClass() {
  document.body.classList.toggle('mode-debug', prismMode !== 'Operation');
  document.body.classList.toggle('mode-operation', prismMode === 'Operation');
}

function applyHeadMinWidth(px) {
  document.documentElement.style.setProperty('--head-min-width', px + 'px');
}

function updateModeBadge() {
  const badge = document.getElementById('modeBadge');
  if (!badge) return;
  badge.textContent = prismMode === 'Operation' ? 'OPERATION' : 'DEBUG';
  badge.className = 'mode-badge ' + (prismMode === 'Operation' ? 'operation' : 'debug');
}

function updateFgGroupVisibility() {
  const fg = document.getElementById('fgGroup');
  if (fg) fg.classList.toggle('hidden', prismMode === 'Operation');
}

async function loadFgPlans() {
  try {
    const res = await fetch('/api/fg-plans');
    const plans = await res.json();
    const sel = document.getElementById('fgSelect');
    if (!sel) return;
    const cur = sel.value;
    sel.innerHTML = '<option value="">— Select Plan —</option>';
    plans.forEach(p => {
      const opt = document.createElement('option');
      opt.value = p.path;
      opt.textContent = p.name;
      if (p.path === cur) opt.selected = true;
      sel.appendChild(opt);
    });
  } catch (e) { console.warn('loadFgPlans failed:', e); }
}

async function applyFgPlan(path) {
  if (!path || !conn) return;
  if (!canControl()) { noControl(); return; }
  await conn.invoke('LoadPlanFromFile', path);
}

async function saveSettings() {
  const cfg = {
    port: parseInt(getV('s-port'),10)||5000,
    specialIPs: getV('s-specialIPs').split(',').map(s=>s.trim()).filter(Boolean),
    modulesBasePath: getV('s-modulesPath'),
    tester: {
      numHeads: parseInt(getV('s-numHeads'),10)||1,
      failBehavior: getV('s-failBehavior'),
      allowRetest: getChk('s-allowRetest'),
      autoStart: getChk('s-autoStart'),
      autoClearSnAfterTest: getChk('s-autoClearSn'),
      snValidationRegex: getV('s-snRegex'),
      useRelayCard: getChk('s-useRelay'),
      numCardRelay: parseInt(getV('s-numCards'),10)||1,
      fgPlansFolder: getV('s-fgPlansFolder')
    },
    mySql: { connectionString: getV('s-dbConn') },
    prism: {
      mode: getV('s-prismMode'),
      dllPath: getV('s-prismDll'),
      processName: getV('s-prismProcess')||'FCT',
      computerName: getV('s-prismComputer'),
      stationName: getV('s-prismStation'),
      snDigits: parseInt(getV('s-prismSnDigits'),10)||0
    },
    script: { pyVersion: getV('s-pyVersion'), pyFolder: getV('s-pyFolder') },
    ui: { barcodeMode: getV('s-barcodeMode'), headMinWidth: parseInt(getV('s-headMinWidth'),10)||260 }
  };
  try {
    await fetch('/api/config', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(cfg) });
    const ss = document.getElementById('saveStatus');
    ss.textContent = '✓ Saved';
    setTimeout(() => ss.textContent = '', 3000);
    numHeads = cfg.tester.numHeads;
    allowRetest = cfg.tester.allowRetest;
    autoStart   = cfg.tester.autoStart;
    autoClearSn = cfg.tester.autoClearSnAfterTest;
    snValidationRegex = cfg.tester.snValidationRegex || '';
    prismMode   = cfg.prism?.mode ?? 'Debug';
    buildHeadsGrid(numHeads);
    applyModeClass();
    updateModeBadge();
    updateFgGroupVisibility();
    applyHeadMinWidth(cfg.ui.headMinWidth);
    if (prismMode === 'Debug') loadFgPlans();
  } catch (e) { alert('Save failed: '+e.message); }
}

async function testDB() {
  document.getElementById('dbTestResult').textContent = '⏳ Testing…';
  try {
    const res = await fetch('/api/db/test');
    const data = await res.json();
    document.getElementById('dbTestResult').textContent = data.connected ? '✓ MySQL Connected' : '✗ ' + data.message;
    document.getElementById('dbTestResult').style.color = data.connected ? 'var(--green)' : 'var(--red)';
  } catch (e) {
    document.getElementById('dbTestResult').textContent = '✗ ' + e.message;
    document.getElementById('dbTestResult').style.color = 'var(--red)';
  }
}

function showStab(name) {
  document.querySelectorAll('.stab').forEach((b,i) => {
    const pane = document.querySelectorAll('.stab-pane')[i];
    const isThis = b.onclick.toString().includes(`'${name}'`);
    b.classList.toggle('active', isThis);
    if (pane) { pane.classList.toggle('active', isThis); pane.classList.toggle('hidden', !isThis); }
  });
}

// ── Connections page ───────────────────────────────────────────────────────────
async function refreshSessions() {
  if (conn) await conn.invoke('GetSessions');
}

function renderSessions(data) {
  const list = document.getElementById('sessionsList');
  const pend = document.getElementById('pendingList');
  const sessions = data.sessions||[];
  const pendings = data.pendingRequests||[];

  list.innerHTML = sessions.length===0 ? '<div class="empty-state">No active sessions</div>' :
    `<div class="session-row header"><span>Name</span><span>IP</span><span>State</span><span>Connected</span><span></span></div>` +
    sessions.map(s=>`<div class="session-row">
      <span>${esc(s.displayName)}${s.isAdmin?' 🔧':''}${s.isSpecialIp?' ⚡':''}</span>
      <span style="font-family:var(--mono);font-size:12px">${esc(s.ipAddress)}</span>
      <span class="sr-state ${s.state.toLowerCase()}">${s.state}</span>
      <span style="font-size:12px;color:var(--text-dim)">${fmtTime(s.connectedAt)}</span>
      <span></span></div>`).join('');

  pend.innerHTML = pendings.length===0 ? '<div class="empty-state">No pending requests</div>' :
    pendings.map(r=>`<div class="session-row">
      <span>${esc(r.displayName)}</span>
      <span style="font-family:var(--mono);font-size:12px">${esc(r.ipAddress)}</span>
      <span class="sr-state pending">Pending</span>
      <span style="font-size:12px;color:var(--text-dim)">${fmtTime(r.requestedAt)}</span>
      <span>
        <button class="btn btn-sm btn-success" onclick="adminApprove('${r.requestId}')">Approve</button>
        <button class="btn btn-sm btn-danger" onclick="adminDeny('${r.requestId}')" style="margin-left:4px">Deny</button>
      </span></div>`).join('');
}

async function adminApprove(id) { if (conn) await conn.invoke('ApproveControl', id); }
async function adminDeny(id)    { if (conn) await conn.invoke('DenyControl', id); }

// ── Page navigation ────────────────────────────────────────────────────────────
function showPage(name) {
  currentPage = name;
  document.querySelectorAll('.page').forEach(p => {
    p.classList.toggle('active', p.id==='page-'+name);
    p.classList.toggle('hidden', p.id!=='page-'+name);
  });
  document.querySelectorAll('.nav-btn').forEach(b => {
    b.classList.toggle('active', b.onclick.toString().includes(`'${name}'`));
  });
  if (name==='modules') loadModulePage();
  if (name==='settings') loadSettings();
  if (name==='connections') refreshSessions();
  if (name==='testplan') {
    if (conn) conn.invoke('GetModules');
    if (currentPlan) renderPlanTable(currentPlan);
    buildFunctionBrowser(allModules);
    buildOverrideRows();
  }
}

// ── UI helpers ─────────────────────────────────────────────────────────────────
function setAccessBadge(mode) {
  const badge = document.getElementById('accessBadge');
  badge.className = 'access-badge '+mode;
  badge.textContent = mode==='pending'?'PENDING':mode==='control'?'CONTROL':'MONITOR';
}

function setConnDot(state) {
  const dot = document.getElementById('connDot');
  dot.className = 'conn-dot '+state;
  dot.title = state==='on'?'Connected':state==='connecting'?'Reconnecting…':'Disconnected';
}

function enableControls(enabled) {
  document.querySelectorAll('.btn-test,.btn-stop,.btn-retest,.btn-clear,.sn-input,#btnStartAll,#btnStopAll').forEach(el => {
    el.disabled = !enabled;
    el.style.opacity = enabled ? '' : '0.4';
  });
  // In monitor mode also lock WO input, settings save, and FG dropdown
  const woEl = document.getElementById('inputWO');
  const setWOEl = document.getElementById('btnSetWO');
  const fgEl = document.getElementById('fgSelect');
  const saveSettingsBtn = document.querySelector('[onclick="saveSettings()"]');
  [woEl, setWOEl, fgEl, saveSettingsBtn].forEach(el => {
    if (!el) return;
    el.disabled = !enabled;
    el.style.opacity = enabled ? '' : '0.4';
  });
  document.body.classList.toggle('monitor-mode', !enabled);
}

function canControl() { return myMode==='control'; }
function noControl() { alert('You are in Monitor mode. Request Control access to interact.'); }

function updateFooter(status, controller, time) {
  if (status!==undefined) document.getElementById('footStatus').textContent = status;
  if (controller!==undefined) document.getElementById('footController').textContent = controller;
  if (time!==undefined) document.getElementById('footTime').textContent = time;
}

function startClock() {
  if (clockInterval) clearInterval(clockInterval);
  clockInterval = setInterval(() => {
    document.getElementById('footTime').textContent = new Date().toLocaleTimeString();
  }, 1000);
}

function esc(str) {
  if (!str) return '';
  return String(str).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

function fmtTime(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return isNaN(d) ? iso : d.toLocaleTimeString();
}

function getV(id) { return (document.getElementById(id)||{}).value||''; }
function setV(id, val) { const el=document.getElementById(id); if(el) el.value=val??''; }
function getChk(id) { return !!(document.getElementById(id)||{}).checked; }
function setChk(id, val) { const el=document.getElementById(id); if(el) el.checked=!!val; }

// ── Init ──────────────────────────────────────────────────────────────────────
window.addEventListener('DOMContentLoaded', async () => {
  buildHeadsGrid(1);
  enableControls(false);

  // Pre-load settings to know prismMode before login (for employee validation)
  try {
    const res = await fetch('/api/config');
    const cfg = await res.json();
    prismMode = cfg.prism?.mode ?? 'Debug';
    applyModeClass();
    updateModeBadge();
  } catch { /* server may not be ready */ }

  // Localhost auto-connects as Control — skip mode selector
  const isLocalhost = ['localhost', '127.0.0.1', '::1'].includes(window.location.hostname);
  if (isLocalhost) {
    myMode = 'control';
    selectMode('control');
    document.getElementById('modeSelectorGroup').classList.add('hidden');
  }

  const nameInput = document.getElementById('inputName');
  nameInput.focus();
  nameInput.addEventListener('keydown', e => { if (e.key==='Enter') connectToServer(); });
});
