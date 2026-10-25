import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { EventEmitter } from 'node:events';
import { launchWindow } from '../scripts/release/launch-process.mjs';

async function service(t) {
  let stops=0;
  const server=http.createServer((req,res)=>res.end('ok'));
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const port=server.address().port;
  t.after(async()=>{if(server.listening)await new Promise(resolve=>server.close(resolve));});
  return {server,worker:{stop(){stops++;}},get stops(){return stops;},port};
}
test('a synchronous window launch failure stops its worker and releases the service port',async t=>{
  const app=await service(t);
  await assert.rejects(launchWindow({executable:'blocked.exe',label:'the link drop box',app,spawnProcess(){throw Object.assign(new Error('spawn UNKNOWN'),{code:'UNKNOWN'});}}),/Could not open the link drop box/);
  assert.equal(app.stops,1); assert.equal(app.server.listening,false);
  const next=http.createServer(); await new Promise((resolve,reject)=>{next.once('error',reject);next.listen(app.port,'127.0.0.1',resolve);});
  await new Promise(resolve=>next.close(resolve));
});
test('an asynchronous spawn error also cleans up its service and signal handlers',async t=>{
  const app=await service(t),signals=new EventEmitter();
  await assert.rejects(launchWindow({executable:'blocked.exe',label:'the link drop box',app,signals,spawnProcess(){const child=new EventEmitter();queueMicrotask(()=>child.emit('error',new Error('denied')));return child;}}),/denied/);
  assert.equal(app.stops,1); assert.equal(app.server.listening,false);
  assert.equal(signals.listenerCount('SIGINT'),0);assert.equal(signals.listenerCount('SIGTERM'),0);
});
test('a real child process exiting unsuccessfully is reported and cannot leave a service running',async t=>{
  const app=await service(t);
  await assert.rejects(launchWindow({executable:process.execPath,args:['-e','process.exit(23)'],label:'the link drop box',app}),/code 23/);
  assert.equal(app.stops,1);assert.equal(app.server.listening,false);
});
test('closing a window normally shuts down only the service owned by that launch',async t=>{
  const app=await service(t);
  await launchWindow({executable:process.execPath,args:['-e','process.exit(0)'],label:'the link drop box',app});
  assert.equal(app.stops,1); assert.equal(app.server.listening,false);
  const reused=await service(t);
  await launchWindow({executable:process.execPath,args:['-e','process.exit(0)'],label:'the link drop box'});
  assert.equal(reused.stops,0);assert.equal(reused.server.listening,true);
});
