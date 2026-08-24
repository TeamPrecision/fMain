// Datalog Viewer — Phase 5
'use strict';

let trendChartInst = null;
let histoChartInst = null;

// ── SignalR live updates ──────────────────────────────────────────────────────
const conn = new signalR.HubConnectionBuilder()
  .withUrl('/testhub')
  .withAutomaticReconnect()
  .build();

conn.on('DatalogSaved', (logId, head, sn, result) => {
  showToast(`New record: SN=${sn} ${result} (ID ${logId})`);
});

conn.onreconnecting(() => setLive(false));
conn.onreconnected(() => setLive(true));

conn.start()
  .then(() => setLive(true))
  .catch(() => setLive(false));

function setLive(on) {
  document.getElementById('liveDot').className = 'live-dot ' + (on ? 'on' : 'off');
  document.getElementById('liveLabel').textContent = on ? 'Live' : 'Offline';
}

// ── Tab switching ─────────────────────────────────────────────────────────────
function showTab(name) {
  document.querySelectorAll('.dl-panel').forEach(p => p.classList.add('hidden'));
  document.querySelectorAll('.dl-tab').forEach(t => t.classList.remove('active'));
  document.getElementById('tab-' + name).classList.remove('hidden');
  event.target.classList.add('active');
}

// ── Filters ───────────────────────────────────────────────────────────────────
function getFilters() {
  return {
    sn:     document.getElementById('fSN').value.trim(),
    wo:     document.getElementById('fWO').value.trim(),
    from:   document.getElementById('fFrom').value,
    to:     document.getElementById('fTo').value,
    result: document.getElementById('fResult').value,
    limit:  parseInt(document.getElementById('fLimit').value) || 100
  };
}

function buildQS(extra = {}) {
  const f = { ...getFilters(), ...extra };
  const p = new URLSearchParams();
  if (f.sn)     p.set('sn', f.sn);
  if (f.wo)     p.set('wo', f.wo);
  if (f.from)   p.set('from', f.from);
  if (f.to)     p.set('to', f.to);
  if (f.result) p.set('result', f.result);
  p.set('limit', f.limit);
  return p.toString();
}

// ── Search logs ───────────────────────────────────────────────────────────────
async function search() {
  const qs = buildQS();
  const resp = await fetch('/api/datalog?' + qs);
  const rows = await resp.json();

  if (rows.error) { showError(rows.error); return; }

  // Filter by result client-side (server query doesn't filter result)
  const rFilter = document.getElementById('fResult').value;
  const data = rFilter ? rows.filter(r => r.result === rFilter) : rows;

  renderLogs(data);
  updateSummary(data);
}

function renderLogs(rows) {
  const tbody = document.getElementById('logBody');
  if (!rows.length) {
    tbody.innerHTML = '<tr><td colspan="9" class="dl-empty">No records found.</td></tr>';
    return;
  }
  tbody.innerHTML = rows.map(r => {
    const start = new Date(r.start_time);
    const end   = new Date(r.end_time);
    const dur   = ((end - start) / 1000).toFixed(1) + 's';
    const resCls = r.result === 'PASS' ? 'res-pass' : r.result === 'FAIL' ? 'res-fail' : '';
    return `<tr class="log-row" data-id="${r.id}">
      <td><button class="expand-btn" onclick="toggleSteps(${r.id}, this)">▶</button></td>
      <td>${r.id}</td>
      <td>${esc(r.work_order)}</td>
      <td>${esc(r.serial_number)}</td>
      <td>${r.head}</td>
      <td>${fmtDate(r.start_time)}</td>
      <td>${dur}</td>
      <td class="${resCls}">${r.result}</td>
      <td>${esc(r.plan_name)} v${esc(r.plan_version)}</td>
    </tr>
    <tr id="steps-${r.id}" class="steps-row hidden"><td colspan="9" id="steps-inner-${r.id}"></td></tr>`;
  }).join('');
}

function updateSummary(rows) {
  const el = document.getElementById('logSummary');
  if (!rows.length) { el.classList.add('hidden'); return; }
  const pass = rows.filter(r => r.result === 'PASS').length;
  const fail = rows.filter(r => r.result === 'FAIL').length;
  const yld  = rows.length > 0 ? (pass * 100 / rows.length).toFixed(1) : 0;
  el.innerHTML = `<span>${rows.length} records</span>
    <span class="res-pass">✓ ${pass} PASS</span>
    <span class="res-fail">✗ ${fail} FAIL</span>
    <span>Yield: <strong>${yld}%</strong></span>`;
  el.classList.remove('hidden');
}

// ── Step expansion ────────────────────────────────────────────────────────────
async function toggleSteps(logId, btn) {
  const row = document.getElementById('steps-' + logId);
  const inner = document.getElementById('steps-inner-' + logId);
  if (!row.classList.contains('hidden')) {
    row.classList.add('hidden');
    btn.textContent = '▶';
    return;
  }
  btn.textContent = '▼';
  inner.innerHTML = '<div class="dl-loading">Loading…</div>';
  row.classList.remove('hidden');

  const resp = await fetch(`/api/datalog/${logId}/steps`);
  const steps = await resp.json();

  if (steps.error || !steps.length) {
    inner.innerHTML = '<div class="dl-empty">No step data.</div>'; return;
  }

  inner.innerHTML = `<table class="dl-sub-table">
    <thead><tr><th>#</th><th>Description</th><th>Function</th><th>Measure</th><th>Result</th><th>LSL</th><th>USL</th><th>Unit</th></tr></thead>
    <tbody>${steps.map(s => {
      const rc = s.result === 'PASS' ? 'res-pass' : s.result === 'FAIL' ? 'res-fail' : '';
      return `<tr>
        <td>${s.step_num}</td>
        <td>${esc(s.description)}</td>
        <td>${esc(s.function)}</td>
        <td>${esc(s.measure)}</td>
        <td class="${rc}">${s.result}</td>
        <td>${esc(s.limit_min)}</td>
        <td>${esc(s.limit_max)}</td>
        <td>${esc(s.unit)}</td>
      </tr>`;
    }).join('')}</tbody>
  </table>`;
}

function closeSteps() {
  document.getElementById('stepsModal').classList.add('hidden');
}

// ── CPK Stats ─────────────────────────────────────────────────────────────────
async function loadStats() {
  const f = getFilters();
  const p = new URLSearchParams();
  if (f.wo)   p.set('wo', f.wo);
  if (f.from) p.set('from', f.from);
  if (f.to)   p.set('to', f.to);

  const resp = await fetch('/api/stats?' + p.toString());
  const rows = await resp.json();

  if (rows.error) { showError(rows.error); return; }
  if (!Array.isArray(rows) || !rows.length) {
    document.getElementById('statsBody').innerHTML =
      '<tr><td colspan="15" class="dl-empty">No data.</td></tr>'; return;
  }

  document.getElementById('statsBody').innerHTML = rows.map(r => {
    const cpk = r.cpk;
    const cpkCls = cpk == null ? '' : cpk >= 1.33 ? 'cpk-good' : cpk >= 1.0 ? 'cpk-ok' : 'cpk-bad';
    const yldCls = r.yield >= 99 ? 'res-pass' : r.yield >= 95 ? '' : 'res-fail';
    return `<tr>
      <td>${esc(r.description)}</td>
      <td>${esc(r.function)}</td>
      <td>${r.total}</td>
      <td>${r.pass}</td>
      <td>${r.fail}</td>
      <td class="${yldCls}">${r.yield}%</td>
      <td>${r.avg ?? '—'}</td>
      <td>${r.stddev ?? '—'}</td>
      <td>${r.min ?? '—'}</td>
      <td>${r.max ?? '—'}</td>
      <td>${r.lmin || '—'}</td>
      <td>${r.lmax || '—'}</td>
      <td class="${cpkCls}">${r.cp != null ? r.cp : '—'}</td>
      <td class="${cpkCls}">${cpk != null ? cpk : '—'}</td>
      <td><button class="btn-sm-link" onclick='showHisto(${JSON.stringify(r)})'>Histogram</button></td>
    </tr>`;
  }).join('');
}

// ── Histogram ─────────────────────────────────────────────────────────────────
async function showHisto(stat) {
  document.getElementById('histoTitle').textContent = `Histogram — ${stat.description}`;
  document.getElementById('histoWrap').classList.remove('hidden');

  // Fetch trend data to get raw values for histogram
  const p = new URLSearchParams({ step: stat.description });
  const f = getFilters();
  if (f.wo)   p.set('wo', f.wo);
  if (f.from) p.set('from', f.from);
  if (f.to)   p.set('to', f.to);
  p.set('limit', '1000');

  const resp = await fetch('/api/trend?' + p.toString());
  const points = await resp.json();

  const values = (Array.isArray(points) ? points : [])
    .map(p => parseFloat(p.measure)).filter(v => !isNaN(v));

  if (!values.length) {
    document.getElementById('histoWrap').classList.add('hidden');
    showToast('No numeric data for histogram.'); return;
  }

  const lo = Math.min(...values), hi = Math.max(...values);
  const bins = 20;
  const w = (hi - lo) / bins || 1;
  const counts = Array(bins).fill(0);
  values.forEach(v => {
    let b = Math.floor((v - lo) / w);
    if (b >= bins) b = bins - 1;
    counts[b]++;
  });
  const labels = counts.map((_, i) => (lo + i * w + w / 2).toFixed(3));

  if (histoChartInst) histoChartInst.destroy();
  const ctx = document.getElementById('histoChart').getContext('2d');
  histoChartInst = new Chart(ctx, {
    type: 'bar',
    data: { labels, datasets: [{ label: 'Count', data: counts, backgroundColor: '#3b82f6', borderWidth: 0 }] },
    options: {
      plugins: { legend: { display: false } },
      scales: {
        x: { title: { display: true, text: stat.unit || 'Value' } },
        y: { title: { display: true, text: 'Count' }, ticks: { precision: 0 } }
      },
      animation: false
    }
  });
}

function closeHisto() {
  document.getElementById('histoWrap').classList.add('hidden');
  if (histoChartInst) { histoChartInst.destroy(); histoChartInst = null; }
}

// ── Trend chart ───────────────────────────────────────────────────────────────
async function loadTrend() {
  const step = document.getElementById('trendStep').value.trim();
  if (!step) { showToast('Enter a step description.'); return; }

  const f = getFilters();
  const p = new URLSearchParams({ step });
  if (f.wo)   p.set('wo', f.wo);
  if (f.from) p.set('from', f.from);
  if (f.to)   p.set('to', f.to);
  p.set('limit', f.limit);

  const resp = await fetch('/api/trend?' + p.toString());
  const points = await resp.json();

  if (points.error) { showError(points.error); return; }
  if (!Array.isArray(points) || !points.length) {
    document.getElementById('trendEmpty').classList.remove('hidden');
    document.getElementById('trendWrap').classList.add('hidden');
    return;
  }

  const numeric = points.filter(p => !isNaN(parseFloat(p.measure)));
  if (!numeric.length) { showToast('No numeric measurements for trend.'); return; }

  const labels  = numeric.map(p => fmtDate(p.start_time));
  const data    = numeric.map(p => parseFloat(p.measure));
  const colors  = numeric.map(p => p.result === 'PASS' ? '#22c55e' : '#ef4444');

  // Limit lines from first data point
  const lsl = parseFloat(numeric[0].limit_min);
  const usl = parseFloat(numeric[0].limit_max);

  const datasets = [{
    label: step,
    data,
    borderColor: '#3b82f6',
    backgroundColor: '#3b82f640',
    pointBackgroundColor: colors,
    pointRadius: 4,
    tension: 0.2
  }];
  if (!isNaN(lsl)) datasets.push({ label: 'LSL', data: Array(data.length).fill(lsl), borderColor: '#f59e0b', borderDash: [5,3], pointRadius: 0, tension: 0 });
  if (!isNaN(usl)) datasets.push({ label: 'USL', data: Array(data.length).fill(usl), borderColor: '#f59e0b', borderDash: [5,3], pointRadius: 0, tension: 0 });

  if (trendChartInst) trendChartInst.destroy();
  const ctx = document.getElementById('trendChart').getContext('2d');
  trendChartInst = new Chart(ctx, {
    type: 'line',
    data: { labels, datasets },
    options: {
      responsive: true,
      plugins: { tooltip: { mode: 'index', intersect: false } },
      scales: {
        x: { ticks: { maxTicksLimit: 20, maxRotation: 45 } },
        y: { title: { display: true, text: numeric[0].unit || 'Value' } }
      },
      animation: false
    }
  });

  document.getElementById('trendEmpty').classList.add('hidden');
  document.getElementById('trendWrap').classList.remove('hidden');
}

// ── Export ────────────────────────────────────────────────────────────────────
function exportData(format) {
  const f = getFilters();
  const p = new URLSearchParams();
  if (f.sn)   p.set('sn', f.sn);
  if (f.wo)   p.set('wo', f.wo);
  if (f.from) p.set('from', f.from);
  if (f.to)   p.set('to', f.to);
  p.set('limit', f.limit);
  window.open('/api/export/' + format + '?' + p.toString(), '_blank');
}

// ── Utilities ─────────────────────────────────────────────────────────────────
function esc(v) {
  return String(v ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}

function fmtDate(v) {
  if (!v) return '';
  const d = new Date(v);
  return d.toLocaleString();
}

function showToast(msg) {
  const t = document.createElement('div');
  t.className = 'toast';
  t.textContent = msg;
  document.body.appendChild(t);
  setTimeout(() => t.remove(), 3500);
}

function showError(msg) {
  showToast('Error: ' + msg);
}
