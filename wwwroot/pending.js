// pending.js
const API_PENDING   = '/jobs/pending';
const API_EVENTS    = '/api/progress/events';
const API_HISTORY   = '/history';

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
        assignedNode:  r.assignedNode ?? r.AssignedNode ?? "-",
        
        // 時間處理
        createTime:    r.createTime ?? r.CreateTime ?? "",
        fileType:      r.filetype ?? r.filetype ?? "PO" // 辨識 PO 或 CM
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

    // === 選取元素 ===
    const $tableBody = root.querySelector('#pendingTable tbody');
    const $histTbody = root.querySelector('#pendHistTable tbody');
    const $btnReload = root.querySelector('#btnPendingReload');
    const $chkAll = root.querySelector('#chkPendingAll');
    const $count = root.querySelector('#pendingCount');

    // === 全域狀態 ===
    let allRows = []; 
    const rowMap = new Map();
    const progressState = new Map();
    const selectedIds = new Set();
    let isSelectBusy = false;
    let pendingLastRenderSignature = '';

    // === 核心渲染函數 ===
    function renderOrUpdateRow(r, seq) {
        const id = r.historyId;
        const key = `TO-${id}`;
        const existing = rowMap.get(id);

        // 狀態邏輯
        const percent = progressState.get(key) ?? 0;
        const isActive = percent > 0 && percent < 100;
        let statusText = isActive ? '執行中' : '排隊中';
        
        // 取消邏輯 (歸檔與回遷開始後不可取消)
        const started = (r.status === 1 ) || (percent > 0);
        const cannotCancel = (r.action === 'move' || r.status === 24 || r.status === 27) && started;

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
            <td>${statusText} ${r.retryCount > 0 ? `<div class="retry-info">重試中(${r.retryCount})</div>` : ''}</td>
            <td>${r.action === 'delete' ? '刪除' : '搬移'}</td>
            <td>
                <div class="progress-wrap" data-progress-key="${key}">
                    <div class="progress"><div style="width:${percent}%"></div></div>
                    <span class="progress-text">${percent}%</span>
                </div>
            </td>
            <td>
                <button class="btn-cancel" data-id="${id}" style="background:${cannotCancel ? '#666' : '#b42318'}" ${cannotCancel ? 'disabled' : ''}>
                    ${cannotCancel ? '不可取消' : '取消'}
                </button>
            </td>`;
    }

    // === 資料載入 ===
    async function loadPending(isAuto = false) {
        if (isAuto && isSelectBusy) return;
        try {
            const resp = await fetch(`${API_PENDING}?ts=${Date.now()}`);
            const rawData = await resp.json();
            
            // 💡 關鍵：資料一進來就洗乾淨
            allRows = rawData.map(normalizeTask); 

            allRows.sort((a, b) => (b.priority - a.priority) || (a.historyId - b.historyId));
            
            const signature = JSON.stringify(allRows.map(r => [r.historyId, r.status, r.priority]));
            if (signature === pendingLastRenderSignature) return;
            pendingLastRenderSignature = signature;

            $tableBody.innerHTML = '';
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
}