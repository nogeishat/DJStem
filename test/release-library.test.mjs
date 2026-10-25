import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { initializeLibrary } from '../scripts/release/library.mjs';
import { separationDevice } from '../lib/stems.mjs';

function fixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'djstem-release-'));
  t.after(() => fs.rmSync(directory, {recursive:true,force:true}));
  const app = path.join(directory,'application'), library = path.join(directory,'My music ü');
  fs.mkdirSync(path.join(app,'defaults'),{recursive:true});
  fs.writeFileSync(path.join(app,'defaults','settings.json'), JSON.stringify({matchedStemsMaxGb:10,separationDevice:'auto'}));
  fs.writeFileSync(path.join(app,'defaults','player-settings.json'), JSON.stringify({volume:42,autoPlay:true,savedMixes:[],playlists:[]}));
  return {directory,app,library};
}
test('new libraries use release defaults and keep all user files in the chosen folder', t => {
  const {app,library} = fixture(t); initializeLibrary(app,library);
  assert.equal(JSON.parse(fs.readFileSync(path.join(library,'settings.json'))).libraryRoot,'.');
  assert.equal(JSON.parse(fs.readFileSync(path.join(library,'data','player-settings.json'))).volume,42);
  for(const folder of ['mp3','stems','data','matched-stems']) assert.ok(fs.statSync(path.join(library,folder)).isDirectory());
  assert.equal(fs.existsSync(path.join(app,'data')),false);
});
test('updates and edition changes preserve existing preferences, playlists, mixes, and songs', t => {
  const {app,library} = fixture(t); initializeLibrary(app,library);
  const prefs = {volume:17,savedMixes:[{Name:'My mix'}],playlists:[{Name:'My playlist'}]};
  fs.writeFileSync(path.join(library,'data','player-settings.json'),JSON.stringify(prefs));
  fs.writeFileSync(path.join(library,'mp3','keep.mp3'),'original audio');
  fs.writeFileSync(path.join(library,'settings.json'),JSON.stringify({matchedStemsMaxGb:4.2,separationDevice:'cpu'}));
  initializeLibrary(app,library);
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(library,'data','player-settings.json'))),prefs);
  assert.equal(JSON.parse(fs.readFileSync(path.join(library,'settings.json'))).matchedStemsMaxGb,4.2);
  assert.equal(fs.readFileSync(path.join(library,'mp3','keep.mp3'),'utf8'),'original audio');
});
test('moving a complete library relocates saved mix paths, dictionary keys, and playlists', t => {
  const {app,library,directory} = fixture(t); initializeLibrary(app,library);
  const audio = path.join(library,'mp3','Artist','Song.mp3');
  const prefs = {audio,savedMixes:[{OriginalAudio:audio,KeyShifts:{[audio]:2}}],playlists:[{Songs:[audio]}]};
  fs.writeFileSync(path.join(library,'data','player-settings.json'),JSON.stringify(prefs));
  const moved = path.join(directory,'Moved library'); fs.renameSync(library,moved); initializeLibrary(app,moved);
  const result=JSON.parse(fs.readFileSync(path.join(moved,'data','player-settings.json')));
  assert.equal(result.audio,path.join(moved,'mp3','Artist','Song.mp3'));
  assert.equal(result.savedMixes[0].KeyShifts[result.audio],2);
  assert.equal(result.playlists[0].Songs[0],result.audio);
});
test('future library formats fail without rewriting their user settings', t => {
  const {app,library} = fixture(t); fs.mkdirSync(library,{recursive:true});
  fs.writeFileSync(path.join(library,'djstem-library.json'),JSON.stringify({format:'djstem-library',version:99}));
  fs.writeFileSync(path.join(library,'settings.json'),'keep me');
  assert.throws(()=>initializeLibrary(app,library),/not supported/);
  assert.equal(fs.readFileSync(path.join(library,'settings.json'),'utf8'),'keep me');
});
test('CPU edition cannot select CUDA and NVIDIA edition respects the saved CPU choice', t => {
  const {app,library} = fixture(t); initializeLibrary(app,library);
  const old=Object.fromEntries(['STEMKIT_USER_DATA','STEMKIT_EDITION','STEMKIT_DEVICE'].map(key=>[key,process.env[key]]));
  t.after(()=>{for(const [key,value] of Object.entries(old)) {if(value===undefined) delete process.env[key]; else process.env[key]=value;}});
  process.env.STEMKIT_USER_DATA=library; process.env.STEMKIT_EDITION='cpu'; process.env.STEMKIT_DEVICE='cuda';
  assert.equal(separationDevice(app),'cpu'); delete process.env.STEMKIT_DEVICE;
  process.env.STEMKIT_EDITION='nvidia'; assert.equal(separationDevice(app),'auto');
  fs.writeFileSync(path.join(library,'settings.json'),JSON.stringify({separationDevice:'cpu'})); assert.equal(separationDevice(app),'cpu');
});
