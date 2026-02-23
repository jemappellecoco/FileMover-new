// pending.js
const API_PENDING   = '/jobs/pending';
const API_EVENTS    = '/api/progress/events';
const API_CONCUR    = '/api/config/concurrency';
const API_HISTORY   = '/history';  
function priDb(r) {
  const v = r?.priority;
  return (typeof v === 'number' && !isNaN(v)) ? v : 0; // 跟 DB default 0 一樣
}
export function initPending(root, statusLine) {
  // 建 HTML 結構（照你原本的樣式，只拿 toolbar + table）
  root.innerHTML = `
    <div class="toolbar">
      <button id="btnPendingReload">重新整理</button>

    <!-- <label>
        樓層：
        <select id="selGroup">
          <option value="all">全部</option>
          <option value="4F">4F</option>
          <option value="7F">7F</option>
        </select>
      </label>

      <label>
        並行數：
        <select id="selParallel">
          ${[1,2,3,4,5,6,7,8,9,10].map(v => `<option value="${v}">${v}</option>`).join('')}
        </select>
      </label>
      <button id="btnSetParallel">套用</button>-->

      <button id="btnCancelSelected"
        style="
          margin-left:12px;
          padding:6px 14px;
          font-size:14px;
          background:#b42318;
          color:white;
          border:none;
          border-radius:4px;
          cursor:pointer;
        ">
        取消任務
      </button>
      <span id="pendingCount" class="muted"></span>
    </div>

    <!-- 🔺 上半：Pending，自己的 scroll 區域 -->
    <div id="pendingPanel"
         style="
           margin-top:8px;
           height:360px;
           overflow:auto;
           border:1px solid #eee;
           border-radius:4px;
         ">
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

        <tbody>
          <tr><td colspan="12" style="text-align:center;color:#999;">載入中…</td></tr>
        </tbody>
      </table>
    </div>

    <!-- 🔻 下半：歷史區塊 -->
    <div id="pendingHistoryPanel"
         style="margin-top:24px;border-top:1px solid #ddd;padding-top:12px;">
      <h3 style="margin:0 0 8px;font-size:16px;">歷史紀錄 / 錯誤</h3>

      <div class="toolbar">
        <label>狀態：
          <select id="pendHistStatus">
            <option value="all">全部</option>
            <option value="success">成功</option>
            <option value="fail">失敗</option>
          </select>
        </label>
        <label style="margin-left:12px;">樓層：
          <select id="pendHistGroup">
            <option value="all">全部</option>
            <option value="4F">4F</option>
            <option value="7F">7F</option>
          </select>
        </label>

        <label>顯示筆數：
          <input id="pendHistTake" type="number" min="10" step="10" value="200" style="width:100px;">
        </label>

        <label>搜尋檔名(UserBit)：
          <input id="pendHistSearch" type="text" placeholder="輸入關鍵字，例如 2101EC3F" style="width:220px;">
          <button id="btnPendHistSearch" style="margin-left:4px;">搜尋</button>
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
          <tbody>
            <tr><td colspan="9" style="text-align:center;color:#999;">載入中…</td></tr>
          </tbody>
        </table>
      </div>
    </div>
  `;


  const $btnReload    = root.querySelector('#btnPendingReload');
  const $tableBody    = root.querySelector('#pendingTable tbody');
  const $count        = root.querySelector('#pendingCount');
  // const $selGroup     = root.querySelector('#selGroup');
  // const $selParallel  = root.querySelector('#selParallel');
  const $btnSetPar    = root.querySelector('#btnSetParallel');
  const $btnCancelSelected= root.querySelector('#btnCancelSelected');
  const $chkAll           = root.querySelector('#chkPendingAll');
  let hasScheduledReloadAfterDone = false;
    // ⭐ 下方歷史區塊的元素
  const $histStatus   = root.querySelector('#pendHistStatus');
  const $histTake     = root.querySelector('#pendHistTake');
  const $histGroup    = root.querySelector('#pendHistGroup');
  const $histSearch   = root.querySelector('#pendHistSearch');
  const $btnHistReload= root.querySelector('#btnPendHistReload');
  const $histTbody    = root.querySelector('#pendHistTable tbody');
  const $histCount    = root.querySelector('#pendHistRowCount');
  
  // === 全域狀態 ===
  let allRows = [];                 // 從 DB 撈到的完整 pending 清單
  const progressState = new Map();  // key → 百分比（key = "TO-7" 這種）
  const rowMap = new Map();         // HistoryId → <tr>
  const lastSample = new Map(); // destKey -> { ts, copied }
  const speedState = new Map(); // destKey -> bytesPerSec (smoothed)
  let isSelectBusy = false;         // ⭐ 使用者是否正在操作某個 select
  const selectedIds = new Set();


  // ⭐ 歷史區塊的狀態
  let histAllRows = [];
  let histCurrentRows = [];
  let histLastRenderSignature = '';
  let pendingLastRenderSignature = '';
// 歷史搜尋進入點
const doHistQuery = () => {
  // 搜尋新關鍵字時，通常希望看到最新狀態
  histLastRenderSignature = ''; 
  loadHistoryInPending(false); 
};
function isRetryable(code) {
  const n = Number(code);
  return [
    91, 92,
    901, 902, 903, 904,
    911, 912, 913, 914,
    921, 922, 923, 915,
    999               // ✅ 取消也允許重試
  ].includes(n);
}

  function isHistErrorStatus(code) {
    const n = Number(code);
    return [
      91, 92, 901, 902, 903,
      911, 912, 913, 914,
      921, 922, 923,915,
      
    ].includes(n);
  }

  function pad2(n) { return String(n).padStart(2, '0'); }

  function histFmtDate(s) {
    if (!s) return '';
    const d = new Date(s);
    if (isNaN(d)) return String(s);

    const y  = d.getFullYear();
    const m  = pad2(d.getMonth() + 1);
    const da = pad2(d.getDate());
    const hh = pad2(d.getHours());
    const mm = pad2(d.getMinutes());
    const ss = pad2(d.getSeconds());

    return `${y}/${m}/${da} ${hh}:${mm}:${ss}`;
  }

  function histStatusLabel(code) {
    const n = Number(code);
    if (n === 11) return '搬移成功';
    if (n === 12) return '刪除成功';
    if (n === 14 || n === 17) return '等待回遷';
    if (String(n).startsWith('91')) return '搬移失敗';
    if (String(n).startsWith('92')) return '刪除失敗';
    if (n === 999) return '使用者取消';
    if (n === 901) return '資料庫錯誤 [From]';
    if (n === 902) return '資料庫錯誤 [To]';
    if (n === 903) return '未設定restore錯誤';
    if (n === 904) return '排程刪除錯誤';
    if (n === 915) return '檔案大小不同';
    if (n === 13) return '等待歸檔';
    return String(code ?? '');
  }

  function histPill(label, tooltip, status) {
  const safeTip = tooltip
    ? String(tooltip).replace(/"/g, '&quot;')
    : '';

  let cls = 'status-pill';
  const n = Number(status);

  if (n === 11 || n === 12) {
    cls += ' ok';
  } else if (isHistErrorStatus(n)) {
    cls += ' fail';     // ✅ 915 會在這裡變紅
  } else {
    cls += ' pending';
  }

  return `<span class="${cls}" title="${safeTip}">${label}</span>`;
}

function actionLabelByRow(r) {
 // 1. 優先判斷「刪除」邏輯
  // 兼顧 camelCase (toStorageId) 與 PascalCase (ToStorageId)
  const action = (r.action ?? "").toLowerCase();
  const toSidRaw = r.toStorageId ?? r.ToStorageId;

  const act = (r.action ?? "").toLowerCase();
  if (act === 'delete') return '刪除';

  const destType = r.destType ?? r.DestType ?? r.toType ?? r.ToType;
  if (!destType) return '搬移';

  const t = String(destType).toUpperCase();
  if (t === 'L1' || t === 'L2') return '歸檔';
  if (t === 'DOWNLOAD')        return '下載';
  return '搬移';
}

  function histRenderRows(rows) {
    if (!$histTbody) return;

    if (!rows.length) {
      $histTbody.innerHTML =
        `<tr><td colspan="8" style="text-align:center;color:#999;">（沒有符合條件的紀錄）</td></tr>`;
      if ($histCount) $histCount.textContent = '0 筆';
      histCurrentRows = [];
      return;
    }

    const frag = document.createDocumentFragment();
    rows.forEach((r, idx) => {
      const label   = histStatusLabel(r.status);      // pill 顯示的：搬移失敗 / 刪除成功 ...
      const detail  = r.statusText || label;         // 後端給的中文細項（例如：搬移失敗－檔案使用中）
      const tooltip = `${r.status} - ${detail}`;     // 例如：912 - 搬移失敗－檔案使用中

      const canRetry = isRetryable(r.status);
      const actionRaw = (r.action ?? r.Action ?? '-').toString().trim().toLowerCase();
      // const actionLabel =
      //   actionRaw === 'copy'   ? '搬檔' :
      //   actionRaw === 'delete' ? '刪除' :
      //   actionRaw === 'move' ? '歸檔' :
      //   actionRaw || '-';
      const actionLabel = actionLabelByRow(r);
      const tr = document.createElement('tr');
      tr.innerHTML = `
        <td>${idx + 1}</td>
        <td>${r.programName || ''}</td>
        <td>${r.fileName || ''}</td>
        <td>${r.sourceStorage || ''}</td>
        <td>${r.destStorage || ''}</td>
        <td>${r.assignedNode || '-'}</td>
        <td>${escapeHtml(actionLabel)}</td>
        <td>${histFmtDate(r.updateTime)}</td>
        <td>
          ${histPill(label, tooltip, r.status)}
          ${canRetry ? `
            <button class="pend-hist-retry"
                    data-id="${r.historyId}"
                    style="margin-left:6px;padding:2px 8px;font-size:12px;">
              重試
            <button class="pend-hist-remove btn-retry"
                    data-id="${r.historyId}"
                    style="margin-left:6px;padding:2px 8px;font-size:12px;">
              移除
            </button>` : ''}
        </td>
        
      `;
      frag.appendChild(tr);
    });

    $histTbody.innerHTML = '';
    $histTbody.appendChild(frag);
    if ($histCount) $histCount.textContent = rows.length + ' 筆';
    histCurrentRows = rows;
  }
// ⭐ 歷史「移除」按鈕
root.addEventListener('click', async (e) => {
  const btn = e.target.closest('.pend-hist-remove');
  if (!btn) return;

  const historyId = Number(btn.dataset.id);
  if (!historyId) return;

  if (!confirm(`確定要移除 HistoryId=${historyId} 嗎？`)) return;

  try {
    const resp = await fetch(`/history/${historyId}/remove`, { method: 'POST' });

    const ct = resp.headers.get('content-type') || '';
    const payload = ct.includes('application/json')
      ? await resp.json()
      : await resp.text();

    if (!resp.ok) {
      const msg =
        typeof payload === 'string'
          ? payload
          : payload.message || JSON.stringify(payload);
      throw new Error(msg);
    }

    alert(payload.message || `已移除 HistoryId=${historyId}`);

    // ✅ 移除後刷新歷史（pending 不用動）
    histLastRenderSignature = '';
    loadHistoryInPending(false);

  } catch (err) {
    alert(`移除失敗（HistoryId=${historyId}）：${err.message || err}`);
  }
});
const $btnHistSearch = root.querySelector('#btnPendHistSearch');

// 1. 點擊搜尋鈕
$btnHistSearch.addEventListener('click', doHistQuery);

// 2. 搜尋框按 Enter
$histSearch.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    doHistQuery();
  }
});

// 3. 狀態或樓層變更時，也直接重新載入 API
$histStatus.addEventListener('change', doHistQuery);
$histGroup.addEventListener('change', doHistQuery);



  // 依狀態 / 樓層 / 關鍵字 過濾
  function histFilterAndRender(force = false) {
    if (!$histStatus || !$histSearch) return;

    const st    = $histStatus.value || 'all';
    const kw    = ($histSearch.value || '').trim().toLowerCase();
    // const group = $selGroup ? ($selGroup.value || 'all') : 'all';

   const group = $histGroup ? ($histGroup.value || 'all') : 'all';

    let rows = histAllRows;
    if (group !== 'all') {
  rows = rows.filter(r => inferGroupFromRow(r) === group.toUpperCase());
}
    if (st !== 'all') {
  if (st === 'success') {
    // 成功：搬移成功 + 刪除成功
    rows = rows.filter(r => r.status === 11 || r.status === 12);
  } else if (st === 'fail') {
    // 失敗：全部錯誤 + 取消
    rows = rows.filter(r => isHistErrorStatus(r.status));
    // isHistErrorStatus 已經包含：
    // 91,92,901,902,903,911,912,913,914,921,922,923,999
  }
}

    // if (group !== 'all') {
    //   rows = rows.filter(r =>
    //     (r.sourceGroup || '').toUpperCase() === group.toUpperCase()
    //   );
    // }

    if (kw) {
      rows = rows.filter(r =>
        (r.fileName || '').toLowerCase().includes(kw)
      );
    }

    const signature = JSON.stringify(rows.map(r => [r.historyId, r.status]));
    if (!force && signature === histLastRenderSignature) {
      return;
    }
    histLastRenderSignature = signature;
    histRenderRows(rows);
  }
 function inferGroupFromRow(r) {
    const s = String(r.sourceStorage || r.sourcePath || '').toUpperCase();
    if (s.includes('4F')) return '4F';
    if (s.includes('7F')) return '7F';
    return '';
  }
 async function loadHistoryInPending(silent = false) {
    if (!$histTbody) return;

    // 1. 處理 UI 載入狀態
    if (!silent) {
      if ($btnHistReload) {
        $btnHistReload.disabled = true;
        $btnHistReload.textContent = '載入中…';
      }
      $histTbody.innerHTML =
        `<tr><td colspan="9" style="text-align:center;color:#999;">載入中…</td></tr>`;
      if ($histCount) $histCount.textContent = '';
    }

    // 2. 取得參數
    const take  = $histTake ? (parseInt($histTake.value || '200', 10) || 200) : 200;
    const group = $histGroup ? $histGroup.value : 'all'; // 確保抓到 4F/7F/all
    const st    = $histStatus ? $histStatus.value : 'all';
    //  取得搜尋框字串
    const q     = $histSearch ? $histSearch.value.trim() : '';
    try {
      const qs = new URLSearchParams({
        take: String(take),
        ts: String(Date.now()),
        status: st,
        q: q // ✅ 帶入關鍵字 q
      });

      if (group !== 'all') {
        qs.set('group', group);
      }

      const resp = await fetch(`${API_HISTORY}?${qs.toString()}`, { 
        cache: 'no-store' 
      });

      if (!resp.ok) throw new Error('HTTP ' + resp.status);

      const payload = await resp.json();
      
      // 4. 解析資料：相容 { rows: [] } 或 [ ] 格式
      const rows = Array.isArray(payload.rows) ? payload.rows : (Array.isArray(payload) ? payload : []);

      histAllRows = rows;
      histLastRenderSignature = ''; // 確保會重畫
      histRenderRows(rows); // 直接畫出後端給的結果

    } catch (e) {
      console.error('Pending History Load Error:', e);
      if (!silent) {
        $histTbody.innerHTML =
          `<tr><td colspan="12" style="color:#c00; text-align:center;">載入失敗：${e.message}</td></tr>`;
      }
    } finally {
      if (!silent && $btnHistReload) {
        $btnHistReload.disabled = false;
        $btnHistReload.textContent = '重新整理';
      }
    }
  }
    // 全選 / 取消全選
  if ($chkAll) {
    $chkAll.addEventListener('change', () => {
      const checked = $chkAll.checked;
      const checks = Array.from(root.querySelectorAll('.chk-pending'));

      checks.forEach(chk => {
        chk.checked = checked;
        const id = Number(chk.dataset.id);
        if (!id) return;

        if (checked) {
          selectedIds.add(id);
        } else {
          selectedIds.delete(id);
        }
      });

      $chkAll.indeterminate = false;
    });
  }

  // ====== 偵測使用者開始操作任一個 select（優先級 / 樓層 / 並行數） ======
  root.addEventListener('mousedown', (e) => {
    const target = e.target;
    if (!target) return;

    if (target.classList.contains('pri-select') 
      // ||
        // target === $selGroup ||
        // target === $selParallel
      ) {
      isSelectBusy = true;
    }
  });

  // 點到非 select 的地方，也可以順便解除 busy
  root.addEventListener('click', (e) => {
    const t = e.target;
    if (!t) return;

    if (!t.classList.contains('pri-select') 
      // &&
        // t !== $selGroup &&
        // t !== $selParallel
      ) {
      isSelectBusy = false;
    }
  });
  

    // 歷史 reload / filter
  if ($btnHistReload) {
    $btnHistReload.addEventListener('click', () => {
      if ($histSearch) $histSearch.value = '';
      histLastRenderSignature = '';
      loadHistoryInPending(false);
    });
  }
  if ($histStatus) {
    $histStatus.addEventListener('change', () => histFilterAndRender(true));
  }
  if ($histTake) {
    $histTake.addEventListener('change', () => loadHistoryInPending(false));
  }
  // if ($histSearch) {
  //   $histSearch.addEventListener('input', () => histFilterAndRender(true));
  // }

  // ⭐ 歷史重試按鈕（綁在 root，避免衝到上面 pending 的 click）
  root.addEventListener('click', async (e) => {
    const btn = e.target.closest('.pend-hist-retry');
    if (!btn) return;

    const historyId = Number(btn.dataset.id);
    if (!historyId) return;

    if (!confirm(`確定要重試 #${historyId} 嗎？`)) return;

    try {
      const resp = await fetch(`/history/${historyId}/retry`, { method: 'POST' });
      if (!resp.ok) throw new Error(await resp.text());
      
      alert('重試任務已送出！');

      // ✅ 1) 立刻把這筆任務的舊進度清掉（避免沿用）
      const key = `TO-${historyId}`;
      progressState.delete(key);      // 清掉舊 percent
      lastSample.delete(key);
      speedState.delete(key);
      // ✅ 2) 立刻把畫面歸零（不等下一次 SSE）
      setProgressForKey(key, 0);

      // ✅ 3) 清掉檔名（避免顯示上一輪的 currentFile）
      setCurrentFileForKey(key, '');

      // ✅ 4) 再刷新列表
      pendingLastRenderSignature = ''; // 保險：強制 renderTableDiff 會更新
      
      loadPending(false);
      loadHistoryInPending(false);
    } catch (err) {
      alert('重試失敗：' + err.message);
    }
  });


 
  function getRealNameFromPath(path) {
      if (!path) return '';
      return String(path).split(/[/\\]/).pop() || '';
    }
  // ====== 建立/更新單筆 row（不重畫整張表） ======
//  function renderOrUpdateRow(r, seq) {
//   // ✅ 兼容大小寫（避免你後端回 HistoryId/Status 時前端拿不到）
//   const id  = Number(r.historyId ?? r.HistoryId ?? r.id ?? 0);
//   const key = `TO-${id}`; // row 的 key 是 "TO-<HistoryId>"

//   const existing = rowMap.get(id);

//   const actionRaw   = (r.action ?? r.Action ?? '-').toString().trim().toLowerCase();
//   const actionLabel = actionLabelByRow(r);

//   const programName = r.programName ?? r.ProgramName ?? '';
//   const fileName    = r.fileName    ?? r.FileName    ?? '';
//   const source      = r.sourceStorage ?? r.SourceStorage ?? r.sourcePath ?? r.SourcePath ?? '';
//   const dest        = r.destStorage   ?? r.DestStorage   ?? r.destPath   ?? r.DestPath   ?? '';

//   const node = r.assignedNode ?? r.AssignedNode ?? '';

//   const statusCode = Number(r.status ?? r.Status ?? r.fileStatus ?? r.FileStatus ?? 0);

//   const retryCount = typeof r.retryCount === 'number'
//     ? r.retryCount
//     : (typeof r.RetryCount === 'number' ? r.RetryCount : 0);

//   const retryCode = (typeof r.retryCode === 'number'
//     ? r.retryCode
//     : (typeof r.RetryCode === 'number' ? r.RetryCode : null));

//   const retryMessage = r.retryMessage ?? r.RetryMessage ?? '';

//   const percent  = progressState.get(key) ?? 0;
//   const priority = r.priority ?? r.Priority;

//   const isChecked = selectedIds.has(id);

//   const hasActiveProgress = percent > 0 && percent < 100;
//   let statusText = hasActiveProgress ? '執行中' : '排隊中';
//   let retryHtml  = '';
//   const isActive = hasActiveProgress;

//   const isPhase2 = (statusCode === 24 || statusCode === 27);
//   const tagHtml  = isPhase2 ? '<span class="tag-badge">回遷</span>' : '';

//   // ⭐ 先保留舊的檔名，避免每次重畫把它洗掉
//   let realFileName = '';
//   if (existing) {
//     const oldFileEl = existing.querySelector('.progress-file');
//     if (oldFileEl) realFileName = oldFileEl.textContent || '';
//   }

//   if (!hasActiveProgress && retryCount > 0) {
//     statusText = `重試等待中（第 ${retryCount} 次）`;

//     const codePart = (retryCode != null) ? `(${retryCode})` : '';
//     const msgPart  = escapeHtml(retryMessage);

//     if (codePart || msgPart) {
//       const full = `最後錯誤${codePart}：${msgPart}`;
//       retryHtml = `<div class="retry-info" title="${full}">${full}</div>`;
//     }
//   }

//   // ✅ 你的最新版規則：
//   // - 24/27 只是「可撿」→ 還沒開始時可以取消
//   // - move 也只在「開始後」禁取消
//   // started 的判斷：status=1/2 或 progress 已動（percent>0 當保險）
//   const isMove = (actionRaw === 'move');
//   const isPhase2Pending = (statusCode === 24 || statusCode === 27);

//   const started = (statusCode === 1 || statusCode === 2) || (percent > 0);

//   const cannotCancel = (isMove || isPhase2Pending) && started;

//   // 取消按鈕顯示 / 樣式
//   const cancelText  = cannotCancel ? '執行中不可取消' : '取消';
//   const cancelStyle = `
//     padding:4px 8px;
//     font-size:12px;
//     background:${cannotCancel ? '#666' : '#b42318'};
//     cursor:${cannotCancel ? 'not-allowed' : 'pointer'};
//     opacity:${cannotCancel ? '0.7' : '1'};
//   `;
//   const cancelAttrs = `
//     ${cannotCancel ? 'disabled' : ''}
//     ${cannotCancel ? 'title="任務已開始執行，無法取消"' : ''}
//   `;

//   let tr;
//   if (!existing) {
//     tr = document.createElement('tr');
//     rowMap.set(id, tr);
//     $tableBody.appendChild(tr);
//   } else {
//     tr = existing;
//   }

//   tr.innerHTML = `
//     <td>
//       <input type="checkbox"
//              class="chk-pending"
//              data-id="${id}"
//              ${isChecked ? 'checked' : ''} />
//     </td>
//     <td>${seq}${tagHtml}</td>
//     <td>
//       <select class="pri-select" data-id="${id}"
//               style="padding:2px 4px;font-size:12px;"
//               ${isActive ? 'disabled' : ''}>
//         ${[0,1,2,3,4,5,6,7,8,9,10].map(v =>
//           `<option value="${v}" ${v === priority ? 'selected' : ''}>${v}</option>`
//         ).join('')}
//       </select>
//     </td>
//     <td>${escapeHtml(programName)}</td>
//     <td>${escapeHtml(fileName)}</td>
//     <td>${escapeHtml(source)}</td>
//     <td>${escapeHtml(dest)}</td>
//     <td>${escapeHtml(node || '-')}</td>
//     <td>${statusText}${retryHtml}</td>
//     <td>${escapeHtml(actionLabel)}</td>
//     <td>
//       <div class="progress-wrap" data-progress-key="${key}" style="width:100%;box-sizing:border-box;">
//         <div class="progress-row" style="display:flex;align-items:center;gap:8px;width:100%;box-sizing:border-box;">
//           <div class="progress" style="flex:1 1 auto;min-width:0;width:100%;">
//             <div style="width:${percent}%"></div>
//           </div>
//           <div class="progress-text" style="width:52px;text-align:right;flex:0 0 auto;">
//             ${percent}%
//           </div>
//         </div>

//         <div class="progress-speed muted" style="margin-top:2px;"></div>
//         <div class="progress-file" style="margin-top:2px;">${escapeHtml(realFileName)}</div>
//       </div>
//     </td>
//     <td>
//       <button class="btn-cancel" data-id="${id}"
//               style="${cancelStyle}"
//               ${cancelAttrs}>
//         ${cancelText}
//       </button>
//     </td>
//   `;
// }
function renderOrUpdateRow(r, seq) {
  // 1. 基本資料宣告
  const id  = Number(r.historyId ?? r.HistoryId ?? r.id ?? 0);
  const key = `TO-${id}`;
  const existing = rowMap.get(id);

  const actionRaw   = (r.action ?? r.Action ?? '-').toString().trim().toLowerCase();
  const actionLabel = actionLabelByRow(r);

  const programName = r.programName ?? r.ProgramName ?? '';
  const fileName    = r.fileName    ?? r.FileName    ?? '';
  const source      = r.sourceStorage ?? r.SourceStorage ?? r.sourcePath ?? r.SourcePath ?? '';
  const dest        = r.destStorage   ?? r.DestStorage   ?? r.destPath   ?? r.DestPath   ?? '';
  const node        = r.assignedNode  ?? r.AssignedNode ?? '';

  const statusCode = Number(r.status ?? r.Status ?? r.fileStatus ?? r.FileStatus ?? 0);
  const percent    = progressState.get(key) ?? 0;
  const priority   = r.priority ?? r.Priority;
  const isChecked  = selectedIds.has(id);

  // 2. 狀態與標籤邏輯
  const hasActiveProgress = percent > 0 && percent < 100;
  const isActive = hasActiveProgress;
  let statusText = hasActiveProgress ? '執行中' : '排隊中';
  
  const isPhase2 = (statusCode === 24 || statusCode === 27);
  const tagHtml  = isPhase2 ? '<span class="tag-badge">回遷</span>' : '';

  // 3. 核心：判斷是否「執行中不可取消」
  // 規則：如果是 move (歸檔) 或 phase2 (回遷)，且已經開始 (started) 則不可取消
  const isMove = (actionRaw === 'move');
  const isPhase2Pending = (statusCode === 24 || statusCode === 27);
  const started = (statusCode === 1 || statusCode === 2) || (percent > 0);
  const cannotCancel = (isMove || isPhase2Pending) && started;

  // 🟢 如果不可取消，主動從已選取清單移除，確保批次操作安全
  if (cannotCancel && selectedIds.has(id)) {
    selectedIds.delete(id);
  }

  // 4. UI 樣式變數 (要在 innerHTML 之前定義好)
  const cancelText  = cannotCancel ? '執行中不可取消' : '取消';
  const cancelStyle = `
    padding:4px 8px;
    font-size:12px;
    background:${cannotCancel ? '#666' : '#b42318'};
    cursor:${cannotCancel ? 'not-allowed' : 'pointer'};
    opacity:${cannotCancel ? '0.7' : '1'};
    color:white; border:none; border-radius:4px;
  `;

  // 取得舊檔名 (避免重畫洗掉)
  let realFileName = '';
  if (existing) {
    const oldFileEl = existing.querySelector('.progress-file');
    if (oldFileEl) realFileName = oldFileEl.textContent || '';
  }

  // 重試資訊
  let retryHtml = '';
  const retryCount = r.retryCount ?? 0;
  if (!hasActiveProgress && retryCount > 0) {
    statusText = `重試等待中（第 ${retryCount} 次）`;
    const msg = r.retryMessage || '';
    if (msg) {
      retryHtml = `<div class="retry-info" title="${escapeHtml(msg)}">最後錯誤：${escapeHtml(msg)}</div>`;
    }
  }

  // 5. 取得或建立 TR 元件
  let tr;
  if (!existing) {
    tr = document.createElement('tr');
    rowMap.set(id, tr);
    $tableBody.appendChild(tr);
  } else {
    tr = existing;
  }

  // 6. 🟢 最終賦值 (只有這一次 innerHTML，左側 Checkbox 已連動)
  tr.innerHTML = `
    <td>
      <input type="checkbox"
             class="chk-pending"
             data-id="${id}"
             ${isChecked && !cannotCancel ? 'checked' : ''} 
             ${cannotCancel ? 'disabled' : ''} 
             title="${cannotCancel ? '任務執行中，無法勾選' : ''}"
             style="${cannotCancel ? 'cursor:not-allowed; opacity:0.5;' : ''}" />
    </td>
    <td>${seq}${tagHtml}</td>
    <td>
      <select class="pri-select" data-id="${id}"
              style="padding:2px 4px;font-size:12px;"
              ${isActive ? 'disabled' : ''}>
        ${[0,1,2,3,4,5,6,7,8,9,10].map(v =>
          `<option value="${v}" ${v === priority ? 'selected' : ''}>${v}</option>`
        ).join('')}
      </select>
    </td>
    <td>${escapeHtml(programName)}</td>
    <td>${escapeHtml(fileName)}</td>
    <td>${escapeHtml(source)}</td>
    <td>${escapeHtml(dest)}</td>
    <td>${escapeHtml(node || '-')}</td>
    <td>${statusText}${retryHtml}</td>
    <td>${escapeHtml(actionLabel)}</td>
    <td>
      <div class="progress-wrap" data-progress-key="${key}" style="width:100%;box-sizing:border-box;">
        <div class="progress-row" style="display:flex;align-items:center;gap:8px;width:100%;box-sizing:border-box;">
          <div class="progress" style="flex:1 1 auto;min-width:0;width:100%;">
            <div style="width:${percent}%"></div>
          </div>
          <div class="progress-text" style="width:52px;text-align:right;flex:0 0 auto;">
            ${percent}%
          </div>
        </div>
        <div class="progress-speed muted" style="margin-top:2px;"></div>
        <div class="progress-file" style="margin-top:2px;">${escapeHtml(realFileName)}</div>
      </div>
    </td>
    <td>
      <button class="btn-cancel" data-id="${id}"
              style="${cancelStyle}"
              ${cannotCancel ? 'disabled title="任務已開始執行，無法取消"' : ''}>
        ${cancelText}
      </button>
    </td>
  `;
}

    // ====== 傳輸速率 ======
  function fmtSpeed(bps) {
  if (!bps || bps <= 0) return '';
  const KB = 1024, MB = KB * 1024, GB = MB * 1024;
  if (bps >= GB) return (bps / GB).toFixed(2) + ' GB/s';
  if (bps >= MB) return (bps / MB).toFixed(2) + ' MB/s';
  if (bps >= KB) return (bps / KB).toFixed(1) + ' KB/s';
  return Math.round(bps) + ' B/s';
}

function setSpeedForKey(destKey, copiedBytes) {
  const key = String(destKey);
  const now = Date.now();

  const prev = lastSample.get(key);
  lastSample.set(key, { ts: now, copied: copiedBytes });

  if (!prev) return;

  const dt = (now - prev.ts) / 1000;
  const dc = copiedBytes - prev.copied;

  // 避免亂跳：時間太短/倒退/歸零就不算
  if (dt <= 0.15 || dc < 0) return;

  const instant = dc / dt; // bytes/sec

  // 小小平滑一下（EMA），不然網路會抖很明顯
  const old = speedState.get(key) ?? instant;
  const alpha = 0.35;
  const smooth = old * (1 - alpha) + instant * alpha;
  speedState.set(key, smooth);

  root.querySelectorAll(`.progress-wrap[data-progress-key="${key}"]`)
    .forEach(wrap => {
      const el = wrap.querySelector('.progress-speed');
      if (el) el.textContent = fmtSpeed(smooth);
    });
}
  // ====== 比對差異：新增/更新/刪除 ======
  function escapeHtml(text) {
  if (!text) return '';
  return String(text)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}
  function renderTableDiff() {
    // const selected = $selGroup.value || 'all';

    let list = allRows;   // 直接用全部，不再分樓層
 const signature = JSON.stringify(
    list.map(r => [
      r.historyId,
      r.status,
      (r.action || r.Action || ''), 
      r.priority,
      r.retryCount ?? 0,
      r.retryCode ?? null, (r.assignedNode ?? r.assigned_node ?? '')
    ])
  );
  if (signature === pendingLastRenderSignature) {
    return;
  }
  pendingLastRenderSignature = signature;
    const newIds = new Set(list.map(r => r.historyId));
    const oldIds = new Set(rowMap.keys());
     // ⭐ 計算這次列表的「簽名」，只看會影響畫面的欄位
 

  
    list.forEach((r, idx) => {
      const seq = idx + 1;
      renderOrUpdateRow(r, seq);
    });

   
    // 移除已不存在的 row
        // 把已經不存在的任務從 selectedIds 移除
    for (const id of Array.from(selectedIds)) {
      if (!newIds.has(id)) {
        selectedIds.delete(id);
      }
    }

    // 更新「全選」checkbox 的勾選 / indeterminate 狀態
    if ($chkAll) {
      if (list.length === 0) {
        $chkAll.checked = false;
        $chkAll.indeterminate = false;
      } else {
        const selectedCount = list.filter(r => selectedIds.has(r.historyId)).length;

        if (selectedCount === 0) {
          $chkAll.checked = false;
          $chkAll.indeterminate = false;
        } else if (selectedCount === list.length) {
          $chkAll.checked = true;
          $chkAll.indeterminate = false;
        } else {
          $chkAll.checked = false;
          $chkAll.indeterminate = true;   // 部份選取
        }
      }
    }

    const frag = document.createDocumentFragment();
    list.forEach(r => {
      const tr = rowMap.get(r.historyId);
      if (tr) frag.appendChild(tr);
    });
    $tableBody.innerHTML = '';
    $tableBody.appendChild(frag);

    $count.textContent = list.length + ' 筆';
  }

  // ====== 抓 pending (不重畫整張 table) ======
  async function loadPending(isAuto = false) {
    // ⭐ 自動刷新 & 使用者正在操作 select → 跳過這次
    if (isAuto && isSelectBusy) {
      return;
    }

    // $btnReload.disabled = true;
    // $btnReload.textContent = '載入中…';

    try {
      const resp = await fetch(`${API_PENDING}?take=200&ts=${Date.now()}`, {
        cache: 'no-store'
      });
      if (!resp.ok) throw new Error('HTTP ' + resp.status);
      allRows = await resp.json();

      allRows.sort((a, b) => {
        const pa = priDb(a);
        const pb = priDb(b);
        if (pa !== pb) return pb - pa;
        return a.historyId - b.historyId;
      });

      renderTableDiff();  // 差異更新，不跳動
    } catch (err) {
      console.error(err);
      $tableBody.innerHTML =
        `<tr><td colspan="12" style="color:#c00;">載入失敗：${err.message}</td></tr>`;
    } finally {
      $btnReload.disabled = false;
      $btnReload.textContent = '重新整理';
    }
  }

  // ====== 進度更新：這裡用 "destKey" 原樣（很重要！不要再多加 TO-） ======
  function setProgressForKey(destKey, percent) {
    const key = String(destKey);  // 例如 "TO-7"
    const p = Math.max(0, Math.min(100, Math.round(percent || 0)));

    progressState.set(key, p);

    root.querySelectorAll(`.progress-wrap[data-progress-key="${key}"]`)
      .forEach(wrap => {
        const bar = wrap.querySelector('.progress > div');
        const txt = wrap.querySelector('.progress-text');
        if (bar) bar.style.width = p + '%';
        if (txt) txt.textContent = p + '%';

        const tr  = wrap.closest('tr');
        if (!tr) return;

        const statusCell = tr.children[8];
        const sel        = tr.querySelector('.pri-select');

        const isActive = p > 0 && p < 100;

        if (statusCell) {
          const current = statusCell.textContent || '';

          if (p >= 100) {
            speedState.delete(key);
            lastSample.delete(key);
            statusCell.textContent = '完成';
          } else if (isActive) {
            statusCell.textContent = '執行中';
          } else {
            if (!current.startsWith('重試等待中')) {
              statusCell.textContent = '排隊中';
            }
          }
        }

        if (sel) {
          sel.disabled = isActive;
        }
      });

    if (p >= 100 && !hasScheduledReloadAfterDone) {
      hasScheduledReloadAfterDone = true;
      setTimeout(() => {
        loadPending(true).finally(() => {   // ⭐ 自動刷新
          hasScheduledReloadAfterDone = false;
        });
      }, 1500);
    }
  }

  function setCurrentFileForKey(destKey, fileName) {
    const key = String(destKey);
    const p = progressState.get(key) ?? 0;
    if (p <= 0 && fileName) return;
    // if (p <= 0) return;

    root.querySelectorAll(`.progress-wrap[data-progress-key="${key}"]`)
      .forEach(wrap => {
        const el = wrap.querySelector('.progress-file');
        if (el) el.textContent = fileName || '';
      });
  }
    // ====== 多選取消 ======
  if ($btnCancelSelected) {
    $btnCancelSelected.addEventListener('click', async () => {
      // 收集所有勾選的 historyId
       const ids = Array.from(selectedIds);;

      if (ids.length === 0) {
        alert('請先勾選要取消的任務');
        return;
      }

      if (!confirm(`確定要取消已勾選的 ${ids.length} 筆任務嗎？`)) {
        return;
      }

      let okCount = 0;
      let failCount = 0;
      let failMsgs = [];

      for (const id of ids) {
        try {
          const resp = await fetch(`/jobs/${id}/cancel`, { method: 'POST' });
          if (!resp.ok) {
            const txt = await resp.text();
            failCount++;
            failMsgs.push(`#${id}：${txt}`);
          } else {
            okCount++;
          }
        } catch (err) {
          failCount++;
          failMsgs.push(`#${id}：${err.message}`);
        }
      }

      let msg = `已成功取消 ${okCount} 筆`;
      if (failCount > 0) {
        msg += `，失敗 ${failCount} 筆。\n\n${failMsgs.join('\n')}`;
      }
      alert(msg);

       // 取消完成後清空選取，重新載入列表
      selectedIds.clear();
      if ($chkAll) {
        $chkAll.checked = false;
        $chkAll.indeterminate = false;
      }
      loadPending(false);
    });
  }
  // ====== 單筆 checkbox 勾選 / 取消 ======
  root.addEventListener('change', (e) => {
    const target = e.target;
    if (!target || !target.classList || !target.classList.contains('chk-pending')) return;

    const id = Number(target.dataset.id);
    if (!id) return;

    if (target.checked) {
      selectedIds.add(id);
    } else {
      selectedIds.delete(id);
    }

    // 更新全選勾勾
    if ($chkAll) {
      const checks = Array.from(root.querySelectorAll('.chk-pending'));
      const checkedCount = checks.filter(c => c.checked).length;

      if (checkedCount === 0) {
        $chkAll.checked = false;
        $chkAll.indeterminate = false;
      } else if (checkedCount === checks.length) {
        $chkAll.checked = true;
        $chkAll.indeterminate = false;
      } else {
        $chkAll.checked = false;
        $chkAll.indeterminate = true;
      }
    }
  });
  // ====== 取消按鈕 ======
  document.addEventListener('click', async (e) => {
    const btn = e.target.closest('.btn-cancel');
    if (!btn) return;

    const historyId = Number(btn.dataset.id);
    if (!historyId) return;

    if (!confirm(`確定要取消 #${historyId} 嗎？`)) return;

    try {
      const resp = await fetch(`/jobs/${historyId}/cancel`, {
        method: "POST"
      });

      if (!resp.ok) throw new Error(await resp.text());

      alert(`已取消 #${historyId}`);
      loadPending(false);   // 手動刷新
    } catch (err) {
      alert('取消失敗：' + err.message);
    }
  });

  // ====== 優先級下拉選單變更 ======
  document.addEventListener('change', async (e) => {
    const sel = e.target;
    if (!sel.classList.contains('pri-select')) return;

    isSelectBusy = false;  // ⭐ 選完優先級 → 解鎖

    const historyId = Number(sel.dataset.id);
    const newValue  = Number(sel.value);
    if (!historyId || isNaN(newValue)) return;

    const row = allRows.find(r => r.historyId === historyId);
    const current = (typeof row?.priority === 'number' && !isNaN(row.priority))
      ? row.priority
      : 0;

    if (newValue === current) return;

    if (newValue < 1 || newValue > 10) {
      alert('優先級範圍為 0～10');
      sel.value = String(current);
      return;
    }

    const delta = newValue - current;
    await adjustPriority(historyId, delta);
  });

  async function adjustPriority(historyId, delta) {
    try {
      const resp = await fetch('/jobs/priority', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ historyId, delta })
      });

      if (!resp.ok) {
        const txt = await resp.text();
        throw new Error(txt);
      }

      const result = await resp.json();

      if (typeof result.priority === 'number') {
        const row = allRows.find(r => r.historyId === historyId);
        if (row) {
          row.priority = result.priority;
        }
      }

      allRows.sort((a, b) => {
        const pa = priDb(a);
        const pb = priDb(b);
        if (pa !== pb) return pb - pa;
        return a.historyId - b.historyId;
      });

      renderTableDiff();
    } catch (err) {
      console.error(err);
      alert('更新優先級失敗：' + err.message);
    }
  }

  // // 並行數 change：也順便解除 busy（選完了）
  // $selParallel.addEventListener('change', () => {
  //   isSelectBusy = false;
  // });

  // ====== 並行數「套用」 ======
  // $btnSetPar.addEventListener('click', async () => {
  //   const v = parseInt($selParallel.value, 10);
  //   if (isNaN(v)) return;

  //   try {
  //     const resp = await fetch(API_CONCUR, {
  //       method: 'POST',
  //       headers: { 'Content-Type': 'application/json' },
  //       body: JSON.stringify(v)
  //     });

  //     if (!resp.ok) {
  //       const txt = await resp.text();
  //       alert('更新失敗：' + txt);
  //       return;
  //     }

  //     const data = await resp.json();
  //     alert('並行數已更新為：' + data.current + '\n新任務會用新的設定。');
  //   } catch (err) {
  //     console.error(err);
  //     alert('呼叫 API 失敗：' + err.message);
  //   }
  // });

  // ====== SSE listener ======
  function startProgressListener() {
    let es;

    function connect() {
      es = new EventSource(API_EVENTS);
      if (statusLine) {
        statusLine.textContent = '（已連線進度事件）';
      }

      es.addEventListener('progress', (e) => {
        try {
          const jobs = JSON.parse(e.data);
          if (!Array.isArray(jobs)) return;

          for (const job of jobs) {
            if (!Array.isArray(job.targets)) continue;

            for (const t of job.targets) {
              if (!t.destId) continue;
              setProgressForKey(t.destId, t.percent);
              // ✅ 用 copied 算速率
             if (typeof t.copied === 'number') {
              setSpeedForKey(t.destId, t.copied);
            }
              // 2) 如果有帶目前檔案路徑，就顯示副檔名
        if (t.currentFile) {                     // ← 或 t.fileName，看你後端欄位
          const name = getRealNameFromPath(t.currentFile);
          setCurrentFileForKey(t.destId, name);
            }
          }}
        } catch (err) {
          console.warn('progress parse error', err);
        }
      });

      es.onerror = () => {
        if (statusLine) {
          statusLine.textContent = '（進度事件斷線，重試中…）';
        }
        try { es.close(); } catch {}
        setTimeout(connect, 1500);
      };
    }

    connect();
  }



  // ====== 自動刷新（例如每 5 秒） ======
  const AUTO_REFRESH_MS = 30000;
  let autoRefreshTimer = null;

  function startAutoRefresh() {
    if (autoRefreshTimer) return;
    autoRefreshTimer = setInterval(() => {
      loadPending(true);   // ⭐ 自動刷新
    }, AUTO_REFRESH_MS);
  }
if ($btnReload) {
  $btnReload.addEventListener('click', () => {
    pendingLastRenderSignature = ''; // 強制讓 renderTableDiff 會更新
    loadPending(false);
  });
}
  // ====== 啟動 ======
  loadPending(false);
  // loadConcurrency();
  startProgressListener();
  startAutoRefresh();
// ⭐ 歷史區塊初次載入 + 每 5 秒靜默刷新
  loadHistoryInPending(false);
  setInterval(() => {
    loadHistoryInPending(true);
  }, 5000);
 
  
  // tab 切換回來時：手動刷新一次
  window.addEventListener('pending-reload', () => {
    // 🔼 上半部 Pending：重新載入
    loadPending(false);

    // 🔽 下半部歷史：清空搜尋 & 狀態，強制重畫
    if ($histSearch) $histSearch.value = '';
    if ($histStatus) $histStatus.value = 'all';
    if ($histTake)   $histTake.value   = '200';
    histLastRenderSignature = '';   // 讓下一次 filter 一定會重畫
    loadHistoryInPending(false);

    // （可選）清掉多選的勾勾
    selectedIds.clear();
    if ($chkAll) {
      $chkAll.checked = false;
      $chkAll.indeterminate = false;
    }
  });
}
