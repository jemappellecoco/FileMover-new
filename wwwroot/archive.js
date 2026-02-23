// archive.js
const API_ARCHIVE      = '/archive';
const API_ARCHIVE_MARK = '/archive/mark';  // ✅ 你要新增的後端 API

export function initArchive(root) {
  root.innerHTML = `
    <div class="toolbar">
      <button id="btnArchiveReload">重新整理</button>
      <button id="btnArchiveMark" disabled>歸檔選取項目</button>
      <span id="archiveCount" class="muted"></span>
    </div>

    <table class="simple-table" id="archiveTable">
      <thead>
        <tr>
          <th style="width:40px;">
            <input type="checkbox" id="archiveChkAll" />
          </th>
          <th style="width:60px;">No.</th>
          <th>節目名稱</th>
          <th id="thFileName">檔名</th>
          <th>來源</th>
          <th>目的</th>
          <th>節點</th>
          <th>歸檔時間</th>
        </tr>
      </thead>
      <tbody>
        <tr><td colspan="8" style="text-align:center;color:#999;">載入中…</td></tr>
      </tbody>
    </table>
  `;

  const $tbody   = root.querySelector('#archiveTable tbody');
  const $count   = root.querySelector('#archiveCount');
  const $btn     = root.querySelector('#btnArchiveReload');
  const $btnMark = root.querySelector('#btnArchiveMark');
  const $chkAll  = root.querySelector('#archiveChkAll');
  const $thFileName = root.querySelector('#thFileName');


  let allRows = [];
  let sortDirection = null; // null | 'asc' | 'desc'
  let lastMeta = null;

  function updateButtons() {
    const checked = root.querySelectorAll('.row-check:checked').length;
    $btnMark.disabled = checked === 0;
    $btnMark.textContent = checked > 0 ? `歸檔選取項目（${checked} 筆）` : '歸檔選取項目';
  }

  function render(rows, meta) {
    $tbody.innerHTML = '';

    if (!rows.length) {
      $tbody.innerHTML =
        `<tr><td colspan="8" style="text-align:center;color:#999;">（目前沒有歸檔資料）</td></tr>`;
      $count.textContent = `0 筆`;
      $chkAll.checked = false;
      updateButtons();
      return;
    }

    const frag = document.createDocumentFragment();

    rows.forEach((r, idx) => {
      const tr = document.createElement('tr');
      tr.innerHTML = `
        <td>
          <input type="checkbox" class="row-check" data-hid="${r.historyId}" />
        </td>
        <td>${idx + 1}</td>
        <td>${r.programName || ''}</td>
        <td>${r.fileName || ''}</td>
        <td>${r.sourceStorage || ''}</td>
        <td>${r.destStorage || ''}</td>
        <td>${r.assignedNode || '-'}</td>
        <td>${r.updateTime ? new Date(r.updateTime).toLocaleString() : ''}</td>
      `;
      frag.appendChild(tr);
    });

    $tbody.appendChild(frag);

    // ✅ row checkbox change
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

    const total = meta?.total ?? rows.length;
    const page = meta?.page ?? 1;
    const totalPages = meta?.totalPages ?? 1;
    $count.textContent = `${total} 筆（第 ${page}/${totalPages} 頁）`;
  }
 // ✅ 只針對檔名排序，然後呼叫 render
  function applySortAndRender(meta) {
    lastMeta = meta || lastMeta;

    let rows = [...allRows];

    if (sortDirection) {
      rows.sort((a, b) => {
        const fa = (a.fileName || '').toLowerCase();
        const fb = (b.fileName || '').toLowerCase();

        if (fa < fb) return sortDirection === 'asc' ? -1 : 1;
        if (fa > fb) return sortDirection === 'asc' ? 1 : -1;
        return 0;
      });
    }

    render(rows, lastMeta);
  }

  // ✅ 綁「檔名」表頭：asc → desc → none
  function setupFileNameSortHeader() {
    if (!$thFileName) return;

    $thFileName.style.cursor = 'pointer';

    const refreshHeaderClass = () => {
      $thFileName.classList.remove('asc', 'desc');

      if (sortDirection) {
        $thFileName.classList.remove('sortable'); // 有排序就拿掉 ⇅
        $thFileName.classList.add(sortDirection);
      } else {
        $thFileName.classList.add('sortable');    // 無排序顯示 ⇅
      }
    };

    refreshHeaderClass();

    $thFileName.addEventListener('click', () => {
      if (sortDirection === null) sortDirection = 'asc';
      else if (sortDirection === 'asc') sortDirection = 'desc';
      else sortDirection = null;

      refreshHeaderClass();
      applySortAndRender(lastMeta);
    });
  }

  async function loadArchive() {
    $tbody.innerHTML =
      `<tr><td colspan="8" style="text-align:center;color:#999;">載入中…</td></tr>`;

    const resp = await fetch(`${API_ARCHIVE}?take=200&page=1&ts=${Date.now()}`, { cache: 'no-store' });
    if (!resp.ok) {
      $tbody.innerHTML = `<tr><td colspan="8" style="color:red;">載入失敗</td></tr>`;
      return;
    }

    const json = await resp.json();
    // const rows = Array.isArray(json.rows) ? json.rows : [];
    // render(rows, json);
    allRows = Array.isArray(json.rows) ? json.rows : [];
applySortAndRender(json);
  }

  async function doMarkArchive() {
    const ids = Array.from(root.querySelectorAll('.row-check:checked'))
      .map(ch => parseInt(ch.dataset.hid, 10))
      .filter(Number.isFinite);

    if (!ids.length) return;
    if (!confirm(`確定要歸檔 ${ids.length} 筆嗎？`)) return;

    $btnMark.disabled = true;
    $btnMark.textContent = '送出中…';

    try {
      const resp = await fetch(API_ARCHIVE_MARK, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ historyIds: ids })
      });
      if (!resp.ok) throw new Error('HTTP ' + resp.status);
      const result = await resp.json();
      alert(result.message || '已送出歸檔');

      await loadArchive();
      // 如果你希望 history tab 自動刷新：
      window.dispatchEvent(new Event('history-reload'));
    } catch (err) {
      console.error(err);
      alert('歸檔失敗：' + err.message);
    } finally {
      updateButtons();
    }
  }

 // tab 切換回來時：手動刷新一次（跟 pending 一樣）
  window.addEventListener('archive-reload', () => {
    // （可選）切回來先把全選取消、按鈕狀態重置
    $chkAll.checked = false;
    updateButtons();

    // 強制重新抓資料
    loadArchive();
  });
  // ✅ 全選
  $chkAll.addEventListener('change', () => {
    const on = $chkAll.checked;
    root.querySelectorAll('.row-check').forEach(ch => (ch.checked = on));
    updateButtons();
  });

  $btn.addEventListener('click', loadArchive);
  $btnMark.addEventListener('click', doMarkArchive);
  setupFileNameSortHeader();
  loadArchive();
}
