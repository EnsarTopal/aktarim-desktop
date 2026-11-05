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
  assert.match(element('files').rows[0].textContent, authFailure ? /Paused/ : /Could not complete/);
  if (!authFailure) assert.match(element('files').rows[1].textContent, /Completed/);
  else assert.match(element('status').textContent, /session is invalid/);
}
async function paused(mode) {
  const elements = {}; const element = id => elements[id] ??= { textContent:'', hidden:false, disabled:false, files:[], rows:[], append(row){this.rows.push(row)}, replaceChildren(){this.rows=[]} };
  element('file').files = [{name:'one.mov',size:0},{name:'two.mov',size:0}]; let attempts=0;
  const response = status => ({status,ok:status>=200&&status<300,json:async()=>({})});
  const context = { document:{documentElement:{},getElementById:element,querySelectorAll:()=>[],createElement:()=>({textContent:''})},navigator:{language:'en'},location:{protocol:'https:',hash:'',pathname:'/'},history:{replaceState(){}},localStorage:{getItem:()=> 'en',setItem(){}},AbortController, setTimeout:(f,ms)=>{if(ms<180000)queueMicrotask(f);return 1},clearTimeout(){},window:{sha256(){}},Worker:class{postMessage(){queueMicrotask(()=>this.onmessage({data:{hash:'A'.repeat(64)}}))}terminate(){}},fetch:async path=>{if(path==='/v1/session')return response(200);attempts++;if(mode==='offline')throw Error('network');return response(507)} };
  vm.runInNewContext(fs.readFileSync('src/Aktarim.Desktop/Web/app.js','utf8'),context); await new Promise(resolve=>setImmediate(resolve)); await element('send').onclick();
  assert.strictEqual(attempts,mode==='offline'?3:1); assert.match(element('files').rows[0].textContent,/Paused/); assert.match(element('files').rows[1].textContent,/Waiting/); assert.match(element('status').textContent,mode==='offline'?/queue is paused/:/disk is full/);
}
(async () => { await test(false); await test(true); await paused('offline'); await paused('disk'); console.log('PASS Safari queue file errors, auth, bounded connection retry and disk-full pause'); })().catch(e => { console.error(e); process.exitCode = 1; });
