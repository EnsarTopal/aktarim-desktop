importScripts('/sha256.min.js');
self.onmessage = async ({data:file}) => {
  try {
    const hash = sha256.create(), size = 4 * 1024 * 1024;
    for (let offset = 0; offset < file.size; offset += size) {
      hash.update(new Uint8Array(await file.slice(offset, offset + size).arrayBuffer()));
      self.postMessage({progress: Math.min(file.size, offset + size)});
    }
    self.postMessage({hash: hash.hex()});
  } catch { self.postMessage({error: true}); }
};
