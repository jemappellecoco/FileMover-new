// history.js
const API_HISTORY = '/history';

export function initHistory(root) {
  root.innerHTML = `
    <div class="toolbar">
      <!--<label>狀態：
        <select id="selHistStatus">
          <option value="all">全部</option>
          <optgroup label="搬移 Move">
            <option value="11">搬移成功 (11)</option>
            <option value="911">搬移失敗－找不到來源 (911)</option>
            <option value="912">搬移失敗－檔案使用中 (912)</option>
            <option value="913">搬移失敗－權限不足 (913)</option>
            <option value="914">搬移失敗－找不到目的地 (914)</option>
            <option value="91">搬移失敗－其他 (91)</option>
          </optgroup>
          <optgroup label="刪除 Delete">
            <option value="12">刪除成功 (12)</option>
            <option value="921">刪除失敗－找不到來源 (921)</option>
            <option value="922">刪除失敗－檔案使用中 (922)</option>
            <option value="923">刪除失敗－權限不足 (923)</option>
            <option value="92">刪除失敗－其他 (92)</option>
          </optgroup>
          <optgroup label="其他">
            <option value="14">等待回遷 (14)</option>
            <option value="17">等待回遷 (17)</option>
            <option value="999">使用者取消 (999)</option>
            <option value="901">資料庫錯誤[From] (901)</option>
            <option value="902">資料庫錯誤[To] (902)</option>
            <option value="903">未設定restore (903)</option>
          </optgroup>
        </select>
      </label>-->
    <label>狀態：
  <select id="selHistStatus">
    <option value="all">全部</option>
    <option value="success">成功</option>
    <option value="fail">失敗</option>
  </select>
</label>

    <!-- ⭐ 新增：樓層濾器（只有 history 頁用） -->
      <label style="margin-left:12px;">
        樓層：
        <select id="selHistGroup">
          <option value="all">全部</option>
          <option value="4F">4F</option>
          <option value="7F">7F</option>
        </select>
      </label>


      <label>顯示筆數：
        <input id="inpHistTake" type="number" min="10" step="10" value="200" style="width:100px;">
      </label>

      <label>搜尋檔名(UserBit)：
        <input id="inpHistSearch" type="text" placeholder="輸入關鍵字，例如 2101EC3F" style="width:220px;">
        <button id="btnHistSearch" style="margin-left:4px;">搜尋</button>
      </label>

      <button id="btnHistReload">重新整理</button>
    
      <span id="histRowCount" class="muted"></span>
    </div>
    <button id="btnHistPrev" style="margin-left:12px;">上一頁</button>
    <span id="histPageInfo" class="muted" style="margin:0 8px;"></span>
    <button id="btnHistNext">下一頁</button>
<label style="margin-left:12px;">
  開始：
  <span
    style="
      position: relative;
      display: inline-flex;
      align-items: center;
      gap: 6px;
    "
  >
    <input
      id="inpHistStartText"
      type="text"
      placeholder="YYYY/MM/DD"
      style="width:140px;"
    >

    <button
      id="btnHistStartPick"
      type="button"
      title="選開始日期"
    >📅</button>

    <!-- 🔑 日曆定位在「文字框正下方」 -->
    <input
      id="inpHistStartDate"
      type="date"
      style="
        position: absolute;
        left: 0;
        top: calc(100% + 2px);
        width: 1px;
        height: 1px;
        opacity: 0;
        z-index: 9999;
      "
    >
  </span>
</label>
<label style="margin-left:12px;">
  結束：
  <span
    style="
      position: relative;
      display: inline-flex;
      align-items: center;
      gap: 6px;
    "
  >
    <input
      id="inpHistEndText"
      type="text"
      placeholder="YYYY/MM/DD"
      style="width:140px;"
    >

    <button
      id="btnHistEndPick"
      type="button"
      title="選結束日期"
    >📅</button>

    <input
      id="inpHistEndDate"
      type="date"
      style="
        position: absolute;
        left: 0;
        top: calc(100% + 2px);
        width: 1px;
        height: 1px;
        opacity: 0;
        z-index: 9999;
      "
    >
  </span>
</label>

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
          <th style="width:150px;">Status</th>
        </tr>
      </thead>
      <tbody>
        <tr><td colspan="9" style="text-align:center;color:#999;">載入中…</td></tr>
      </tbody>
    </table>
  `;
//   function actionZh(action) {
//   const a = (action ?? '').toString().trim().toLowerCase();
//   if (a === 'copy')   return '搬檔'; 
//   if (a === 'delete') return '刪除';
//    if (a === 'move') return '歸檔';
//   if (!a) return '-';
//   return action; // 其他未知值就原樣顯示
// }
function actionLabelByRow(r) {
  // delete：只有確定有 ToStorageId 欄位才判斷
  const hasToSid = (r.toStorageId != null) || (r.ToStorageId != null);
  if (hasToSid) {
    const toSid = Number(r.toStorageId ?? r.ToStorageId);
    if (toSid === 0) return '刪除';
  }

  const destType = r.destType ?? r.DestType ?? r.toType ?? r.ToType;
  if (!destType) return '搬移';

  const t = String(destType).toUpperCase();
  if (t === 'L1' || t === 'L2') return '歸檔';
  if (t === 'DOWNLOAD')        return '下載';
  return '搬移';
}

  const $selStatus = root.querySelector('#selHistStatus');
  const $selGroup  = root.querySelector('#selHistGroup'); 
  const $inpTake   = root.querySelector('#inpHistTake');
  const $inpSearch = root.querySelector('#inpHistSearch');
  const $btnReload = root.querySelector('#btnHistReload');
  const $tbody     = root.querySelector('#histTable tbody');
  const $count     = root.querySelector('#histRowCount');

  let allRows = [];
  let currentRows = [];
  let lastRenderSignature = '';
  // 可以重試
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
 //錯誤碼
  function isErrorStatus(code) {
    const n = Number(code);
    return [
      91, 92,902,903,901,
      911, 912, 913, 914,
      921, 922, 923,915,
        // ★手動取消也可重試
    ].includes(n);
  }
// ===== 日期篩選（顯示 YYYY/MM/DD + 可點日曆）=====
const $startText = root.querySelector('#inpHistStartText');
const $endText   = root.querySelector('#inpHistEndText');
const $startDate = root.querySelector('#inpHistStartDate'); // hidden date for picker
const $endDate   = root.querySelector('#inpHistEndDate');   // hidden date for picker
const $btnStart  = root.querySelector('#btnHistStartPick');
const $btnEnd    = root.querySelector('#btnHistEndPick');

function pad2(n){ return String(n).padStart(2,'0'); }
function toYmd(d){ return `${d.getFullYear()}-${pad2(d.getMonth()+1)}-${pad2(d.getDate())}`; } // YYYY-MM-DD
function toYmdSlash(d){ return `${d.getFullYear()}/${pad2(d.getMonth()+1)}/${pad2(d.getDate())}`; } // YYYY/MM/DD
function addDays(d, days){
  const x = new Date(d.getFullYear(), d.getMonth(), d.getDate());
  x.setDate(x.getDate() + days);
  return x;
}
function parseSlash(s){
  // accept YYYY/MM/DD or YYYY-MM-DD
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
  if ($startText) $startText.value = toYmdSlash(dd);
  if ($startDate) $startDate.value = toYmd(dd);
}
function setEnd(d){
  const dd = new Date(d.getFullYear(), d.getMonth(), d.getDate());
  if ($endText) $endText.value = toYmdSlash(dd);
  if ($endDate) $endDate.value = toYmd(dd);
}

// ★ 用來判斷「結束日期是否被使用者手動改過」
let endUserEdited = false;

// ✅ default：結束 today，開始 today-6（共 7 天）
const today = new Date();
setEnd(today);           // 結束設為今天
setStart(addDays(today, -6)); // 開始設為今天往前推 6 天
endUserEdited = false;

// 打開原生日曆
$btnStart?.addEventListener('click', () => {
  if ($startDate?.showPicker) $startDate.showPicker();
  else $startDate?.click();
});
$btnEnd?.addEventListener('click', () => {
  if ($endDate?.showPicker) $endDate.showPicker();
  else $endDate?.click();
});

// 來源：picker 選開始
$startDate?.addEventListener('change', () => {
  const d = parseSlash($startDate.value);
  if (!d) return;

  // 如果結束還沒被手動改過，跟著維持 +6
  const oldEnd = parseSlash($endText?.value);
  setStart(d);

  if (!endUserEdited) {
    setEnd(addDays(d, 6));
  } else {
    // 已手動改過，不碰結束
    // 但如果你希望「結束 < 開始」時自動修正，也可加這段：
    const newEnd = parseSlash($endText?.value);
    if (newEnd && newEnd < d) setEnd(d);
  }

  currentPage = 1;
  loadHistory(false);
});

// 來源：picker 選結束
$endDate?.addEventListener('change', () => {
  const d = parseSlash($endDate.value);
  if (!d) return;
  endUserEdited = true;
  setEnd(d);

  // 結束不能早於開始（保護一下）
  const s = parseSlash($startText?.value);
  if (s && d < s) setEnd(s);

  currentPage = 1;
  loadHistory(false);
});

// 來源：手打開始（YYYY/MM/DD）
$startText?.addEventListener('change', () => {
  const d = parseSlash($startText.value);
  if (!d) {
    // 不合法就回復 hidden date 的值（或今天）
    const fallback = parseSlash($startDate?.value) || today;
    setStart(fallback);
    return;
  }
  setStart(d);

  if (!endUserEdited) {
    setEnd(addDays(d, 6));
  } else {
    const newEnd = parseSlash($endText?.value);
    if (newEnd && newEnd < d) setEnd(d);
  }

  currentPage = 1;
  loadHistory(false);
});

// 來源：手打結束（YYYY/MM/DD）
$endText?.addEventListener('change', () => {
  const d = parseSlash($endText.value);
  if (!d) {
    const fallback = parseSlash($endDate?.value) || addDays(parseSlash($startText?.value) || today, 6);
    setEnd(fallback);
    return;
  }
  endUserEdited = true;
  setEnd(d);

  const s = parseSlash($startText?.value);
  if (s && d < s) setEnd(s);

  currentPage = 1;
  loadHistory(false);
});


//   function pad2(n) {
//   return String(n).padStart(2, '0');
// }

function fmtDate(s) {
  if (!s) return '';
  const d = new Date(s);
  if (isNaN(d)) return s;

  const y = d.getFullYear();
  const m = pad2(d.getMonth() + 1);
  const day = pad2(d.getDate());
  const hh = pad2(d.getHours());
  const mm = pad2(d.getMinutes());
  const ss = pad2(d.getSeconds());

  return `${y}/${m}/${day} ${hh}:${mm}:${ss}`;
}

  function statusLabel(code) {
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
     if (n === 904) return '排程刪除失敗'
     if (n === 915) return '檔案大小不同';
    if (n === 13) return '等待歸檔';
    return String(code ?? '');
  }
function pill(label, tooltip, status) {
  const safeTip = tooltip
    ? String(tooltip).replace(/"/g, '&quot;')
    : '';

  let cls = 'status-pill';
  const n = Number(status);

  // ✅ 成功
  if (n === 11 || n === 12) {
    cls += ' ok';
  }
  // ❌ 失敗 / 取消 / 錯誤（含 915）
  else if (isErrorStatus(n)) {
    cls += ' fail';
  }
  // ⏳ 等待 / 其他
  else {
    cls += ' pending';
  }

  return `<span class="${cls}" title="${safeTip}">${label}</span>`;
}

  // function pill(label, tooltip) {
  //   const safeTip = tooltip
  //     ? String(tooltip).replace(/"/g, '&quot;')
  //     : '';

  //   let cls = 'status-pill';

  //   if (label.includes('成功'))
  //     cls += ' ok';
  //   else if (label.includes('失敗') || label.includes('取消') || label.includes('錯誤'))
  //     cls += ' fail';
  //   else if (label.includes('等待') || label.includes('未設定restore錯誤'))
  //     cls += ' pending';

  //   return `<span class="${cls}" title="${safeTip}">${label}</span>`;
  // }

  function renderRows(rows) {
    if (!rows.length) {
      $tbody.innerHTML =
        `<tr><td colspan="9" style="text-align:center;color:#999;">（沒有符合條件的紀錄）</td></tr>`;
      $count.textContent = '0 筆';
      currentRows = [];
      return;
    }

    const frag = document.createDocumentFragment();
    rows.forEach((r, idx) => {
      const label   = statusLabel(r.status); // pill 上顯示的「大分類」：成功 / 搬移失敗 / 刪除失敗...
      const detail  = r.statusText || label; // 後端傳來的詳細說明
      const tooltip = `${r.status} - ${detail}`; // 例如：912 - 搬移失敗－檔案使用中
      
      const canRetry = isRetryable(Number(r.status));

        // ✅ UserBit 顯示規則：
      // - 預設顯示 r.fileName（你目前欄位就是 UserBit）
      // - 但「刪除錯誤」且 fileId=0 時，改顯示 r.note
      let userbitText = r.fileName || '';

      const act = (r.action ?? '').toString().trim().toLowerCase();
      const fid = Number(r.fileId || 0);
      const st  = Number(r.status || 0);

      if (act === 'delete' && fid === 0 && isErrorStatus(st)) {
        userbitText = (r.note || '').trim() || userbitText; // ✅ note 優先
      }

      const tr = document.createElement('tr');
      tr.innerHTML = `
        <td>${idx + 1}</td>
        <td>${r.programName || ''}</td>
        <td>${r.fileName || ''}</td>
        <td>${r.sourceStorage || ''}</td>
        <td>${r.destStorage || ''}</td>
        <td>${r.assignedNode || '-'}</td>
        <td>${fmtDate(r.updateTime)}</td>
        <td>${actionLabelByRow(r)}</td>  
       <td>
          ${pill(label, tooltip, r.status)}
          ${canRetry
            ? `<button class="btn-retry"
                data-id="${r.historyId}"
                style="margin-left:6px;padding:2px 8px;font-size:12px;">
                重試
              </button>
               <button class="btn-remove"
      data-id="${r.historyId}"
      style="margin-left:6px;padding:2px 8px;font-size:12px;">
      移除
    </button>`
            : ''}
        </td>

      `;
      frag.appendChild(tr);
    });

    $tbody.innerHTML = '';
    $tbody.appendChild(frag);
    $count.textContent = rows.length + ' 筆';
    currentRows = rows;
  }

  // ⭐ 只在資料真的有變化時才重畫
  function filterAndRender(force = false) {
    const st = $selStatus.value || 'all';
    const kw = ($inpSearch.value || '').trim().toLowerCase();

    let rows = allRows;

    // if (st !== 'all') {
    //   rows = rows.filter(r => String(r.status) === st);
    // }
    if (st !== 'all') {
      if (st === 'success') {
        // 成功：搬移成功 + 刪除成功
        rows = rows.filter(r => Number(r.status) === 11 || Number(r.status) === 12);
      } else if (st === 'fail') {
        // 失敗：所有錯誤 & 取消
        const failCodes = [
          91, 92,              // 其他失敗
          901, 902, 903, 904,      // DB / restore 設定錯誤
          911, 912, 913, 914,  // 搬移失敗細項
          921, 922, 923,915,       // 刪除失敗細項
          999                  // 使用者取消
        ];
        rows = rows.filter(r => failCodes.includes(Number(r.status)));
      }
    }


    if (kw) {
        rows = rows.filter(r =>
    ((r.fileName || '').toLowerCase().includes(kw)) ||
    ((r.UserBit || '').toLowerCase().includes(kw))
  )
    }

    // 建立這次畫面的簽名（只抓幾個關鍵欄位）
    const signature = JSON.stringify(
      rows.map(r => [r.historyId, r.status,r.action ])
    );

    if (!force && signature === lastRenderSignature) {
      // 沒變化就不重畫 → 不會閃
      return;
    }

    lastRenderSignature = signature;
    renderRows(rows);
  }

  // ⭐ silent=true：背景刷新，不清空表格、不顯示「載入中」
    // ===== 分頁狀態 =====
  let currentPage = 1;
  let totalPages = 1;
  let total = 0;

  const $btnPrev   = root.querySelector('#btnHistPrev');
  const $btnNext   = root.querySelector('#btnHistNext');
  const $pageInfo  = root.querySelector('#histPageInfo');

  function updatePager() {
    if ($pageInfo) $pageInfo.textContent = `第 ${currentPage} / ${totalPages} 頁，共 ${total} 筆`;
    if ($btnPrev) $btnPrev.disabled = currentPage <= 1;
    if ($btnNext) $btnNext.disabled = currentPage >= totalPages;
  }

  async function loadHistory(silent = false) {
    if (!silent) {
      $btnReload.disabled = true;
      $btnReload.textContent = '載入中…';
      $tbody.innerHTML = `<tr><td colspan="9" style="text-align:center;color:#999;">載入中…</td></tr>`;
      $count.textContent = '';
    }

    const take  = parseInt($inpTake.value || '200', 10) || 200;
    const group = $selGroup ? ($selGroup.value || 'all') : 'all';
    const st    = $selStatus ? ($selStatus.value || 'all') : 'all';
    const q     = ($inpSearch?.value || '').trim();
    const sObj = parseSlash($startText?.value); // Date
    const eObj = parseSlash($endText?.value);   // Date

    let fromStr = '';
    let toStr   = '';

    if (sObj) fromStr = toYmd(sObj);

    // to 用「排他」：end + 1 day
    if (eObj) {
      const e2 = addDays(eObj, 1);
      toStr = toYmd(e2);
    }

    try {
      const url =
        `${API_HISTORY}?take=${take}` +
        `&page=${currentPage}` +
        `&group=${encodeURIComponent(group)}` +
        `&status=${encodeURIComponent(st)}` +
        `&q=${encodeURIComponent(q)}` +
        `&ts=${Date.now()}`+ 
        `&from=${encodeURIComponent(fromStr)}`+
         `&to=${encodeURIComponent(toStr)}`
        ;

      const resp = await fetch(url, { cache: 'no-store' });
      if (!resp.ok) throw new Error('HTTP ' + resp.status);

      // ✅ 後端回傳：{ total, rows, page, totalPages, take }
      const payload = await resp.json();

      total       = Number(payload.total || 0);
      totalPages  = Math.max(1, Number(payload.totalPages || 1));
      currentPage = Math.min(Math.max(1, Number(payload.page || 1)), totalPages);

      const rows = Array.isArray(payload.rows) ? payload.rows : [];
      allRows = rows;

      // 分頁模式：直接畫，不要再用前端 filter 切
      lastRenderSignature = ''; // 換頁視為新畫面
      renderRows(rows);
      updatePager();

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

  // ⭐ retry handler（綁 root）
  root.addEventListener("click", async (e) => {
    const btn = e.target.closest(".btn-retry");
    if (!btn) return;

    const historyId = Number(btn.dataset.id);
    if (!historyId) return;

    if (!confirm(`確定要重試這筆任務嗎？#${historyId} 嗎？`)) return;

    try {
      const resp = await fetch(`/history/${historyId}/retry`, { method: 'POST' });
      if (!resp.ok) throw new Error(await resp.text());

      alert("重試任務已送出！");
      // retry 後用正常模式 reload 一次
      loadHistory(false);

    } catch (err) {
      alert("重試失敗：" + err.message);
    }
      });
  // ⭐ remove handler（把 status 改 111）
root.addEventListener("click", async (e) => {
  const btn = e.target.closest(".btn-remove");
  if (!btn) return;

  const historyId = Number(btn.dataset.id);
  if (!historyId) return;

  if (!confirm(`確定要移除 HistoryId=${historyId} 嗎？`)) return;

  try {
    const resp = await fetch(`/history/${historyId}/remove`, { method: "POST" });

    const ct = resp.headers.get("content-type") || "";
    const payload = ct.includes("application/json")
      ? await resp.json()
      : await resp.text();

    if (!resp.ok) {
      const msg =
        typeof payload === "string"
          ? payload
          : payload.message || JSON.stringify(payload);
      throw new Error(msg);
    }

    // ✅ 成功一定顯示是哪一筆
    const msg =
      typeof payload === "string"
        ? `已移除 HistoryId=${historyId}`
        : payload.message || `已移除 HistoryId=${payload.historyId}`;

    alert(msg);
    loadHistory(false);

  } catch (err) {
    alert(`移除失敗（HistoryId=${historyId}）：${err.message || err}`);
  }
});


  // === 事件綁定 ===
  $btnReload.addEventListener('click', () => {
  if ($inpSearch) $inpSearch.value = '';   // 清除搜尋字
  lastRenderSignature = '';               // 強迫重畫
  loadHistory(false);
});
  $selStatus.addEventListener('change', () => {
  currentPage = 1;
  loadHistory(false);
});
  $inpTake.addEventListener('change', () => {
  currentPage = 1;
  loadHistory(false);
});

// 搜尋按鈕
const $btnSearch = root.querySelector('#btnHistSearch'); // 新增的搜尋鈕

// 建立一個統一的執行函式
const doQuery = () => {
  currentPage = 1; // 搜尋時一定要回到第一頁
  loadHistory(false); // 執行 API 請求（false 代表顯示「載入中」）
};

// 1. 搜尋按鈕點擊
$btnSearch.addEventListener('click', doQuery);

// 2. 搜尋框按 Enter 鍵
$inpSearch.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    doQuery();
  }
});
  // $inpSearch.addEventListener('input', () => filterAndRender(true));
  // ⭐ 樓層變更：重新打 API（而不是只前端 filter）
  if ($selGroup) {
    $selGroup.addEventListener('change', () => {
      lastRenderSignature = '';   // 換樓層當成新畫面
      loadHistory(false);
    });
  }
   $btnPrev?.addEventListener('click', () => {
  if (currentPage > 1) {
    currentPage--;
    loadHistory(false);
  }
});

$btnNext?.addEventListener('click', () => {
  if (currentPage < totalPages) {
    currentPage++;
    loadHistory(false);
  }
});
  
  // 第一次載入：正常模式（會顯示載入中）
  loadHistory(false);
  
  // tab 切換時：正常 reload 一次
  window.addEventListener('history-reload', () => {
    
  // 清除搜尋字
  if ($inpSearch) $inpSearch.value = '';

  // 狀態 → 回到「全部」
  if ($selStatus) $selStatus.value = 'all';

  // 顯示筆數 → 回到 200
  if ($inpTake) $inpTake.value = '200';

  // 樓層 → 回到「全部」
  if ($selGroup) $selGroup.value = 'all';

  // 強迫重畫
  lastRenderSignature = '';

  // 重新載入
  loadHistory(false);
});

  // ⭐ 每 5 秒靜默刷新：不清空畫面，只有有變化才重畫
  setInterval(() => loadHistory(true), 5000);
}