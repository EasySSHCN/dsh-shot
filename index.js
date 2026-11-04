/**
 * dsh-shot — Host half.
 *
 * Captures the desktop through a PowerShell-hosted WinForms overlay (no compiled exe: the C#
 * window code is compiled in-process by Add-Type, which avoids the "fresh unsigned binary"
 * AV heuristic) and hands the resulting PNG to DSH as a durable image attachment.
 *
 * Entry points:
 *   tool  screenshot  — model-callable, returns the image
 *   command /shot     — captures and submits the image into the current conversation
 */
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import fs from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'

const here = path.dirname(fileURLToPath(import.meta.url))
const HELPER = path.join(here, 'helper', 'dsh-shot.ps1')
const SHOTD = path.join(here, 'helper', 'shotd.ps1')

/**
 * Latest captured shot, served to the Web UI client over a localhost route
 * (GET /dsh-shot/seq, GET /dsh-shot/last?since=N). The client plugin polls it and
 * inserts the image into the composer draft — never auto-submitting.
 */
const shots = { seq: 0, last: null }

function storeShot(ev, data) {
  shots.seq += 1
  shots.last = {
    seq: shots.seq,
    action: ev.action || 'session',
    name: path.basename(ev.path),
    width: ev.width,
    height: ev.height,
    time: Date.now(),
    dataB64: data.toString('base64'),
  }
}

function handleShotHttp(ctx, req, res) {
  let url
  try { url = new URL(req.url, 'http://localhost') } catch { res.writeHead(400); res.end(); return }
  const json = (code, body) => {
    res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' })
    res.end(JSON.stringify(body))
  }
  if (url.pathname === '/dsh-shot/capture') {
    // POST: trigger one capture from the Web UI button (same pipeline as the
    // global hotkey: capture -> spool PNG -> storeShot; the client poller then
    // inserts it into the composer draft, never auto-submitting).
    if (req.method !== 'POST') return json(405, { ok: false, error: 'POST required' })
    let body = ''
    req.on('data', (d) => { body += d })
    req.on('end', async () => {
      try {
        const args = {}
        try {
          const p = JSON.parse(body || '{}')
          if (p && p.mode === 'screen') args.mode = 'screen'
          if (p && p.region) args.region = p.region
        } catch {}
        const shot = await capture(ctx, args, undefined)
        const png = await fs.readFile(shot.path)
        storeShot({ path: shot.path, width: shot.width, height: shot.height, action: 'session' }, png)
        json(200, { ok: true, seq: shots.seq })
      } catch (e) {
        json(500, { ok: false, error: e?.message || String(e) })
      }
    })
    return
  }
  if (url.pathname === '/dsh-shot/seq') return json(200, { seq: shots.seq })
  if (url.pathname === '/dsh-shot/last') {
    const since = Number(url.searchParams.get('since') || '0')
    if (!shots.last || shots.last.seq <= since) return json(200, { none: true, seq: shots.seq })
    // stale shots (>10 min) are not delivered to a freshly opened page
    if (Date.now() - shots.last.time > 10 * 60 * 1000) return json(200, { none: true, seq: shots.last.seq })
    return json(200, shots.last)
  }
  res.writeHead(404, { 'Content-Type': 'text/plain' })
  res.end('not found')
}

function tempPng(tag) {
  const stamp = new Date().toISOString().replace(/[:.]/g, '-')
  return path.join(os.tmpdir(), 'dsh-shot', `${tag}-${stamp}.png`)
}

/** Best-effort cleanup of stale captures (older than 48h) at plugin start. */
async function cleanupTempDir() {
  const root = path.join(os.tmpdir(), 'dsh-shot')
  const cutoff = Date.now() - 48 * 3600 * 1000
  try {
    const entries = await fs.readdir(root, { withFileTypes: true })
    for (const e of entries) {
      if (!e.isFile() || !e.name.endsWith('.png')) continue
      const full = path.join(root, e.name)
      try {
        const st = await fs.stat(full)
        if (st.mtimeMs < cutoff) await fs.unlink(full)
      } catch {}
    }
  } catch {}
}

function runHelper(args, signal) {
  return new Promise((resolve, reject) => {
    let settled = false
    const child = spawn(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', HELPER, ...args],
      { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] },
    )
    let out = ''
    let err = ''
    child.stdout.setEncoding('utf8')
    child.stderr.setEncoding('utf8')
    child.stdout.on('data', (d) => { out += d })
    child.stderr.on('data', (d) => { err += d })

    const onAbort = () => { try { child.kill() } catch {} }
    if (signal) {
      if (signal.aborted) onAbort()
      else signal.addEventListener('abort', onAbort, { once: true })
    }

    const finish = (fn, value) => {
      if (settled) return
      settled = true
      if (signal) signal.removeEventListener('abort', onAbort)
      fn(value)
    }

    child.on('error', (e) => finish(reject, e))
    child.on('close', (code) => {
      const line = out.trim().split(/\r?\n/).filter(Boolean).pop() || ''
      let json
      try { json = JSON.parse(line) } catch {
        return finish(reject, new Error(
          `screenshot helper produced no result (exit ${code})${err ? `: ${err.trim().slice(0, 400)}` : ''}`,
        ))
      }
      if (!json.ok) {
        const reason = json.error || 'capture failed'
        return finish(reject, new Error(reason === 'cancelled' ? 'Screenshot cancelled by the user.' : `Screenshot failed: ${reason}`))
      }
      finish(resolve, json)
    })
  })
}

async function capture(ctx, args, signal) {
  const mode = args?.mode === 'screen' ? 'screen' : 'region'
  const out = tempPng('shot')
  let argv

  if (mode === 'screen') {
    // whole desktop, no interaction
    argv = ['-Mode', 'capture', '-Out', out]
  } else {
    argv = ['-Mode', 'overlay', '-Out', out]
    const region = args?.region
    if (region && Number.isFinite(region.x) && Number.isFinite(region.width) && region.width > 1 && region.height > 1) {
      argv.push('-Scripted', `${Math.round(region.x)},${Math.round(region.y)},${Math.round(region.width)},${Math.round(region.height)}`, '-AutoAcceptMs', '400')
    }
    // no explicit rect => interactive overlay: the user drags and presses Enter
  }

  const result = await runHelper(argv, signal)

  // overlay mode reports captureMs/totalMs; capture-only mode reports a single "ms"
  const captureMs = result.captureMs ?? result.ms
  const totalMs = result.totalMs ?? result.ms

  const data = await fs.readFile(result.path)
  const ref = await ctx.attachments.saveImage({
    data,
    mediaType: 'image/png',
    name: path.basename(result.path),
  })
  return {
    attachment: ref,
    path: result.path,
    width: result.width,
    height: result.height,
    bytes: data.length,
    captureMs,
    totalMs,
  }
}

function describe(shot, prefix) {
  return `${prefix}${shot.width}x${shot.height}, ${Math.round(shot.bytes / 1024)} KB, capture ${shot.captureMs ?? '?'} ms`
}

export const inject = ['tools', 'attachments', 'commands', 'webServer']

/**
 * Global-hotkey daemon wiring. Spawns helper/shotd.ps1 (resident), parses its JSON-per-line
 * stdout protocol, and on each "shot" event saves the spooled PNG as an attachment and
 * queues it into the most recently active eligible session. The returned disposer kills the
 * daemon and its whole child tree (the overlay PowerShell is a grandchild, so taskkill /T).
 * An unexpected daemon exit is reported once and hotkeys stay dead until plugin reload —
 * no automatic restart, by design.
 */
function startHotkeyDaemon(ctx, config) {
  const hotkey = (config && typeof config.hotkey === 'string' && config.hotkey.trim()) || 'Ctrl+Alt+A'
  const spool = path.join(os.tmpdir(), 'dsh-shot', 'spool')
  let child = null
  let stopping = false
  let buffer = ''
  let stderrBuf = ''

  async function deliverShot(ev) {
    const log = (msg) => {
      try {
        fs.appendFile(path.join(os.tmpdir(), 'dsh-shot', 'delivery.log'),
          `${new Date().toISOString()} ${msg}\n`).catch(() => {})
      } catch {}
    }
    try {
      log(`shot event: ${ev.path} ${ev.width}x${ev.height} action=${ev.action || 'session'}`)
      if (ev.action === 'copy') {
        // clipboard-only: the helper already copied the bitmap; do not store the shot,
        // so the client poller never sees it and no session/composer is opened.
        log('copy action: clipboard only; draft insert skipped')
        return
      }
      const data = await fs.readFile(ev.path)
      // Store for the client plugin's poller; the Web UI inserts it into the composer
      // draft (image + preset caption for translate/ocr). Never auto-submits.
      storeShot(ev, data)
      log(`stored seq=${shots.seq} action=${shots.last.action} bytes=${data.length} — waiting for client draft insert`)
    } catch (e) {
      const detail = {
        message: e?.message,
        code: e?.code,
        details: e?.details,
        name: e?.name,
        stack0: (e?.stack || '').split('\n')[0],
      }
      log(`DELIVERY FAILED: ${JSON.stringify(detail)}`)
      console.error('[dsh-shot] hotkey delivery failed:', e?.message || e)
    }
  }

  function handleLine(line) {
    let ev
    try { ev = JSON.parse(line) } catch {
      console.error('[dsh-shot] daemon sent a non-JSON line:', line.slice(0, 200))
      return
    }
    switch (ev.event) {
      case 'shot': return void deliverShot(ev)
      case 'error': return void console.error('[dsh-shot daemon]', ev.message || JSON.stringify(ev))
      case 'ready': return // hotkey registered; nothing to do
      case 'hotkey':
      case 'cancelled': return // informational
      default: return
    }
  }

  function start() {
    if (child) return
    stopping = false
    buffer = ''
    child = spawn(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', SHOTD, '--hotkey', hotkey, '--spool', spool],
      { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] },
    )
    child.stdout.setEncoding('utf8')
    child.stderr.setEncoding('utf8')
    child.stdout.on('data', (d) => {
      buffer += d
      let i
      while ((i = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, i).trim()
        buffer = buffer.slice(i + 1)
        if (line) handleLine(line)
      }
    })
    child.stderr.on('data', (d) => {
      stderrBuf += d
      if (stderrBuf.length > 4000) stderrBuf = stderrBuf.slice(-4000)
    })
    child.on('error', (e) => {
      console.error('[dsh-shot] failed to spawn hotkey daemon:', e.message)
    })
    child.on('close', (code, sig) => {
      const tail = stderrBuf.trim()
      child = null
      if (!stopping) {
        console.error(
          `[dsh-shot] hotkey daemon exited unexpectedly (code=${code} signal=${sig}); ` +
          `global hotkey '${hotkey}' is disabled until the plugin is reloaded` +
          (tail ? `; stderr tail: ${tail.slice(-400)}` : ''),
        )
      }
    })
  }

  function dispose() {
    stopping = true
    if (!child) return
    const pid = child.pid
    // kill the tree: the overlay window (DshShot.Run) runs as a grandchild powershell
    try {
      spawn('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' })
    } catch {}
    try { child.kill() } catch {}
    child = null
  }

  return { start, dispose, hotkey }
}

export function apply(ctx, config) {
  const enabled = config?.enabled !== false
  if (!enabled) return

  void cleanupTempDir()

  // localhost route the client plugin polls: /dsh-shot/seq, /dsh-shot/last?since=N
  ctx.effect(() => ctx.webServer.register({
    kind: 'prefix',
    path: '/dsh-shot',
    handler: (req, res) => handleShotHttp(ctx, req, res),
  }))

  // global hotkey -> capture -> store for the client draft inserter (never auto-submits)
  if (process.platform === 'win32') {
    const daemon = startHotkeyDaemon(ctx, config)
    ctx.effect(() => {
      daemon.start()
      return daemon.dispose
    })
  }

  ctx.effect(() => ctx.tools.register({
    name: 'screenshot',
    description:
      'Capture the user\'s screen and return the image. Use when the user asks you to look at their screen, ' +
      'a window, or an error dialog. Default mode shows a full-screen overlay so the user drags the region ' +
      'and presses Enter; pass mode="region" with an explicit region to skip the overlay, or mode="screen" ' +
      'for the whole desktop.',
    parameters: {
      type: 'object',
      properties: {
        mode: {
          type: 'string',
          enum: ['region', 'screen'],
          description: 'region (default) captures a selected area; screen captures the whole desktop without interaction.',
        },
        region: {
          type: 'object',
          description: 'Explicit rectangle in physical pixels. When given, no overlay is shown.',
          properties: {
            x: { type: 'integer' },
            y: { type: 'integer' },
            width: { type: 'integer' },
            height: { type: 'integer' },
          },
          required: ['x', 'y', 'width', 'height'],
        },
      },
      additionalProperties: false,
    },
    output: {
      schema: { type: 'object', additionalProperties: true },
      render(args, value) {
        const blocks = []
        if (value && value.attachment) blocks.push({ type: 'image', attachment: value.attachment })
        blocks.push({ type: 'text', text: describe(value || {}, 'Screenshot ') + ` (${value?.path || ''})` })
        return blocks
      },
    },
    isConcurrencySafe: () => false,
    timeoutMs: 180000,
    async execute(args, exec) {
      const shot = await capture(ctx, args, exec.signal)
      return {
        path: shot.path,
        width: shot.width,
        height: shot.height,
        bytes: shot.bytes,
        captureMs: shot.captureMs,
        totalMs: shot.totalMs,
        attachment: shot.attachment,
      }
    },
  }))

  ctx.effect(() => ctx.commands.register({
    name: 'shot',
    description: '截图并放进输入框草稿（不自动发送；可带区域：/shot 100,100,800,600）',
    input: { hint: '可选：x,y,宽,高；留空则拖框选择', attachments: false },
    async handler(invocation) {
      try {
        const raw = (invocation.rawInput || '').trim()
        const args = {}
        const parts = raw.split(',').map((s) => Number(s.trim()))
        if (parts.length === 4 && parts.every((n) => Number.isFinite(n))) {
          args.region = { x: parts[0], y: parts[1], width: parts[2], height: parts[3] }
        }
        const shot = await capture(ctx, args, invocation.signal)
        const png = await fs.readFile(shot.path)
        storeShot({ path: shot.path, width: shot.width, height: shot.height, action: 'session' }, png)
        return { kind: 'success', text: describe(shot, '已截图，已放入输入框草稿（按回车发送）：') }
      } catch (e) {
        return { kind: 'error', text: '截图失败：' + (e?.message || String(e)) }
      }
    },
  }))
}
