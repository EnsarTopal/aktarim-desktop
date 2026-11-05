'use strict';
const words = {
 tr:{title:'Windows’a gönder',intro:'Dosyalar uygulamasından bir veya birden çok dosya seçin.',choose:'Dosyaları seç',send:'Gönder / devam et',hint:'Bağlantı kesilirse açık sayfa yeniden dener. Sayfayı kapatırsanız tekrar açıp aynı dosyaları seçin.',waiting:'Windows’ta isteği onaylayın…',paired:'Eşleştirildi. Dosyaları seçebilirsiniz.',hashing:'Dosya kontrol ediliyor',uploading:'Aktarılıyor',verifying:'SHA-256 doğrulanıyor…',done:'Doğrulandı ve kaydedildi: ',error:'Aktarım tamamlanamadı. Alıcıyı kontrol edip dosyaları yeniden seçin.',auth:'Oturum geçersiz veya süresi doldu. Windows’ta alıcıyı yeniden açıp yeni QR kodunu okutun.',select:'Önce dosya seçin.',selected:'dosya seçildi',reused:'doğrulanmış parça kullanıldı',offline:'Bağlantı bekleniyor; yeniden deneniyor…',completed:'Tamamlandı',failed:'Tamamlanamadı'},
 en:{title:'Send to Windows',intro:'Select one or more files from Files.',choose:'Choose files',send:'Send / resume',hint:'If disconnected, this page retries. If you close it, reopen and reselect the same files.',waiting:'Approve the request on Windows…',paired:'Paired. Choose your files.',hashing:'Checking file',uploading:'Uploading',verifying:'Verifying SHA-256…',done:'Verified and saved: ',error:'Transfer could not finish. Check the receiver and reselect the files.',auth:'The session is invalid or expired. Restart the receiver on Windows and scan its new QR code.',select:'Choose files first.',selected:'files selected',reused:'verified chunks reused',offline:'Waiting for connection; retrying…',completed:'Completed',failed:'Could not complete'}
};
let lang=localStorage.getItem('aktarim-language')||(navigator.language.toLowerCase().startsWith('tr')?'tr':'en'); if(!words[lang])lang='en';
Object.assign(words.tr,{disk:'Windows hedef diski dolu. Yer açıp gönderime devam edin.',paused:'Bağlantı kurulamadı. Kuyruk duraklatıldı; bağlantıyı düzeltip yeniden deneyin.',pending:'Bekliyor',rowPaused:'Duraklatıldı'});
Object.assign(words.en,{disk:'The Windows destination disk is full. Free space and resume.',paused:'Could not connect. The queue is paused; restore the connection and retry.',pending:'Waiting',rowPaused:'Paused'});
const $=id=>document.getElementById(id),status=$('status');
function translate(){document.documentElement.lang=lang;document.querySelectorAll('[data-i18n]').forEach(e=>e.textContent=words[lang][e.dataset.i18n]);$('security').textContent=location.protocol==='http:'?(lang==='tr'?'Şifresiz yerel aktarım: ağdaki kişiler verileri okuyabilir veya değiştirebilir.':'Unencrypted local transfer: people on the network can read or modify data.'):'';localStorage.setItem('aktarim-language',lang);selection()}
function selection(){$('selection').textContent=$('file').files.length?$('file').files.length+' '+words[lang].selected:''}
$('file').onchange=selection;$('tr').onclick=()=>{lang='tr';translate()};$('en').onclick=()=>{lang='en';translate()};translate();
function paired(){$('pair').hidden=true;$('transfer').hidden=false;status.textContent=words[lang].paired}
const json=obj=>({method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(obj)});
async function api(path,options={}){
 for(let attempt=0;;attempt++){
  try{
   const controller=new AbortController(), timer=setTimeout(()=>controller.abort(),180000);
   let response;try{response=await fetch(path,{...options,credentials:'same-origin',cache:'no-store',signal:controller.signal})}finally{clearTimeout(timer)}
   if(response.status===507)throw Error('disk');
   if(response.status>=500||response.status===429)throw Error('network');
   if(response.status===401)throw Error('auth');
   if(!response.ok)throw Error('request');
   return response;
  }catch(e){if(e.message==='auth'||e.message==='request'||e.message==='disk')throw e;if(attempt>=2)throw Error('offline');status.textContent=words[lang].offline;await new Promise(r=>setTimeout(r,1000*(attempt+1)))}
 }
}
async function pair(){
 const code=location.hash.slice(1);history.replaceState(null,'',location.pathname);
 try{await api('/v1/session',{method:'POST'});paired();return}catch(e){if(e.message!=='auth'){status.textContent=words[lang].error;return}}
 if(!/^[A-F0-9]{64}$/.test(code)){status.textContent=words[lang].auth;return}
 try{const {ticket}=await(await api('/v1/pair',json({code}))).json();status.textContent=words[lang].waiting;for(;;){await new Promise(r=>setTimeout(r,1000));const r=await(await api('/v1/pair/status',json({ticket}))).json();if(r.approved){paired();break}}}catch{status.textContent=words[lang].auth}
}pair();
function identity(file){return new Promise((resolve,reject)=>{const worker=new Worker('/worker.js');worker.onmessage=({data})=>{if(data.error){worker.terminate();reject(Error('hash'))}else if(data.hash){worker.terminate();resolve(data.hash)}else status.textContent=words[lang].hashing+' '+Math.floor(100*data.progress/file.size)+'%'};worker.onerror=()=>{worker.terminate();reject(Error('worker'))};worker.postMessage(file)})}
async function transfer(file,row){
 status.textContent=words[lang].hashing+': '+file.name;const digest=await identity(file);
 const transfer=await(await api('/v1/transfers',json({name:file.name,length:file.size,sha256:digest}))).json();
 if(transfer.completed){row.textContent=file.name+' — '+words[lang].completed;return}
 const completed=new Map(transfer.chunks.map(c=>[c.index,c]));let done=0;const total=Math.ceil(file.size/transfer.chunkSize);
 for(let i=0;i<total;i++){
  const length=Math.min(transfer.chunkSize,file.size-i*transfer.chunkSize);
  if(!completed.has(i)){const bytes=await file.slice(i*transfer.chunkSize,i*transfer.chunkSize+length).arrayBuffer();await api(`/v1/transfers/${transfer.id}/chunks/${i}`,{method:'PUT',headers:{'Content-Type':'application/octet-stream','X-Chunk-Sha256':window.sha256(bytes)},body:bytes})}
  done+=length;$('progress').value=file.size?100*done/file.size:100;row.textContent=file.name+' — '+Math.floor($('progress').value)+'%';status.textContent=words[lang].uploading+' '+Math.floor($('progress').value)+'% · '+completed.size+' '+words[lang].reused;
 }
 status.textContent=words[lang].verifying;const result=await(await api(`/v1/transfers/${transfer.id}/commit`,{method:'POST'})).json();$('progress').value=100;row.textContent=file.name+' — '+words[lang].completed;status.textContent=words[lang].done+result.name;
}
$('send').onclick=async()=>{
 const files=Array.from($('file').files);if(!files.length){status.textContent=words[lang].select;return}
 $('send').disabled=true;$('file').disabled=true;$('files').replaceChildren();
 let failed=false;
 const rows=files.map(file=>{const row=document.createElement('li');row.textContent=file.name+' — '+words[lang].pending;$('files').append(row);return row});
 try{for(let i=0;i<files.length;i++){const file=files[i],row=rows[i];$('progress').value=0;try{await transfer(file,row)}catch(e){if(['auth','disk','offline'].includes(e.message)){row.textContent=file.name+' — '+words[lang].rowPaused;throw e}row.textContent=file.name+' — '+words[lang].failed;failed=true}}if(failed)status.textContent=words[lang].error}
 catch(e){status.textContent=e.message==='auth'?words[lang].auth:e.message==='disk'?words[lang].disk:e.message==='offline'?words[lang].paused:words[lang].error}
 finally{$('send').disabled=false;$('file').disabled=false}
};
