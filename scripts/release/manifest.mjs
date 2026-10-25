import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';

const root = path.resolve(process.argv[2]);
const build = JSON.parse(fs.readFileSync(path.join(root,'release-build.json'),'utf8'));
const files = {};
for(const name of fs.readdirSync(path.join(root,'tools'))) {
  const file=path.join(root,'tools',name);
  if(!fs.statSync(file).isFile() || !/\.(exe|dll)$/.test(name)) continue;
  files['tools/'+name]={bytes:fs.statSync(file).size,sha256:createHash('sha256').update(fs.readFileSync(file)).digest('hex')};
}
const python=path.join(root,'tools','stem-runtime','python.exe');
const result=spawnSync(python,['-c','import importlib.metadata as m,json,sys; print(json.dumps({"python":sys.version,"packages":sorted([{"name":d.metadata["Name"],"version":d.version,"license":d.metadata.get("License"),"homePage":d.metadata.get("Home-page")} for d in m.distributions()],key=lambda d:d["name"].lower())}))'],{encoding:'utf8',windowsHide:true,env:{...process.env,PYTHONNOUSERSITE:'1'}});
if(result.status!==0)throw new Error(result.stderr);
const version=name=>spawnSync(path.join(root,'tools',name+'.exe'),[name==='yt-dlp'?'--version':'-version'],{encoding:'utf8',windowsHide:true}).stdout.trim().split(/\r?\n/)[0];
fs.writeFileSync(path.join(root,'dependency-manifest.json'),JSON.stringify({version:build.version,edition:build.edition,files,versions:{node:spawnSync(path.join(root,'tools','node.exe'),['--version'],{encoding:'utf8',windowsHide:true}).stdout.trim(),ffmpeg:version('ffmpeg'),ytdlp:version('yt-dlp')},...JSON.parse(result.stdout),models:JSON.parse(fs.readFileSync(path.join(root,'tools','stem-models','model-receipts.json'),'utf8'))},null,2)+'\n');
console.log('Dependency manifest created: '+build.edition);
