import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const target=path.join(root,'tools','ReleaseLauncherTests.exe');
const compiler=path.join(process.env.WINDIR || 'C:\\Windows','Microsoft.NET','Framework64','v4.0.30319','csc.exe');
const built=spawnSync(compiler,['/nologo','/target:exe','/platform:x64','/main:ReleaseLauncherTests','/out:'+target,'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll',path.join(root,'scripts','release','Launcher.cs'),path.join(root,'scripts','release','LauncherTests.cs')],{encoding:'utf8',windowsHide:true});
if(built.status!==0) throw new Error(built.stdout || built.stderr || built.error?.message);
const parent=path.join(root,'tools','release-support'); fs.mkdirSync(parent,{recursive:true});
const fixture=fs.mkdtempSync(path.join(parent,'launcher-test-'));
try {
  const result=spawnSync(target,[fixture],{encoding:'utf8',windowsHide:true,timeout:30000});
  if(result.stdout)console.log(result.stdout.trim());
  if(result.status!==0)throw new Error(result.stderr || result.error?.message || 'Launcher regression failed.');
} finally {
  if(!path.resolve(fixture).startsWith(path.resolve(parent)+path.sep))throw new Error('Invalid test cleanup path.');
  fs.rmSync(fixture,{recursive:true,force:true});
}
