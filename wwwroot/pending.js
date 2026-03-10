// pending.js
const API_PENDING   = '/jobs/pending';
const API_EVENTS    = '/api/progress/events';
const API_HISTORY_RECENT  = '/history/recent';

/**
 * 💡 資料正規化 (Normalization)
 * 解決 JSON 小寫與欄位錯置問題。對齊 C# HistoryTask 與 SQL Alias。
 */
function normalizeTask(r) {
    if (!r) return null;
    
    // 注意：後端 API 回傳的 JSON 屬性名稱通常會與 C# 屬性名稱一致（大寫或小寫視序列化設定而定）
    // 這裡加上兼容性檢查 ??
    return {
        historyId:     r.historyId ?? r.HistoryId ?? 0,
        // 對應 C# FileName (資料庫存的是節目名稱)
        programName:   r.fileName ?? r.FileName ?? "", 
        // 對應 C# UserBit (資料庫存的是檔案編號，例如 20240101001)
        userBit:       r.userBit ?? r.UserBit ?? "", 
        
        // 來源與目的
        fromStorage:   r.fromStorageName ?? "",
        toStorage:     r.toStorageName ??  "",
       

        action:        (r.action ?? r.Action ?? "").toLowerCase().trim(),
        status:        Number(r.historyStatus ?? r.HistoryStatus ?? 0), // 注意後端是 HistoryStatus
        priority:      Number(r.priority ?? r.Priority ?? 0),
        // assignedNode:  r.assignedNode ?? r.AssignedNode ?? "-",
        assignedNode:  String(r.assignedNode ?? r.AssignedNode ?? "").trim(),
        
        // 時間處理
        createTime:    r.createTime ?? r.CreateTime ?? "",
        fileType:      r.filetype ?? r.Filetype ?? "PO" // 辨識 PO 或 CM
    };
}

function priDb(r) {
    const v = r?.priority;
    return (typeof v === 'number' && !isNaN(v)) ? v : 0;
}

function escapeHtml(text) {
    if (!text) return '';
    return String(text)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

export function initPending(root, statusLine) {
    // === HTML 結構保持不變 ===
    root.innerHTML = `
    <div class="toolbar">
      <button id="btnPendingReload">重新整理</button>
      <button id="btnCancelSelected" style="margin-left:12px;padding:6px 14px;font-size:14px;background:#b42318;color:white;border:none;border-radius:4px;cursor:pointer;">
        取消任務
      </button>
      <span id="pendingCount" class="muted"></span>
    </div>

    <div id="pendingPanel" style="margin-top:8px;height:360px;overflow:auto;border:1px solid #eee;border-radius:4px;">
      <table id="pendingTable">
        <thead>
          <tr>
            <th style="width:40px;"><input type="checkbox" id="chkPendingAll" /></th>
            <th style="width:60px;">No.</th>
            <th style="width:60px;">優先級</th>
            <th style="width:180px;">節目名稱</th>
            <th style="width:100px;">檔名(UserBit)</th>
            <th style="width:80px;">來源</th>
            <th style="width:80px;">目的地</th>
            <th style="width:90px;">節點</th>
            <th style="width:220px;">狀態</th>
            <th style="width:80px;">Action</th>
            <th style="width:250px;">進度</th>
            <th style="width:80px;">取消</th>
          </tr>
        </thead>
        <tbody><tr><td colspan="12" style="text-align:center;color:#999;">載入中…</td></tr></tbody>
      </table>
    </div>

    <div id="pendingHistoryPanel" style="margin-top:24px;border-top:1px solid #ddd;padding-top:12px;">
      <h3 style="margin:0 0 8px;font-size:16px;">歷史紀錄 / 錯誤</h3>
      <div class="toolbar">
        <label>狀態：
          <select id="pendHistStatus">
            <option value="all">全部</option>
            <option value="success">成功</option>
            <option value="fail">失敗</option>
          </select>
        </label>
        <label>搜尋檔名(UserBit)：
          <input id="pendHistSearch" type="text" placeholder="輸入關鍵字" style="width:220px;">
          <button id="btnPendHistSearch">搜尋</button>
        </label>
        <button id="btnPendHistReload">重新整理</button>
        <span id="pendHistRowCount" class="muted"></span>
      </div>
      <div style="height:260px; overflow:auto; margin-top:4px; border:1px solid #eee; border-radius:4px;">
        <table id="pendHistTable">
          <thead>
            <tr>
              <th style="width:70px;">#</th>
              <th style="width:180px;">節目名稱</th>
              <th style="width:180px;">檔名(UserBit)</th>
              <th style="width:150px;">來源 Storage</th>
              <th style="width:150px;">目的 Storage</th>
              <th style="width:120px;">節點</th>      
              <th style="width:100px;">Action</th> 
              <th style="width:170px;">UpdateTime</th>
              <th style="width:150px;">Status</th>
            </tr>
          </thead>
          <tbody><tr><td colspan="9" style="text-align:center;color:#999;">載入中…</td></tr></tbody>
        </table>
      </div>
    </div>`;
    const timer = setInterval(() => {
        loadPending(true); // 每 5 秒從 API 抓一次最新列表
    }, 3000);
    // === 全域狀態 ===
    let allRows = []; 
    const rowMap = new Map();
    const progressState = new Map(); 
    const rateState = new Map(); 
    const selectedIds = new Set();
    let isSelectBusy = false;
    let pendingLastRenderSignature = '';
    function startProgressSse() {
    const es = new EventSource(`${API_EVENTS}?ts=${Date.now()}`);

  // 若後端用 event: progress
  es.addEventListener('progress', (e) => {
    let ev;
    try { ev = JSON.parse(e.data); } catch { return; }

    const key = ev.key || `TO-${ev.historyId}`;
    if (!key) return;

    const prev = progressState.get(key) || {};

    const bytesDone = ev.bytesDone != null ? Number(ev.bytesDone) : (prev.bytesDone ?? 0);
    const bytesTotal = ev.bytesTotal != null ? Number(ev.bytesTotal) : (prev.bytesTotal ?? 0);

    // ✅ percent 沒給也能算
  //  const percent = bytesTotal > 0 ? Math.floor((bytesDone / bytesTotal) * 100) : Number(ev.percent ?? prev.percent ?? 0);
    let percent;
    if (bytesTotal > 0) {
      const remain = bytesTotal - bytesDone;
      percent = Math.floor((bytesDone / bytesTotal) * 100);

      // ✅ 完成判斷（兩個條件擇一）
      if (bytesDone >= bytesTotal) percent = 100;

      // ✅ 容忍：差 1MB 內也當完成（你可調 64KB / 10MB）
      if (remain >= 0 && remain <= 1024 * 1024) percent = 100;

      percent = Math.max(0, Math.min(100, percent));
    } else {
      percent = Number(ev.percent ?? prev.percent ?? 0);
    }
        // ✅ 自己算 speedBps
        const now = Date.now();
        const last = rateState.get(key);
        let speedBps = prev.speedBps;

        if (last) {
          const dBytes = bytesDone - last.lastBytesDone;
          const dSec = (now - last.lastTs) / 1000;
          if (dSec > 0 && dBytes >= 0) speedBps = dBytes / dSec;
        }

    rateState.set(key, { lastBytesDone: bytesDone, lastTs: now });

    progressState.set(key, {
      ...prev,
      percent,
      speedBps,
      bytesDone,
      bytesTotal,
      fileName: ev.fileName ?? prev.fileName,
    });

  updateProgressDom(key);
    });

  // 如果你後端沒有 event name，只用 onmessage：
  es.onmessage = (e) => {
    // optional: 你可刪掉，或拿來兼容後端沒寫 event:progress 的情況
  };

  es.onerror = () => {setTimeout(() => loadPending(true), 1500);
    // EventSource 會自動重連；這裡可以留空
  };

  return es;
}

// 啟動 SSE
startProgressSse();
    function fmtBytes(n) {
      const v = Number(n);
      if (!isFinite(v) || v <= 0) return '0 B';
      const units = ['B','KB','MB','GB','TB'];
      let i = 0, x = v;
      while (x >= 1024 && i < units.length - 1) { x /= 1024; i++; }
      return `${x.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
    }
    function fmtSpeed(bps) {
      const v = Number(bps);
      if (!isFinite(v) || v <= 0) return '';
      return `${(v/(1024*1024)).toFixed(1)} MB/s`;
    }

    function updateProgressDom(key) {
      const wrap = root.querySelector(`.progress-wrap[data-progress-key="${CSS.escape(key)}"]`);
      if (!wrap) return;

      const st = progressState.get(key);
      if (!st) return;

      const percent = Math.max(0, Math.min(100, Number(st.percent ?? 0)));

      const bar = wrap.querySelector('.progress > div');
      if (bar) bar.style.width = `${percent}%`;

      const pct = wrap.querySelector('.progress-text');
      if (pct) pct.textContent = `${percent}%`;

      // 你目前 HTML 沒有 speed 行，如果你要顯示速度：
      // 請把 renderOrUpdateRow 的 progress-wrap 改成你之前那版（含 .progress-speed / .progress-file）
     const speedEl = wrap.querySelector('.progress-speed');
        if (speedEl) {
          const sp = fmtSpeed(st.speedBps);
          speedEl.textContent = sp || '';
        }
       // ✅ 只要有進度就先鎖 UI
   const tr = wrap.closest('tr');
  if (!tr) return;

  // 先更新狀態文字
  if (percent > 0) {
    const statusTd = tr.querySelector('td:nth-child(9)');
    if (statusTd) statusTd.innerHTML = '執行中';
  }

  // ✅ 依照同一套規則判斷是否要鎖 cancel
  const rowId = Number(tr.querySelector('.btn-cancel')?.dataset.id || 0);
  const row = allRows.find(x => x.historyId === rowId);

  const shouldLockCancel =
    row &&
    (row.action === 'delete' || row.action === 'move' || row.status === 24 || row.status === 27);

  if (percent > 0 && shouldLockCancel) {
    const priSelect = tr.querySelector('.pri-select');
    if (priSelect) priSelect.disabled = true;

    const cancelBtn = tr.querySelector('.btn-cancel');
    if (cancelBtn) {
      cancelBtn.disabled = true;
      cancelBtn.textContent = '不可取消';
      cancelBtn.style.background = '#666';
    }

    const chk = tr.querySelector('.chk-pending');
    if (chk) chk.disabled = true;
  }
        // --- 新增：即時同步狀態文字 ---
    if (percent > 0 && percent < 100) {
        const tr = wrap.closest('tr');
        if (tr) {
            // 找到狀態那一欄（假設是第 8 欄，根據你的 HTML 結構）
            // 建議直接抓包含 "排隊中" 字樣的 td
            const statusTd = tr.querySelector('td:nth-child(9)'); 
            if (statusTd && statusTd.innerText.includes('排隊中')) {
                statusTd.innerHTML = '執行中';
                
                // 順便把優先級選單禁用，避免執行中還能改優先級
                const priSelect = tr.querySelector('.pri-select');
                if (priSelect) priSelect.disabled = true;
            }
        }
    }
      if (percent >= 100) {
      const tr = wrap.closest('tr');
      if (tr && !tr.dataset.isFinishing) {
        tr.dataset.isFinishing = "true";
        setTimeout(() => loadPending(true), 2000);
      }
   }
    }
    // === 選取元素 ===
    const $tableBody = root.querySelector('#pendingTable tbody');
    const $histTbody = root.querySelector('#pendHistTable tbody');
    const $btnReload = root.querySelector('#btnPendingReload');
    const $chkAll = root.querySelector('#chkPendingAll');
    const $count = root.querySelector('#pendingCount');

  

    // === 核心渲染函數 ===
    function renderOrUpdateRow(r, seq) {
        const id = r.historyId;
        const key = `TO-${id}`;
        const existing = rowMap.get(id);

        // 狀態邏輯
        // const percent = progressState.get(key) ?? 0;
        // const isActive = percent > 0 && percent < 100;
        const pst = progressState.get(key);
        const percent = Number(pst?.percent ?? 0);
        const startedByStatus = (r.status === 1);
        const isAssigned = !!r.assignedNode;
        const isActive = startedByStatus || (percent > 0 && percent < 100);
        let statusText = isActive ? '執行中' : '排隊中';
        
        // 取消邏輯 (歸檔與回遷開始後不可取消)
        // const started = (r.status === 1) || (percent > 0);
        // const cannotCancel = (r.action === 'move' || r.status === 24 || r.status === 27) && started;
        const cannotCancel = (r.action === 'delete'||r.action === 'move' || r.status === 24 || r.status === 27) && isAssigned;
        let tr = existing || document.createElement('tr');
        if (!existing) {
            rowMap.set(id, tr);
            $tableBody.appendChild(tr);
        }

        tr.innerHTML = `
            <td><input type="checkbox" class="chk-pending" data-id="${id}" ${selectedIds.has(id) ? 'checked' : ''} ${cannotCancel ? 'disabled' : ''}></td>
            <td>${seq}${r.status === 24 || r.status === 27 ? '<span class="tag-badge">回遷</span>' : ''}</td>
            <td>
                <select class="pri-select" data-id="${id}" ${isActive ? 'disabled' : ''}>
                    ${[0,1,2,3,4,5,6,7,8,9,10].map(v => `<option value="${v}" ${v === r.priority ? 'selected' : ''}>${v}</option>`).join('')}
                </select>
            </td>
            <td>${escapeHtml(r.programName)}</td>
            <td>${escapeHtml(r.userBit)}</td>
            <td>${escapeHtml(r.fromStorage)}</td>
            <td>${escapeHtml(r.toStorage)}</td>
            <td>${escapeHtml(r.assignedNode)}</td>
            <td>${statusText} </td>
            <td>${r.action === 'delete' ? '刪除' : '搬移'}</td>
           <td>
              <div class="progress-wrap" data-progress-key="${key}"   style="width:300px;display:flex;flex-direction:column;gap:4px;">
                <div class="progress-container" style="display: flex; align-items: center; gap: 8px;">
                  <div class="progress" style="flex: 1; ">
                    <div style="width: ${percent}%; "></div>
                  </div>
                  <span class="progress-text" style="min-width: 35px; font-size: 12px; font-weight: bold;">${percent}%</span>
                </div>
                
                <div style="display: flex; ">
                  <span class="progress-speed"></span>
                </div>
              </div>
            </td>
            <td>
                <button class="btn-cancel" data-id="${id}" style="background:${cannotCancel ? '#666' : '#b42318'}" ${cannotCancel ? 'disabled' : ''}>
                    ${cannotCancel ? '不可取消' : '取消'}
                </button>
            </td>`;
    }
    async function requestCancel(ids) {
    if (!ids || ids.length === 0) return;

    try {
        // 統一呼叫 Master 的 Batch API
        const resp = await fetch('/api/jobs/cancel-batch', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(ids)
        });

        if (resp.ok) {
          // 清除進度快取，讓進度條歸零
            ids.forEach(id => {
                const key = `TO-${id}`;
                progressState.delete(key);
                rateState.delete(key);
            });
            alert(`取消指令已送出 (共 ${ids.length} 筆)`);
            loadPending(false); // 立即刷新列表
            selectedIds.clear(); // 清除勾選狀態
        } else {
            const msg = await resp.text();
            throw new Error(msg || '取消失敗');
        }
    } catch (err) {
        alert('操作失敗: ' + err.message);
    }
}
root.querySelector('#btnCancelSelected')?.addEventListener('click', () => {
    const checked = Array.from(root.querySelectorAll('.chk-pending:checked'));
    if (checked.length === 0) return alert('請先勾選任務');

    const ids = checked.map(chk => parseInt(chk.dataset.id));
    if (confirm(`確定取消選取的 ${ids.length} 筆任務？`)) {
        requestCancel(ids);
    }
});

    // === 資料載入 ===
    async function loadPending(isAuto = true) {
        if (isAuto && isSelectBusy) return;
        try {
            const resp = await fetch(`${API_PENDING}?ts=${Date.now()}`);
            const rawData = await resp.json();
            
            // 💡 關鍵：資料一進來就洗乾淨
            allRows = rawData.map(normalizeTask); 

            allRows.sort((a, b) => (b.priority - a.priority) || (a.historyId - b.historyId));
            //更新欄位
            const signature = allRows.map(r => 
                `${r.historyId}-${r.status}-${r.priority}-${r.assignedNode}`
            ).join('|');

            if (isAuto && signature === pendingLastRenderSignature) {
                // 即使簽名一樣，我們也要確保「進度條」在自動刷時同步最新的 progressState
                // 因為 SSE 可能在 loadPending 期間有更新
                allRows.forEach(r => updateProgressDom(`TO-${r.historyId}`));
                return;
            }
            pendingLastRenderSignature = signature;
            $tableBody.innerHTML = '';
            rowMap.clear();
            allRows.forEach((r, idx) => renderOrUpdateRow(r, idx + 1));
            $count.textContent = `${allRows.length} 筆`;
        } catch (err) {
            $tableBody.innerHTML = `<tr><td colspan="12" style="color:red">載入失敗</td></tr>`;
        }
    }

    // === 事件監聽與 SSE 略 (保持您原本的邏輯) ===
    $btnReload.addEventListener('click', () => loadPending(false));
    
    // 啟動
    loadPending(false);
    // =========================
// ✅ Pending 下方 History（recent）
// 參考 history.js：一次抓 -> 前端 filter -> 可 silent refresh
// =========================
const $pendSelStatus = root.querySelector('#pendHistStatus');
const $pendSearch    = root.querySelector('#pendHistSearch');
const $pendBtnSearch = root.querySelector('#btnPendHistSearch');
const $pendBtnReload = root.querySelector('#btnPendHistReload');
const $pendCount     = root.querySelector('#pendHistRowCount');

let histAll = [];
let histView = [];
let histLastSig = '';

function makeSigRecent(rows){
  // 用 historyId + status + updateTime 避免一直重畫
  return JSON.stringify((rows || []).map(r => [r.historyId, r.status, r.updateTime]));
}

function normalizeRecent(r){
  if (!r) return null;
  return {
    historyId:    r.historyId ?? r.HistoryId ?? 0,
    programName:  r.programName ?? "",
    userBit:      r.fileName  ??"",
    fromType:     r.fromType ?? r.FromType ?? "",
    fromGroup:    r.fromGroup ?? r.FromGroup ?? "",
    toType:       r.toType ?? r.ToType ?? r.destType ?? r.DestType ?? "",
    sourceStorage:r.sourceStorage ?? r.SourceStorage ?? r.fromStorage ?? "",
    destStorage:  r.destStorage ?? r.DestStorage ?? r.toStorage ?? "",
    assignedNode: r.assignedNode ?? r.AssignedNode ?? "-",
    action:       (r.action ?? r.Action ?? "").toLowerCase().trim(),
    updateTime:   r.updateTime ?? r.UpdateTime ?? "",
    status:       Number(r.status ?? r.Status ?? 0),
    note:         r.note ?? r.Note ?? ""
  };
}

function isErrorStatus(code){
  const n = Number(code);
  return [
    91,92,901,902,903,904,911,912,913,914,915,921,922,923,999
  ].includes(n) || (n >= 900 && n <= 999);
}
function actionLabelByRow(r) {
  const act = String(r.action || r.Action || '').toLowerCase();
  if (act === 'move') return '搬移';
  if (act === 'delete') return '刪除';

  const destType = r.destType ?? r.DestType ?? r.toType ?? r.ToType;
  if (!destType) return '搬移';

  const t = String(destType).toUpperCase();
  if (t === 'L1' || t === 'L2') return '歸檔';
  if (t === 'DOWNLOAD') return '下載';
  if (t === 'IMPORT') return '搬移';
  return '搬移';
}
function applyPendHistFilters() {
  const st = (root.querySelector('#pendHistStatus')?.value || 'all').toLowerCase();
  const kw = (root.querySelector('#pendHistSearch')?.value || '').trim().toLowerCase();

  let rows = histAll;

  // ✅ 成功 / 失敗 / 全部
  if (st === 'success') {
    rows = rows.filter(r => Number(r.status) === 11 || Number(r.status) === 12);
  } else if (st === 'fail') {
    rows = rows.filter(r => isErrorStatus(Number(r.status)));
  }

  // ✅ 搜尋：只在這 200 筆裡面搜尋
  if (kw) {
    rows = rows.filter(r => {
      const fields = [
        r.programName,
        r.userBit,
        r.note,
        r.sourceStorage,
        r.destStorage,
        r.assignedNode,
        r.action,
        String(r.historyId ?? '')
      ];
      return fields.some(v => String(v || '').toLowerCase().includes(kw));
    });
  }

  // 最新在上
  rows = [...rows].sort((a, b) => {
    const ta = new Date(a.updateTime || '').getTime() || 0;
    const tb = new Date(b.updateTime || '').getTime() || 0;
    return tb - ta;
  });

  histView = rows;
  renderPendHistTable();   // 你已經有的 render（含 pill + 按鈕）
}

function fmtDateTime(s){
  if (!s) return '';
  const d = new Date(s);
  if (isNaN(d)) return String(s);
  const pad2 = (n)=>String(n).padStart(2,'0');
  return `${d.getFullYear()}/${pad2(d.getMonth()+1)}/${pad2(d.getDate())} ${pad2(d.getHours())}:${pad2(d.getMinutes())}:${pad2(d.getSeconds())}`;
}

function renderPendHistTable(){
  if (!$histTbody) return;

  if (!histView.length) {
    $histTbody.innerHTML = `<tr><td colspan="9" style="text-align:center;color:#999;">無資料</td></tr>`;
    if ($pendCount) $pendCount.textContent = `0 筆`;
    return;
  }
// function isErrorStatus(code) {
//   const n = Number(code);
//   return [
//     91, 92, 901, 902, 903, 904,
//     911, 912, 913, 914, 915,
//     921, 922, 923, 999
//   ].includes(n) || (n >= 900 && n <= 999);
// }
function isRetryable(code) {
  return isErrorStatus(code); // 你現在規則：錯誤/取消都可重試
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
  if (n === 915) return '檔案大小不同';
  return String(code ?? '');
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
$histTbody.innerHTML = histView.map((r,i)=> {
  const label = statusLabel(r.status, r.action);
  const tooltip = `${r.status} - ${label}`;
  const canRetry = isRetryable(r.status);
return `
      <tr>
        <td>${i + 1}</td>
        <td>${escapeHtml(r.programName)}</td>
        <td>${escapeHtml(r.userBit)}</td>
        <td>${escapeHtml(r.sourceStorage)}</td>
        <td>${escapeHtml(r.destStorage)}</td>
        <td>${escapeHtml(r.assignedNode)}</td>
        <td>${escapeHtml(actionLabelByRow(r))}</td>
        <td>${escapeHtml(fmtDateTime(r.updateTime))}</td>
        <td>
          ${pill(label, tooltip, r.status)}
          ${canRetry ? `
            <button class="btn-retry"
              data-id="${r.historyId}"
              data-action="${escapeHtml(String(r.action || '').toLowerCase())}"
              data-fromtype="${escapeHtml(String(r.fromType || ''))}"
              data-fromgroup="${escapeHtml(String(r.fromGroup || ''))}"
              data-totype="${escapeHtml(String(r.toType || r.destType || ''))}"
              style="margin-left:6px;padding:2px 8px;font-size:12px;">重試</button>

            <button class="btn-remove"
              data-id="${r.historyId}"
              style="margin-left:6px;padding:2px 8px;font-size:12px;">移除</button>
          ` : ''}
        </td>
      </tr>
    `;
}).join('');

  if ($pendCount) $pendCount.textContent = `${histView.length} 筆`;
}

async function loadPendHistoryRecent({ silent=false } = {}){
  if (!silent) {
    $histTbody.innerHTML = `<tr><td colspan="9" style="text-align:center;color:#999;">載入中…</td></tr>`;
    if ($pendCount) $pendCount.textContent = '';
  }

  try {
    // 你要幾筆自己調：take=200 / 300 / 500
    const take = 200;
    const resp = await fetch(`${API_HISTORY_RECENT}?take=${take}&ts=${Date.now()}`, { cache:'no-store' });
    if (!resp.ok) throw new Error('HTTP ' + resp.status);

    const raw = await resp.json();
    const rows = Array.isArray(raw) ? raw : (Array.isArray(raw.rows) ? raw.rows : []);

    const sig = makeSigRecent(rows);
    if (silent && sig === histLastSig) return;
    histLastSig = sig;

    histAll = rows.map(normalizeRecent).filter(Boolean);
    applyPendHistFilters();
  } catch (e) {
    if (!silent) {
      $histTbody.innerHTML = `<tr><td colspan="9" style="text-align:center;color:#c00;">載入失敗：${escapeHtml(e.message || String(e))}</td></tr>`;
    }
  }
}
root.addEventListener('change', async (e) => {
  const sel = e.target.closest('.pri-select');
  if (!sel) return;

  const historyId = Number(sel.dataset.id);
  const newPriority = Number(sel.value);

  if (!historyId || !Number.isFinite(newPriority)) return;

  // 如果正在執行中，被 disabled 的話基本不會進來；保險再擋一次
  if (sel.disabled) return;

  try {
    const resp = await fetch('/api/jobs/update-priority', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ historyId, priority: newPriority })
    });

    if (!resp.ok) {
      const msg = await resp.text();
      throw new Error(msg || 'update-priority failed');
    }

    // ✅ 成功：不用整頁重拉也可以
    // 1) 先更新本地 allRows，避免下次 auto-refresh 短暫跳回舊值
    const row = allRows.find(x => x.historyId === historyId);
    if (row) row.priority = newPriority;

    // 2) 你如果想立刻讓排序生效（不用等 5 秒），就手動重畫一次
    //    但不要閃：你可以直接呼叫 loadPending(false)（保守、最簡單）
    loadPending(false);
  } catch (err) {
    alert('修改優先級失敗：' + (err.message || err));
    loadPending(false); // 拉回 DB 正確值
  }
});
$chkAll?.addEventListener('change', (e) => {
    const isChecked = e.target.checked;
    root.querySelectorAll('.chk-pending:not(:disabled)').forEach(chk => {
        chk.checked = isChecked;
    });
});
root.addEventListener('click', async (e) => {
  // === 1. 單筆取消邏輯 ===
  const cancelBtn = e.target.closest('.btn-cancel');
    if (cancelBtn) {
        const id = Number(cancelBtn.dataset.id);
        if (!id) return;

        // 判斷是否正在執行中 (可以看文字或狀態)
        const isRunning = cancelBtn.innerText.includes('中斷') || 
                          cancelBtn.closest('tr').innerText.includes('執行中');

        const msg = isRunning 
            ? ` 任務 #${id} 執行中` 
            : `確定要取消任務 #${id} 嗎？`;

        if (confirm(msg)) {
            await requestCancel([id]); // 雖然只有一筆，也包成陣列 [id]
        }
        return;
    }

    // ✅ Priority change -> update DB

    // === 2. 原本的 Retry 邏輯 (你提供的代碼) ===
  const retryBtn = e.target.closest('.btn-retry');
  if (retryBtn) {
    const historyId = Number(retryBtn.dataset.id);
    if (!historyId) return;
    if (!confirm(`確定要重試這筆任務嗎？#${historyId}`)) return;

    try {
      const action    = retryBtn.dataset.action || '';
      const fromType  = retryBtn.dataset.fromtype || '';
      const fromGroup = retryBtn.dataset.fromgroup || '';
      const toType    = retryBtn.dataset.totype || '';
      console.log('[RETRY_PAYLOAD]', { historyId, action, fromType, fromGroup, toType });
      const resp = await fetch(`/history/${historyId}/retry`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ action, fromType, fromGroup, toType })
      });

      if (!resp.ok) {
        const ct = resp.headers.get('content-type') || '';
        const payload = ct.includes('application/json') ? await resp.json() : await resp.text();
        const msg = typeof payload === 'string' ? payload : (payload.message || JSON.stringify(payload));
        throw new Error(msg);
      }
      // 重試成功，清除舊的進度快取
    const key = `TO-${historyId}`;
    progressState.delete(key);
    rateState.delete(key)
      alert(`重試已送出：#${historyId}`);
      // 你可以選擇：只 reload recent（快） or reload pending（保守）
      loadPendHistoryRecent({ silent: false });
      loadPending(false);
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
      loadPendHistoryRecent({ silent: false });
    } catch (err) {
      alert(`移除失敗（HistoryId=${historyId}）：${err.message || err}`);
    }
    return;
  }
});
  // 在 initPending 結尾處加入


 
// ---- events ----
$pendBtnReload?.addEventListener('click', () => loadPendHistoryRecent({ silent:false }));
$pendBtnSearch?.addEventListener('click', applyPendHistFilters);
$pendSearch?.addEventListener('keydown', (e)=>{ if(e.key==='Enter') applyPendHistFilters(); });
$pendSelStatus?.addEventListener('change', applyPendHistFilters);

// ---- first load + silent refresh ----
loadPendHistoryRecent({ silent:false });
setInterval(() => loadPendHistoryRecent({ silent:true }), 15000);

    
}