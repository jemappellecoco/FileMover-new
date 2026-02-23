// restore.js
const API_PHASE2_LIST  = '/jobs/phase2-pending';
const API_PHASE2_START = '/jobs/phase2/start';
const API_CANCEL_BATCH = '/jobs/cancel-batch'; 
export function initRestore(root) {
  // 建立畫面結構（對應原本 restore.html 的 toolbar + table）
  root.innerHTML = `
    <div class="toolbar">
      <button id="btnPhase2Reload">重新整理</button>
      <button id="btnPhase2Restore" disabled>回遷選取項目</button>
       <button id="btnPhase2Cancel" disabled>取消選取項目</button>
      <span id="restoreInfo" class="muted"></span>
    </div>

    <table id="restoreTable">
      <thead>
        <tr>
          <!-- 全選 -->
          <th style="width:40px;">
            <input type="checkbox" id="restoreChkAll" />
          </th>
          <!-- No. 流水號 -->
          <th style="width:60px;">No.</th>
          <th style="width:280px;">節目名稱</th>
          <th style="width:200px;">檔名(UserBit)</th>
          <th style="width:200px;">磁帶位置</th>
          <th>來源（RESTORE 路徑）</th>
          <th>目的（回遷目的地）</th>
        </tr>
      </thead>
      <tbody>
        <tr><td colspan="6" style="text-align:center;color:#999;">載入中…</td></tr>
      </tbody>
    </table>
  `;

  const $tbody        = root.querySelector('#restoreTable tbody');
  const $btnReload    = root.querySelector('#btnPhase2Reload');
  const $btnRestore   = root.querySelector('#btnPhase2Restore');
  const $chkAll       = root.querySelector('#restoreChkAll');
  const $info         = root.querySelector('#restoreInfo');
  const $btnCancel = root.querySelector('#btnPhase2Cancel'); 
 function updateButtons() {
  const checked = root.querySelectorAll('.row-check:checked').length;

  $btnRestore.disabled = checked === 0;
  $btnCancel.disabled  = checked === 0;

  $btnRestore.textContent = checked > 0 ? `回遷選取項目（${checked} 筆）` : '回遷選取項目';
  $btnCancel.textContent  = checked > 0 ? `取消選取項目（${checked} 筆）` : '取消選取項目';
}

  function render(rows) {
    $tbody.innerHTML = '';

    if (!rows || rows.length === 0) {
      $tbody.innerHTML =
        `<tr><td colspan="7" style="text-align:center;color:#999;">（目前沒有待回遷的任務）</td></tr>`;
      $info.textContent = '0 筆';
      $chkAll.checked = false;
      updateButtons();
      return;
    }

    const frag = document.createDocumentFragment();

    rows.forEach((r, idx) => {
      const tr = document.createElement('tr');

      tr.innerHTML = `
        <td>
          <input type="checkbox" class="row-check"
                 data-hid="${r.historyId}" />
        </td>
        <td>${idx + 1}</td>
        <td>${r.programName || ''}</td>
        <td>${r.fileName || ''}</td>
        <td>${r.tape || '-'}</td>
        <td>${r.sourceStorage || ''}</td>
        <td>${r.destStorage || ''}</td>
      `;
      frag.appendChild(tr);
    });

    $tbody.appendChild(frag);
    $info.textContent = rows.length + ' 筆';

    // 綁定 row checkbox 事件
    $tbody.querySelectorAll('.row-check').forEach(chk => {
      chk.addEventListener('change', () => {
        const all = root.querySelectorAll('.row-check').length;
        const checked = root.querySelectorAll('.row-check:checked').length;
        $chkAll.checked = (all > 0 && checked === all);
        updateButtons();
      });
    });

    $chkAll.checked = false;
    updateButtons();
  }
async function doCancel() {
  const ids = Array.from(root.querySelectorAll('.row-check:checked'))
    .map(ch => parseInt(ch.dataset.hid, 10))
    .filter(x => Number.isFinite(x));

  if (!ids.length) return;
  if (!confirm(`確定要取消 ${ids.length} 筆回遷任務嗎？`)) return;

  $btnRestore.disabled = true;
  $btnCancel.disabled  = true;
  $btnCancel.textContent = '取消中…';

  try {
    const resp = await fetch(API_CANCEL_BATCH, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ historyIds: ids })
    });
    if (!resp.ok) throw new Error('HTTP ' + resp.status);

    const result = await resp.json();
    alert(result.message || '已取消（999）');

    await loadPhase2();                 // ✅ 取消後刷新 Restore 清單
    window.dispatchEvent(new Event('history-reload')); // ✅ (可留著) 讓 history 自動刷新
  } catch (err) {
    console.error(err);
    alert('取消失敗：' + err.message);
  } finally {
    updateButtons();
  }
}
  async function loadPhase2() {
    $btnReload.disabled = true;
    $btnReload.textContent = '載入中…';
    $info.textContent = '';
    $tbody.innerHTML =
      `<tr><td colspan="7" style="text-align:center;color:#999;">載入中…</td></tr>`;

    try {
      const resp = await fetch(
        `${API_PHASE2_LIST}?take=300&ts=${Date.now()}`,
        { cache: 'no-store' }
      );
      if (!resp.ok) throw new Error('HTTP ' + resp.status);
      const rows = await resp.json();
      render(Array.isArray(rows) ? rows : []);
    } catch (err) {
      console.error(err);
      $tbody.innerHTML =
        `<tr><td colspan="7" style="color:#c00;">載入失敗：${err.message}</td></tr>`;
    } finally {
      $btnReload.disabled = false;
      $btnReload.textContent = '重新整理';
    }
  }

  async function doRestore() {
    const checked = Array.from(root.querySelectorAll('.row-check:checked'));
    if (checked.length === 0) return;

    const ids = checked
      .map(ch => parseInt(ch.dataset.hid, 10))
      .filter(x => !isNaN(x));

    if (!ids.length) return;
    if (!confirm(`確定要送出 ${ids.length} 筆回遷任務嗎？`)) return;

    $btnRestore.disabled = true;
    $btnRestore.textContent = '送出中…';

    try {
      const resp = await fetch(API_PHASE2_START, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ historyIds: ids })
      });
      if (!resp.ok) throw new Error('HTTP ' + resp.status);
      const result = await resp.json();
      alert(result.message || '已送出回遷');

      await loadPhase2(); // 重新載入列表
    } catch (err) {
      console.error(err);
      alert('回遷失敗：' + err.message);
    } finally {
      $btnRestore.disabled = false;
      updateButtons();
    }
  }

  // 全選 / 全不選
  $chkAll.addEventListener('change', () => {
    const on = $chkAll.checked;
    root.querySelectorAll('.row-check').forEach(ch => {
      ch.checked = on;
    });
    updateButtons();
  });

  $btnReload.addEventListener('click', loadPhase2);
  $btnRestore.addEventListener('click', doRestore);
  $btnCancel.addEventListener('click', doCancel); 
  // 初次載入
  loadPhase2();
 window.addEventListener("restore-reload", () => {
  loadPhase2();
});

}
