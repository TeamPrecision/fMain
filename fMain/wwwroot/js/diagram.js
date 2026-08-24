// Block Diagram Editor — Phase 5
'use strict';

// ── State ─────────────────────────────────────────────────────────────────────
let plan = null;           // TestPlan JSON from server
let selectedId = null;     // currently selected step ID
let scale = 1;
let offsetX = 0, offsetY = 0;
let panning = false, panStartX = 0, panStartY = 0, panOffX = 0, panOffY = 0;
let dragging = null;       // {id, startX, startY, origX, origY}
let stepPositions = {};    // {stepId: {x, y}}

// Layout constants
const BOX_W = 230, BOX_H = 52, BOX_GAP = 80, BOX_INDENT = 60;

// ── Load plan ─────────────────────────────────────────────────────────────────
async function loadCurrentPlan() {
  try {
    const resp = await fetch('/api/plan');
    plan = await resp.json();
    setStatus('Plan loaded: ' + plan.name);
    autoLayout();
    render();
  } catch (e) {
    setStatus('Failed to load plan: ' + e.message, true);
  }
}

// ── Save plan back ────────────────────────────────────────────────────────────
async function savePlanBack() {
  if (!plan) { setStatus('No plan loaded.', true); return; }
  try {
    await fetch('/api/plan', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(plan)
    });
    setStatus('Saved to server ✓');
  } catch (e) {
    setStatus('Save failed: ' + e.message, true);
  }
}

// ── Auto layout ───────────────────────────────────────────────────────────────
function autoLayout() {
  if (!plan) return;
  stepPositions = {};
  let y = 40;
  plan.steps.forEach((s, i) => {
    const x = s.rowType === 'Header' ? 40 : BOX_INDENT;
    stepPositions[s.id] = { x, y };
    y += BOX_H + BOX_GAP;
  });
}

// ── Render ────────────────────────────────────────────────────────────────────
function render() {
  const root = document.getElementById('dg-root');
  root.innerHTML = '';
  document.getElementById('dgHint').style.display = plan ? 'none' : '';
  if (!plan || !plan.steps.length) return;

  // Arrows first (behind boxes)
  const arrowG = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  arrowG.id = 'arrow-layer';
  plan.steps.forEach(s => {
    if (s.rowType === 'Header') return;
    const fromPos = stepPositions[s.id];
    if (!fromPos) return;
    const fx = fromPos.x + BOX_W / 2, fy = fromPos.y + BOX_H;

    // Default sequential arrow
    const nextStep = plan.steps.find((ns, ni) => ni > plan.steps.indexOf(s) && ns.rowType !== 'Header');
    if (nextStep && s.onPassGoto === 'next' && s.onFailGoto !== 'next') {
      // Only draw sequential if pass also goes next
    }
    if (nextStep && (s.onPassGoto === 'next' || !s.onPassGoto)) {
      const tp = stepPositions[nextStep.id];
      if (tp) drawArrow(arrowG, fx, fy, tp.x + BOX_W / 2, tp.y, 'gray');
    }
    // OnPassGoto jump
    if (s.onPassGoto && s.onPassGoto !== 'next' && s.onPassGoto !== 'end') {
      const tp = stepPositions[s.onPassGoto];
      if (tp) drawArrow(arrowG, fx - 10, fy, tp.x + BOX_W / 2 - 10, tp.y, 'green');
    }
    // OnFailGoto
    if (s.onFailGoto && s.onFailGoto !== 'end') {
      const tp = s.onFailGoto === 'next'
        ? stepPositions[nextStep?.id]
        : stepPositions[s.onFailGoto];
      if (tp) drawArrow(arrowG, fx + 10, fy, tp.x + BOX_W / 2 + 10, tp.y, 'red');
    }
  });
  root.appendChild(arrowG);

  // Boxes
  const boxG = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  boxG.id = 'box-layer';
  plan.steps.forEach(s => {
    const pos = stepPositions[s.id] || { x: 40, y: 40 };
    boxG.appendChild(makeBox(s, pos));
  });
  root.appendChild(boxG);

  updateTransform();
  populateGotoDropdowns();
}

function drawArrow(parent, x1, y1, x2, y2, color) {
  const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  // Bezier curve
  const mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
  const d = `M${x1},${y1} C${x1},${my} ${x2},${my} ${x2},${y2}`;
  path.setAttribute('d', d);
  path.setAttribute('fill', 'none');
  const colors = { green: '#22c55e', red: '#ef4444', gray: '#94a3b8' };
  path.setAttribute('stroke', colors[color] || '#94a3b8');
  path.setAttribute('stroke-width', color === 'gray' ? '1.5' : '2');
  path.setAttribute('opacity', '0.8');
  path.setAttribute('marker-end', `url(#arrow${color.charAt(0).toUpperCase()+color.slice(1)})`);
  parent.appendChild(path);
}

function makeBox(step, pos) {
  const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  g.setAttribute('class', 'dg-step' + (step.id === selectedId ? ' selected' : ''));
  g.setAttribute('data-id', step.id);
  g.setAttribute('transform', `translate(${pos.x},${pos.y})`);

  const isHeader = step.rowType === 'Header';
  const isSkip   = step.rowType === 'Skip';

  const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
  rect.setAttribute('width', isHeader ? BOX_W + 40 : BOX_W);
  rect.setAttribute('height', isHeader ? 28 : BOX_H);
  rect.setAttribute('rx', isHeader ? 4 : 8);
  rect.setAttribute('class', isHeader ? 'box-header' : isSkip ? 'box-skip' : 'box-normal');
  g.appendChild(rect);

  const desc = makeText(step.description || '(no description)', 12, isHeader ? 14 : 16, isHeader ? 12 : 13, isHeader ? 'bold' : 'normal');
  g.appendChild(desc);

  if (!isHeader) {
    const fn = makeText(step.function ? '⚙ ' + step.function : '', 12, 32, 11, 'normal', '#94a3b8');
    g.appendChild(fn);

    const hasLimits = step.min || step.max;
    if (hasLimits) {
      const limTxt = [step.min && `≥${step.min}`, step.max && `≤${step.max}`, step.unit].filter(Boolean).join(' ');
      const lim = makeText(limTxt, BOX_W - 8, 14, 10, 'normal', '#64748b');
      lim.setAttribute('text-anchor', 'end');
      g.appendChild(lim);
    }
  }

  // Interaction
  g.style.cursor = 'pointer';
  g.addEventListener('mousedown', e => startDrag(e, step.id, pos));
  g.addEventListener('click', e => { e.stopPropagation(); selectStep(step.id); });

  return g;
}

function makeText(content, x, y, size, weight, fill) {
  const t = document.createElementNS('http://www.w3.org/2000/svg', 'text');
  t.setAttribute('x', x);
  t.setAttribute('y', y);
  t.setAttribute('font-size', size);
  t.setAttribute('font-weight', weight || 'normal');
  t.setAttribute('fill', fill || 'currentColor');
  t.setAttribute('class', 'dg-text');
  // Truncate long text
  const max = 28;
  t.textContent = content.length > max ? content.slice(0, max - 1) + '…' : content;
  return t;
}

// ── Selection ─────────────────────────────────────────────────────────────────
function selectStep(id) {
  selectedId = id;
  document.querySelectorAll('.dg-step').forEach(g => {
    g.classList.toggle('selected', g.getAttribute('data-id') === id);
  });
  const step = plan?.steps.find(s => s.id === id);
  if (!step) { showPanel(false); return; }
  showPanel(true);
  document.getElementById('fp-desc').value    = step.description || '';
  document.getElementById('fp-fn').value      = step.function    || '';
  document.getElementById('fp-min').value     = step.min         || '';
  document.getElementById('fp-max').value     = step.max         || '';
  document.getElementById('fp-unit').value    = step.unit        || '';
  document.getElementById('fp-timeout').value = step.timeoutMs   || 30000;
  document.getElementById('fp-type').value    = step.rowType     || 'Normal';
  populateGotoDropdowns(step);
}

function showPanel(show) {
  document.getElementById('panelEmpty').classList.toggle('hidden', show);
  document.getElementById('panelForm').classList.toggle('hidden', !show);
}

function applyPanel() {
  if (!selectedId || !plan) return;
  const step = plan.steps.find(s => s.id === selectedId);
  if (!step) return;
  step.description = document.getElementById('fp-desc').value;
  step.function    = document.getElementById('fp-fn').value;
  step.min         = document.getElementById('fp-min').value;
  step.max         = document.getElementById('fp-max').value;
  step.unit        = document.getElementById('fp-unit').value;
  step.timeoutMs   = parseInt(document.getElementById('fp-timeout').value) || 30000;
  step.rowType     = document.getElementById('fp-type').value;
  render();
}

function applyGoto() {
  if (!selectedId || !plan) return;
  const step = plan.steps.find(s => s.id === selectedId);
  if (!step) return;
  step.onPassGoto = document.getElementById('fp-pass').value;
  step.onFailGoto = document.getElementById('fp-fail').value;
  render();
}

function populateGotoDropdowns(step) {
  if (!plan) return;
  const passEl = document.getElementById('fp-pass');
  const failEl = document.getElementById('fp-fail');
  const makeOpts = (selected) => {
    const base = [
      `<option value="next"${selected==='next'?' selected':''}>→ Next step</option>`,
      `<option value="end"${selected==='end'?' selected':''}>⏹ End / Abort</option>`
    ];
    plan.steps.filter(s => s.rowType !== 'Header').forEach(s => {
      const sel = selected === s.id ? ' selected' : '';
      base.push(`<option value="${s.id}"${sel}>⤵ ${s.description || s.id}</option>`);
    });
    return base.join('');
  };
  passEl.innerHTML = makeOpts(step?.onPassGoto || 'next');
  failEl.innerHTML = makeOpts(step?.onFailGoto || 'end');
}

function deleteSelected() {
  if (!selectedId || !plan) return;
  plan.steps = plan.steps.filter(s => s.id !== selectedId);
  delete stepPositions[selectedId];
  selectedId = null;
  showPanel(false);
  render();
}

function addStep() {
  if (!plan) { setStatus('Load a plan first.', true); return; }
  const id = Math.random().toString(36).slice(2, 10);
  const newStep = {
    id, rowType: 'Normal', stepNum: plan.steps.length + 1,
    description: 'New Step', function: '', param1: '', param2: '', param3: '', param4: '',
    min: '', max: '', unit: '', failBehavior: 'Stop', timeoutMs: 30000,
    onPassGoto: 'next', onFailGoto: 'end'
  };
  plan.steps.push(newStep);
  const lastPos = Object.values(stepPositions).sort((a,b) => b.y - a.y)[0];
  stepPositions[id] = { x: BOX_INDENT, y: (lastPos?.y ?? 0) + BOX_H + BOX_GAP };
  render();
  selectStep(id);
}

// ── Drag ──────────────────────────────────────────────────────────────────────
function startDrag(e, id, pos) {
  e.stopPropagation();
  dragging = { id, startX: e.clientX, startY: e.clientY, origX: pos.x, origY: pos.y };
  document.addEventListener('mousemove', onDragMove);
  document.addEventListener('mouseup', onDragEnd);
}

function onDragMove(e) {
  if (!dragging) return;
  const dx = (e.clientX - dragging.startX) / scale;
  const dy = (e.clientY - dragging.startY) / scale;
  stepPositions[dragging.id] = { x: dragging.origX + dx, y: dragging.origY + dy };
  const g = document.querySelector(`[data-id="${dragging.id}"]`);
  if (g) {
    const p = stepPositions[dragging.id];
    g.setAttribute('transform', `translate(${p.x},${p.y})`);
  }
  // Re-render arrows only
  renderArrows();
}

function onDragEnd() {
  dragging = null;
  document.removeEventListener('mousemove', onDragMove);
  document.removeEventListener('mouseup', onDragEnd);
}

function renderArrows() {
  const arrowG = document.getElementById('arrow-layer');
  if (!arrowG || !plan) return;
  arrowG.innerHTML = '';
  plan.steps.forEach(s => {
    if (s.rowType === 'Header') return;
    const fromPos = stepPositions[s.id];
    if (!fromPos) return;
    const fx = fromPos.x + BOX_W / 2, fy = fromPos.y + BOX_H;
    const ni = plan.steps.indexOf(s);
    const nextStep = plan.steps.slice(ni + 1).find(ns => ns.rowType !== 'Header');

    if (nextStep && (s.onPassGoto === 'next' || !s.onPassGoto)) {
      const tp = stepPositions[nextStep.id];
      if (tp) drawArrow(arrowG, fx, fy, tp.x + BOX_W / 2, tp.y, 'gray');
    }
    if (s.onPassGoto && s.onPassGoto !== 'next' && s.onPassGoto !== 'end') {
      const tp = stepPositions[s.onPassGoto];
      if (tp) drawArrow(arrowG, fx - 10, fy, tp.x + BOX_W / 2 - 10, tp.y, 'green');
    }
    if (s.onFailGoto && s.onFailGoto !== 'end') {
      const tp2 = s.onFailGoto === 'next' ? stepPositions[nextStep?.id] : stepPositions[s.onFailGoto];
      if (tp2) drawArrow(arrowG, fx + 10, fy, tp2.x + BOX_W / 2 + 10, tp2.y, 'red');
    }
  });
}

// ── Pan ───────────────────────────────────────────────────────────────────────
const wrap = document.getElementById('canvasWrap');
wrap.addEventListener('mousedown', e => {
  if (e.target.id === 'dg-svg' || e.target === wrap) {
    panning = true;
    panStartX = e.clientX; panStartY = e.clientY;
    panOffX = offsetX; panOffY = offsetY;
  }
});
document.addEventListener('mousemove', e => {
  if (!panning) return;
  offsetX = panOffX + (e.clientX - panStartX);
  offsetY = panOffY + (e.clientY - panStartY);
  updateTransform();
});
document.addEventListener('mouseup', () => { panning = false; });

wrap.addEventListener('wheel', e => {
  e.preventDefault();
  zoom(e.deltaY < 0 ? 0.1 : -0.1);
}, { passive: false });

document.getElementById('dg-svg').addEventListener('click', e => {
  if (e.target.id === 'dg-svg' || e.target.tagName === 'svg') {
    selectedId = null;
    document.querySelectorAll('.dg-step').forEach(g => g.classList.remove('selected'));
    showPanel(false);
  }
});

function zoom(delta) {
  scale = Math.min(3, Math.max(0.2, scale + delta));
  updateTransform();
}

function zoomFit() {
  if (!plan || !plan.steps.length) return;
  const positions = Object.values(stepPositions);
  if (!positions.length) return;
  const maxX = Math.max(...positions.map(p => p.x)) + BOX_W + 40;
  const maxY = Math.max(...positions.map(p => p.y)) + BOX_H + 40;
  const ww = wrap.clientWidth - 280, wh = wrap.clientHeight - 60;
  scale = Math.min(ww / maxX, wh / maxY, 1.5);
  offsetX = 20; offsetY = 20;
  updateTransform();
}

function updateTransform() {
  document.getElementById('dg-root').setAttribute('transform',
    `translate(${offsetX},${offsetY}) scale(${scale})`);
}

// ── Status ────────────────────────────────────────────────────────────────────
function setStatus(msg, err = false) {
  const el = document.getElementById('dg-status');
  el.textContent = msg;
  el.style.color = err ? '#ef4444' : '#22c55e';
  if (!err) setTimeout(() => { if (el.textContent === msg) el.textContent = ''; }, 4000);
}

// ── Init ──────────────────────────────────────────────────────────────────────
loadCurrentPlan();
