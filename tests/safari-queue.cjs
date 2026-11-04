const fs = require('fs');
const vm = require('vm');
const assert = require('assert');

async function test(authFailure) {
  const elements = {};
  const element = id => elements[id] ??= { textContent: '', hidden: false, disabled: false, files: [], append(row) { this.rows.push(row); }, replaceChildren() { this.rows = []; }, rows: [] };
  const document = { documentElement: {}, getElementById: element, querySelectorAll: () => [], createElement: () => ({ textContent: '' }) };
  const files = ['bad', 'good', 'last'].map(name => ({ name, size: 0 }));
  element('file').files = files;
  const called = [];
  const response = (status, value = {}) => ({ status, ok: status >= 200 && status < 300, json: async () => value });
  const context = {
    document, navigator: { language: 'en' }, location: { protocol: 'https:', hash: '', pathname: '/' }, history: { replaceState() {} },
    localStorage: { getItem: () => 'en', setItem() {} }, AbortController, setTimeout, clearTimeout,
    window: { sha256() {} }, Worker: class { postMessage() { queueMicrotask(() => this.onmessage({ data: { hash: 'A'.repeat(64) } })); } terminate() {} },
    fetch: async (path, options) => {
      if (path === '/v1/session') return response(200);
      if (path === '/v1/transfers') {
        const { name } = JSON.parse(options.body); called.push(name);
        return name === 'bad' ? response(authFailure ? 401 : 400) : response(200, { completed: true });
      }
      throw Error('Unexpected request: ' + path);
    }
  };
  vm.runInNewContext(fs.readFileSync('src/Aktarim.Desktop/Web/app.js', 'utf8'), context);
  await new Promise(resolve => setImmediate(resolve));
  await element('send').onclick();
  assert.deepStrictEqual(called, authFailure ? ['bad'] : ['bad', 'good', 'last']);
  assert.match(element('files').rows[0].textContent, /Could not complete/);
  if (!authFailure) assert.match(element('files').rows[1].textContent, /Completed/);
  else assert.match(element('status').textContent, /session is invalid/);
}
(async () => { await test(false); await test(true); console.log('PASS Safari queue continues after file failure and stops on invalid session'); })().catch(e => { console.error(e); process.exitCode = 1; });
