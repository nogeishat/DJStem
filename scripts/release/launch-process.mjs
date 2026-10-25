import { spawn } from 'node:child_process';

// The launcher owns only the service it started, never a reused service.
export async function launchWindow({ executable, args = [], label, app, spawnProcess = spawn, stdio = 'ignore', signals = process }) {
  let child, interrupted = false;
  const stop = () => { interrupted = true; child?.kill(); };
  try {
    child = spawnProcess(executable, args, { windowsHide: false, stdio });
    signals.once('SIGINT', stop); signals.once('SIGTERM', stop);
    await new Promise((resolve, reject) => {
      child.once('error', reject);
      child.once('close', (code, signal) => {
        if (interrupted || code === 0) resolve();
        else reject(new Error(`${label} exited ${signal ? `with signal ${signal}` : `with code ${code}`}.`));
      });
    });
  } catch (error) {
    const policyHint = process.platform === 'win32' && ['UNKNOWN', 'EACCES', 'EPERM'].includes(error.code) ? '\nIf Windows reports an application-control or signing-policy block, the app needs a trusted code-signing certificate.' : '';
    throw new Error(`Could not open ${label}. ${error.message}${error.code ? ` (${error.code})` : ''}${policyHint}`, { cause: error });
  } finally {
    signals.removeListener('SIGINT', stop); signals.removeListener('SIGTERM', stop);
    if (app) {
      app.worker.stop();
      if (app.server.listening) await new Promise((resolve, reject) => {
        app.server.close(error => error ? reject(error) : resolve());
        // An unfinished local HTTP request must not keep Node and its launch log
        // alive after the drop box closes. The worker has already been stopped.
        app.server.closeAllConnections();
      });
    }
  }
}
