const API = '/api/config/ftp';

export function initFtp(root) {
  root.innerHTML = `
    <div class="toolbar">
      <button id="btnAdd">➕ 新增 IC</button>
      <button id="btnSave">💾 儲存設定</button>
      <span id="msg" class="muted"></span>
    </div>

    <table class="table">
      <thead>
        <tr>
          <th>Key</th>
          <th>Host</th>
          <th>Port</th>
          <th>BasePath</th>
          <th>User</th>
          <th>Pass</th>
          <th></th>
        </tr>
      </thead>
      <tbody id="rows"></tbody>
    </table>
  `;

  const $rows = root.querySelector('#rows');
  const $msg  = root.querySelector('#msg');

  let data = {};

  async function load() {
    const res = await fetch(API);
    data = await res.json();
    render();
  }

  function render() {
    $rows.innerHTML = '';
    Object.entries(data).forEach(([key, v]) => {
      const tr = document.createElement('tr');
      tr.innerHTML = `
        <td><input value="${key}"></td>
        <td><input value="${v.host}"></td>
        <td><input type="number" value="${v.port}"></td>
        <td><input value="${v.basePath}"></td>
        <td><input value="${v.user}"></td>
        <td><input value="${v.pass}"></td>
        <td><button class="del">❌</button></td>
      `;
      tr.querySelector('.del').onclick = () => tr.remove();
      $rows.appendChild(tr);
    });
  }

  root.querySelector('#btnAdd').onclick = () => {
    data['NEW-IC'] = { host:'', port:21, basePath:'/', user:'', pass:'' };
    render();
  };

  root.querySelector('#btnSave').onclick = async () => {
    const rows = [...$rows.children];
    const payload = {};
    rows.forEach(r => {
      const i = r.querySelectorAll('input');
      payload[i[0].value] = {
        host: i[1].value,
        port: Number(i[2].value),
        basePath: i[3].value,
        user: i[4].value,
        pass: i[5].value
      };
    });

    const resp = await fetch(API, {
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body: JSON.stringify(payload)
    });

    $msg.textContent = resp.ok ? '✅ 已儲存' : '❌ 儲存失敗';
  };

  load();
}
