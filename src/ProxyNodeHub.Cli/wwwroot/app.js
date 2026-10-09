/* ══════════════════════════════════════════════════════════
   ProxyNodeHub Web 管理界面
   原生 JS，无框架、无构建步骤 —— 随 CLI 一起发布。
   ══════════════════════════════════════════════════════════ */

'use strict';

const $ = (id) => document.getElementById(id);

const state = {
  repos: [],
  favorites: [],
  nodes: [],
  catalog: [],          // 节点清单（当前筛选后的结果）
  catalogLoaded: false, // 清单是否加载过 —— 决定筛选条是否显示
  catalogLoadedAt: null,// 上次已消费的加载时间戳（轮询据此判断要不要刷新）
  catalogTotal: 0,      // 清单总条数（未经筛选）
  selectedUris: new Set(),   // 勾选的节点 URI
  selProtocols: new Set(),   // 选中的协议（多选）
  selRepos: new Set(),       // 选中的来源（多选）
  selRegions: new Set(),     // 选中的地区预置名（多选）
  selPlatforms: new Set(),   // 选中的流媒体检测平台（多选）
  regionPresets: [],         // 地区预置列表 [{name, pattern}]
  subUrls: [],               // 已保存的订阅 [{name, url}]
  selSubUrls: new Set(),     // 选中的订阅 url（多选）
  facets: { protocols: [], repos: [] },
  view: 'dashboard',
  selected: null,
  sort: 'Score',
  sortDesc: true,
  logCount: 0,
  searching: false,
  speedTesting: false,
  worldMapSvg: null,
  worldMapCollapsed: localStorage.getItem('worldMapCollapsed') === '1',
  _lastPassedCount: 0,
  aliveNodes: [],
  aliveStats: null,
  pipelineRunning: false,
};

// ══════════════════ 工具 ══════════════════

async function api(path, opts) {
  const res = await fetch('/api' + path, opts);
  if (!res.ok) {
    let msg = res.statusText;
    try { msg = (await res.json()).error || msg; } catch {}
    throw new Error(msg);
  }
  const ct = res.headers.get('content-type') || '';
  return ct.includes('json') ? res.json() : res.text();
}

function toast(msg, kind) {
  const t = $('toast');
  t.textContent = msg;
  t.className = 'toast show' + (kind ? ' ' + kind : '');
  clearTimeout(t._timer);
  t._timer = setTimeout(() => { t.className = 'toast'; }, 2600);
}

function fmtSpeed(kbps) {
  if (!kbps || kbps <= 0) return '—';
  return kbps >= 1024 ? (kbps / 1024).toFixed(1) + ' MB/s' : kbps + ' KB/s';
}

function scoreClass(s) {
  return s >= 75 ? 'high' : s >= 45 ? 'mid' : 'low';
}

function statusPill(r) {
  const cls = r.daysInactive <= 0 ? 'live' : r.daysInactive <= 3 ? 'stale' : 'gone';
  return `<span class="pill ${cls}">${escapeHtml(r.statusText)}</span>`;
}

function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));
}

function isFavorite(fullName) {
  return state.favorites.some(f => f.fullName.toLowerCase() === fullName.toLowerCase());
}

// ══════════════════ 页签 ══════════════════

document.querySelectorAll('.tab').forEach(tab => {
  tab.addEventListener('click', () => {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
    tab.classList.add('active');
    state.view = tab.dataset.view;
    $('view-' + state.view).classList.add('active');
    if (state.view === 'dashboard') renderWorldMap();
    if (state.view === 'alive') loadAliveNodes();
    if (state.view === 'favorites') loadFavorites();
    if (state.view === 'settings') loadSettings();
  });
});

// 设置页左菜单：切换右侧显示的设置项
document.querySelectorAll('.settings-nav-item').forEach(item => {
  item.addEventListener('click', () => {
    document.querySelectorAll('.settings-nav-item').forEach(n => n.classList.remove('active'));
    document.querySelectorAll('.settings-pane').forEach(p => p.classList.remove('active'));
    item.classList.add('active');
    const pane = document.querySelector(`.settings-pane[data-pane="${item.dataset.settings}"]`);
    if (pane) pane.classList.add('active');
  });
});

// ══════════════════ 仓库表格 ══════════════════

async function loadRepos() {
  const params = new URLSearchParams({
    filter: $('filterText').value || '',
    inactiveDays: $('filterInactive').value || 7,
    minCommits: $('filterCommits').value || 0,
    minActiveDays: $('filterAge').value || 0,
    autoClean: $('filterAutoClean').checked,
    sort: state.sort,
    desc: state.sortDesc,
  });
  try {
    state.repos = await api('/repos?' + params);
    renderRepos();
  } catch (e) {
    toast('加载失败: ' + e.message, 'error');
  }
}

function renderRepos() {
  const body = $('gridBody');
  $('repoCount').textContent = `${state.repos.length} 个仓库`;
  $('emptyHint').classList.toggle('show', state.repos.length === 0);

  body.innerHTML = state.repos.map(r => {
    const fav = isFavorite(r.fullName);
    const usable = r.usableScore >= 0 ? r.usableScore : '—';
    return `
    <tr data-name="${escapeHtml(r.fullName)}" class="${state.selected === r.fullName ? 'selected' : ''}">
      <td>
        <span class="repo-name">${escapeHtml(r.fullName)}</span>
        ${r.description ? `<span class="repo-desc">${escapeHtml(r.description)}</span>` : ''}
      </td>
      <td class="num">${r.stars}</td>
      <td>${escapeHtml(relTime(r.lastPush))}</td>
      <td>${statusPill(r)}</td>
      <td class="num">${r.commitsLast7Days}</td>
      <td class="num">${r.ageDays}d</td>
      <td>${escapeHtml(r.processingType)}</td>
      <td class="num"><span class="score ${scoreClass(r.score)}">${r.score}</span></td>
      <td class="num">${r.totalNodes || '—'}</td>
      <td class="num">${r.links.length}</td>
      <td class="num">${usable}${r.demoted ? ' ↓' : ''}</td>
      <td class="num">${r.avgSpeedKbps ? fmtSpeed(r.avgSpeedKbps) : '—'}</td>
      <td><button class="star-btn ${fav ? 'on' : ''}" data-fav="${escapeHtml(r.fullName)}"
            title="${fav ? '取消收藏' : '收藏'}">${fav ? '★' : '☆'}</button></td>
    </tr>`;
  }).join('');

  // 行点击 → 详情
  body.querySelectorAll('tr').forEach(tr => {
    tr.addEventListener('click', (e) => {
      if (e.target.closest('.star-btn')) return;
      selectRepo(tr.dataset.name);
    });
  });

  // 收藏按钮
  body.querySelectorAll('.star-btn').forEach(btn => {
    btn.addEventListener('click', async (e) => {
      e.stopPropagation();
      await toggleFavorite(btn.dataset.fav);
    });
  });

  updateActionButtons();
}

function relTime(iso) {
  if (!iso) return '?';
  const t = new Date(iso);
  if (isNaN(t)) return iso.slice(0, 10);
  const mins = (Date.now() - t.getTime()) / 60000;
  if (mins < 1) return '刚刚';
  if (mins < 60) return Math.floor(mins) + '分钟前';
  if (mins < 1440) return Math.floor(mins / 60) + '小时前';
  const days = Math.floor(mins / 1440);
  if (days < 30) return days + '天前';
  return iso.slice(5, 10);
}

// ══════════════════ 详情面板 ══════════════════

function selectRepo(fullName) {
  state.selected = fullName;
  const r = state.repos.find(x => x.fullName === fullName)
         || state.favorites.find(x => x.fullName === fullName);
  if (!r) return;

  $('detailTitle').textContent = r.fullName;
  $('detailBody').innerHTML = `
    <div class="meta-grid">
      <div class="meta-item"><b>活跃度</b><span class="score ${scoreClass(r.score)}">${r.score}</span></div>
      <div class="meta-item"><b>Star</b><span>${r.stars}</span></div>
      <div class="meta-item"><b>7 天提交</b><span>${r.commitsLast7Days}</span></div>
      <div class="meta-item"><b>活跃天数</b><span>${r.distinctActiveDays}</span></div>
      <div class="meta-item"><b>库龄</b><span>${r.ageDays} 天</span></div>
      <div class="meta-item"><b>处理方式</b><span>${escapeHtml(r.processingType)}</span></div>
      <div class="meta-item"><b>验证节点</b><span>${r.totalNodes || 0}</span></div>
      <div class="meta-item"><b>默认分支</b><span>${escapeHtml(r.branch)}</span></div>
    </div>
    ${r.description ? `<p class="hint" style="margin-bottom:10px">${escapeHtml(r.description)}</p>` : ''}
    ${r.links.length === 0
      ? '<p class="hint">未检测到订阅链接。</p>'
      : r.links.map(l => `
        <div class="link-row">
          <a class="link-url" href="${escapeHtml(l.url)}" target="_blank" rel="noopener">${escapeHtml(l.url)}</a>
          <span class="link-meta">
            ${escapeHtml(l.name)} · ${escapeHtml(l.type)} ·
            ${l.nodeCount > 0 ? l.nodeCount + ' 节点'
              : l.nodeCount === -1 ? '[未验证]' : '[无效]'}
          </span>
        </div>`).join('')}
  `;
  $('detail').classList.add('open');
  renderRepos();
}

$('btnDetailClose').addEventListener('click', () => {
  $('detail').classList.remove('open');
  state.selected = null;
  renderRepos();
});

// ══════════════════ 排序 ══════════════════

document.querySelectorAll('.grid thead th[data-sort]').forEach(th => {
  th.addEventListener('click', () => {
    const field = th.dataset.sort;
    if (state.sort === field) state.sortDesc = !state.sortDesc;
    else { state.sort = field; state.sortDesc = true; }
    document.querySelectorAll('.grid thead th').forEach(x =>
      x.classList.remove('sort-asc', 'sort-desc'));
    th.classList.add(state.sortDesc ? 'sort-desc' : 'sort-asc');
    loadRepos();
  });
});

// ══════════════════ 筛选 ══════════════════

let filterTimer = null;
['filterText', 'filterInactive', 'filterCommits', 'filterAge', 'filterAutoClean']
  .forEach(id => {
    $(id).addEventListener('input', () => {
      clearTimeout(filterTimer);
      filterTimer = setTimeout(loadRepos, 220);
    });
  });

// ══════════════════ 搜索 ══════════════════

$('btnSearch').addEventListener('click', async () => {
  if (state.searching) {
    await api('/search/cancel', { method: 'POST' });
    return;
  }
  try {
    await api('/search', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        count: +$('optCount').value,
        sinceDays: +$('optDays').value,
        autoProxy: $('optAutoProxy').checked,
        skipSearched: $('optSkipSearched').checked,
      }),
    });
    openLog();
  } catch (e) {
    toast(e.message, 'error');
  }
});

function setSearching(on) {
  state.searching = on;
  const btn = $('btnSearch');
  btn.classList.toggle('running', on);
  btn.querySelector('.ico').textContent = on ? '■' : '▶';
  $('btnSearchText').textContent = on ? '取消' : '搜索并分析';
  updateActionButtons();
}

function updateActionButtons() {
  const hasData = state.repos.length > 0;
  $('btnExport').disabled = !hasData || state.searching;
  $('btnMerge').disabled = !hasData || state.searching;
}

// ══════════════════ 进度轮询 ══════════════════

async function pollState() {
  try {
    const s = await api('/state');

    // 搜索状态
    if (s.searching !== state.searching) setSearching(s.searching);
    if (s.searching) {
      const wrap = $('progressWrap');
      wrap.classList.add('active');
      const total = s.searchTotal || 1;
      const pct = s.searchPhase === 'search'
        ? Math.round(s.searchDone / total * 30)
        : s.searchPhase === 'analyze'
          ? 30 + Math.round(s.searchDone / total * 70)
          : 2;
      $('progressBar').style.width = Math.min(100, pct) + '%';
      $('progressText').textContent =
        s.searchPhase === 'analyze'
          ? `分析 ${s.searchDone}/${s.searchTotal} · ${s.searchMessage}`
          : s.searchPhase === 'search'
            ? `搜索 ${s.searchDone}/${s.searchTotal} · ${s.searchMessage}`
            : s.searchMessage;
    } else {
      $('progressWrap').classList.remove('active');
      if (state.repos.length === 0 && s.repoCount > 0) loadRepos();
    }

    // 清单加载状态
    if (s.loadingCatalog !== state.loadingCatalog) setLoadingCatalog(s.loadingCatalog);
    if (s.loadingCatalog) {
      const wrap = $('catProgressWrap');
      wrap.classList.add('active');
      const total = s.catalogTotal || 1;
      $('catProgressBar').style.width = Math.min(100, s.catalogDone / total * 100) + '%';
      $('catProgressText').textContent = `拉取订阅 ${s.catalogDone}/${s.catalogTotal}`;
    } else {
      $('catProgressWrap').classList.remove('active');
      // 只在「有新的加载完成」时刷新一次。
      // 之前拿筛选后的条数跟总数比较，只要筛选生效两者就不相等，
      // 于是每 1.2 秒重新拉一遍清单 —— 表现就是输入一半被自动提交、
      // 而且持续打断输入。
      if (s.catalogLoadedAt && s.catalogLoadedAt !== state.catalogLoadedAt) loadCatalog();
    }

    // 测速状态
    if (s.speedTesting !== state.speedTesting) setSpeedTesting(s.speedTesting);
    if (s.speedTesting && s.speedStatus) {
      const p = s.speedStatus.pipeline;
      const total = p?.total || s.speedStatus.proxyCount || 1;
      const done = p?.speedDone || 0;
      $('spProgressWrap').classList.add('active');
      $('spProgressBar').style.width = Math.min(100, done / total * 100) + '%';
      $('spProgressText').textContent =
        `${total} 节点 · 存活 ${p?.alivePass ?? 0} · 测速通过 ${p?.speedPass ?? 0}`;
      // 测速进行中：实时刷新结果表 + 地图（后端 PollUntilDoneAsync 定期
      // 把 /api/results 的已完成节点灌进 state._nodes）
      if (s.passedCount !== state._lastPassedCount) {
        state._lastPassedCount = s.passedCount;
        loadNodes().then(() => renderWorldMap());
      }
    } else if (!s.speedTesting) {
      $('spProgressWrap').classList.remove('active');
      // 测速刚结束 → 自动刷新结果表 + 世界地图，否则用户得手动切回页面才看得到
      if (s.speedFinishedAt && s.speedFinishedAt !== state._lastSpeedFinish) {
        state._lastSpeedFinish = s.speedFinishedAt;
        loadNodes().then(() => renderWorldMap());
      }
    }

    // 存活池与全自动流水线状态
    const aliveBadge = $('aliveBadge');
    if (aliveBadge) aliveBadge.textContent = s.aliveCount ?? 0;
    const aliveTotalChip = $('aliveTotalChip');
    if (aliveTotalChip) aliveTotalChip.textContent = `${s.aliveCount ?? 0} 个存活`;

    if (s.pipelineRunning !== state.pipelineRunning) {
      state.pipelineRunning = s.pipelineRunning;
      const btnStart = $('btnStartPipeline');
      const btnCancel = $('btnCancelPipeline');
      if (btnStart) btnStart.disabled = s.pipelineRunning;
      if (btnCancel) btnCancel.style.display = s.pipelineRunning ? '' : 'none';
      if (!s.pipelineRunning && state.view === 'alive') {
        loadAliveNodes();
      }
    }

    const pWrap = $('pipelineProgressWrap');
    if (pWrap) {
      if (s.pipelineRunning) {
        pWrap.classList.add('active');
        $('pipelineProgressBar').style.width = '100%';
        $('pipelineProgressText').textContent = s.pipelineMessage || '流水线运行中…';
      } else {
        pWrap.classList.remove('active');
      }
    }

    // 内核状态
    const chip = $('kernelChip');
    if (s.kernelInstalled) {
      chip.textContent = `内核 ${s.version} · ${s.platform}`;
      chip.className = 'status-chip ok';
    } else {
      chip.textContent = '内核未安装';
      chip.className = 'status-chip warn';
    }
  } catch (e) {
    // 服务重启中，静默
  }
}

setInterval(pollState, 1200);

// ══════════════════ 日志（SSE） ══════════════════

function openLog() { $('logDrawer').classList.add('open'); }

$('logDrawer').querySelector('.log-head').addEventListener('click', (e) => {
  if (e.target.closest('.btn')) return;
  $('logDrawer').classList.toggle('open');
});

$('btnLogToggle').addEventListener('click', (e) => {
  e.stopPropagation();
  const d = $('logDrawer');
  d.classList.toggle('open');
  $('btnLogToggle').textContent = d.classList.contains('open') ? '▾' : '▴';
});

$('btnLogClear').addEventListener('click', async (e) => {
  e.stopPropagation();
  await api('/log/clear', { method: 'POST' });
  $('logBody').innerHTML = '';
  state.logCount = 0;
  $('logCount').textContent = '';
});

function appendLog(l) {
  const body = $('logBody');
  const atBottom = body.scrollTop + body.clientHeight >= body.scrollHeight - 24;
  const span = document.createElement('div');
  if (l.level && l.level !== 'info') span.className = l.level;
  span.textContent = `${l.at}  ${l.text}`;
  body.appendChild(span);
  state.logCount++;
  $('logCount').textContent = state.logCount + ' 行';
  while (body.childElementCount > 2000) body.removeChild(body.firstChild);
  if (atBottom) body.scrollTop = body.scrollHeight;
}

function connectLog() {
  const es = new EventSource('/api/log');
  es.onmessage = (ev) => {
    try { appendLog(JSON.parse(ev.data)); } catch {}
  };
  es.onerror = () => {
    // 断线重连由 EventSource 自己处理；服务重启时会短暂失败
  };
}

// ══════════════════ 导出 / 合并 ══════════════════

$('btnExport').addEventListener('click', async () => {
  try {
    const text = await api('/export/links');
    showModal('订阅链接清单', '每条链接后面标注了文件名、类型与验证到的节点数。', text, 'links.txt');
  } catch (e) { toast(e.message, 'error'); }
});

$('btnMerge').addEventListener('click', async () => {
  const btn = $('btnMerge');
  btn.disabled = true;
  btn.textContent = '合并中…';
  openLog();
  try {
    const r = await api('/merge', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({}),
    });
    showModal('合并订阅（Base64）',
      `原始 ${r.total} 节点 → 去重后 ${r.unique} 个。可直接粘贴到 V2RayN / v2rayNG / Shadowrocket。`,
      r.b64, 'merged.b64');
  } catch (e) {
    toast('合并失败: ' + e.message, 'error');
  } finally {
    btn.disabled = false;
    btn.textContent = '⚡ 合并订阅';
  }
});

// ══════════════════ 弹窗 ══════════════════

let modalFileName = 'export.txt';

function showModal(title, hint, text, fileName) {
  $('modalTitle').textContent = title;
  $('modalHint').textContent = hint || '';
  $('modalText').value = text;
  modalFileName = fileName || 'export.txt';
  $('modalBackdrop').classList.add('open');
}

$('btnModalClose').addEventListener('click', () =>
  $('modalBackdrop').classList.remove('open'));
$('modalBackdrop').addEventListener('click', (e) => {
  if (e.target === $('modalBackdrop')) $('modalBackdrop').classList.remove('open');
});
$('btnModalCopy').addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText($('modalText').value);
    toast('已复制到剪贴板', 'success');
  } catch {
    $('modalText').select();
    toast('请按 Ctrl/Cmd+C 复制');
  }
});
$('btnModalDownload').addEventListener('click', () => {
  const blob = new Blob([$('modalText').value], { type: 'text/plain;charset=utf-8' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = modalFileName;
  a.click();
  URL.revokeObjectURL(a.href);
});

// ══════════════════ 收藏 ══════════════════

async function toggleFavorite(fullName) {
  const fav = isFavorite(fullName);
  try {
    if (fav) {
      await api('/favorites?fullName=' + encodeURIComponent(fullName), { method: 'DELETE' });
      toast('已取消收藏');
    } else {
      await api('/favorites', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ fullName }),
      });
      toast('已收藏 ★', 'success');
    }
    await loadFavorites();
    renderRepos();
  } catch (e) { toast(e.message, 'error'); }
}

async function loadFavorites() {
  try {
    state.favorites = await api('/favorites');
    $('favCount').textContent = state.favorites.length;
    const body = $('favBody');
    $('favEmpty').classList.toggle('show', state.favorites.length === 0);
    body.innerHTML = state.favorites.map(r => `
      <tr data-name="${escapeHtml(r.fullName)}">
        <td><span class="repo-name">${escapeHtml(r.fullName)}</span>
          ${r.description ? `<span class="repo-desc">${escapeHtml(r.description)}</span>` : ''}</td>
        <td class="num">${r.stars}</td>
        <td>${statusPill(r)}</td>
        <td class="num">${r.totalNodes || '—'}</td>
        <td class="num">${r.links.length}</td>
        <td><button class="star-btn on" data-fav="${escapeHtml(r.fullName)}" title="取消收藏">★</button></td>
      </tr>`).join('');

    body.querySelectorAll('.star-btn').forEach(btn => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        toggleFavorite(btn.dataset.fav);
      });
    });
    body.querySelectorAll('tr').forEach(tr => {
      tr.addEventListener('click', (e) => {
        if (e.target.closest('.star-btn')) return;
        selectRepo(tr.dataset.name);
      });
    });
  } catch (e) { /* 静默 */ }
}

// ══════════════════ 测速（三步：加载清单 → 勾选 → 只测选中）══════════════════

// ── 第一步：加载清单 ──

$('btnLoadCatalog').addEventListener('click', async () => {
  if (state.loadingCatalog) {
    await api('/catalog/cancel', { method: 'POST' });
    return;
  }
  try {
    const r = await api('/catalog/load', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        urls: $('catUrls').value,
        includeResults: $('catResults').checked,
        includeFavorites: $('catFavorites').checked,
      }),
    });
    toast(`正在加载 ${r.sources} 个订阅来源…`);
    openLog();
  } catch (e) {
    toast(e.message, 'error');
  }
});

function setLoadingCatalog(on) {
  state.loadingCatalog = on;
  const btn = $('btnLoadCatalog');
  btn.classList.toggle('running', on);
  btn.querySelector('.ico').textContent = on ? '■' : '↓';
  $('btnLoadText').textContent = on ? '取消加载' : '加载节点清单';
}

async function loadCatalog() {
  // 关键词合成：手动输入 + 选中的地区预置 pattern，OR-join 进同一个正则。
  // 地区 chip 是多选：每个选中预置的 Pattern 包成 (?:...)，用 | 连接。
  // 手动输入若是正则模式，原样并入；若是文本模式，转义元字符后并入。
  const manual = $('catFilter').value.trim();
  const isRegex = $('catRegex').checked;
  const manualPart = manual
    ? (isRegex ? manual : manual.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
    : '';
  const regionParts = [...state.selRegions].map(name => {
    const p = state.regionPresets.find(r => r.name === name);
    return p ? `(?:${p.pattern})` : null;
  }).filter(Boolean);
  const keyword = [manualPart, ...regionParts].filter(Boolean).join('|');

  const params = new URLSearchParams({
    keyword,
    protocols: [...state.selProtocols].join(','),
    repos: [...state.selRepos].join(','),
  });

  try {
    const d = await api('/catalog?' + params);
    state.catalog = d.nodes || [];
    state.catalogTotal = d.total || 0;
    state.facets = { protocols: d.protocols || [], repos: d.repos || [] };
    if (d.loadedAt) { state.catalogLoaded = true; state.catalogLoadedAt = d.loadedAt; }
    renderFacetChips();
    renderRegionChips();
    renderCatalog();
    // 正则非法时后端退化为文本匹配，界面要说明，否则用户以为筛选坏了
    const hint = $('regexHint');
    if (d.matchError) {
      hint.textContent = '正则有误，已按文本匹配';
      hint.className = 'regex-hint warn';
    } else if (d.matchMode === 'regex') {
      hint.textContent = '正则';
      hint.className = 'regex-hint ok';
    } else {
      hint.textContent = '';
      hint.className = 'regex-hint';
    }
  } catch (e) { /* 静默 */ }
}

// ── 多选筛选：协议与来源 ──

// 流媒体检测平台候选（与内核 config.yaml 的 platforms 一致）
const ALL_PLATFORMS = ['iprisk','tiktok','youtube','netflix','disney','openai','gemini','claude','spotify'];

function renderPlatformChips() {
  const host = $('platformChips');
  host.innerHTML = ALL_PLATFORMS.map(p => `
    <button class="chip ${state.selPlatforms.has(p) ? 'on' : ''}" data-v="${escapeHtml(p)}">
      ${escapeHtml(p)}
    </button>`).join('');
  host.querySelectorAll('.chip').forEach(btn => {
    btn.addEventListener('click', () => {
      const v = btn.dataset.v;
      if (state.selPlatforms.has(v)) state.selPlatforms.delete(v); else state.selPlatforms.add(v);
      btn.classList.toggle('on');
    });
  });
}

function renderFacetChips() {
  const build = (host, items, selSet, labelOf) => {
    host.innerHTML = items.map(v => `
      <button class="chip ${selSet.has(v) ? 'on' : ''}" data-v="${escapeHtml(v)}">
        ${escapeHtml(labelOf ? labelOf(v) : v)}
      </button>`).join('');
    host.querySelectorAll('.chip').forEach(btn => {
      btn.addEventListener('click', () => {
        const v = btn.dataset.v;
        if (selSet.has(v)) selSet.delete(v); else selSet.add(v);
        btn.classList.toggle('on');
        loadCatalog();
      });
    });
  };

  build($('protocolChips'), state.facets.protocols, state.selProtocols);
  build($('repoChips'), state.facets.repos, state.selRepos, r => r.split('/').pop() || r);
}

// ── 已保存订阅 chip（多选，勾选后与手动输入框的 URL 合并）──

function renderSubUrlChips() {
  const host = $('subUrlChips');
  const items = state.subUrls;
  $('catSubUrlChips').style.display = items.length ? 'flex' : 'none';
  host.innerHTML = items.map(u => `
    <button class="chip ${state.selSubUrls.has(u.url) ? 'on' : ''}" data-url="${escapeHtml(u.url)}" title="${escapeHtml(u.url)}">
      ${escapeHtml(u.name || u.url)}
    </button>`).join('');
  host.querySelectorAll('.chip').forEach(btn => {
    btn.addEventListener('click', () => {
      const url = btn.dataset.url;
      if (state.selSubUrls.has(url)) state.selSubUrls.delete(url);
      else state.selSubUrls.add(url);
      btn.classList.toggle('on');
      // 把勾选的 URL 同步进文本框，让 btnLoadCatalog 一起发
      syncSubUrlsToTextarea();
    });
  });
}

function syncSubUrlsToTextarea() {
  const ta = $('catUrls');
  const manual = ta.value.split('\n').map(s => s.trim()).filter(s => s && !state.subUrls.some(u => u.url === s));
  const selected = [...state.selSubUrls];
  ta.value = [...manual, ...selected].join('\n');
}

// ── 设置页：订阅编辑列表 ──

function renderSubUrlEditList() {
  const list = $('subUrlEditList');
  if (!list) return;
  list.innerHTML = '';
  const addRow = (name = '', url = '') => {
    const row = document.createElement('div');
    row.className = 'preset-edit-row';
    row.innerHTML = `
      <input type="text" class="preset-name" placeholder="名称（如：Pawdroid）" value="${escapeHtml(name)}">
      <input type="text" class="preset-pattern" placeholder="https://raw.githubusercontent.com/..." value="${escapeHtml(url)}">
      <button class="btn tiny preset-del">✕</button>`;
    row.querySelector('.preset-del').addEventListener('click', () => row.remove());
    list.appendChild(row);
  };
  state.subUrls.forEach(u => addRow(u.name, u.url));
  if (list.children.length === 0) addRow();
  $('btnAddSubUrl').onclick = () => addRow();
}

$('btnSaveSubUrls').addEventListener('click', async () => {
  const rows = [...$('subUrlEditList').querySelectorAll('.preset-edit-row')];
  const urls = rows.map(r => ({
    name: r.querySelector('.preset-name').value.trim(),
    url: r.querySelector('.preset-pattern').value.trim(),
  })).filter(u => u.url);
  try {
    await api('/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ subUrls: urls }),
    });
    state.subUrls = urls;
    renderSubUrlChips();
    toast('订阅清单已保存', 'success');
  } catch (e) { toast(e.message, 'error'); }
});

// ── 多选筛选：地区预置 ──

function renderRegionChips() {
  const host = $('regionChips');
  const items = state.regionPresets;
  $('catRegionChips').style.display = items.length ? 'flex' : 'none';
  host.innerHTML = items.map(p => `
    <button class="chip ${state.selRegions.has(p.name) ? 'on' : ''}" data-v="${escapeHtml(p.name)}" title="${escapeHtml(p.pattern)}">
      ${escapeHtml(p.name)}
    </button>`).join('');
  host.querySelectorAll('.chip').forEach(btn => {
    btn.addEventListener('click', () => {
      const v = btn.dataset.v;
      if (state.selRegions.has(v)) state.selRegions.delete(v); else state.selRegions.add(v);
      btn.classList.toggle('on');
      loadCatalog();
    });
  });
}

// ── 地区预置管理弹窗 ──

function openRegionManager() {
  const list = $('regionEditList');
  list.innerHTML = '';
  const addRow = (name = '', pattern = '') => {
    const row = document.createElement('div');
    row.className = 'preset-edit-row';
    row.innerHTML = `
      <input type="text" class="preset-name" placeholder="名称（如：香港）" value="${escapeHtml(name)}">
      <input type="text" class="preset-pattern" placeholder="正则（如：香港|HK|Hong Kong）" value="${escapeHtml(pattern)}">
      <button class="btn tiny preset-del">✕</button>`;
    row.querySelector('.preset-del').addEventListener('click', () => row.remove());
    list.appendChild(row);
  };
  state.regionPresets.forEach(p => addRow(p.name, p.pattern));
  if (list.children.length === 0) addRow();
  $('btnAddRegion').onclick = () => addRow();
  $('regionModalBackdrop').classList.add('open');
}

$('btnManageRegions').addEventListener('click', openRegionManager);
$('btnRegionModalClose').addEventListener('click', () =>
  $('regionModalBackdrop').classList.remove('open'));
$('btnRegionModalCancel').addEventListener('click', () =>
  $('regionModalBackdrop').classList.remove('open'));
$('regionModalBackdrop').addEventListener('click', (e) => {
  if (e.target === $('regionModalBackdrop')) $('regionModalBackdrop').classList.remove('open');
});
$('btnRegionModalSave').addEventListener('click', async () => {
  const rows = [...$('regionEditList').querySelectorAll('.preset-edit-row')];
  const presets = rows.map(r => ({
    name: r.querySelector('.preset-name').value.trim(),
    pattern: r.querySelector('.preset-pattern').value.trim(),
  })).filter(p => p.name && p.pattern);
  try {
    await api('/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ regionPresets: presets }),
    });
    state.regionPresets = presets;
    // 之前选中的地区若已被删，清掉
    const names = new Set(presets.map(p => p.name));
    for (const n of [...state.selRegions]) if (!names.has(n)) state.selRegions.delete(n);
    renderRegionChips();
    loadCatalog();
    $('regionModalBackdrop').classList.remove('open');
    toast('地区预置已保存', 'success');
  } catch (e) { toast(e.message, 'error'); }
});

function renderCatalog() {
  // 可见性取决于「清单是否加载过」，而不是「筛选后还剩几条」——
  // 用后者的话，筛选条件没匹配到任何节点时筛选条会连同输入框一起
  // 消失，用户想改条件都没法改，只能刷新页面。
  const loaded = state.catalogLoaded;
  const has = state.catalog.length > 0;
  const show = loaded ? '' : 'none';
  $('catBar').style.display = show;
  $('catFilters').style.display = loaded ? 'flex' : 'none';
  $('catProtocolChips').style.display = loaded && state.facets.protocols.length ? 'flex' : 'none';
  $('catRepoChips').style.display = loaded && state.facets.repos.length ? 'flex' : 'none';
  $('catRegionChips').style.display = loaded && state.regionPresets.length ? 'flex' : 'none';
  $('catWrap').style.display = show;
  $('catCount').textContent = loaded && !has
    ? `0 / ${state.catalogTotal || 0} 个节点`
    : `${state.catalog.length} 个节点`;

  // 筛选无结果时给一行提示，否则表格空着看起来像加载失败
  if (loaded && !has) {
    $('catBody').innerHTML = `<tr class="cat-empty"><td colspan="6">
      没有匹配的节点 —— 试试放宽筛选条件，或点「清空」重置。
    </td></tr>`;
    updateTestButton();
    return;
  }

  const body = $('catBody');
  // 大列表（几千行）一次 innerHTML 会卡住主线程。
  // 分批插入：每批 200 行，让浏览器有机会响应输入。
  // 用 DocumentFragment + requestAnimationFrame 调度，避免每次
  // appendChild 触发重排。title 用 data 属性延后绑定省 listener。
  const BATCH = 200;
  const total = state.catalog.length;
  let rendered = 0;
  // 先清空，否则筛选后行数变少会残留旧行
  body.innerHTML = '';

  const renderBatch = () => {
    const frag = document.createDocumentFragment();
    const end = Math.min(rendered + BATCH, total);
    for (let i = rendered; i < end; i++) {
      const n = state.catalog[i];
      const tr = document.createElement('tr');
      tr.dataset.uri = n.uri || '';
      tr.dataset.idx = String(i);
      // innerHTML 单行比逐个 createElement 快；分批保证不卡
      tr.innerHTML =
        `<td class="col-chk"><input type="checkbox" data-idx="${i}"${state.selectedUris.has(n.uri) ? ' checked' : ''}></td>` +
        `<td class="col-name" title="${escapeHtml(n.name || '')}">${escapeHtml(n.name || '—')}</td>` +
        `<td class="col-proto"><span class="proto-tag ${(n.protocol || '').toLowerCase()}">${escapeHtml(n.protocol || '—')}</span></td>` +
        `<td class="col-server" title="${escapeHtml(n.server)}"><span style="font-family:var(--mono);font-size:11px">${escapeHtml(n.server)}</span></td>` +
        `<td class="num col-port">${escapeHtml(n.port)}</td>` +
        `<td class="col-src" title="${escapeHtml(n.repo || n.sourceName || '')}">${escapeHtml(n.repo || n.sourceName || '—')}</td>`;
      frag.appendChild(tr);
    }
    body.appendChild(frag);
    rendered = end;

    if (rendered < total) {
      requestAnimationFrame(renderBatch);
    } else {
      // 全部渲染完再绑事件（事件委托更省内存，但复选框需逐个初始状态）
      bindCatCheckboxes();
    }
  };
  renderBatch();
}

// 复选框用事件委托：只在 tbody 上绑一个 listener，几千行也不增内存
let _catChkBound = false;
function bindCatCheckboxes() {
  const body = $('catBody');
  if (!_catChkBound) {
    body.addEventListener('change', (e) => {
      const chk = e.target.closest('input[type=checkbox]');
      if (!chk) return;
      const n = state.catalog[+chk.dataset.idx];
      if (!n) return;
      if (chk.checked) state.selectedUris.add(n.uri);
      else state.selectedUris.delete(n.uri);
      updateTestButton();
    });
    _catChkBound = true;
  }
  updateTestButton();
}

function updateTestButton() {
  const n = state.selectedUris.size;
  $('btnTestSelected').disabled = n === 0 || state.speedTesting;
  $('btnTestText').textContent = n > 0 ? `测速选中 ${n} 个` : '测速选中节点';
}

// 输入法（拼音等）组合期间不触发筛选：组合中的每个按键都会发 input
// 事件，逐次请求既浪费也会让输入卡顿。等 compositionend 再跑一次。
let composing = false;
$('catFilter').addEventListener('compositionstart', () => { composing = true; });
$('catFilter').addEventListener('compositionend', () => {
  composing = false;
  clearTimeout(window._catTimer);
  window._catTimer = setTimeout(loadCatalog, 300);
});
$('catFilter').addEventListener('input', (e) => {
  if (composing || e.isComposing) return;
  clearTimeout(window._catTimer);
  window._catTimer = setTimeout(loadCatalog, 450);
});
$('catRegex').addEventListener('change', loadCatalog);

$('btnSelectAll').addEventListener('click', () => {
  for (const n of state.catalog) state.selectedUris.add(n.uri);
  renderCatalog();
});
$('btnSelectNone').addEventListener('click', () => {
  state.selectedUris.clear();
  renderCatalog();
});
$('btnSelectFast').addEventListener('click', () => {
  // 「前 20 个」按清单当前顺序取 —— 用户通常先筛协议再选
  state.selectedUris.clear();
  for (const n of state.catalog.slice(0, 20)) state.selectedUris.add(n.uri);
  renderCatalog();
  toast('已选中前 20 个');
});
$('chkAll').addEventListener('change', (e) => {
  if (e.target.checked) for (const n of state.catalog) state.selectedUris.add(n.uri);
  else state.selectedUris.clear();
  renderCatalog();
});

// ── 第二步：只测选中的节点 ──

$('btnTestSelected').addEventListener('click', async () => {
  if (state.speedTesting) {
    await api('/speedtest/cancel', { method: 'POST' });
    return;
  }
  const uris = [...state.selectedUris];
  if (uris.length === 0) { toast('请先勾选要测的节点'); return; }
  if (uris.length > 200 && !confirm(`将测速 ${uris.length} 个节点，可能耗时较久。继续？`)) return;

  try {
    await api('/speedtest/selected', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ uris, withHistory: false, keepRounds: 0 }),
    });
    toast(`开始测速 ${uris.length} 个节点`, 'success');
    openLog();
  } catch (e) {
    toast(e.message, 'error');
  }
});

function setSpeedTesting(on) {
  state.speedTesting = on;
  const btn = $('btnTestSelected');
  btn.classList.toggle('running', on);
  btn.querySelector('.ico').textContent = on ? '■' : '▶';
  updateTestButton();
}

async function loadNodes() {
  try {
    state.nodes = await api('/speedtest/nodes');
    renderNodes();
  } catch {}
}

function renderNodes() {
  const body = $('spBody');
  const hasNodes = state.nodes.length > 0;
  $('spEmpty').classList.toggle('show', !hasNodes);
  $('spBar').style.display = hasNodes ? '' : 'none';
  const passedCount = state.nodes.filter(n => n.passed).length;
  $('spPassedCount').textContent = `${passedCount} 个通过`;
  $('spBody').innerHTML = state.nodes.map(n => {
    const maxSpeed = Math.max(...state.nodes.map(x => x.speed), 1);
    const barW = n.speed > 0 ? Math.round(n.speed / maxSpeed * 40) : 0;
    return `
    <tr>
      <td class="col-name" title="${escapeHtml(n.displayName || n.name || n.baseName || '')}">${escapeHtml(n.displayName || n.name || n.baseName || '—')}</td>
      <td class="col-proto"><span class="proto-tag ${(n.type || '').toLowerCase()}">${escapeHtml(n.type || '—')}</span></td>
      <td class="col-server" title="${escapeHtml(n.server)}:${escapeHtml(n.port)}"><span style="font-family:var(--mono);font-size:11px">${escapeHtml(n.server)}:${escapeHtml(n.port)}</span></td>
      <td class="num col-speed">
        ${n.speed > 0 ? `<span class="speed-bar" style="width:${barW}px"></span>${fmtSpeed(n.speed)}` : '—'}
      </td>
      <td class="col-country">${escapeHtml(n.country || '—')}</td>
      <td class="col-city" title="${escapeHtml([n.ipInfoCity, n.ipInfoRegion, n.ipInfoOrg, n.ipInfoAsn].filter(Boolean).join(' / '))}">${
        n.ipInfoCity || n.ipInfoOrg
          ? `<span style="font-size:11px">${escapeHtml(n.ipInfoCity || '—')}${n.isResidential ? ' <span class="pill live">家宽</span>' : ''}${n.ipInfoOrg ? '<br><span style="color:var(--ink-mid);font-size:10px">' + escapeHtml(n.ipInfoOrg) + '</span>' : ''}</span>`
          : '—'
      }</td>
      <td class="col-ip">${escapeHtml(n.ipRisk || '—')}</td>
      <td class="col-media">${(n.media || []).map(m => `<span class="pill live">${escapeHtml(m.short_)}</span>`).join(' ') || '—'}</td>
      <td class="col-src" title="${escapeHtml(n.subTag || '')}">${escapeHtml(n.subTag || '—')}</td>
    </tr>`;
  }).join('');

  const passed = state.nodes.filter(n => n.passed).length;
  const speeds = state.nodes.filter(n => n.speed > 0).map(n => n.speed);
  const avg = speeds.length ? Math.round(speeds.reduce((a, b) => a + b, 0) / speeds.length) : 0;
  const best = speeds.length ? Math.max(...speeds) : 0;
  $('spStats').classList.toggle('show', state.nodes.length > 0);
  $('spStats').innerHTML = state.nodes.length === 0 ? '' : `
    <div class="stat"><b>测速节点</b><span>${state.nodes.length}</span></div>
    <div class="stat"><b>通过</b><span class="live">${passed}</span></div>
    <div class="stat"><b>平均速度</b><span>${fmtSpeed(avg)}</span></div>
    <div class="stat"><b>最快</b><span>${fmtSpeed(best)}</span></div>
  `;
}

// ══════════════════ 设置 ══════════════════

async function loadSettings() {
  try {
    const s = await api('/settings');
    $('tokenState').textContent = s.hasToken
      ? '当前已设置令牌。填入新值可覆盖，留空则保持不变。'
      : '当前未设置令牌。';
    $('setRepoCount').value = s.repoCount;
    $('setInactive').value = s.inactiveDays;
    $('setCommits').value = s.minCommitsPerDay;
    $('setAge').value = s.minActiveDays;
    $('setAutoClean').checked = s.autoClean;
    $('kernelInfo').textContent =
      `内核路径: ${s.kernelPath}`;
    $('setSubConverter').value = s.subConverterUrl || '';
    $('setSpeedTimeout').value = s.speedTimeout ?? '';
    $('setDownloadMb').value = s.downloadMb ?? '';
    $('setMinSpeed').value = s.minSpeed ?? '';
    $('setConcurrent').value = s.concurrent ?? '';
    $('setSpeedConcurrent').value = s.speedConcurrent ?? '';
    $('setSpeedTestUrl').value = s.speedTestUrl || '';
    $('setIpInfoToken').value = s.ipInfoToken || '';
    $('setMediaCheck').checked = s.mediaCheck ?? false;
    $('setMediaCheckTimeout').value = s.mediaCheckTimeout ?? '';
    state.selPlatforms = new Set(s.platforms || []);
    renderPlatformChips();
    state.regionPresets = s.regionPresets || [];
    renderRegionChips();
    state.subUrls = s.subUrls || [];
    renderSubUrlChips();
    renderSubUrlEditList();
  } catch (e) { toast(e.message, 'error'); }
}

$('btnSaveToken').addEventListener('click', async () => {
  const v = $('setToken').value.trim();
  if (!v) { toast('请先填入令牌'); return; }
  try {
    await api('/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: v }),
    });
    $('setToken').value = '';
    toast('令牌已保存', 'success');
    loadSettings();
  } catch (e) { toast(e.message, 'error'); }
});

$('btnSaveSettings').addEventListener('click', async () => {
  try {
    await api('/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        repoCount: +$('setRepoCount').value,
        inactiveDays: +$('setInactive').value,
        minCommitsPerDay: +$('setCommits').value,
        minActiveDays: +$('setAge').value,
        autoClean: $('setAutoClean').checked,
      }),
    });
    toast('设置已保存', 'success');
  } catch (e) { toast(e.message, 'error'); }
});

$('btnInstallKernel').addEventListener('click', async () => {
  try {
    await api('/kernel/install', { method: 'POST' });
    toast('已开始检查内核更新，进度见日志');
    openLog();
  } catch (e) { toast(e.message, 'error'); }
});

$('btnSaveSubConverter').addEventListener('click', async () => {
  const v = $('setSubConverter').value.trim();
  try {
    await api('/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ subConverterUrl: v }),
    });
    toast(v ? '转换后端已保存' : '已清空转换后端（直连订阅）', 'success');
    loadSettings();
  } catch (e) { toast(e.message, 'error'); }
});

$('btnSaveIpInfoToken').addEventListener('click', async () => {
  const v = $('setIpInfoToken').value.trim();
  try {
    await api('/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ipInfoToken: v }),
    });
    toast(v ? 'IP 信息 token 已保存' : '已清空 IP 信息 token（不查）', 'success');
    loadSettings();
  } catch (e) { toast(e.message, 'error'); }
});

$('btnSaveSpeed').addEventListener('click', async () => {
  const num = (id) => { const v = $(id).value.trim(); return v === '' ? null : +v; };
  try {
    await api('/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        speedTimeout: num('setSpeedTimeout'),
        downloadMb: num('setDownloadMb'),
        minSpeed: num('setMinSpeed'),
        concurrent: num('setConcurrent'),
        speedConcurrent: num('setSpeedConcurrent'),
        speedTestUrl: $('setSpeedTestUrl').value.trim(),
        mediaCheck: $('setMediaCheck').checked,
        mediaCheckTimeout: num('setMediaCheckTimeout'),
        platforms: [...state.selPlatforms],
      }),
    });
    toast('测速参数已保存', 'success');
    loadSettings();
  } catch (e) { toast(e.message, 'error'); }
});

// ── 导出测速结果 ──

$('btnExportPassed').addEventListener('click', () => {
  // 直接触发浏览器下载，YAML 几千行预览没意义
  window.open('/api/export/passed?format=yaml', '_blank');
  toast('已导出 Clash YAML（passed.yaml）', 'success');
});

// ══════════════════ 全球节点分布图 ══════════════════

// 异步加载内嵌的 world.svg，注入到 #worldMap，加载完调 renderWorldMap
async function loadWorldMapSvg() {
  try {
    const resp = await fetch('/world.svg');
    const svgText = await resp.text();
    const host = $('worldMap');
    // 用 DOMParser 解析而非 innerHTML：1MB 的 SVG 直接 innerHTML
    // 在某些浏览器（尤其移动端 Safari）会超时或被截断，DOMParser 更稳。
    const doc = new DOMParser().parseFromString(svgText, 'image/svg+xml');
    const svg = doc.querySelector('svg');
    if (svg) {
      svg.removeAttribute('width');
      svg.removeAttribute('height');
      host.innerHTML = '';
      host.appendChild(svg);
      state.worldMapSvg = svg;
      renderWorldMap();
    }
  } catch (e) { /* 静默，下次 pollState 会重试 */ }
}

function getMapThemeColors() {
  const isDark = document.documentElement.dataset.theme === 'dark' ||
    (!document.documentElement.dataset.theme && window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
  return {
    rect: isDark ? '#080c14' : '#e2e8f0',
    land: isDark ? '#1e293b' : '#cbd5e1',
    stroke: isDark ? '#0f172a' : '#f8fafc',
    highlight: isDark ? 'rgba(59, 130, 246, 0.45)' : 'rgba(37, 99, 235, 0.35)',
    pinStroke: isDark ? '#0f172a' : '#ffffff',
    text: '#ffffff',
  };
}

function renderWorldMap() {
  const svg = state.worldMapSvg;
  if (!svg) return;

  const colors = getMapThemeColors();
  const bgRect = svg.querySelector('rect');
  if (bgRect) bgRect.setAttribute('fill', colors.rect);

  // 先清掉上一轮的定位图标
  svg.querySelectorAll('g.pin, circle.bubble, .country-overlay').forEach(el => el.remove());

  const passed = state.nodes.filter(n => n.passed);
  $('worldMapCount').textContent = `${passed.length} 个节点`;
  const empty = $('worldMapEmpty');
  if (passed.length === 0) {
    empty.classList.remove('hide');
    // 重置所有国家为默认色
    svg.querySelectorAll('path').forEach(p => {
      p.setAttribute('fill', colors.land);
      p.setAttribute('stroke', colors.stroke);
    });
    return;
  }
  empty.classList.add('hide');

  // 按国家分组统计节点数
  const byCountry = {};
  passed.forEach(n => {
    const c = (n.country || '').toLowerCase();
    if (!c) return;
    if (!byCountry[c]) byCountry[c] = { count: 0, nodes: [] };
    byCountry[c].count++;
    byCountry[c].nodes.push(n);
  });
  const maxCount = Math.max(1, ...Object.values(byCountry).map(v => v.count));

  // 国家轮廓保持默认色（不按节点数着色，只靠气泡标注位置）
  svg.querySelectorAll('path').forEach(p => {
    p.setAttribute('fill', colors.land);
    p.setAttribute('stroke', colors.stroke);
  });

  // 城市气泡（按 ipInfoLoc 经纬度聚合）
  const viewBox = svg.getAttribute('viewBox') || '0 0 2000 1000';
  const [vx, vy, vw, vh] = viewBox.split(/\s+/).map(Number);
  const project = (lng, lat) => [
    (lng + 180) * (vw / 360),
    (90 - lat) * (vh / 180),
  ];

  const byLoc = {};
  passed.forEach(n => {
    if (!n.ipInfoLoc) return;
    const [city, ...rest] = [n.ipInfoCity || '', n.country || ''];
    const key = n.ipInfoLoc;
    if (!byLoc[key]) byLoc[key] = { loc: key, city: n.ipInfoCity || '?', country: n.country || '?', nodes: [] };
    byLoc[key].nodes.push(n);
  });

  const maxCity = Math.max(1, ...Object.values(byLoc).map(v => v.nodes.length));

  // tooltip 元素
  let tooltip = document.querySelector('.world-map-tooltip');
  if (!tooltip) {
    tooltip = document.createElement('div');
    tooltip.className = 'world-map-tooltip';
    document.body.appendChild(tooltip);
  }

  Object.values(byLoc).forEach(v => {
    // ipinfo.io 的 loc 格式是 "lat,lng"（纬度在前，如 "34.0522,-118.2437"）
    const [latStr, lngStr] = v.loc.split(',');
    const lat = parseFloat(latStr);
    const lng = parseFloat(lngStr);
    if (isNaN(lng) || isNaN(lat)) return;
    const [x, y] = project(lng, lat);
    const count = v.nodes.length;
    const r = 10 + Math.min(12, count * 1.5);  // 圆圈半径，节点越多越大
    const SVGNS = 'http://www.w3.org/2000/svg';

    // 定位图标组：尖针 + 圆圈 + 数字
    const cy = y - r - 12;
    const tipY = y;
    const g = document.createElementNS(SVGNS, 'g');
    g.setAttribute('class', 'pin');
    g.style.filter = 'drop-shadow(0 2px 4px rgba(0,0,0,.35))';
    g.style.cursor = 'pointer';

    const residential = v.nodes.some(n => n.isResidential);
    const pinColor = residential ? '#10b981' : '#3b82f6';
    const countryCodes = [...new Set(v.nodes.map(n => (n.country || '').toLowerCase()).filter(Boolean))];

    g.addEventListener('mouseenter', (e) => {
      // 高亮该节点所属国家
      const cnMappings = { hk: ['hk','tw','mo','cn'], tw: ['hk','tw','mo','cn'], mo: ['hk','tw','mo','cn'] };
      const cc = (v.country || '').toLowerCase();
      countryCodes.forEach(c => {
        const targets = cnMappings[c] || [c];
        targets.forEach(t => {
          const p = svg.querySelector(`#country-${t}`);
          if (p) p.setAttribute('fill', colors.highlight);
        });
      });
      const resLabel = resCount > 0 ? `（${resCount} 家宽）` : '';
      tooltip.innerHTML = `<b>${escapeHtml(v.city)}</b> · ${escapeHtml(v.country)}<br>${count} 节点${resLabel} · 平均 ${fmtSpeed(avgSpeed)}`;
      tooltip.classList.add('show');
    });
    g.addEventListener('mousemove', (e) => {
      tooltip.style.left = (e.clientX + 14) + 'px';
      tooltip.style.top = (e.clientY - 32) + 'px';
    });
    g.addEventListener('mouseleave', () => {
      // 恢复国家默认色
      const cnMappings = { hk: ['hk','tw','mo','cn'], tw: ['hk','tw','mo','cn'], mo: ['hk','tw','mo','cn'] };
      countryCodes.forEach(c => {
        const targets = cnMappings[c] || [c];
        targets.forEach(t => {
          const p = svg.querySelector(`#country-${t}`);
          if (p) p.setAttribute('fill', colors.land);
        });
      });
      tooltip.classList.remove('show');
    });

    // 尖针：从圆圈底部到地图点
    const needle = document.createElementNS(SVGNS, 'path');
    needle.setAttribute('d', `M${x},${tipY} L${(x - r * 0.4).toFixed(1)},${cy.toFixed(1)} L${(x + r * 0.4).toFixed(1)},${cy.toFixed(1)} Z`);
    needle.setAttribute('fill', pinColor);
    g.appendChild(needle);

    // 圆圈底
    const circle = document.createElementNS(SVGNS, 'circle');
    circle.setAttribute('cx', x.toFixed(1));
    circle.setAttribute('cy', cy.toFixed(1));
    circle.setAttribute('r', r.toFixed(1));
    circle.setAttribute('fill', pinColor);
    circle.setAttribute('stroke', colors.pinStroke);
    circle.setAttribute('stroke-width', '1.5');
    g.appendChild(circle);

    // 数字
    const text = document.createElementNS(SVGNS, 'text');
    text.setAttribute('x', x.toFixed(1));
    text.setAttribute('y', (cy + 4).toFixed(1));
    text.setAttribute('text-anchor', 'middle');
    text.setAttribute('fill', colors.text);
    text.setAttribute('font-size', Math.max(10, r).toFixed(0));
    text.setAttribute('font-family', 'ui-monospace, monospace');
    text.setAttribute('font-weight', '700');
    text.textContent = String(count);
    g.appendChild(text);

    const avgSpeed = Math.round(v.nodes.reduce((s, n) => s + (n.speed || 0), 0) / v.nodes.length);
    const resCount = v.nodes.filter(n => n.isResidential).length;
    svg.appendChild(g);
  });
}

// ══════════════════ 主题管理 ══════════════════

function initTheme() {
  const saved = localStorage.getItem('theme') || 'auto';
  applyTheme(saved);

  const btn = $('btnThemeToggle');
  if (btn) {
    btn.addEventListener('click', () => {
      const current = localStorage.getItem('theme') || 'auto';
      const next = current === 'auto' ? 'dark' : current === 'dark' ? 'light' : 'auto';
      localStorage.setItem('theme', next);
      applyTheme(next);
      const label = next === 'dark' ? '🌙 深色模式' : next === 'light' ? '☀️ 浅色模式' : '🖥️ 跟随系统';
      toast('主题模式: ' + label, 'info');
    });
  }

  if (window.matchMedia) {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
      if ((localStorage.getItem('theme') || 'auto') === 'auto') {
        applyTheme('auto');
      }
    });
  }
}

function applyTheme(theme) {
  const isDark = theme === 'dark' || (theme === 'auto' && window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
  if (theme === 'auto') {
    document.documentElement.removeAttribute('data-theme');
    document.documentElement.dataset.themeAuto = isDark ? 'dark' : 'light';
  } else {
    document.documentElement.dataset.theme = theme;
    delete document.documentElement.dataset.themeAuto;
  }
  const btn = $('btnThemeToggle');
  if (btn) {
    btn.innerHTML = `<span class="theme-icon">${theme === 'dark' ? '🌙' : theme === 'light' ? '☀️' : '🌓'}</span>`;
    btn.title = `当前主题: ${theme === 'dark' ? '深色' : theme === 'light' ? '浅色' : '跟随系统'} (点击切换)`;
  }
  if (state.worldMapSvg) {
    renderWorldMap();
  }
}

// ══════════════════ 存活池与全自动流水线 ══════════════════

async function loadAliveNodes() {
  try {
    const country = $('aliveCountrySel')?.value || '';
    const res = await api(`/alive/nodes${country ? '?country=' + encodeURIComponent(country) : ''}`);
    state.aliveNodes = res.nodes || [];
    state.aliveStats = res.stats || {};
    renderAliveNodes();
  } catch (e) {
    toast('加载存活池失败: ' + e.message, 'error');
  }
}

function renderAliveNodes() {
  const body = $('aliveBody');
  if (!body) return;
  const nodes = state.aliveNodes || [];
  const stats = state.aliveStats || {};

  $('aliveEmpty')?.classList.toggle('show', nodes.length === 0);
  if ($('aliveTotalChip')) $('aliveTotalChip').textContent = `${stats.totalCount ?? nodes.length} 个存活`;
  if ($('aliveStableChip')) $('aliveStableChip').textContent = `${stats.stableCount ?? 0} 个稳定`;
  if ($('aliveBadge')) $('aliveBadge').textContent = stats.totalCount ?? nodes.length;

  const sel = $('aliveCountrySel');
  if (sel && stats.countries && sel.options.length <= 1) {
    const curVal = sel.value;
    sel.innerHTML = '<option value="">全部国家/地区</option>' +
      stats.countries.map(c => `<option value="${escapeHtml(c.country)}">${escapeHtml(c.country)} (${c.count})</option>`).join('');
    sel.value = curVal;
  }

  body.innerHTML = nodes.map(n => `
    <tr>
      <td class="col-name" title="${escapeHtml(n.name)}">${escapeHtml(n.name || '—')}</td>
      <td class="col-proto"><span class="proto-tag ${(n.protocol || '').toLowerCase()}">${escapeHtml(n.protocol || '—')}</span></td>
      <td class="col-server" title="${escapeHtml(n.server)}:${escapeHtml(n.port)}"><span style="font-family:var(--mono);font-size:11px">${escapeHtml(n.server)}:${escapeHtml(n.port)}</span></td>
      <td class="col-country">${escapeHtml(n.country || '—')}</td>
      <td class="col-city">${escapeHtml(n.city || '—')}</td>
      <td class="num col-speed">${n.latencyMs > 0 ? n.latencyMs + ' ms' : '—'}</td>
      <td class="col-media">
        <span class="pill ${n.consecutiveAlive >= 2 ? 'live' : 'stale'}">${n.consecutiveAlive} 轮</span>
      </td>
      <td class="col-src">${relTime(n.lastAliveAt)}</td>
    </tr>
  `).join('');
}

async function startPipeline() {
  if (state.pipelineRunning) return;
  try {
    await api('/pipeline/start', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({})
    });
    toast('全自动流水线已启动', 'success');
    openLog();
  } catch (e) {
    toast('启动流水线失败: ' + e.message, 'error');
  }
}

async function cancelPipeline() {
  try {
    await api('/pipeline/cancel', { method: 'POST' });
    toast('已请求取消流水线', 'warn');
  } catch (e) {
    toast('取消失败: ' + e.message, 'error');
  }
}

async function speedTestAlivePool() {
  try {
    await api('/speedtest/alive', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ withHistory: false, keepRounds: 3 })
    });
    toast('已从存活池启动测速', 'success');
    document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.dataset.view === 'speedtest'));
    document.querySelectorAll('.view').forEach(v => v.classList.toggle('active', v.id === 'view-speedtest'));
    state.view = 'speedtest';
    openLog();
  } catch (e) {
    toast('启动测速失败: ' + e.message, 'error');
  }
}

async function probeAlivePool() {
  try {
    await api('/alive/probe', { method: 'POST' });
    toast('已启动存活池真实落地探测', 'success');
    openLog();
  } catch (e) {
    toast('启动探测失败: ' + e.message, 'error');
  }
}

function exportAlivePool() {
  window.open('/api/export/alive', '_blank');
}

// ══════════════════ 快捷键 ══════════════════

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    $('modalBackdrop').classList.remove('open');
    $('detail').classList.remove('open');
  }
  if ((e.ctrlKey || e.metaKey) && e.key === 'f') {
    e.preventDefault();
    $('filterText').focus();
  }
  if ((e.ctrlKey || e.metaKey) && e.key === 'l') {
    e.preventDefault();
    $('logDrawer').classList.toggle('open');
  }
});

// ══════════════════ 启动 ══════════════════

(async function init() {
  initTheme();
  await loadFavorites();
  await loadRepos();
  await loadNodes();
  loadWorldMapSvg();   // 异步加载，不阻塞首屏

  $('btnStartPipeline')?.addEventListener('click', startPipeline);
  $('btnCancelPipeline')?.addEventListener('click', cancelPipeline);
  $('btnSpeedTestAlive')?.addEventListener('click', speedTestAlivePool);
  $('btnSpeedTestAliveDirect')?.addEventListener('click', speedTestAlivePool);
  $('btnProbeAlive')?.addEventListener('click', probeAlivePool);
  $('btnExportAlive')?.addEventListener('click', exportAlivePool);
  $('btnRefreshAlive')?.addEventListener('click', loadAliveNodes);
  $('aliveCountrySel')?.addEventListener('change', loadAliveNodes);

  // 先拉一次设置，把地区预置填进 state.regionPresets，让首屏渲染时 chip 就在
  try {
    const s = await api('/settings');
    state.regionPresets = s.regionPresets || [];
  } catch {}
  // 清单可能从盘上回填过（容器重建后），首屏就要把它标成已加载，
  // 否则 catBar / catFilters / 地区 chip 行都因 catalogLoaded=false 而隐藏
  try {
    const s = await api('/state');
    if (s.catalogCount > 0 && s.catalogLoadedAt) {
      state.catalogLoaded = true;
      state.catalogLoadedAt = s.catalogLoadedAt;
    }
    if (s.aliveCount > 0) {
      $('aliveBadge').textContent = s.aliveCount;
    }
  } catch {}
  // 把清单 + chip 渲染出来（之前只有切到测速页才触发）
  try { await loadCatalog(); } catch {}
  connectLog();
  pollState();
})();
