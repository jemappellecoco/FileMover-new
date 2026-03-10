// history.js (frontend filter version)
// ✅ Only GET /history once (and on manual reload).
// ✅ Filter by: status / group(sourceStorage prefix) / keyword(userBit|fileName|note) / date(updateTime)
// ✅ Pagination done in frontend with "take" as pageSize.

const API_HISTORY = '/history';

export function initHistory(root) {
  root.innerHTML = `
    <div class="toolbar" style="display:flex;flex-wrap:wrap;gap:10px;align-items:center;">
      <label>狀態：
        <select id="selHistStatus">
          <option value="all">全部</option>
          <option value="success">成功</option>
          <option value="fail">失敗</option>
        </select>
      </label>

      <label>樓層：
        <select id="selHistGroup">
          <option value="all">全部</option>
          <option value="4F">4F</option>
          <option value="7F">7F</option>
        </select>
      </label>

      <label>顯示筆數：
        <input id="inpHistTake" type="number" min="10" step="10" value="180" style="width:90px;">
      </label>

      <label>搜尋檔名(UserBit)：
        <input id="inpHistSearch" type="text" placeholder="輸入關鍵字，例如 2101EC3F" style="width:220px;">
        <button id="btnHistSearch" type="button" style="margin-left:4px;">搜尋</button>
      </label>

      <label style="margin-left:6px;">開始：
        <span style="position:relative;display:inline-flex;align-items:center;gap:6px;">
          <input id="inpHistStartText" type="text" placeholder="YYYY/MM/DD" style="width:120px;">
          <button id="btnHistStartPick" type="button" title="選開始日期">📅</button>
          <input id="inpHistStartDate" type="date"
            style="position:absolute;left:0;top:calc(100% + 2px);width:1px;height:1px;opacity:0;z-index:9999;">
        </span>
      </label>

      <label>結束：
        <span style="position:relative;display:inline-flex;align-items:center;gap:6px;">
          <input id="inpHistEndText" type="text" placeholder="YYYY/MM/DD" style="width:120px;">
          <button id="btnHistEndPick" type="button" title="選結束日期">📅</button>
          <input id="inpHistEndDate" type="date"
            style="position:absolute;left:0;top:calc(100% + 2px);width:1px;height:1px;opacity:0;z-index:9999;">
        </span>
      </label>

      <button id="btnHistReload" type="button">重新整理</button>
      <span id="histRowCount" class="muted"></span>
    </div>

    <div style="margin:10px 0;display:flex;align-items:center;gap:10px;">
      <button id="btnHistPrev" type="button">上一頁</button>
      <span id="histPageInfo" class="muted"></span>
      <button id="btnHistNext" type="button">下一頁</button>
    </div>

    <table id="histTable">
      <thead>
        <tr>
          <th style="width:70px;">#</th>
          <th style="width:180px;">節目名稱</th>
          <th style="width:150px;">檔名(UserBit)</th>
          <th style="width:150px;">來源 Storage</th>
          <th style="width:150px;">目的 Storage</th>
          <th style="width:100px;">節點</th>
          <th style="width:170px;">UpdateTime</th>
          <th style="width:90px;">Action</th>
          <th style="width:160px;">Status</th>
        </tr>
      </thead>
      <tbody>
        <tr><td colspan="9" style="text-align:center;color:#999;">載入中…</td></tr>
      </tbody>
    </table>
  `;

  // ---- DOM ----
  const $selStatus = root.querySelector('#selHistStatus');
  const $selGroup  = root.querySelector('#selHistGroup');
  const $inpTake   = root.querySelector('#inpHistTake');
  const $inpSearch = root.querySelector('#inpHistSearch');
  const $btnSearch = root.querySelector('#btnHistSearch');
  const $btnReload = root.querySelector('#btnHistReload');

  const $tbody     = root.querySelector('#histTable tbody');
  const $count     = root.querySelector('#histRowCount');

  const $btnPrev   = root.querySelector('#btnHistPrev');
  const $btnNext   = root.querySelector('#btnHistNext');
  const $pageInfo  = root.querySelector('#histPageInfo');

  const $startText = root.querySelector('#inpHistStartText');
  const $endText   = root.querySelector('#inpHistEndText');
  const $startDate = root.querySelector('#inpHistStartDate');
  const $endDate   = root.querySelector('#inpHistEndDate');
  const $btnStart  = root.querySelector('#btnHistStartPick');
  const $btnEnd    = root.querySelector('#btnHistEndPick');

  // ---- state ----
  let allRows = [];        // full dataset from /history
  let viewRows = [];       // filtered rows
  let currentPage = 1;     // 1-based
  let pageSize = 180;      // from inpHistTake
  let endUserEdited = false;
  let lastAllSig = '';
  // ---- helpers ----
  const pad2 = (n) => String(n).padStart(2, '0');
  const toYmd = (d) => `${d.getFullYear()}-${pad2(d.getMonth()+1)}-${pad2(d.getDate())}`;
  const toYmdSlash = (d) => `${d.getFullYear()}/${pad2(d.getMonth()+1)}/${pad2(d.getDate())}`;
function makeSig(rows) {
  // 只抓會影響畫面的欄位，避免 JSON 很大
  // 用 historyId + status + updateTime 最夠用
  return JSON.stringify(rows.map(r => [r.historyId, r.status, r.updateTime]));
}
  function addDays(d, days){
    const x = new Date(d.getFullYear(), d.getMonth(), d.getDate());
    x.setDate(x.getDate() + days);
    return x;
  }

  function parseDateText(s){
    const t = (s || '').trim();
    if (!t) return null;
    const m = t.match(/^(\d{4})[\/\-](\d{1,2})[\/\-](\d{1,2})$/);
    if (!m) return null;
    const y = +m[1], mo = +m[2], da = +m[3];
    const d = new Date(y, mo - 1, da);
    if (d.getFullYear() !== y || (d.getMonth()+1) !== mo || d.getDate() !== da) return null;
    return d;
  }

  function setStart(d){
    const dd = new Date(d.getFullYear(), d.getMonth(), d.getDate());
    $startText.value = toYmdSlash(dd);
    $startDate.value = toYmd(dd);
  }

  function setEnd(d){
    const dd = new Date(d.getFullYear(), d.getMonth(), d.getDate());
    $endText.value = toYmdSlash(dd);
    $endDate.value = toYmd(dd);
  }

  function fmtDateTime(s){
    if (!s) return '';
    const d = new Date(s);
    if (isNaN(d)) return String(s);
    const y = d.getFullYear();
    const m = pad2(d.getMonth()+1);
    const da = pad2(d.getDate());
    const hh = pad2(d.getHours());
    const mm = pad2(d.getMinutes());
    const ss = pad2(d.getSeconds());
    return `${y}/${m}/${da} ${hh}:${mm}:${ss}`;
  }

  function isErrorStatus(code) {
    const n = Number(code);
    return [
      91, 92,
      901, 902, 903, 904,
      911, 912, 913, 914,
      921, 922, 923, 915,
      999
    ].includes(n);
  }

  function isRetryable(code) {
    return isErrorStatus(code); // 你現在規則就是錯誤/取消都可重試
  }

  function statusLabel(code,action) {
    const n = Number(code);
    const act = String(action || '').toLowerCase();
    if (n === 11) return '搬移成功';
    if (n === 12) return '刪除成功';
    if (n === 13) return '等待歸檔';
    if (n === 14 || n === 17) return '等待回遷';

    // if (String(n).startsWith('91')) return '搬移失敗';
    // if (String(n).startsWith('92')) return '刪除失敗';
     if (String(n).startsWith('91')) {
    if (act === 'delete') return '刪除失敗';
    else if (act === 'move' || act === 'copy') return '搬移失敗';
    
  }
  if (n === 922) return '其他線程占用';
    if (n === 999) return '使用者取消';
    if (n === 901) return '資料庫錯誤 [From]';
    if (n === 902) return '資料庫錯誤 [To]';
    if (n === 903) return '未設定restore錯誤';
    // if (n === 904) return '排程刪除失敗';
    if (n === 904) {
  if (act === 'delete') return '刪除驗證失敗';
  else if (act === 'move' || act === 'copy') return '搬移驗證失敗';
 
}
    if (n === 915) return '檔案大小不同';

    return String(code ?? '');
  }

    function actionLabelByRow(r) {
      // 1. 先抓出 action 字串做最準確的判斷
      const act = String(r.action || r.Action || '').toLowerCase();
      if (act === 'move') return '搬移';
      if (act === 'delete') return '刪除';

      

      // 3. 最後看類型判斷
      const destType = r.destType ?? r.DestType ?? r.toType ?? r.ToType;
      if (!destType) return '搬移';

      const t = String(destType).toUpperCase();
      if (t === 'L1' || t === 'L2') return '歸檔';
      if (t === 'DOWNLOAD') return '下載';
      if (t === 'IMPORT') return '搬移'; // 你的資料裡 destType 是 IMPORT

      return '搬移';
    }

  function pill(label, tooltip, status) {
    const safeTip = tooltip ? String(tooltip).replace(/"/g, '&quot;') : '';
    let cls = 'status-pill';
    const n = Number(status);

    if (n === 11 || n === 12) cls += ' ok';
    else if (isErrorStatus(n)) cls += ' fail';
    else cls += ' pending';

    return `<span class="${cls}" title="${safeTip}">${label}</span>`;
  }

  function getUserbitText(r) {
    // 你資料裡可能是 userBit；也可能某些地方塞 fileName
    // delete 且 fileId=0 且錯誤時，顯示 note
    const act = (r.action ?? '').toString().trim().toLowerCase();
    const fid = Number(r.fileId || 0);
    const st  = Number(r.status || 0);

    if (act === 'delete' && fid === 0 && isErrorStatus(st)) {
      const nt = (r.note || '').trim();
      if (nt) return nt;
    }

    return r.userBit || r.fileName || '';
  }

  // ---- filtering ----
  function applyFilters({ resetPage = true } = {}) {
  const st = ($selStatus.value || 'all').toLowerCase();
  const group = ($selGroup.value || 'all').toUpperCase();
  const kw = ($inpSearch.value || '').trim().toLowerCase();

  const sObj = parseDateText($startText.value);
  const eObj = parseDateText($endText.value);

  const startMs = sObj ? new Date(sObj.getFullYear(), sObj.getMonth(), sObj.getDate()).getTime() : null;
  const endMs = eObj ? (new Date(eObj.getFullYear(), eObj.getMonth(), eObj.getDate(), 23, 59, 59, 999)).getTime() : null;

  let rows = allRows;

  // ✅ 樓層：用 fromGroup（你要的）
  if (group !== 'ALL') {
    rows = rows.filter(r => String(r.fromGroup || '').toUpperCase() === group);
  }

  // 狀態
  if (st !== 'all') {
    if (st === 'success') {
      rows = rows.filter(r => Number(r.status) === 11 || Number(r.status) === 12);
    } else if (st === 'fail') {
      rows = rows.filter(r => isErrorStatus(Number(r.status)));
    }
  }

  // 日期（用 updateTime）
  if (startMs != null || endMs != null) {
    rows = rows.filter(r => {
      const t = new Date(r.updateTime || r.UpdateTime || '').getTime();
      if (!t || isNaN(t)) return false;
      if (startMs != null && t < startMs) return false;
      if (endMs != null && t > endMs) return false;
      return true;
    });
  }

  // 搜尋
  if (kw) {
    rows = rows.filter(r => {
      const fields = [
        r.fileName,      // 你現在 fileName 塞 UserBit
        r.userBit,
        r.note,
        r.programName,
        r.sourceStorage,
        r.destStorage,
        r.assignedNode,
        r.action,
        r.fromGroup,
        r.toGroup,
        String(r.historyId ?? ''),
        String(r.fileId ?? '')
      ];
      return fields.some(v => String(v || '').toLowerCase().includes(kw));
    });
  }

  // 排序：最新在上
  rows = [...rows].sort((a, b) => {
    const ta = new Date(a.updateTime || '').getTime() || 0;
    const tb = new Date(b.updateTime || '').getTime() || 0;
    return tb - ta;
  });

   viewRows = rows;

  if (resetPage) currentPage = 1;

  const tp = Math.max(1, Math.ceil(viewRows.length / pageSize));
  if (currentPage > tp) currentPage = tp;

  renderPage();
}

  // ---- pagination + render ----
  function getTotalPages() {
    return Math.max(1, Math.ceil(viewRows.length / pageSize));
  }

  function updatePager() {
    const tp = getTotalPages();
    if (currentPage > tp) currentPage = tp;

    $pageInfo.textContent = `第 ${currentPage} / ${tp} 頁，共 ${viewRows.length} 筆`;
    $btnPrev.disabled = currentPage <= 1;
    $btnNext.disabled = currentPage >= tp;

    $count.textContent = `${viewRows.length} 筆`;
  }

  function renderPage() {
    const tp = getTotalPages();
    if (currentPage < 1) currentPage = 1;
    if (currentPage > tp) currentPage = tp;

    const start = (currentPage - 1) * pageSize;
    const end = start + pageSize;
    const rows = viewRows.slice(start, end);

    if (!rows.length) {
      $tbody.innerHTML = `<tr><td colspan="9" style="text-align:center;color:#999;">（沒有符合條件的紀錄）</td></tr>`;
      updatePager();
      return;
    }

    const frag = document.createDocumentFragment();

    rows.forEach((r, i) => {
      const label   = statusLabel(r.status,r.action);
      const detail  = r.statusText || label;
      const tooltip = `${r.status} - ${detail}`;
      const canRetry = isRetryable(r.status);

      const tr = document.createElement('tr');
      tr.innerHTML = `
        <td>${start + i + 1}</td>
        <td>${r.programName || ''}</td>
        <td>${getUserbitText(r)}</td>
        <td>${r.sourceStorage || ''}</td>
        <td>${r.destStorage || ''}</td>
        <td>${r.assignedNode || '-'}</td>
        <td>${fmtDateTime(r.updateTime)}</td>
        <td>${actionLabelByRow(r)}</td>
        <td>
          ${pill(label, tooltip, r.status)}
          ${canRetry ? `
            <button class="btn-retry"
              data-id="${r.historyId}"
              data-action="${String(r.action ?? r.Action ?? '').toLowerCase()}"
              data-fromtype="${String(r.fromType ?? r.FromType ?? '')}"
              data-fromgroup="${String(r.fromGroup ?? r.FromGroup ?? '')}"
              data-to-type="${String(r.destType ??r.DestType ?? '')}"
              style="margin-left:6px;padding:2px 8px;font-size:12px;">重試</button>
              
            <button class="btn-remove" data-id="${r.historyId}"
              style="margin-left:6px;padding:2px 8px;font-size:12px;">移除</button>
          ` : ''}
        </td>
      `;
      frag.appendChild(tr);
    });

    $tbody.innerHTML = '';
    $tbody.appendChild(frag);
    updatePager();
  }

  // ---- load full history once ----
  async function loadAllHistory({ silent = false } = {}) {
  if (!silent) {
    $btnReload.disabled = true;
    $btnReload.textContent = '載入中…';
    $tbody.innerHTML = `<tr><td colspan="9" style="text-align:center;color:#999;">載入中…</td></tr>`;
    $count.textContent = '';
  }

  try {
    const resp = await fetch(`${API_HISTORY}?ts=${Date.now()}`, { cache: 'no-store' });
    if (!resp.ok) throw new Error('HTTP ' + resp.status);

    const payload = await resp.json();
    const rows = Array.isArray(payload.rows) ? payload.rows : [];

    // ✅ silent 時，如果資料完全沒變，就不要重畫
    const sig = makeSig(rows);
    if (silent && sig === lastAllSig) return;
    lastAllSig = sig;

    allRows = rows;

    // ✅ 保留使用者目前頁碼
    applyFilters({ resetPage: false });
  } catch (e) {
    console.error(e);
    if (!silent) {
      $tbody.innerHTML = `<tr><td colspan="9" style="color:#c00;">載入失敗：${e.message}</td></tr>`;
    }
  } finally {
    if (!silent) {
      $btnReload.disabled = false;
      $btnReload.textContent = '重新整理';
    }
  }
}

  // ---- date defaults (last 7 days) ----
  const today = new Date();
  setEnd(today);
  setStart(addDays(today, -6));
  endUserEdited = false;

  // picker open
  $btnStart.addEventListener('click', () => ($startDate.showPicker ? $startDate.showPicker() : $startDate.click()));
  $btnEnd.addEventListener('click', () => ($endDate.showPicker ? $endDate.showPicker() : $endDate.click()));

  // picker change -> sync text then filter
  $startDate.addEventListener('change', () => {
    const d = parseDateText($startDate.value);
    if (!d) return;

    setStart(d);
    if (!endUserEdited) setEnd(addDays(d, 6));
    else {
      const e = parseDateText($endText.value);
      if (e && e < d) setEnd(d);
    }
    applyFilters();
  });

  $endDate.addEventListener('change', () => {
    const d = parseDateText($endDate.value);
    if (!d) return;

    endUserEdited = true;
    const s = parseDateText($startText.value);
    if (s && d < s) setEnd(s);
    else setEnd(d);

    applyFilters();
  });

  // manual text change
  $startText.addEventListener('change', () => {
    const d = parseDateText($startText.value);
    if (!d) { setStart(parseDateText($startDate.value) || today); return; }

    setStart(d);
    if (!endUserEdited) setEnd(addDays(d, 6));
    else {
      const e = parseDateText($endText.value);
      if (e && e < d) setEnd(d);
    }
    applyFilters();
  });

  $endText.addEventListener('change', () => {
    const d = parseDateText($endText.value);
    if (!d) { setEnd(parseDateText($endDate.value) || addDays(parseDateText($startText.value) || today, 6)); return; }

    endUserEdited = true;
    const s = parseDateText($startText.value);
    if (s && d < s) setEnd(s);
    else setEnd(d);

    applyFilters();
  });

  // ---- events ----
  const doSearch = () => applyFilters();

  $btnSearch.addEventListener('click', doSearch);
  $inpSearch.addEventListener('keydown', (e) => { if (e.key === 'Enter') doSearch(); });

  $selStatus.addEventListener('change', applyFilters);
  $selGroup.addEventListener('change', applyFilters);

  $inpTake.addEventListener('change', () => {
    const v = parseInt($inpTake.value || '180', 10);
    pageSize = (isNaN(v) || v < 10) ? 10 : v;
    currentPage = 1;
    renderPage();
  });

  $btnReload.addEventListener('click', () => {
    $inpSearch.value = '';
    currentPage = 1;
    loadAllHistory({ silent: false });
  });

  $btnPrev.addEventListener('click', () => {
    if (currentPage > 1) {
      currentPage--;
      renderPage();
    }
  });

  $btnNext.addEventListener('click', () => {
    const tp = getTotalPages();
    if (currentPage < tp) {
      currentPage++;
      renderPage();
    }
  });

  // ---- retry/remove (delegate) ----
  root.addEventListener('click', async (e) => {
    const retryBtn = e.target.closest('.btn-retry');
    if (retryBtn) {
      const historyId = Number(retryBtn.dataset.id);
      if (!historyId) return;
      if (!confirm(`確定要重試這筆任務嗎？#${historyId}`)) return;

      try {
        // const resp = await fetch(`/history/${historyId}/retry`, { method: 'POST' });
        const action    = retryBtn.dataset.action || '';
        const fromType  = retryBtn.dataset.fromtype || '';
        const fromGroup = retryBtn.dataset.fromgroup || '';
         const toType = retryBtn.dataset.toType || retryBtn.dataset.totype|| '';

        const resp = await fetch(`/history/${historyId}/retry`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ action, fromType, fromGroup,toType })
        })
      
            console.log("Payload:", { action, fromType, fromGroup, toType });
        // if (!resp.ok) throw new Error(await resp.text());
        if (!resp.ok) {
        const ct = resp.headers.get('content-type') || '';
        const payload = ct.includes('application/json') ? await resp.json() : await resp.text();
        const msg = typeof payload === 'string' ? payload : (payload.message || JSON.stringify(payload));
        throw new Error(msg);
      }
        alert(`重試已送出：#${historyId}`);
        // 這裡不一定要重抓全量，但狀態會變，所以保守重抓一次
        loadAllHistory({ silent: false });
      } catch (err) {
        alert(`重試失敗：${err.message || err}`);
      }
      return;
    }

    const rmBtn = e.target.closest('.btn-remove');
    if (rmBtn) {
      const historyId = Number(rmBtn.dataset.id);
      if (!historyId) return;
      if (!confirm(`確定要移除 HistoryId=${historyId} 嗎？`)) return;

      try {
        const resp = await fetch(`/history/${historyId}/remove`, { method: 'POST' });
        const ct = resp.headers.get('content-type') || '';
        const payload = ct.includes('application/json') ? await resp.json() : await resp.text();

        if (!resp.ok) {
          const msg = typeof payload === 'string' ? payload : (payload.message || JSON.stringify(payload));
          throw new Error(msg);
        }

        const msg = typeof payload === 'string'
          ? `已移除 HistoryId=${historyId}`
          : (payload.message || `已移除 HistoryId=${payload.historyId || historyId}`);

        alert(msg);
        loadAllHistory({ silent: false });
      } catch (err) {
        alert(`移除失敗（HistoryId=${historyId}）：${err.message || err}`);
      }
    }
  });

  // tab 切換 refresh（保留你的事件）
  window.addEventListener('history-reload', () => {
    $inpSearch.value = '';
    $selStatus.value = 'all';
    $selGroup.value  = 'all';
    $inpTake.value   = '180';

    pageSize = 180;
    currentPage = 1;

    // 不強制改日期（你若希望回預設 7 天也可在這裡 setStart/setEnd）
    loadAllHistory({ silent: false });
  });

  // ---- first load ----
  loadAllHistory({ silent: false });

  // ✅ 靜默刷新：建議拉長一點，不然一直抓 1655 筆其實沒必要
  // 如果你一定要 5 秒也可以改回 5000
  setInterval(() => loadAllHistory({ silent: true }), 15000);
}