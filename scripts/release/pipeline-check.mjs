import fs from 'node:fs';
import path from 'node:path';
import { initializeLibrary, configureRelease } from './library.mjs';
import { run } from '../portable/verify.mjs';

const root=path.resolve(process.argv[2]), library=path.resolve(process.argv[3]), device=process.argv[4] || 'cpu';
const build=JSON.parse(fs.readFileSync(path.join(root,'release-build.json'),'utf8'));
initializeLibrary(root,library); configureRelease(root,library,build.edition);
const work=path.join(library,'data','pipeline-test'); fs.mkdirSync(work,{recursive:true});
const mp3=path.join(work,'test.mp3'), output=path.join(work,'stems');
await run(path.join(root,'tools','ffmpeg.exe'),['-hide_banner','-loglevel','error','-y','-f','lavfi','-i','aevalsrc=0.10*sin(2*PI*220*t)+0.07*sin(2*PI*440*t)+0.08*sin(2*PI*70*t)*exp(-10*(1-cos(4*PI*t))):s=44100:d=8','-ac','2','-codec:a','libmp3lame','-q:a','2',mp3]);
console.log('Generated an 8-second synthetic MP3.');
await run(process.env.STEMKIT_PYTHON,['-u',path.join(root,'scripts','analyze-music.py'),'--input',mp3,'--output',path.join(work,'analysis.json')],240000);
console.log('Bundled music analysis passed.');
console.log(await run(process.env.STEMKIT_PYTHON,['-u',path.join(root,'scripts','separate-stems.py'),'--input',mp3,'--output',output,'--device',device],600000));
const result=JSON.parse(fs.readFileSync(path.join(output,'separation.json'),'utf8'));
for(const name of ['drums','bass','other','vocals']) if(fs.statSync(path.join(output,name+'.wav')).size<=44)throw new Error('Empty '+name+' stem.');
fs.writeFileSync(path.join(library,'data','pipeline-check.json'),JSON.stringify({passed:true,edition:build.edition,requestedDevice:device,devices:result.devices,sampleRate:result.sampleRate,frames:result.frames},null,2));
console.log('Analysis and four-stem separation passed: '+JSON.stringify(result.devices));
