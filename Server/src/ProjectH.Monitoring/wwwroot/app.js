// ProjectH Monitoring dashboard (Monitoring D13). Vanilla JS: polls the REST API and draws four uPlot charts.
// Values are formatted with units; raw bytes are never shown (request §81).
(function () {
  'use strict';

  const LIST_POLL_MS = 2000;
  const HISTORY_POLL_MS = 5000;
  const HISTORY_MINUTES = 5;
  const CHART_HEIGHT = 180;
  const COLORS = { a: '#58a6ff', b: '#3fb950', c: '#d29922', axis: '#8b949e', grid: '#21262d' };

  const $ = (id) => document.getElementById(id);
  let selected = decodeURIComponent(location.hash.slice(1)) || null;
  let servers = [];
  let charts = null;

  // ----- formatting -----
  const fmt = {
    ms: (v) => v.toFixed(2) + ' ms',
    pct: (v) => v.toFixed(1) + ' %',
    mb: (bytes) => (bytes / 1048576).toFixed(0) + ' MB',
    kbps: (bytesPerSecond) => (bytesPerSecond / 1024).toFixed(1) + ' KB/s',
    int: (v) => Math.round(v).toLocaleString(),
    seconds: (s) => s.toFixed(1) + ' s',
    uptime: (seconds) => {
      const d = Math.floor(seconds / 86400), h = Math.floor(seconds % 86400 / 3600), m = Math.floor(seconds % 3600 / 60), s = Math.floor(seconds % 60);
      return (d ? d + 'd ' : '') + String(h).padStart(2, '0') + ':' + String(m).padStart(2, '0') + ':' + String(s).padStart(2, '0');
    },
    time: (iso) => new Date(iso).toLocaleString(),
  };

  async function getJson(url) {
    const response = await fetch(url, { cache: 'no-store' });
    if (!response.ok) throw new Error(url + ' → HTTP ' + response.status);
    return response.json();
  }

  // ----- server list -----
  function renderList() {
    const list = $('server-list');
    list.innerHTML = '';
    if (servers.length === 0) {
      list.innerHTML = '<li class="muted">No server has posted yet.</li>';
      return;
    }
    for (const s of servers) {
      const li = document.createElement('li');
      li.className = s.serverId === selected ? 'selected' : '';
      li.innerHTML = '<span class="dot ' + s.status + '"></span><span class="name"></span><span class="meta"></span>';
      li.querySelector('.name').textContent = s.serverId;
      li.querySelector('.meta').textContent = s.status === 'offline' ? 'OFFLINE' : s.latest.players + ' players';
      li.addEventListener('click', () => select(s.serverId));
      list.appendChild(li);
    }
  }

  function select(serverId) {
    if (selected === serverId) return;
    selected = serverId;
    location.hash = encodeURIComponent(serverId);
    renderList();
    const s = servers.find((x) => x.serverId === serverId);
    if (s) renderDetail(s);
    // Empty the charts first: if this server's history cannot be fetched, the previous server's lines must not stay.
    if (charts) renderHistory([]);
    refreshHistory();
  }

  // ----- detail cards -----
  function renderDetail(s) {
    const l = s.latest;
    const offline = s.status === 'offline';
    $('top-server').textContent = s.serverId;
    const pill = $('top-state');
    pill.textContent = s.status.toUpperCase();
    pill.className = 'pill ' + s.status;

    $('offline-banner').classList.toggle('hidden', !offline);
    $('last-seen').textContent = fmt.time(s.lastReceivedAt);
    const warnings = s.warnings.filter((w) => !w.startsWith('offline'));
    $('warnings').classList.toggle('hidden', warnings.length === 0);
    $('warnings').textContent = warnings.join('\n');
    $('cards').classList.toggle('stale', offline);
    document.querySelector('[data-card="tick"]').classList.toggle('warn', warnings.some((w) => w.startsWith('tick')));
    document.querySelector('[data-card="memory"]').classList.toggle('warn', warnings.some((w) => w.startsWith('working set')));

    $('c-status').textContent = s.status.toUpperCase();
    $('c-uptime').textContent = fmt.uptime(l.uptimeSeconds);
    $('c-version').textContent = 'v' + l.version + ' · protocol ' + l.protocolVersion;
    $('c-players').textContent = fmt.int(l.players);
    $('c-peers').textContent = fmt.int(l.connectedPeers);
    $('c-graced').textContent = fmt.int(l.graced);
    $('c-match').textContent = l.matchState;
    $('c-round').textContent = '#' + l.round;
    $('c-tick-p95').textContent = fmt.ms(l.tickP95Ms);
    $('c-tick-p50').textContent = fmt.ms(l.tickP50Ms);
    $('c-tick-p99').textContent = fmt.ms(l.tickP99Ms);
    $('c-tick-max').textContent = fmt.ms(l.tickMaxMs);
    $('c-tick-samples').textContent = fmt.int(l.tickSamples);
    $('c-cpu').textContent = fmt.pct(l.cpuPercent);
    $('c-window').textContent = fmt.seconds(l.windowSeconds);
    $('c-managed').textContent = fmt.mb(l.managedMemoryBytes);
    $('c-workingset').textContent = fmt.mb(l.workingSetBytes);
    $('c-gc').textContent = l.gcGen0 + ' / ' + l.gcGen1 + ' / ' + l.gcGen2;
    $('c-net-out').textContent = fmt.kbps(l.bytesOutPerSecond);
    $('c-net-in').textContent = fmt.kbps(l.bytesInPerSecond);
    $('c-pkt').textContent = fmt.int(l.packetsOutPerSecond) + ' / ' + fmt.int(l.packetsInPerSecond) + ' pkt/s';
    $('c-exceptions').textContent = fmt.int(l.exceptions);
    $('c-invalid').textContent = fmt.int(l.invalidPackets);
    $('c-disconnects').textContent = fmt.int(l.disconnects);
    $('c-db').textContent = l.dbQueueCount + ' / ' + l.dbQueueCapacity;
  }

  function renderNoServer() {
    $('top-server').textContent = '—';
    $('top-state').textContent = 'NO DATA';
    $('top-state').className = 'pill';
  }

  // ----- charts -----
  function chartOptions(width, seriesNames, colors, format) {
    const axis = { stroke: COLORS.axis, grid: { stroke: COLORS.grid }, ticks: { stroke: COLORS.grid } };
    return {
      width, height: CHART_HEIGHT,
      scales: { x: { time: true }, y: { range: (u, min, max) => [0, max <= 0 ? 1 : max * 1.1] } },
      series: [{}].concat(seriesNames.map((name, i) => ({ label: name, stroke: colors[i], width: 1.5, value: (u, v) => v == null ? '—' : format(v) }))),
      axes: [axis, Object.assign({ size: 64, values: (u, vals) => vals.map(format) }, axis)],
      legend: { show: true },
      cursor: { drag: { x: false, y: false } },
    };
  }

  function makeCharts() {
    const width = $('chart-tick').clientWidth || 400;
    const empty = (n) => [[]].concat(Array.from({ length: n }, () => []));
    return {
      tick: new uPlot(chartOptions(width, ['P50', 'P95', 'P99'], [COLORS.b, COLORS.a, COLORS.c], (v) => v.toFixed(2)), empty(3), $('chart-tick')),
      cpu: new uPlot(chartOptions(width, ['CPU %'], [COLORS.a], (v) => v.toFixed(1)), empty(1), $('chart-cpu')),
      memory: new uPlot(chartOptions(width, ['Managed MB', 'Working Set MB'], [COLORS.b, COLORS.a], (v) => v.toFixed(0)), empty(2), $('chart-memory')),
      network: new uPlot(chartOptions(width, ['Send KB/s', 'Receive KB/s'], [COLORS.a, COLORS.b], (v) => v.toFixed(1)), empty(2), $('chart-network')),
    };
  }

  function renderHistory(samples) {
    if (!charts) charts = makeCharts();
    const x = samples.map((s) => Date.parse(s.receivedAt) / 1000);
    const pick = (f) => samples.map((s) => f(s.snapshot));
    charts.tick.setData([x, pick((s) => s.tickP50Ms), pick((s) => s.tickP95Ms), pick((s) => s.tickP99Ms)]);
    charts.cpu.setData([x, pick((s) => s.cpuPercent)]);
    charts.memory.setData([x, pick((s) => s.managedMemoryBytes / 1048576), pick((s) => s.workingSetBytes / 1048576)]);
    charts.network.setData([x, pick((s) => s.bytesOutPerSecond / 1024), pick((s) => s.bytesInPerSecond / 1024)]);
  }

  function resizeCharts() {
    if (!charts) return;
    const width = $('chart-tick').clientWidth || 400;
    for (const c of Object.values(charts)) c.setSize({ width, height: CHART_HEIGHT });
  }

  // ----- polling -----
  async function refreshList() {
    try {
      servers = await getJson('/api/servers');
      $('list-error').textContent = '';
      if (!selected && servers.length > 0) {
        selected = servers[0].serverId;
        location.hash = encodeURIComponent(selected);
        refreshHistory();
      }
      renderList();
      const s = servers.find((x) => x.serverId === selected);
      if (s) renderDetail(s); else renderNoServer();
    } catch (e) {
      $('list-error').textContent = 'Monitoring server unreachable: ' + e.message;
      renderNoServer();
    }
  }

  async function refreshHistory() {
    if (!selected) return;
    try {
      const history = await getJson('/api/servers/' + encodeURIComponent(selected) + '/metrics?minutes=' + HISTORY_MINUTES);
      renderHistory(history.samples);
    } catch (e) {
      // the list poll shows the error; keep the last chart
    }
  }

  window.addEventListener('resize', resizeCharts);
  window.addEventListener('hashchange', () => {
    const id = decodeURIComponent(location.hash.slice(1));
    if (id && id !== selected) select(id);
  });
  refreshList();
  refreshHistory();
  setInterval(refreshList, LIST_POLL_MS);
  setInterval(refreshHistory, HISTORY_POLL_MS);
})();
